using Microsoft.AspNetCore.Mvc;
using System;
using System.IO;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using System.Net;
using System.Net.Http;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using System.Diagnostics;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Identity.Client;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using Microsoft.Identity.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using System.Reflection.Metadata.Ecma335;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Http;

namespace AspNetCoreVerifiableCredentials
{
    [Route("api/[controller]/[action]")]
    [ApiController]
    public class IssuerController : ControllerBase
    {
        protected IMemoryCache _cache;
        protected readonly ILogger<IssuerController> _log;
        private IHttpClientFactory _httpClientFactory;
        private IConfiguration _configuration;
        private string _apiKey;
        public IssuerController(IConfiguration configuration, IMemoryCache memoryCache, ILogger<IssuerController> log, IHttpClientFactory httpClientFactory)
        {
            _cache = memoryCache;
            _log = log;
            _configuration = configuration;
            _httpClientFactory = httpClientFactory;
            _apiKey = System.Environment.GetEnvironmentVariable("API-KEY");
        }

        private IssuanceRequest SetClaims( IssuanceRequest request ) {
            request.claims = new Dictionary<string, string>();
            request.claims.Add( "given_name", "Megan" );
            request.claims.Add( "family_name", "Bowen" );

            string photoClaimName = "";
            // get photo claim from manifest
            if (GetCredentialManifest( out string manifest, out string error )) {
                JObject jsonManifest = JObject.Parse(manifest);
                foreach( var claim in jsonManifest["display"]["claims"] ) {
                    string claimName = ((JProperty)claim).Name;                    
                    if (jsonManifest["display"]["claims"][claimName]["type"].ToString() == "image/jpg;base64url" ) {
                        photoClaimName = claimName.Replace( "vc.credentialSubject.", "");
                    }
                }
            }
            if (!string.IsNullOrWhiteSpace( photoClaimName) ) {
                // if we have a photoId in the Session
                string photoId = this.Request.Headers["rsid"];
                if ( !string.IsNullOrWhiteSpace(photoId) ) {
                    // if we have a photo in-mem cache
                    if ( _cache.TryGetValue( photoId, out string photo ) ) {
                        _log.LogTrace( $"Adding user photo to credential. photoId: {photoId}");
                        request.claims.Add( photoClaimName, photo );
                    } else {
                        _log.LogTrace( $"Couldn't find a user photo to add to credential. photoId: {photoId}" );
                    }
                }
            }
            return request;
        }

        public static string Sha256Hash( string source ) {
            string hash = null;
            using (SHA256 sha256Hash = SHA256.Create()) {
                byte[] bytes = sha256Hash.ComputeHash( Encoding.UTF8.GetBytes( source ) );
                hash = Convert.ToBase64String( bytes );
            }
            return hash;
        }

        /// <summary>
        /// This method is called from the UI to initiate the issuance of the verifiable credential
        /// </summary>
        /// <returns>JSON object with the address to the presentation request and optionally a QR code and a state value which can be used to check on the response status</returns>
        [AllowAnonymous]
        [HttpGet("/api/issuer/issuance-request")]
        public async Task<ActionResult> IssuanceRequest()
        {
            _log.LogTrace( this.HttpContext.Request.GetDisplayUrl() );
            try
            {
                string manifestUrl = _configuration["VerifiedID:CredentialManifest"];
                if ( string.IsNullOrWhiteSpace(manifestUrl ) ) {
                    string errmsg = $"Manifest missing in config file";
                    _log.LogError( errmsg );
                    return BadRequest( new { error = "400", error_description = errmsg } );
                }
                string tenantId = _configuration["VerifiedID:TenantId"];
                string manifestTenantId = manifestUrl.Split("/")[5];
                if ( manifestTenantId != tenantId)
                {
                    string errmsg = $"TenantId in ManifestURL {manifestTenantId}. does not match tenantId in config file {tenantId}";
                    _log.LogError(errmsg);
                    return BadRequest(new { error = "400", error_description = errmsg });
                }

                try
                {
                    //The VC Request API is an authenticated API. We need to clientid and secret (or certificate) to create an access token which 
                    //needs to be send as bearer to the VC Request API
                    var accessToken = await MsalAccessTokenHandler.GetAccessToken( _configuration );
                    if (accessToken.Item1 == String.Empty)
                    {
                        _log.LogError(String.Format("failed to acquire accesstoken: {0} : {1}", accessToken.error, accessToken.error_description));
                        return BadRequest(new { error = accessToken.error, error_description = accessToken.error_description });
                    }
                    IssuanceRequest request = CreateIssuanceRequest( out string pinCode );                    

                    // If the credential uses the idTokenHint attestation flow, then you must set the claims before
                    // calling the Request Service API
                    SetClaims( request );

                    string jsonString = JsonConvert.SerializeObject( request, Newtonsoft.Json.Formatting.None, new JsonSerializerSettings {
                        NullValueHandling = NullValueHandling.Ignore
                    } );

                    _log.LogTrace( $"Request API payload: {jsonString}" );
                    var client = _httpClientFactory.CreateClient();
                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.token);
                    string url = $"{_configuration["VerifiedID:ApiEndpoint"]}createIssuanceRequest";
                    HttpResponseMessage res = await client.PostAsync(url, new StringContent(jsonString, Encoding.UTF8, "application/json"));
                    string response = await res.Content.ReadAsStringAsync();

                    if (res.StatusCode == HttpStatusCode.Created)
                    {
                        _log.LogTrace("succesfully called Request API");
                        JObject requestConfig = JObject.Parse(response);
                        if (!string.IsNullOrEmpty(pinCode) ) { 
                            requestConfig["pin"] = pinCode;
                        }
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
                        _log.LogError("Unsuccesfully called Request API" + response);
                        return BadRequest(new { error = "400", error_description = "Something went wrong calling the API: " + response });
                    }
                }
                catch (Exception ex)
                {
                    return BadRequest(new { error = "400", error_description = "Something went wrong calling the API: " + ex.Message });
                }
            }
            catch (Exception ex)
            {
                return BadRequest(new { error = "400", error_description = ex.Message });
            }
        }

        private bool GetCredentialManifest( out string manifest, out string error) {
            error = null;
            if (!_cache.TryGetValue( "manifest", out manifest )) {
                string manifestUrl = _configuration["VerifiedID:CredentialManifest"];
                if (string.IsNullOrWhiteSpace( manifestUrl )) {
                    error = $"Manifest missing in config file";                    
                    return false;
                }
                var client = _httpClientFactory.CreateClient();
                HttpResponseMessage res = client.GetAsync( manifestUrl ).Result;
                string response = res.Content.ReadAsStringAsync().Result;
                if (res.StatusCode != HttpStatusCode.OK) {
                    error = $"HTTP status {(int)res.StatusCode} retrieving manifest from URL {manifestUrl}";
                    return false;
                }
                JObject resp = JObject.Parse( response );
                if ( resp.ContainsKey("token")) {
                    string jwtToken = resp["token"].ToString();
                    jwtToken = jwtToken.Replace( "_", "/" ).Replace( "-", "+" ).Split( "." )[1];
                    jwtToken = jwtToken.PadRight( 4 * ((jwtToken.Length + 3) / 4), '=' );
                    manifest = System.Text.Encoding.UTF8.GetString( Convert.FromBase64String( jwtToken ) );
                } else {
                    manifest = response;
                }
                _cache.Set( "manifest", manifest );
            }
            return true;
        }
        [HttpGet("/api/issuer/get-manifest")]
        public ActionResult getManifest() {
            _log.LogTrace( this.HttpContext.Request.GetDisplayUrl() );
            try {
                if ( !GetCredentialManifest( out string manifest, out string errmsg ) ) {
                    _log.LogError( errmsg );
                    return BadRequest( new { error = "400", error_description = errmsg } );
                }
                return new ContentResult { ContentType = "application/json", Content = manifest };
            } catch (Exception ex) {
                return BadRequest(new { error = "400", error_description = ex.Message });
            }
        }

        [HttpGet( "/api/issuer/selfie-request" )]
        public ActionResult SelfieRequest() {
            _log.LogTrace( this.HttpContext.Request.GetDisplayUrl() );
            try {
                string hostname = GetRequestHostName();
                string id = Guid.NewGuid().ToString();
                var request = new {
                    id = id,
                    url = $"{hostname}/selfie.html?callbackUrl={hostname}/api/issuer/selfie/{id}",
                    expiry = DateTimeOffset.UtcNow.AddMinutes( 5 ).ToUnixTimeSeconds(),
                    photo = "",
                    status = "request_created"
                };
                string resp = _cache.Set( id, JsonConvert.SerializeObject( request )
                    , DateTimeOffset.Now.AddSeconds( _configuration.GetValue<int>( "AppSettings:CacheExpiresInSeconds", 300 ) ) );
                return new ContentResult { StatusCode = (int)HttpStatusCode.Created, ContentType = "application/json", Content = resp };
            } catch (Exception ex) {
                return BadRequest( new { error = "400", error_description = ex.Message } );
            }
        }

        [AllowAnonymous]
        [HttpPost( "/api/issuer/userphoto" )]
        public ActionResult SetUserPhoto() {
            _log.LogTrace( this.HttpContext.Request.GetDisplayUrl() );
            try {
                string body = new System.IO.StreamReader( this.Request.Body ).ReadToEndAsync().Result;
                _log.LogTrace( body );
                int idx = body.IndexOf( ";base64," );
                if (-1 == idx) {
                    return BadRequest( new { error = "400", error_description = $"Image must be 'data:image/jpeg;base64,'" } );
                }
                string photo = body.Substring( idx + 8 );
                string photoId = this.Request.Headers["rsid"];
                int cacheSeconds = _configuration.GetValue<int>( "AppSettings:CacheExpiresInSeconds", 300 );
                _cache.Set( photoId, photo, DateTimeOffset.Now.AddSeconds( cacheSeconds ) );
                _log.LogTrace( $"User set photo to add to credential. photoId: {photoId}" );
                return new ContentResult { StatusCode = (int)HttpStatusCode.Created, ContentType = "application/json"
                                , Content = JsonConvert.SerializeObject( new { id = photoId, message = $"Photo will be cached for {cacheSeconds} seconds" } ) };
            } catch (Exception ex) {
                return BadRequest( new { error = "400", error_description = ex.Message } );
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

        protected bool IsMobile()
        {
            string userAgent = this.Request.Headers["User-Agent"];
            return (userAgent.Contains("Android") || userAgent.Contains("iPhone"));
        }

        public IssuanceRequest CreateIssuanceRequest( out string pinCode ) {
            pinCode = null;
            IssuanceRequest request = new IssuanceRequest() {
                includeQRCode = _configuration.GetValue( "VerifiedID:includeQRCode", false ),
                authority = _configuration["VerifiedID:DidAuthority"],
                registration = new Registration() {
                    clientName = _configuration["VerifiedID:client_name"],
                    purpose = _configuration.GetValue( "VerifiedID:purpose", "" )
                },
                callback = new Callback() {
                    url = $"{GetRequestHostName()}/api/issuer/issuecallback",
                    state = Guid.NewGuid().ToString(),
                    headers = new Dictionary<string, string>() { { "api-key", this._apiKey } }
                },
                type = "ignore-this",
                manifest = _configuration["VerifiedID:CredentialManifest"],
                pin = null
            };
            if ("" == request.registration.purpose) {
                request.registration.purpose = null;
            }
            if ( !IsMobile() ) {
                int issuancePinCodeLength = _configuration.GetValue( "VerifiedID:IssuancePinCodeLength", 0 );
                // if pincode is required, set it up in the request
                if (issuancePinCodeLength > 0 ) {
                    int pinCodeInt = RandomNumberGenerator.GetInt32( 1, int.Parse( "".PadRight( issuancePinCodeLength, '9' ) ) );
                    pinCode = string.Format( "{0:D" + issuancePinCodeLength.ToString() + "}", pinCodeInt );
                    _log.LogTrace( "pin={0}", pinCode );
                    if (_configuration.GetValue( "VerifiedID:HashPinCode", false ) ) {
                        string salt = _configuration.GetValue( "VerifiedID:PinCodeSalt", Guid.NewGuid().ToString() );
                        SetPinCode( request, Sha256Hash( salt + pinCode ), issuancePinCodeLength, salt );
                    } else {
                        SetPinCode( request, pinCode, issuancePinCodeLength );
                    }
                }
            }
            SetExpirationDate( request );
            return request;
        }
        private IssuanceRequest SetExpirationDate( IssuanceRequest request) {
            string credentialExpiration = _configuration.GetValue( "VerifiedID:CredentialExpiration", "" );
            DateTime expDateUtc;
            DateTime utcNow = DateTime.UtcNow;
            // This is just examples for how to specify your own expiry dates
            switch (credentialExpiration.ToUpperInvariant()) {
                case "EOD":
                    expDateUtc = DateTime.UtcNow;
                    break;
                case "EOW":
                    int start = (int)utcNow.DayOfWeek;
                    int target = (int)DayOfWeek.Sunday;
                    if (target <= start)
                        target += 7;
                    expDateUtc = utcNow.AddDays( target - start );
                    break;
                case "EOM":
                    expDateUtc = new DateTime( utcNow.Year, utcNow.Month, DateTime.DaysInMonth( utcNow.Year, utcNow.Month ) );
                    break;
                case "EOQ":
                    int quarterEndMonth = (int)(3 * Math.Ceiling( (double)utcNow.Month / 3 ));
                    expDateUtc = new DateTime( utcNow.Year, quarterEndMonth, DateTime.DaysInMonth( utcNow.Year, quarterEndMonth ) );
                    break;
                case "EOY":
                    expDateUtc = new DateTime( utcNow.Year, 12, 31 );
                    break;
                default:
                    return request;
            }
            // Remember that this date is expressed in UTC and Wallets/Authenticator displays the expiry date 
            // in local timezone. So for example, EOY will be displayed as "Jan 1" if the user is in a timezone
            // east of GMT. Also, if you issue a VC that should expire 5pm locally, then you need to calculate
            // what 5pm locally is in UTC time
            request.expirationDate = $"{Convert.ToDateTime( expDateUtc ).ToString( "yyyy-MM-dd" )}T23:59:59.000Z";
            return request;
        }
        public IssuanceRequest SetPinCode( IssuanceRequest request, string pinCode = null, int pinCodeLength = 0, string salt = null) {
            if (string.IsNullOrWhiteSpace( pinCode )) {
                request.pin = null;
            } else {
                request.pin = new Pin() {
                    length = pinCodeLength,
                    value = pinCode
                };
                // if hashed pin code
                if ( !string.IsNullOrWhiteSpace( salt )) { 
                    request.pin.salt = salt;
                    request.pin.alg = "sha256";
                    request.pin.iterations = 1;
                }
            }
            return request;
        }

    }
}
