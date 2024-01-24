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
using OnboardWithTAP.Helpers;
using OnboardWithTAP.Models;
using Microsoft.Graph;
using System.Globalization;
using Azure.Identity;

namespace OnboardWithTAP.Controllers
{
    //[Route("api/[controller]/[action]")]
    public class GuestController : Controller
    {
        //protected readonly AppSettingsModel AppSettings;
        protected IMemoryCache _cache;
        protected readonly ILogger<EmployeeController> _log;
        private IHttpClientFactory _httpClientFactory;
        private string _apiKey;
        private IConfiguration _configuration;
        public GuestController(IConfiguration configuration, IMemoryCache memoryCache, ILogger<EmployeeController> log, IHttpClientFactory httpClientFactory)
        {
            _configuration = configuration;
            _cache = memoryCache;
            _log = log;
            _httpClientFactory = httpClientFactory;
            _apiKey = System.Environment.GetEnvironmentVariable("API-KEY");
        }

        protected Microsoft.Graph.GraphServiceClient GetGraphClient() {
            var clientSecretCredential = new ClientSecretCredential( _configuration["AzureAd:TenantId"]
                                                                    , _configuration["AzureAd:ClientId"], _configuration["AzureAd:ClientSecret"]
                                                                    , new TokenCredentialOptions { AuthorityHost = AzureAuthorityHosts.AzurePublicCloud }
                                                                    );
            var scopes = new[] { "https://graph.microsoft.com/.default" };
            return new Microsoft.Graph.GraphServiceClient( clientSecretCredential, scopes );
        }

        [Authorize]
        public IActionResult TrustedPartners() {
            string path = Path.Combine( Path.GetDirectoryName( System.Reflection.Assembly.GetEntryAssembly().Location ), "trustedpartnerlist.txt" );
            string list = System.IO.File.ReadAllText( path );
            ViewData["trustedPartners"] = list;
            var allowedUserAdminRole = _configuration["AzureAd:AllowedUserAdminRole"];
            if ( !string.IsNullOrWhiteSpace( allowedUserAdminRole ) && !User.IsInRole(allowedUserAdminRole) ) {
                ViewData["message"] = "You only have read-only access to this data.";
                ViewData["accessLevel"] = "RO";
            } else {
                ViewData["message"] = "";
                ViewData["accessLevel"] = "RW";
            }
            return View();
        }

        [Authorize( Policy = "alloweduseradmins" )]
        //[Authorize]
        public async Task<IActionResult> SaveTrustedPartners( string trustedPartners ) {
            _log.LogTrace( this.HttpContext.Request.GetDisplayUrl() );
            string path = Path.Combine( Path.GetDirectoryName( System.Reflection.Assembly.GetEntryAssembly().Location ), "trustedpartnerlist.txt" );
            System.IO.File.WriteAllText( path, trustedPartners );
            ViewData["trustedPartners"] = trustedPartners;
            ViewData["message"] = $"List updated: {DateTime.UtcNow.ToString( "o", CultureInfo.InvariantCulture )}";
            return View( "TrustedPartners" );
        }
        [AllowAnonymous]
        public IActionResult GuestOnboarding() {
            return View();
        }


        /// <summary>
        /// This method is called from the UI to initiate the presentation of the verifiable credential
        /// </summary>
        /// <returns>JSON object with the address to the presentation request and optionally a QR code and a state value which can be used to check on the response status</returns>
        [AllowAnonymous]
        [HttpGet( "/api/verifier/guestonboarding" )]
        public async Task<ActionResult> OnboardGuestPresentationRequest()
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
                _log.LogTrace( accessToken.token );

                string url = $"{_configuration["VerifiedID:ApiEndpoint"]}createPresentationRequest";
                OnboardWithTAP.Models.PresentationRequest request = CreatePresentationRequest( null, null );
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
        /// This method creates a PresentationRequest object instance from configuration
        /// </summary>
        /// <param name="stateId"></param>
        /// <param name="credentialType"></param>
        /// <param name="acceptedIssuers"></param>
        /// <returns></returns>
        public OnboardWithTAP.Models.PresentationRequest CreatePresentationRequest( string stateId = null, string credentialType = null ) {
            OnboardWithTAP.Models.PresentationRequest request = new OnboardWithTAP.Models.PresentationRequest() {
                includeQRCode = _configuration.GetValue( "VerifiedID:includeQRCode", false ),
                authority = _configuration["VerifiedID:DidAuthority"],
                registration = new Registration() {
                    clientName = _configuration["VerifiedID:client_name_guest"],
                    purpose = _configuration.GetValue( "VerifiedID:purpose_guest", "" )
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
                credentialType = _configuration.GetValue("VerifiedID:CredentialTypeGuest", "VerifiedEmployee");
            }
            bool allowRevoked = _configuration.GetValue( "VerifiedID:allowRevoked", false );
            bool validateLinkedDomain = _configuration.GetValue( "VerifiedID:validateLinkedDomain", true );
            AddRequestedCredential( request, credentialType, null, allowRevoked, validateLinkedDomain );
            return request;
        }
        public OnboardWithTAP.Models.PresentationRequest AddRequestedCredential( OnboardWithTAP.Models.PresentationRequest request
                                                , string credentialType, List<string> acceptedIssuers
                                                , bool allowRevoked = false, bool validateLinkedDomain = true ) {
            request.requestedCredentials.Add( new RequestedCredential() {
                type = credentialType,
                acceptedIssuers = (null == acceptedIssuers ? new List<string>() : acceptedIssuers),
                configuration = new OnboardWithTAP.Models.Configuration() {
                    validation = new Validation() {
                        allowRevoked = allowRevoked,
                        validateLinkedDomain = validateLinkedDomain
                    }
                }
            } );
            return request;
        }

        private string ReverseString( string value ) {
            char[] arr = value.ToCharArray();
            Array.Reverse( arr );
            return new string( arr );
        }
        [AllowAnonymous]
        [HttpPost( "/api/onboardGuest/{id}" )]
        public async Task<ActionResult> onboardGuest( string id ) {
            try {
                _log.LogTrace( this.HttpContext.Request.GetDisplayUrl() );
                //the id is the state value initially created when the issuanc request was requested from the request API
                //the in-memory database uses this as key to get and store the state of the process so the UI can be updated
                /**/
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
                string credentialTypeGuest = _configuration.GetValue("VerifiedID:CredentialTypeGuest", "VerifiedEmployee" );
                string guestEmailClaimName = _configuration.GetValue("VerifiedID:GuestEmailClaimName", "mail");
                string guestDisplayNameClaimName = _configuration.GetValue("VerifiedID:GuestDisplayNameClaimName", "displayName");
                foreach (var vc in callback.verifiedCredentialsData) {
                    if (vc.type.Contains( credentialTypeGuest )) {
                        linkedDomain = vc.domainValidation.url;
                        didIssuer = vc.issuer;
                        if (vc.claims.ContainsKey( guestEmailClaimName )) {
                            email = vc.claims[guestEmailClaimName].ToString();
                        }
                        if (vc.claims.ContainsKey( guestDisplayNameClaimName )) {
                            displayName = vc.claims[guestDisplayNameClaimName].ToString();
                        }
                        // We could mke use of other VerifiedEmployee claims, like firstName, lastName, jobTitle and photo
                        // and update the guest account user profile with it. Setting the photo would make the user thumbnail
                        // work in the guest tenant.
                    }
                }
                if (string.IsNullOrWhiteSpace( email ) || string.IsNullOrWhiteSpace( displayName )) {
                    return BadRequest( new { error = "400", error_description = $"{guestEmailClaimName}/{guestDisplayNameClaimName} missing in presented credential." } );
                }

                // check trusted partner list
                string path = Path.Combine( Path.GetDirectoryName( System.Reflection.Assembly.GetEntryAssembly().Location ), "trustedpartnerlist.txt" );
                string[] list = System.IO.File.ReadAllText( path ).Split( "\r\n" );
                // is it an allowed did?
                string domain = linkedDomain.Replace( "https://", "" ).Replace( "/", "" );
                string reverseDomain = ReverseString( domain );
                bool isTrustedPartner = list.Contains<string>( didIssuer ); // if issuer did is in allowed list
                if (!isTrustedPartner) {
                    // is it an allowed linked domain?
                    foreach (string trustedDomain in list) {
                        if (!trustedDomain.StartsWith( "did:" )) {
                            if (trustedDomain == domain
                                || trustedDomain == "*" // if you allow anything
                                || (trustedDomain.StartsWith( "*." ) && reverseDomain.StartsWith( ReverseString( trustedDomain.Substring( 2 ) ) ))) {
                                isTrustedPartner = true;
                                break;
                            }
                        }
                    }
                }

                if (!isTrustedPartner) {
                    return BadRequest( new { error = "400", error_description = $"Guest onboarding is not allowed for your company" } );
                }

                //
                var mgClient = GetGraphClient();

                var users = await mgClient.Users.Request().Filter( $"mail eq '{email}'" ).GetAsync();
                if (null != users && users.Count >= 1) {
                    return BadRequest( new { error = "400", error_description = $"Guest account already exists for user '{email}'" } );
                }

                Invitation invitation = new Invitation() {
                    InvitedUserEmailAddress = email,
                    InvitedUserDisplayName = displayName,
                    InviteRedirectUrl = $"https://myapps.microsoft.com/?tenantId={_configuration["AzureAD:TenantID"]}"
                };
                var inviteResult = await mgClient.Invitations.Request().AddAsync( invitation );
                var userObjectId = inviteResult.InvitedUser.Id;
                var cacheData = new {
                    status = "invitation_created",
                    message = $"Guest account invitation created!",
                    userDisplayName = displayName,
                    email = email,
                    userObjectId = userObjectId,
                    inviteRedirectUrl = invitation.InviteRedirectUrl
                };
                _log.LogTrace( $"{cacheData.message}.objectId={userObjectId}, email={email}" );
                _cache.Set( id, JsonConvert.SerializeObject( cacheData ) );
                //return new OkResult();
                return new ContentResult { StatusCode = (int)HttpStatusCode.Created, ContentType = "application/json", Content = JsonConvert.SerializeObject( cacheData ) };
            } catch (Exception ex) {
                return BadRequest( new { error = "400", error_description = ex.Message } );
            }
        }

    } // cls
} // ns
