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

            // If apsettings.json say we should have a photo claim
            string photoClaimName = _configuration.GetValue( "VerifiedID:PhotoClaimName", "");
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
                    var accessToken = await GetAccessToken();
                    if (accessToken.Item1 == String.Empty)
                    {
                        _log.LogError(String.Format("failed to acquire accesstoken: {0} : {1}", accessToken.error, accessToken.error_description));
                        return BadRequest(new { error = accessToken.error, error_description = accessToken.error_description });
                    }

                    IssuanceRequest request = CreateIssuanceRequest();

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
                        if (request.pin != null) { requestConfig["pin"] = request.pin.value; }
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

        [HttpGet("/api/issuer/get-manifest")]
        public ActionResult getManifest() {
            _log.LogTrace( this.HttpContext.Request.GetDisplayUrl() );
            try {
                if (!_cache.TryGetValue("manifest", out string manifest))
                {
                    string manifestUrl = _configuration["VerifiedID:CredentialManifest"];
                    if (string.IsNullOrWhiteSpace( manifestUrl )) {
                        string errmsg = $"Manifest missing in config file";
                        _log.LogError( errmsg );
                        return BadRequest( new { error = "400", error_description = errmsg } );
                    }
                    var client = _httpClientFactory.CreateClient();
                    HttpResponseMessage res = client.GetAsync(manifestUrl).Result;
                    string response = res.Content.ReadAsStringAsync().Result;
                    if (res.StatusCode != HttpStatusCode.OK)
                    {
                        return BadRequest(new { error = "400", 
                            error_description = $"HTTP status {(int)res.StatusCode} retrieving manifest from URL {manifestUrl}" });
                    }
                    JObject resp = JObject.Parse(response);
                    string jwtToken = resp["token"].ToString();
                    jwtToken = jwtToken.Replace("_", "/").Replace("-", "+").Split(".")[1];
                    jwtToken = jwtToken.PadRight(4 * ((jwtToken.Length + 3) / 4), '=');
                    manifest = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(jwtToken));
                    _cache.Set( "manifest", manifest );
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
        private X509Certificate2 ReadCertificate( string certificateName ) {
            if (string.IsNullOrWhiteSpace( certificateName )) {
                throw new ArgumentException( "certificateName should not be empty. Please set the CertificateName setting in the appsettings.json", "certificateName" );
            }
            CertificateDescription certificateDescription = CertificateDescription.FromStoreWithDistinguishedName( certificateName );
            DefaultCertificateLoader defaultCertificateLoader = new DefaultCertificateLoader();
            defaultCertificateLoader.LoadIfNeeded( certificateDescription );
            return certificateDescription.Certificate;
        }

        protected async Task<(string token, string error, string error_description)> GetAccessToken()
        {
            // You can run this sample using ClientSecret or Certificate. The code will differ only when instantiating the IConfidentialClientApplication
            string authority = $"{_configuration["VerifiedID:Authority"]}{_configuration["VerifiedID:TenantId"]}";
            string clientSecret = _configuration.GetValue("VerifiedID:ClientSecret", "");
            // Since we are using application permissions this will be a confidential client application
            IConfidentialClientApplication app;
            if (!string.IsNullOrWhiteSpace( clientSecret ))
            {
                app = ConfidentialClientApplicationBuilder.Create(_configuration["VerifiedID:ClientId"])
                    .WithClientSecret(clientSecret)
                    .WithAuthority(new Uri(authority))
                    .Build();
            }
            else
            {
                X509Certificate2 certificate = ReadCertificate( _configuration["VerifiedID:CertificateName"] );
                app = ConfidentialClientApplicationBuilder.Create( _configuration["VerifiedID:ClientId"] )
                    .WithCertificate(certificate)
                    .WithAuthority( new Uri( authority ) )
                    .Build();
            }

            //configure in memory cache for the access tokens. The tokens are typically valid for 60 seconds,
            //so no need to create new ones for every web request
            app.AddDistributedTokenCache(services =>
            {
                services.AddDistributedMemoryCache();
                services.AddLogging(configure => configure.AddConsole())
                .Configure<LoggerFilterOptions>(options => options.MinLevel = Microsoft.Extensions.Logging.LogLevel.Debug);
            });

            // With client credentials flows the scopes is ALWAYS of the shape "resource/.default", as the 
            // application permissions need to be set statically (in the portal or by PowerShell), and then granted by
            // a tenant administrator. 
            string[] scopes = new string[] { _configuration["VerifiedID:scope"] };

            AuthenticationResult result = null;
            try
            {
                result = await app.AcquireTokenForClient(scopes)
                    .ExecuteAsync();
            }
            catch (MsalServiceException ex) when (ex.Message.Contains("AADSTS70011"))
            {
                // Invalid scope. The scope has to be of the form "https://resourceurl/.default"
                // Mitigation: change the scope to be as expected
                return (string.Empty, "500", "Scope provided is not supported");
                //return BadRequest(new { error = "500", error_description = "Scope provided is not supported" });
            }
            catch (MsalServiceException ex)
            {
                // general error getting an access token
                return (String.Empty, "500", "Something went wrong getting an access token for the client API:" + ex.Message);
                //return BadRequest(new { error = "500", error_description = "Something went wrong getting an access token for the client API:" + ex.Message });
            }

            _log.LogTrace(result.AccessToken);
            return (result.AccessToken, String.Empty, String.Empty);
        }
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

        public IssuanceRequest CreateIssuanceRequest( string? stateId = null ) {
            IssuanceRequest request = new IssuanceRequest() {
                includeQRCode = _configuration.GetValue( "VerifiedID:includeQRCode", false ),
                authority = _configuration["VerifiedID:DidAuthority"],
                registration = new Registration() {
                    clientName = _configuration["VerifiedID:client_name"],
                    purpose = _configuration.GetValue( "VerifiedID:purpose", "" )
                },
                callback = new Callback() {
                    url = $"{GetRequestHostName()}/api/issuer/issuecallback",
                    state = string.IsNullOrEmpty( stateId ) ? Guid.NewGuid().ToString() : stateId,
                    headers = new Dictionary<string, string>() { { "api-key", this._apiKey } }
                },
                type = "ignore-this",
                manifest = _configuration["VerifiedID:CredentialManifest"],
                pin = null
            };
            if ("" == request.registration.purpose) {
                request.registration.purpose = null;
            }
            int issuancePinCodeLength = _configuration.GetValue( "VerifiedID:IssuancePinCodeLength", 0 );
            // if pincode is required, set it up in the request
            if (issuancePinCodeLength > 0 && !IsMobile() ) {
                int pinCode = RandomNumberGenerator.GetInt32( 1, int.Parse( "".PadRight( issuancePinCodeLength, '9' ) ) );
                SetPinCode( request, string.Format( "{0:D" + issuancePinCodeLength.ToString() + "}", pinCode ) );
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
        public IssuanceRequest SetPinCode( IssuanceRequest request, string pinCode = null ) {
            _log.LogTrace( "pin={0}", pinCode );
            if (string.IsNullOrWhiteSpace( pinCode )) {
                request.pin = null;
            } else {
                request.pin = new Pin() {
                    length = pinCode.Length,
                    value = pinCode
                };
            }
            return request;
        }

    }
}
