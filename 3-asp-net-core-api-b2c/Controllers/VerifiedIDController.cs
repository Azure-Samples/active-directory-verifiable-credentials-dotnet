using System;
using System.Text;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Security.Cryptography;
using System.Security.Claims;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http.Extensions;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using Azure.Core;
using B2CVerifiedID.Helpers;

namespace B2CVerifiedID {
    [Route("api/[controller]/[action]")]
    [ApiController]
    public class VerifiedIDController : ControllerBase
    {
        protected IMemoryCache _cache;
        protected readonly ILogger<VerifiedIDController> _log;
        private IHttpClientFactory _httpClientFactory;
        private IConfiguration _configuration;
        private string _apiKey;
        public VerifiedIDController(IConfiguration configuration, IMemoryCache memoryCache, ILogger<VerifiedIDController> log, IHttpClientFactory httpClientFactory)
        {
            _cache = memoryCache;
            _log = log;
            _configuration = configuration;
            _httpClientFactory = httpClientFactory;
            _apiKey = System.Environment.GetEnvironmentVariable("API-KEY");
        }

        /////////////////////////////////////////////////////////////////////////////////////
        // Helpers
        /////////////////////////////////////////////////////////////////////////////////////
        protected string GetRequestHostName() {
            string scheme = "https";// : this.Request.Scheme;
            string originalHost = this.Request.Headers["x-original-host"];
            string hostname = "";
            if (!string.IsNullOrEmpty( originalHost ))
                hostname = string.Format( "{0}://{1}", scheme, originalHost );
            else hostname = string.Format( "{0}://{1}", scheme, this.Request.Host );
            return hostname;
        }

        protected bool IsMobile() {
            string userAgent = this.Request.Headers["User-Agent"];
            return (userAgent.Contains( "Android" ) || userAgent.Contains( "iPhone" ));
        }

        protected bool VerifyB2CApiKey() {
            bool rc = true;
            // if the appSettings has an API key for B2C, make sure the caller passes it
            string b2cApiKey = _configuration.GetValue( "AzureAdB2C:B2C1ARestApiKey", "" );
            if (!string.IsNullOrWhiteSpace( b2cApiKey )) {
                string xApiKey = this.Request.Headers["x-api-key"];
                if (string.IsNullOrWhiteSpace( xApiKey )) {
                    _log.LogError( "Missing header: x-api-key" );
                    rc = false;
                } else if (xApiKey != b2cApiKey) {
                    _log.LogError( "invalid x-api-key: {0}", xApiKey );
                    rc = false;
                }
            }
            return rc;
        }
        public JObject GetJsonFromJwtToken( string jwtToken ) {
            jwtToken = jwtToken.Replace( "_", "/" ).Replace( "-", "+" ).Split( "." )[1];
            jwtToken = jwtToken.PadRight( 4 * ((jwtToken.Length + 3) / 4), '=' );
            return JObject.Parse( System.Text.Encoding.UTF8.GetString( Convert.FromBase64String( jwtToken ) ) );
        }
        private string Base64UrlEncode( string base64 ) {
            return base64.Replace( '+', '-' ).Replace( "/", "_" ).Replace( "=", "" );
        }

        /////////////////////////////////////////////////////////////////////////////////////
        // Issuance
        /////////////////////////////////////////////////////////////////////////////////////

        protected Dictionary<string, string> GetSelfAssertedClaims( JObject manifest ) {
            Dictionary<string, string> claims = new Dictionary<string, string>();
            if (manifest["input"]["attestations"]["idTokens"][0]["id"].ToString() == "https://self-issued.me") {
                foreach (var claim in manifest["input"]["attestations"]["idTokens"][0]["claims"]) {
                    claims.Add( claim["claim"].ToString(), "" );
                }
            }
            return claims;
        }
        private IssuanceRequest SetClaimsFromB2C( IssuanceRequest request, JObject b2cClaims ) {
            GetCredentialManifest( out string manifest, out string error );
            JObject jsonManifest = JObject.Parse( manifest );
            Dictionary<string, string> manifestClaims = GetSelfAssertedClaims( jsonManifest );
            request.claims = new Dictionary<string, string>();
            // Regardless of which claims B2C gave us, we only use the claims that are defined in the manifest for the Verified ID credential
            // (as they would be ignored in issuance and not be part of the VC anyway)
            // Any surplus claims from B2C are ignored
            foreach (KeyValuePair<string, string> kvp in manifestClaims) {
                if (b2cClaims.ContainsKey( kvp.Key )) {
                    request.claims.Add( kvp.Key, b2cClaims[kvp.Key].ToString() );
                }
            }
            // if B2C gave us a pincode, then set it
            if (b2cClaims.ContainsKey( "pinCode" )) {
                string pinCode = b2cClaims["pinCode"].ToString();
                _log.LogTrace( "B2C pin={0}", pinCode );
                request.pin = new Pin() { length = pinCode.Length, value = pinCode };
            }
            return request;
        }
        private string? GetOid() {
            Claim? oidClaim = User?.Claims.Where( x => x.Type == "oid" ).FirstOrDefault();
            if (null == oidClaim) {
                oidClaim = User?.Claims.Where( x => x.Type == "uid" ).FirstOrDefault();
            }
            if (null == oidClaim) {
                oidClaim = User?.Claims.Where( x => x.Type == "http://schemas.microsoft.com/identity/claims/objectidentifier" ).FirstOrDefault();
            }
            if (null == oidClaim) {
                return null;
            } else {
                return oidClaim.Value;
            }
        }

        private Dictionary<string, string> GetClaimsFromInteractiveUser() {
            if (null == User) {
                throw new ArgumentNullException( "No user principal available" );
            }
            string? oid = GetOid();
            if (null == oid) {
                throw new ArgumentNullException( "Could not determind signed in user's oid claim" );
            }
            GetCredentialManifest( out string manifest, out string error );
            bool schemaContainsPhoto = manifest.Contains("image/jp");

            string photo = null; 
            if (schemaContainsPhoto ) {
                if (!_cache.TryGetValue( $"{oid}_photo", out photo ) ) {
                    // default photo is a generic faceless human
                    photo = "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";
                }
            }
            Dictionary<string, string> claims = new Dictionary<string, string>();
            claims.Add( "email", User.FindFirst( "email" )!.Value );
            claims.Add( "name", User.FindFirst( "name" )!.Value );
            claims.Add( "oid", oid );
            claims.Add( "tid", User.FindFirst( "tid" )!.Value );
            claims.Add( "given_name", User.FindFirst( "given_name" )!.Value );
            claims.Add( "family_name", User.FindFirst( "family_name" )!.Value );
            claims.Add( "memberStatus", "Diamond" );        // this illustrates that you can set claims that come from other sources
            claims.Add( "membershipNo", "123456789" );
            claims.Add( "legalAgeGroupClassification", "Adult" );
            claims.Add( "preferredLanguage", "en-us" );
            claims.Add( "country", "IE" );
            if (schemaContainsPhoto ) {
                if ( null == photo ) {
                    throw new ArgumentNullException("Schema has a photo but no photo provided");
                }
                claims.Add( _configuration["VerifiedID:PhotoClaimName"], Base64UrlEncode( photo! ) );
            }
            return claims;
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
                // Check if working together with B2CCustom Policy
                string correlationId = this.Request.Query["id"];
                JObject cachedB2CClaims = null;
                // if caller is passing and 'id' query string parameter, then we should have called issuance-claims-b2c and cached claims
                // if no 'id', then this is a standard issuance request
                if (!string.IsNullOrWhiteSpace( correlationId )) {
                    if (!_cache.TryGetValue<JObject>( correlationId, out cachedB2CClaims )) {
                        _log.LogError( "No cached claims for correlationId {0}", correlationId );
                        return BadRequest( new { error = "400", error_description = "Invalid correlationId" } );
                    }
                } else {
                    correlationId = Guid.NewGuid().ToString();
                }

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
                    IssuanceRequest request = CreateIssuanceRequest( correlationId );

                    // 1) If we have an interactive user, take the claims from the user session
                    // 2) If B2C has given us claims by calling issuance-claims-b2c, then use them (this happens for Issuance inside a B2C policy)
                    // 3) otherwise revert to the default Megan Bowen claims
                    if (User != null) {
                        request.claims = GetClaimsFromInteractiveUser();
                    } else {
                        if (cachedB2CClaims != null ) {
                            SetClaimsFromB2C( request, cachedB2CClaims );
                        } else {
                            SetClaims( request );
                        }
                    }
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

        /// <summary>
        /// Azure AD B2C REST API Endpoint for storing claims for future VC issuance request
        /// HTTP POST comes from Azure AD B2C 
        /// body : The InputClaims from the B2C policy. The 'id' is B2C's correlationId
        ///        Other claims are claims that may be used in the VC (see issue-request above)
        /// </summary>
        /// <returns>200 OK, 401 (api-key) or 404 (missing id/oid)</returns>
        [AllowAnonymous]
        [HttpPost( "/api/issuer/issuance-claims-b2c" )]
        public ActionResult IssuanceClaimsB2C() {
            _log.LogTrace( this.HttpContext.Request.GetDisplayUrl() );
            try {
                string body = new System.IO.StreamReader( this.Request.Body ).ReadToEndAsync().Result;
                _log.LogTrace( body );
                // if the appSettings has an API key for B2C, make sure B2C passes it
                if (!VerifyB2CApiKey()) {
                    return new ContentResult() { StatusCode = (int)HttpStatusCode.Unauthorized, Content = "invalid x-api-key" };
                }
                // make sure B2C passed the 'id' claim (correlationId) that we use for caching
                // (without it we will never be able to find these claims again)
                JObject b2cClaims = JObject.Parse( body );
                string correlationId = b2cClaims["id"].ToString();
                if (string.IsNullOrEmpty( correlationId )) {
                    return BadRequest( new { error = "400", error_description = "Missing claim 'id'" } );
                }
                // make sure B2C atleast passes the oid claim as that is the key for identifying a B2C user from a VC
                string oid = b2cClaims["oid"].ToString();
                if (string.IsNullOrEmpty( oid )) {
                    return BadRequest( new { error = "400", error_description = "Missing claim 'oid'" } );
                }
                _cache.Set( correlationId, b2cClaims, DateTimeOffset.Now.AddSeconds( _configuration.GetValue<int>( "AppSettings:CacheExpiresInSeconds", 300 ) ) );
                return new OkResult();
            } catch (Exception ex) {
                return BadRequest( new { error = "400", error_description = ex.Message } );
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
                string jwtToken = resp["token"].ToString();
                jwtToken = jwtToken.Replace( "_", "/" ).Replace( "-", "+" ).Split( "." )[1];
                jwtToken = jwtToken.PadRight( 4 * ((jwtToken.Length + 3) / 4), '=' );
                manifest = System.Text.Encoding.UTF8.GetString( Convert.FromBase64String( jwtToken ) );
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

        [Authorize]
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
                int cacheSeconds = _configuration.GetValue<int>( "AppSettings:CacheExpiresInSeconds", 300 );
                _cache.Set( $"{GetOid()}_photo", photo, DateTimeOffset.Now.AddSeconds( cacheSeconds ) );
                return new ContentResult { StatusCode = (int)HttpStatusCode.Created, ContentType = "application/json"
                                , Content = JsonConvert.SerializeObject( new { message = $"Photo will be cached for {cacheSeconds} seconds" } ) };
            } catch (Exception ex) {
                return BadRequest( new { error = "400", error_description = ex.Message } );
            }
        }

        private IssuanceRequest CreateIssuanceRequest( string stateId = null ) {
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
        private IssuanceRequest SetPinCode( IssuanceRequest request, string pinCode = null ) {
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

        /////////////////////////////////////////////////////////////////////////////////////
        // Verification
        /////////////////////////////////////////////////////////////////////////////////////
        /// <summary>
        /// This method is called from the UI to initiate the presentation of the verifiable credential
        /// </summary>
        /// <returns>JSON object with the address to the presentation request and optionally a QR code and a state value which can be used to check on the response status</returns>
        [AllowAnonymous]
        [HttpGet( "/api/verifier/presentation-request" )]
        public async Task<ActionResult> PresentationRequest() {
            _log.LogTrace( this.Request.GetDisplayUrl() );
            try {
                //The VC Request API is an authenticated API. We need to clientid and secret (or certificate) to create an access token which 
                //needs to be send as bearer to the VC Request API
                var accessToken = await MsalAccessTokenHandler.GetAccessToken( _configuration );
                if (accessToken.Item1 == String.Empty) {
                    _log.LogError( String.Format( "failed to acquire accesstoken: {0} : {1}", accessToken.error, accessToken.error_description ) );
                    return BadRequest( new { error = accessToken.error, error_description = accessToken.error_description } );
                }

                string correlationId = this.Request.Query["id"];
                string url = $"{_configuration["VerifiedID:ApiEndpoint"]}createPresentationRequest";
                PresentationRequest request = CreatePresentationRequest( correlationId );

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
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue( "Bearer", accessToken.token );
                HttpResponseMessage res = await client.PostAsync( url, new StringContent( jsonString, Encoding.UTF8, "application/json" ) );
                string response = await res.Content.ReadAsStringAsync();
                HttpStatusCode statusCode = res.StatusCode;

                if (statusCode == HttpStatusCode.Created) {
                    _log.LogTrace( "succesfully called Request Service API" );
                    JObject requestConfig = JObject.Parse( response );
                    requestConfig.Add( new JProperty( "id", request.callback.state ) );
                    jsonString = JsonConvert.SerializeObject( requestConfig );

                    //We use in memory cache to keep state about the request. The UI will check the state when calling the presentationResponse method
                    var cacheData = new {
                        status = "request_created",
                        message = "Waiting for QR code to be scanned",
                        expiry = requestConfig["expiry"].ToString()
                    };
                    _cache.Set( request.callback.state, JsonConvert.SerializeObject( cacheData )
                                    , DateTimeOffset.Now.AddSeconds( _configuration.GetValue<int>( "AppSettings:CacheExpiresInSeconds", 300 ) ) );
                    //the response from the VC Request API call is returned to the caller (the UI). It contains the URI to the request which Authenticator can download after
                    //it has scanned the QR code. If the payload requested the VC Request service to create the QR code that is returned as well
                    //the javascript in the UI will use that QR code to display it on the screen to the user.
                    return new ContentResult { ContentType = "application/json", Content = jsonString };
                } else {
                    _log.LogError( "Error calling Verified ID API: " + response );
                    return BadRequest( new { error = "400", error_description = "Verified ID API error response: " + response, request = jsonString } );
                }
            } catch (Exception ex) {
                _log.LogError( "Exception: " + ex.Message );
                return BadRequest( new { error = "400", error_description = $"Exception: {ex.Message}", error_stacktrace = ex.StackTrace } );
            }
        }

        //
        //this function is called from the UI to get some details to display in the UI about what
        //credential is being asked for
        //
        [AllowAnonymous]
        [HttpGet( "/api/verifier/get-presentation-details" )]
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
                return new ContentResult { ContentType = "application/json", Content = JsonConvert.SerializeObject( details ) };
            } catch (Exception ex) {
                return BadRequest( new { error = "400", error_description = ex.Message } );
            }
        }

        /// <summary>
        /// This method is called from the B2C HTML/javascript to get a QR code deeplink that 
        /// points back to this API instead of the VC Request Service API.
        /// You need to pass in QueryString parameters such as 'id' or 'StateProperties' which both
        /// are the B2C CorrelationId. StateProperties is a base64 encoded JSON structure.
        /// </summary>
        /// <returns>JSON deeplink to this API</returns>
        [AllowAnonymous]
        [HttpGet("/api/verifier/presentation-request-link")]
        public async Task<ActionResult> StaticPresentationReferenceGet() {
            _log.LogTrace(this.Request.GetDisplayUrl());
            try {
                string correlationId = this.Request.Query["id"];
                string stateProp = this.Request.Query["StateProperties"]; // may come from SETTINGS.transId
                if (string.IsNullOrWhiteSpace(correlationId) && !string.IsNullOrWhiteSpace(stateProp)) {
                    stateProp = stateProp.PadRight(stateProp.Length + (stateProp.Length % 4), '=');
                    JObject spJson = JObject.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(stateProp)));
                    correlationId = spJson["TID"].ToString();
                }
                if (string.IsNullOrWhiteSpace(correlationId)) {
                    correlationId = Guid.NewGuid().ToString();
                }
                _cache.Remove(correlationId);
                var resp = new
                {
                    requestId = correlationId,
                    url = $"openid-vc://?request_uri={GetRequestHostName()}/api/verifier/presentation-request-proxy/{correlationId}",
                    expiry = (int)(DateTime.UtcNow.AddDays(1) - new DateTime(1970, 1, 1)).TotalSeconds,
                    id = correlationId
                };
                string respJson = JsonConvert.SerializeObject(resp);
                _log.LogTrace("API static request Response\n{0}", respJson);
                return new ContentResult { ContentType = "application/json", Content = respJson };
            }
            catch (Exception ex) {
                return BadRequest(new { error = "400", error_description = ex.Message });
            }
        }
        /// <summary>
        /// This method get's called by the Microsoft Authenticator when it scans the QR code and 
        /// wants to retrieve the request. We call the VC Request Service API, get the request_uri 
        /// in the response, invoke that url and retrieve the response and pass it to the caller.
        /// This way this API acts as a proxy.
        /// </summary>
        /// <returns></returns>
        [AllowAnonymous]
        [HttpGet("/api/verifier/presentation-request-proxy/{id}")]
        public async Task<ActionResult> StaticPresentationReferenceProxy(string id) {
            _log.LogTrace(this.Request.GetDisplayUrl());
            try
            {
                var accessToken = await MsalAccessTokenHandler.GetAccessToken(_configuration);
                if (accessToken.Item1 == String.Empty) {
                    _log.LogError(String.Format("failed to acquire accesstoken: {0} : {1}", accessToken.error, accessToken.error_description));
                    return BadRequest(new { error = accessToken.error, error_description = accessToken.error_description });
                }

                // 1. Create a Presentation Request and call the Client API to get the 'real' request_uri
                //string correlationId = this.Request.Query["id"];
                string correlationId = id; // correlationId.Trim();
                PresentationRequest request = CreatePresentationRequest(correlationId);
                if (!hasFaceCheck(request) && _configuration.GetValue("VerifiedID:useFaceCheck", false)) {
                    AddFaceCheck(request, null, this.Request.Query["photoClaimName"]); // when qp is null, appsettings value is used
                }
                AddClaimsConstrains(request);

                string jsonString = JsonConvert.SerializeObject(request, Newtonsoft.Json.Formatting.None, new JsonSerializerSettings
                {
                    NullValueHandling = NullValueHandling.Ignore
                });
                _log.LogTrace("VC Client API Request\n{0}", jsonString);
                string url = $"{_configuration["VerifiedID:ApiEndpoint"]}createPresentationRequest";

                var client = _httpClientFactory.CreateClient();
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.token);
                HttpResponseMessage res = await client.PostAsync(url, new StringContent(jsonString, Encoding.UTF8, "application/json"));
                string contents = await res.Content.ReadAsStringAsync();
                HttpStatusCode statusCode = res.StatusCode;
                if (statusCode == HttpStatusCode.Created) {
                    _log.LogTrace("succesfully called Request Service API");
                    // 2. Get the 'real' request_uri from the response and make a HTTP GET to it to retrieve the JWT Token for the Authenticator
                    JObject apiResp = JObject.Parse(contents);
                    string request_uri = apiResp["url"].ToString().Split("=")[1]; // openid-vc://?request_uri=<...url to retrieve request...>
                    string response = null;
                    string contentType = null;

                    // simulate the wallet and retrieve the presentation request
                    var clientWalletSim = _httpClientFactory.CreateClient();
                    string preferHeader = this.Request.Headers["prefer"].ToString();
                    if (!string.IsNullOrWhiteSpace(preferHeader)) {
                        clientWalletSim.DefaultRequestHeaders.Add("prefer", preferHeader); // JWT-interop-profile-0.0.1
                    }
                    res = await clientWalletSim.GetAsync(request_uri);
                    response = await res.Content.ReadAsStringAsync();
                    statusCode = res.StatusCode;
                    contentType = res.Content.Headers.ContentType.ToString();

                    //We use in memory cache to keep state about the request. The UI will check the state when calling the presentationResponse method
                    var cacheData = new
                    {
                        status = "request_created",
                        message = "Waiting for QR code to be scanned",
                        expiry = apiResp["expiry"].ToString()
                    };
                    _cache.Set(request.callback.state, JsonConvert.SerializeObject(cacheData)
                                    , DateTimeOffset.Now.AddSeconds(_configuration.GetValue<int>("AppSettings:CacheExpiresInSeconds", 300)));

                    // 3. Return the response to the Authenticator
                    _log.LogTrace("VC Client API GET Response\nStatusCode={0}\nContent-Type={1}\n{2}", statusCode, contentType, response);
                    return new ContentResult { StatusCode = (int)statusCode, ContentType = contentType, Content = response };
                }
                else {
                    _log.LogError("Error calling Verified ID API: " + contents);
                    return BadRequest(new { error = "400", error_description = "Verified ID API error response: " + contents, request = jsonString });
                }
            }
            catch (Exception ex) {
                return BadRequest(new { error = "400", error_description = ex.Message });
            }
        }

        /// <summary>
        /// Azure AD B2C REST API Endpoint for retrieveing the VC presentation response
        /// HTTP POST comes from Azure AD B2C 
        /// body : The InputClaims from the B2C policy.It will only be one claim named 'id'
        /// </summary>
        /// <returns>returns a JSON structure with claims from the VC presented</returns>
        [AllowAnonymous]
        [HttpPost( "/api/verifier/presentation-response-b2c" )]
        public ActionResult PresentationResponseB2C() {
            _log.LogTrace( this.Request.GetDisplayUrl() );
            try {
                string body = new System.IO.StreamReader( this.Request.Body ).ReadToEndAsync().Result;
                _log.LogTrace( body );
                // if the appSettings has an API key for B2C, make sure the caller passes it
                if (!VerifyB2CApiKey()) {
                    return new ContentResult() { StatusCode = (int)HttpStatusCode.Unauthorized, Content = "invalid x-api-key" };
                }
                JObject b2cRequest = JObject.Parse( body );
                string correlationId = b2cRequest["id"].ToString();
                if (string.IsNullOrEmpty( correlationId )) {
                    return BadRequest( new { error = "400", error_description = "Missing claim 'id'" } );
                }
                if (!_cache.TryGetValue( correlationId, out string requestState )) {
                    var msg = new { version = "1.0.0", status = 400, userMessage = "Verifiable Credentials not presented" };
                    return new ContentResult { StatusCode = 409, ContentType = "application/json", Content = JsonConvert.SerializeObject( msg ) };
                }
                JObject reqState = JObject.Parse( requestState );
                CallbackEvent callback = JsonConvert.DeserializeObject<CallbackEvent>( reqState["callback"].ToString() );
                if (callback.requestStatus != "presentation_verified") {
                    var msg = new { version = "1.0.0", status = 400, userMessage = "Verifiable Credentials not successfully presented" };
                    return new ContentResult { StatusCode = 409, ContentType = "application/json", Content = JsonConvert.SerializeObject( msg ) };
                }
                // remove cache data now, because if we crash, we don't want to get into an infinite loop of crashing 
                _cache.Remove( correlationId );
                //
                string vcKey = null;
                if (null != callback.receipt && null != callback.receipt.vp_token) {
                    JObject vpToken = GetJsonFromJwtToken( callback.receipt.vp_token[0] );
                    JObject vc = GetJsonFromJwtToken( vpToken["vp"]["verifiableCredential"][0].ToString() );
                    vcKey = vc["jti"].ToString().Replace( ":", "." );
                }
                // if we didn't get a receipt or a jti, use the wallet's subject as key, but remove all colons
                if (vcKey == null) {
                    string walletSubject = callback.subject.Replace( "did:", "did." );
                    int idx = walletSubject.Substring(4).IndexOf( ":" );
                    if (idx != -1) {
                        walletSubject = walletSubject.Substring( 0, idx+4 ) + walletSubject.Substring( idx+4 ).Replace(":",".");
                    }
                    vcKey = walletSubject.Split( ":" )[0];
                }
                // setup the response that we are returning to B2C
                var obj = new {
                    vcType = callback.verifiedCredentialsData[0].type[callback.verifiedCredentialsData[0].type.Length - 1], // last
                    vcIss = callback.verifiedCredentialsData[0].issuer,
                    vcSub = callback.subject,
                    // key is intended to be user in user's profile 'identities' collection as a signInName,
                    // and it can't have colons, therefor we modify the value (and clip at following :)
                    vcKey = vcKey
                };
                JObject b2cResponse = JObject.Parse( JsonConvert.SerializeObject( obj ) );
                if (!string.IsNullOrWhiteSpace( callback.verifiedCredentialsData[0].expirationDate )) {
                    b2cResponse.Add( new JProperty( "expirationDate", callback.verifiedCredentialsData[0].expirationDate ) );
                }
                if (!string.IsNullOrWhiteSpace( callback.verifiedCredentialsData[0].issuanceDate )) {
                    b2cResponse.Add( new JProperty( "issuanceDate ", callback.verifiedCredentialsData[0].issuanceDate ) );
                }
                if (callback.verifiedCredentialsData[0].faceCheck != null) {
                    b2cResponse.Add( new JProperty( "matchConfidenceScore", callback.verifiedCredentialsData[0].faceCheck.matchConfidenceScore.ToString() ) );
                }
                // add all the additional claims in the VC as claims to B2C
                foreach (KeyValuePair<string, string> kvp in callback.verifiedCredentialsData[0].claims) {
                    b2cResponse.Add( new JProperty( kvp.Key, kvp.Value ) );
                }
                string resp = JsonConvert.SerializeObject( b2cResponse );
                _log.LogTrace( resp );
                return new ContentResult { ContentType = "application/json", Content = resp };
            } catch (Exception ex) {
                return BadRequest( new { error = "400", error_description = ex.Message } );
            }
        }
        /// This method creates a PresentationRequest object instance from configuration
        /// </summary>
        /// <param name="stateId"></param>
        /// <param name="credentialType"></param>
        /// <param name="acceptedIssuers"></param>
        /// <returns></returns>
        private PresentationRequest CreatePresentationRequest( string stateId = null, string credentialType = null, string[] acceptedIssuers = null ) {
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
        private PresentationRequest AddRequestedCredential( PresentationRequest request
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
            if (string.IsNullOrWhiteSpace( sourcePhotoClaimName )) {
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
                if (null != requestedCredential.configuration.validation.faceCheck) {
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
            if (constraintOp == "value") {
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
            if (null != constraint) {
                // if request was created from template, constraint may already exist - update it if so
                if (null != request.requestedCredentials[0].constraints) {
                    bool found = false;
                    for (int i = 0; i < request.requestedCredentials[0].constraints.Count; i++) {
                        if (request.requestedCredentials[0].constraints[i].claimName == constraintClaim) {
                            request.requestedCredentials[0].constraints[i] = constraint;
                            found = true;
                        }
                    }
                    if (!found) {
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
