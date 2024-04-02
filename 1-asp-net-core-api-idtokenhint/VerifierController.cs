using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Identity.Client;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Security.Cryptography.X509Certificates;
using System.Net.Http.Headers;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Identity.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using System.Security.Policy;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Azure.Core;

namespace AspNetCoreVerifiableCredentials
{
    [Route("api/[controller]/[action]")]
    public class VerifierController : Controller
    {
        //protected readonly AppSettingsModel AppSettings;
        protected IMemoryCache _cache;
        protected readonly ILogger<VerifierController> _log;
        private IHttpClientFactory _httpClientFactory;
        private string _apiKey;
        private IConfiguration _configuration;
        public VerifierController(IConfiguration configuration, IMemoryCache memoryCache, ILogger<VerifierController> log, IHttpClientFactory httpClientFactory)
        {
            _configuration = configuration;
            _cache = memoryCache;
            _log = log;
            _httpClientFactory = httpClientFactory;
            _apiKey = System.Environment.GetEnvironmentVariable("API-KEY");
        }

        /// <summary>
        /// This method is called from the UI to initiate the presentation of the verifiable credential
        /// </summary>
        /// <returns>JSON object with the address to the presentation request and optionally a QR code and a state value which can be used to check on the response status</returns>
        [AllowAnonymous]
        [HttpGet("/api/verifier/presentation-request")]
        public async Task<ActionResult> PresentationRequest()
        {
            _log.LogTrace( this.Request.GetDisplayUrl() );
            try
            {
                //The VC Request API is an authenticated API. We need to clientid and secret (or certificate) to create an access token which 
                //needs to be send as bearer to the VC Request API
                var accessToken = await MsalAccessTokenHandler.GetAccessToken( _configuration );
                if (accessToken.Item1 == String.Empty)
                {
                    _log.LogError(String.Format("failed to acquire accesstoken: {0} : {1}"), accessToken.error, accessToken.error_description);
                    return BadRequest(new { error = accessToken.error, error_description = accessToken.error_description });
                }

                string url = $"{_configuration["VerifiedID:ApiEndpoint"]}createPresentationRequest";
                string template = HttpContext.Session.GetString( "presentationRequestTemplate" );
                PresentationRequest request = null;
                if ( !string.IsNullOrWhiteSpace(template) ) {
                    request = CreatePresentationRequestFromTemplate( template );
                } else {
                    request = CreatePresentationRequest();
                }

                string faceCheck = this.Request.Query["faceCheck"];
                bool useFaceCheck = (!string.IsNullOrWhiteSpace( faceCheck ) && (faceCheck == "1" || faceCheck == "true"));
                if (!hasFaceCheck( request ) && (useFaceCheck || _configuration.GetValue( "VerifiedID:useFaceCheck", false ))) {
                    AddFaceCheck( request, null, this.Request.Query["photoClaimName"] ); // when qp is null, appsettings value is used
                }
                AddClaimsConstrains( request );

                string jsonString = JsonConvert.SerializeObject( request, Newtonsoft.Json.Formatting.None, new JsonSerializerSettings {
                    NullValueHandling = NullValueHandling.Ignore
                } );

                _log.LogTrace( $"Request API payload: {jsonString}" );
                var client = _httpClientFactory.CreateClient();
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.token);
                HttpResponseMessage res = await client.PostAsync(url, new StringContent(jsonString, Encoding.UTF8, "application/json"));
                string response = await res.Content.ReadAsStringAsync();
                HttpStatusCode statusCode = res.StatusCode;

                if (statusCode == HttpStatusCode.Created)
                {
                    _log.LogTrace("succesfully called Request Service API");
                    JObject requestConfig = JObject.Parse(response);
                    requestConfig.Add(new JProperty("id", request.callback.state));
                    jsonString = JsonConvert.SerializeObject(requestConfig);

                    //We use in memory cache to keep state about the request. The UI will check the state when calling the presentationResponse method
                    var cacheData = new
                    {
                        status = "request_created",
                        message = "Waiting for QR code to be scanned",
                        expiry = requestConfig["expiry"].ToString()
                    };
                    _cache.Set(request.callback.state, JsonConvert.SerializeObject(cacheData)
                                    , DateTimeOffset.Now.AddSeconds( _configuration.GetValue<int>( "AppSettings:CacheExpiresInSeconds", 300 ) ) );
                    //the response from the VC Request API call is returned to the caller (the UI). It contains the URI to the request which Authenticator can download after
                    //it has scanned the QR code. If the payload requested the VC Request service to create the QR code that is returned as well
                    //the javascript in the UI will use that QR code to display it on the screen to the user.
                    return new ContentResult { ContentType = "application/json", Content = jsonString };
                }
                else
                {
                    _log.LogError("Error calling Verified ID API: "  + response );
                    return BadRequest(new { error = "400", error_description = "Verified ID API error response: " + response, request = jsonString });
                }
            }
            catch (Exception ex)
            {
                _log.LogError( "Exception: " + ex.Message );
                return BadRequest(new { error = "400", error_description = "Exception: " + ex.Message });
            }            
        }

        //
        //this function is called from the UI to get some details to display in the UI about what
        //credential is being asked for
        //
        [AllowAnonymous]
        [HttpGet("/api/verifier/get-presentation-details")]
        public ActionResult getPresentationDetails() {
            _log.LogTrace( this.Request.GetDisplayUrl() );
            try {
                PresentationRequest request = CreatePresentationRequest();
                var details = new {
                    clientName = request.registration.clientName,
                    purpose = request.registration.purpose,
                    VerifierAuthority = request.authority,
                    type = request.requestedCredentials[0].type,
                    acceptedIssuers = request.requestedCredentials[0].acceptedIssuers.ToArray(),
                    PhotoClaimName = _configuration.GetValue( "VerifiedID:PhotoClaimName", "" )
                };
                return new ContentResult { ContentType = "application/json", Content = JsonConvert.SerializeObject(details) };
            } catch (Exception ex) {
                return BadRequest(new { error = "400", error_description = ex.Message });
            }
        }

        //some helper functions
        protected string GetRequestHostName()
        {
            string scheme = "https";// : this.Request.Scheme;
            string originalHost = this.Request.Headers["x-original-host"];
            string hostname = "";
            if (!string.IsNullOrEmpty(originalHost))
                hostname = string.Format("{0}://{1}", scheme, originalHost);
            else hostname = string.Format("{0}://{1}", scheme, this.Request.Host);
            return hostname;
        }
        /// <summary>
        /// This method creates a PresentationRequest object instance from a JSON template
        /// </summary>
        /// <param name="template">JSON template of a Request Service API presentation payload</param>
        /// <param name="stateId"></param>
        /// <returns></returns>
        public PresentationRequest CreatePresentationRequestFromTemplate( string template, string stateId = null ) {
            PresentationRequest request = JsonConvert.DeserializeObject<PresentationRequest>( template );
            request.authority = _configuration["VerifiedID:DidAuthority"];
            if ( null == request.callback ) {
                request.callback = new Callback();
            }
            request.callback.url = $"{GetRequestHostName()}/api/verifier/presentationcallback";
            request.callback.state = (string.IsNullOrWhiteSpace( stateId ) ? Guid.NewGuid().ToString() : stateId);
            request.callback.headers = new Dictionary<string, string>() { { "api-key", this._apiKey } };
            return request;
        }
        /// <summary>
        /// This method creates a PresentationRequest object instance from configuration
        /// </summary>
        /// <param name="stateId"></param>
        /// <param name="credentialType"></param>
        /// <param name="acceptedIssuers"></param>
        /// <returns></returns>
        public PresentationRequest CreatePresentationRequest( string stateId = null, string credentialType = null, string[] acceptedIssuers = null ) {
            PresentationRequest request = new PresentationRequest() {
                includeQRCode = _configuration.GetValue( "VerifiedID:includeQRCode", false ),
                authority = _configuration["VerifiedID:DidAuthority"],
                registration = new Registration() {
                    clientName = _configuration["VerifiedID:client_name"],
                    purpose = _configuration.GetValue( "VerifiedID:purpose", "" )
                },
                callback = new Callback() {
                    url = $"{GetRequestHostName()}/api/verifier/presentationcallback",
                    state = (string.IsNullOrWhiteSpace( stateId ) ? Guid.NewGuid().ToString() : stateId),
                    headers = new Dictionary<string, string>() { { "api-key", this._apiKey } }
                },
                includeReceipt = _configuration.GetValue( "VerifiedID:includeReceipt", false ),
                requestedCredentials = new List<RequestedCredential>(),
            };
            if ("" == request.registration.purpose) {
                request.registration.purpose = null;
            }
            if (string.IsNullOrEmpty( credentialType )) {
                credentialType = _configuration["VerifiedID:CredentialType"];
            }
            List<string> okIssuers;
            if (acceptedIssuers == null) {
                okIssuers = new List<string>( new string[] { _configuration["VerifiedID:DidAuthority"] } );
            } else {
                okIssuers = new List<string>( acceptedIssuers );
            }
            bool allowRevoked = _configuration.GetValue( "VerifiedID:allowRevoked", false );
            bool validateLinkedDomain = _configuration.GetValue( "VerifiedID:validateLinkedDomain", true );
            AddRequestedCredential( request, credentialType, okIssuers, allowRevoked, validateLinkedDomain );
            return request;
        }
        public PresentationRequest AddRequestedCredential( PresentationRequest request
                                                , string credentialType, List<string> acceptedIssuers
                                                , bool allowRevoked = false, bool validateLinkedDomain = true ) {
            request.requestedCredentials.Add( new RequestedCredential() {
                type = credentialType,
                acceptedIssuers = (null == acceptedIssuers ? new List<string>() : acceptedIssuers),
                configuration = new Configuration() {
                    validation = new Validation() {
                        allowRevoked = allowRevoked,
                        validateLinkedDomain = validateLinkedDomain
                    }
                }
            } );
            return request;
        }
        private PresentationRequest AddFaceCheck( PresentationRequest request ) {
            string sourcePhotoClaimName = _configuration.GetValue( "VerifiedID:PhotoClaimName", "photo" );
            int matchConfidenceThreshold = _configuration.GetValue( "VerifiedID:matchConfidenceThreshold", 70 );
            return AddFaceCheck( request, request.requestedCredentials[0].type, sourcePhotoClaimName, matchConfidenceThreshold );
        }
        private PresentationRequest AddFaceCheck( PresentationRequest request, string credentialType, string sourcePhotoClaimName = "photo", int matchConfidenceThreshold = 70 ) {
            if ( string.IsNullOrWhiteSpace(sourcePhotoClaimName) ){
                sourcePhotoClaimName = _configuration.GetValue( "VerifiedID:PhotoClaimName", "photo" );
            }
            foreach (var requestedCredential in request.requestedCredentials) {
                if (null == credentialType || requestedCredential.type == credentialType) {
                    requestedCredential.configuration.validation.faceCheck = new FaceCheck() { sourcePhotoClaimName = sourcePhotoClaimName, matchConfidenceThreshold = matchConfidenceThreshold };
                    request.includeReceipt = false; // not supported while doing faceCheck
                }
            }
            return request;
        }
        private bool hasFaceCheck( PresentationRequest request ) {
            foreach (var requestedCredential in request.requestedCredentials) {
                if ( null != requestedCredential.configuration.validation.faceCheck ) {
                    return true;
                }
            }
            return false;
        }

        private PresentationRequest AddClaimsConstrains( PresentationRequest request ) {
            // This illustrates who you can set constraints of claims in requested credential.
            // The example just sets one constraint, but you can set multiple. If you set
            // multiple, all constraints must evaluate to true (AND logic)
            string constraintClaim = this.Request.Query["claim"];
            if (string.IsNullOrWhiteSpace( constraintClaim )) {
                return request;
            }
            string constraintOp = this.Request.Query["op"];
            string constraintValue = this.Request.Query["value"];

            Constraint constraint = null;
            if ( constraintOp == "value" ) {
                constraint = new Constraint() {
                    claimName = constraintClaim,
                    values = new List<string>() { constraintValue }
                };            
            }
            if (constraintOp == "contains") {
                constraint = new Constraint() {
                    claimName = constraintClaim,
                    contains = constraintValue
                };
            }
            if (constraintOp == "startsWith") {
                constraint = new Constraint() {
                    claimName = constraintClaim,
                    startsWith = constraintValue
                };
            }
            if ( null != constraint ) {
                // if request was created from template, constraint may already exist - update it if so
                if (null != request.requestedCredentials[0].constraints) {
                    bool found = false;
                    for( int i = 0; i < request.requestedCredentials[0].constraints.Count; i++ ) {
                        if (request.requestedCredentials[0].constraints[i].claimName == constraintClaim) {
                            request.requestedCredentials[0].constraints[i] = constraint;
                            found = true;
                        }
                    }
                    if ( !found ) {
                        request.requestedCredentials[0].constraints.Add( constraint );
                    }
                } else {
                    request.requestedCredentials[0].constraints = new List<Constraint>();
                    request.requestedCredentials[0].constraints.Add( constraint );
                }
            }
            return request;
        }
    } // cls
} // ns
