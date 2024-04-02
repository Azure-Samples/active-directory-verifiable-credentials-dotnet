using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Authorization;
using WoodgroveHelpdesk.Helpers;
using WoodgroveHelpdesk.Models;

namespace WoodgroveHelpdesk.Controllers
{
    //[Route("api/[controller]/[action]")]
    public class HomeController : Controller
    {
        protected IMemoryCache _cache;
        protected readonly ILogger<HomeController> _log;
        private IHttpClientFactory _httpClientFactory;
        private string _apiKey;
        private IConfiguration _configuration;
        public HomeController(IConfiguration configuration, IMemoryCache memoryCache, ILogger<HomeController> log, IHttpClientFactory httpClientFactory)
        {
            _configuration = configuration;
            _cache = memoryCache;
            _log = log;
            _httpClientFactory = httpClientFactory;
            _apiKey = System.Environment.GetEnvironmentVariable("API-KEY");
        }
        //some helper functions
        protected string GetRequestHostName() {
            string scheme = "https";// : this.Request.Scheme;
            string originalHost = this.Request.Headers["x-original-host"];
            string hostname = "";
            if (!string.IsNullOrEmpty( originalHost ))
                hostname = string.Format( "{0}://{1}", scheme, originalHost );
            else hostname = string.Format( "{0}://{1}", scheme, this.Request.Host );
            return hostname;
        }

        [AllowAnonymous]
        public IActionResult Index() {
            return View();
        }
        [AllowAnonymous]
        [ResponseCache( Duration = 0, Location = ResponseCacheLocation.None, NoStore = true )]
        public IActionResult Error() {
            return View( new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier } );
        }

        [AllowAnonymous]
        [HttpGet( "/api/verifier/presentation-request" )]
        public async Task<ActionResult> PresentationRequest()
        {
            _log.LogTrace( this.Request.GetDisplayUrl() );
            try
            {
                var accessToken = await MsalAccessTokenHandler.GetAccessToken( _configuration );
                if (accessToken.Item1 == String.Empty) {
                    _log.LogError(String.Format("failed to acquire accesstoken: {0} : {1}"), accessToken.error, accessToken.error_description);
                    return BadRequest(new { error = accessToken.error, error_description = accessToken.error_description });
                }

                WoodgroveHelpdesk.Models.PresentationRequest request = CreatePresentationRequest( null, null );
                string jsonString = JsonConvert.SerializeObject( request, Newtonsoft.Json.Formatting.None, new JsonSerializerSettings {
                    NullValueHandling = NullValueHandling.Ignore
                } );
                _log.LogTrace( $"Request API payload: {jsonString}" );
                string url = $"{_configuration["VerifiedID:ApiEndpoint"]}createPresentationRequest";
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

        public WoodgroveHelpdesk.Models.PresentationRequest CreatePresentationRequest( string stateId = null, string credentialType = null ) {
            WoodgroveHelpdesk.Models.PresentationRequest request = new WoodgroveHelpdesk.Models.PresentationRequest() {
                includeQRCode = _configuration.GetValue( "VerifiedID:includeQRCode", false ),
                authority = _configuration["VerifiedID:DidAuthority"],
                registration = new Registration() {
                    clientName = _configuration.GetValue("VerifiedID:client_name", "Woodgrove Helpdesk"),
                    purpose = _configuration.GetValue( "VerifiedID:purpose", "To prove your identity" )
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
                credentialType = _configuration.GetValue("VerifiedID:CredentialType", "VerifiedEmployee");
            }
            bool allowRevoked = _configuration.GetValue( "VerifiedID:allowRevoked", false );
            bool validateLinkedDomain = _configuration.GetValue( "VerifiedID:validateLinkedDomain", true );
            AddRequestedCredential( request, credentialType, null, allowRevoked, validateLinkedDomain );
            return request;
        }
        public WoodgroveHelpdesk.Models.PresentationRequest AddRequestedCredential( WoodgroveHelpdesk.Models.PresentationRequest request
                                                , string credentialType, List<string> acceptedIssuers
                                                , bool allowRevoked = false, bool validateLinkedDomain = true ) {
            request.requestedCredentials.Add( new RequestedCredential() {
                type = credentialType,
                acceptedIssuers = (null == acceptedIssuers ? new List<string>() : acceptedIssuers),
                configuration = new WoodgroveHelpdesk.Models.Configuration() {
                    validation = new Validation() {
                        allowRevoked = allowRevoked,
                        validateLinkedDomain = validateLinkedDomain,
                        faceCheck = new FaceCheck() {
                            sourcePhotoClaimName = _configuration.GetValue( "VerifiedID:sourcePhotoClaimName", "photo" ),
                            matchConfidenceThreshold = _configuration.GetValue( "VerifiedID:matchConfidenceThreshold", 70 )
                        }
                    }
                }
            } );
            return request;
        }
        public bool IsFaceCheckRequested( WoodgroveHelpdesk.Models.PresentationRequest request ) {
            foreach( var rc in request.requestedCredentials ) {
                if ( rc.configuration.validation.faceCheck != null ) {
                    return true;
                }
            }
            return false;
        }

        [AllowAnonymous]
        [HttpPost( "/api/processpresentedcredentials/{id}" )]
        public async Task<ActionResult> ProcessPresentedCredentials( string id ) {
            try {
                _log.LogTrace( this.HttpContext.Request.GetDisplayUrl() );
                //the id is the state value initially created when the issuanc request was requested from the request API
                //the in-memory database uses this as key to get and store the state of the process so the UI can be updated
                if (string.IsNullOrEmpty( id )) {
                    _log.LogTrace( $"Missing argument 'id'" );
                    return BadRequest( new { error = "400", error_description = "Missing argument 'id'" } );
                }
                if (!_cache.TryGetValue( id, out string buf )) {
                    _log.LogTrace( $"Cached data not found for id: {id}" );
                    return new NotFoundResult();
                }
                JObject cachedData = JObject.Parse( buf );
                CallbackEvent callback = JsonConvert.DeserializeObject<CallbackEvent>( cachedData["callback"].ToString() );
                if (callback.requestStatus != "presentation_verified") {
                    return BadRequest( new { error = "400", error_description = $"Wrong status in cached data" } );
                }
                // Find the Verified ID credential we asked for and get the first- and last name
                string didIssuer = null;
                string linkedDomain = null;
                string email = null;
                string displayName = null;
                string expiryDate = null;
                double matchConfidenceScore = (double)0;
                string credentialType = _configuration.GetValue("VerifiedID:CredentialType", "VerifiedEmployee" );
                string emailClaimName = _configuration.GetValue("VerifiedID:EmailClaimName", "mail");
                string displayNameClaimName = _configuration.GetValue("VerifiedID:DisplayNameClaimName", "displayName");
                foreach (var vc in callback.verifiedCredentialsData) {
                    if (vc.type.Contains( credentialType )) {
                        linkedDomain = vc.domainValidation.url;
                        didIssuer = vc.issuer;
                        if (vc.claims.ContainsKey( emailClaimName )) {
                            email = vc.claims[emailClaimName].ToString();
                        }
                        if (vc.claims.ContainsKey( displayNameClaimName )) {
                            displayName = vc.claims[displayNameClaimName].ToString();
                        }
                        expiryDate = vc.expirationDate;
                        matchConfidenceScore = vc.faceCheck.matchConfidenceScore;
                    }
                }
                if ( string.IsNullOrWhiteSpace( credentialType )) {
                    return BadRequest( new { error = "400", error_description = $"Expected Verified ID credential type {credentialType} not received." } );
                }
                if (string.IsNullOrWhiteSpace( email ) || string.IsNullOrWhiteSpace( displayName )) {
                    return BadRequest( new { error = "400", error_description = $"{emailClaimName}/{displayNameClaimName} missing in presented credential." } );
                }
                string domain = linkedDomain.Replace("https://", "").Replace("/", "");
                //
                var cacheData = new {
                    status = "user_authenticated",
                    message = $"{credentialType} presented",
                    displayName = displayName,
                    email = email,
                    domain = domain,
                    expiryData = expiryDate,
                    matchConfidenceScore = matchConfidenceScore,
                    didIssuer = didIssuer
                };
                _log.LogTrace( $"{cacheData.message}. email={email}" );
                _cache.Set( id, JsonConvert.SerializeObject( cacheData ) );
                return new ContentResult { StatusCode = (int)HttpStatusCode.Created, ContentType = "application/json", Content = JsonConvert.SerializeObject( cacheData ) };
            } catch (Exception ex) {
                return BadRequest( new { error = "400", error_description = ex.Message } );
            }
        }

    } // cls
} // ns
