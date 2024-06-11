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

namespace ExternalIDVerifiedID {
    [Route("api/[controller]/[action]")]
    [ApiController]
    public class VerifiedIDController : ControllerBase
    {
        protected IMemoryCache _cache;
        protected readonly ILogger<VerifiedIDController> _log;
        private IHttpClientFactory _httpClientFactory;
        private IConfiguration _configuration;
        private string _apiKey;
        private string _apiEndpoint;
        public VerifiedIDController(IConfiguration configuration, IMemoryCache memoryCache, ILogger<VerifiedIDController> log, IHttpClientFactory httpClientFactory)
        {
            _cache = memoryCache;
            _log = log;
            _configuration = configuration;
            _httpClientFactory = httpClientFactory;
            _apiKey = System.Environment.GetEnvironmentVariable("API-KEY");
            _apiEndpoint = _configuration.GetValue( "VerifiedID:ApiEndpoint", "https://verifiedid.did.msidentity.com/v1.0/verifiableCredentials/" );
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

        private JObject GetJsonFromJwtToken( string jwtToken, int part = 1 ) {
            return JObject.Parse( GetJwtTokenFromBase64( jwtToken, part ) );
        }
        private string GetJwtTokenFromBase64( string jwtToken, int part = 1 ) {
            jwtToken = Base64UrlDecode( jwtToken.Split(".")[part] );
            return System.Text.Encoding.UTF8.GetString( Convert.FromBase64String( jwtToken ) );
        }
        private string Base64UrlEncode( string base64 ) {
            return base64.Replace( '+', '-' ).Replace( "/", "_" ).Replace( "=", "" );
        }
        private string Base64UrlDecode( string base64 ) {
            base64 = base64.Replace( "_", "/" ).Replace( "-", "+" );
            return base64.PadRight( 4 * ((base64.Length + 3) / 4), '=' );
        }

        private string SerializeToJsonNoNull( object? value ) {
            return JsonConvert.SerializeObject( value, Newtonsoft.Json.Formatting.None, new JsonSerializerSettings {
                NullValueHandling = NullValueHandling.Ignore
            } );
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

        private string HandleCreatedRequest( string state, string response, string pinCode = null ) {
            _log.LogTrace( "succesfully called Request Service API" );
            JObject requestConfig = JObject.Parse( response );
            if ( !string.IsNullOrWhiteSpace(pinCode)) {
                requestConfig["pin"] = pinCode;
            }
            requestConfig.Add( new JProperty( "id", state ) );
            var cacheData = new {
                status = "request_created",
                expiry = requestConfig["expiry"].ToString()
            };
            _cache.Set( state, JsonConvert.SerializeObject( cacheData )
                            , DateTimeOffset.Now.AddSeconds( _configuration.GetValue<int>( "AppSettings:CacheExpiresInSeconds", 300 ) ) );
            return JsonConvert.SerializeObject( requestConfig );
        }
        /////////////////////////////////////////////////////////////////////////////////////
        // Issuance Helpers
        /////////////////////////////////////////////////////////////////////////////////////
        private Dictionary<string, string> GetClaimsFromInteractiveUser() {
            if (null == User) {
                throw new ArgumentNullException( "No user principal available" );
            }
            string? oid = GetOid();
            if (null == oid) {
                throw new ArgumentNullException( "Could not determind signed in user's oid claim" );
            }
            // just make sure it's loaded - should be in cache
            GetCredentialManifest( out string manifest, out string error );
            string photoClaimName = GetPhotoClaimName();
            // default photo is a generic faceless human
            string photo = "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";
            if (!string.IsNullOrWhiteSpace( photoClaimName )) {
                _cache.TryGetValue( $"{oid}_photo", out photo );
                if ( string.IsNullOrEmpty(photo)) {
                    _log.LogTrace( $"Photo is null" );
                }
            }
            string upn = "";
            if ( null != User.FindFirst( "upn" ) ) {
                upn = User.FindFirst( "upn" )!.Value;
            } else {
                _log.LogTrace($"upn claim not found in id_token - please update 'Token configuration' for your app");
                upn = $"{oid}@{_configuration["AzureAd:TenantName"]}";
            }
            Dictionary<string, string> claims = new Dictionary<string, string>();
            claims.Add( "email", User.FindFirst( "email" )!.Value );
            claims.Add( "name", User.FindFirst( "name" )!.Value );
            claims.Add( "upn", upn );
            claims.Add( "memberStatus", "Diamond" );        // this illustrates that you can set claims that come from other sources
            claims.Add( "membershipNo", "123456789" );
            if (!string.IsNullOrWhiteSpace( photoClaimName )) {
                claims.Add( photoClaimName, Base64UrlEncode( photo! ) );
            }
            return claims;
        }

        private string GetPhotoClaimName() {
            string photoClaimName = null;
            Dictionary<string, ManifestClaim> manifestClaims = (Dictionary<string, ManifestClaim>)_cache.Get( "ManifestClaims" );
            if (manifestClaims != null) {
                foreach (KeyValuePair<string, ManifestClaim> mc in manifestClaims) {
                    if (mc.Value.type.StartsWith( "image/jp" )) {
                        photoClaimName = mc.Key;
                        break;
                    }
                }
            }
            return photoClaimName;
        }
        private void CacheManifestClaims( JObject manifest ) {
            Dictionary<string, ManifestClaim> manifestClaims = new Dictionary<string, ManifestClaim>();
            foreach (var claim in manifest["display"]["claims"]) {
                string claimName = ((JProperty)claim).Name;
                ManifestClaim mc = new ManifestClaim() {
                    name = claimName.Replace( "vc.credentialSubject.", "" ),
                    type = manifest.SelectToken( $"display.claims['{claimName}'].type" ).ToString(),
                    label = manifest.SelectToken( $"display.claims['{claimName}'].label" ).ToString()
                };
                manifestClaims.Add( mc.name, mc );
            }
            _cache.Set( "ManifestClaims", manifestClaims );
        }
        private bool GetCredentialManifest( out string manifest, out string error ) {
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
                manifest = GetJwtTokenFromBase64( resp["token"].ToString() );
                _cache.Set( "manifest", manifest );

                CacheManifestClaims( JObject.Parse( manifest ) );
            }
            return true;
        }

        private bool ValidateCredentialManifest( out string error ) {
            if (!GetCredentialManifest( out string manifest, out error )) {
                return false;
            }
            string manifestUrl = _configuration["VerifiedID:CredentialManifest"];
            string tenantId = _configuration.GetValue( "VerifiedID:tenantId", _configuration["AzureAd:TenantId"] );
            string manifestTenantId = manifestUrl.Split( "/" )[5];
            if (manifestTenantId != tenantId) {
                error = $"TenantId in ManifestURL {manifestTenantId} does not match tenantId in config file {tenantId}";
                return false;
            }
            JObject jsonManifest = JObject.Parse( manifest );
            string didIssuer = jsonManifest["input"]["issuer"].ToString();
            string didAuthority = _configuration["VerifiedID:DidAuthority"];
            if (didIssuer != didAuthority) {
                error = $"Issuing DID in Manifest {didIssuer} does not match DID authority in config file {didAuthority}";
                return false;
            }
            return true;
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
            if (issuancePinCodeLength > 0 && !IsMobile()) {
                int pinCode = RandomNumberGenerator.GetInt32( 1, int.Parse( "".PadRight( issuancePinCodeLength, '9' ) ) );
                SetPinCode( request, string.Format( "{0:D" + issuancePinCodeLength.ToString() + "}", pinCode ) );
            }
            SetExpirationDate( request );
            return request;
        }
        private IssuanceRequest SetExpirationDate( IssuanceRequest request ) {
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
        // Presentation Helpers
        /////////////////////////////////////////////////////////////////////////////////////
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

        /////////////////////////////////////////////////////////////////////////////////////
        // Issuance Endpoints
        /////////////////////////////////////////////////////////////////////////////////////

        [AllowAnonymous]
        [HttpGet("/api/issuer/issuance-request")]
        public async Task<ActionResult> IssuanceRequest()
        {
            _log.LogTrace( this.HttpContext.Request.GetDisplayUrl() );
            try
            {
                // Before starting issuance, check that we can access the manifest and that it is for this tenant/authority
                // This is just so we fail fast when config is wrong
                if ( !ValidateCredentialManifest( out string errmsg ) ) {
                    _log.LogError( errmsg );
                    return BadRequest( new { error = "400", error_description = errmsg } );
                }

                try {
                    var accessToken = await MsalAccessTokenHandler.GetAccessToken( _configuration );
                    if (accessToken.Item1 == String.Empty) {
                        _log.LogError(String.Format("failed to acquire accesstoken: {0} : {1}", accessToken.error, accessToken.error_description));
                        return BadRequest(new { error = accessToken.error, error_description = accessToken.error_description });
                    }
                    IssuanceRequest request = CreateIssuanceRequest();
                    request.claims = GetClaimsFromInteractiveUser();
                    string jsonString = SerializeToJsonNoNull( request );

                    string url = $"{_apiEndpoint}createIssuanceRequest";
                    _log.LogTrace( $"Request API {url}\n{jsonString}" );
                    var client = _httpClientFactory.CreateClient();
                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.token);
                    HttpResponseMessage res = await client.PostAsync(url, new StringContent(jsonString, Encoding.UTF8, "application/json"));
                    string response = await res.Content.ReadAsStringAsync();

                    if (res.StatusCode == HttpStatusCode.Created) {
                        string pinCode = null;
                        if (request.pin != null) {
                            pinCode = request.pin.value;                            
                        }
                        jsonString = HandleCreatedRequest( request.callback.state, response, pinCode );
                        return new ContentResult { ContentType = "application/json", Content = jsonString };
                    } else {
                        _log.LogError("Unsuccesfully called Request API" + response);
                        return BadRequest(new { error = "400", error_description = "Something went wrong calling the API: " + response });
                    }
                } catch (Exception ex) {
                    return BadRequest(new { error = "400", error_description = "Something went wrong calling the API: " + ex.Message });
                }
            } catch (Exception ex) {
                return BadRequest(new { error = "400", error_description = ex.Message });
            }
        }

        [AllowAnonymous]
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

        // This API is called from the UI to render a QR code the user can scan with the QR Code Reader app
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

        // This API called by the from the UI before issuance process is started. It is the authenticated user that 
        // calls the API to set the selfie photo to be used for the VC. The selfie posted from the mobile is
        // handled in CallbackController.cs in the  API /api/issuer/selfie/{id}
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

        /////////////////////////////////////////////////////////////////////////////////////
        // Presentation Endpoints
        /////////////////////////////////////////////////////////////////////////////////////

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

                PresentationRequest request = CreatePresentationRequest();

                string faceCheck = this.Request.Query["faceCheck"];
                bool useFaceCheck = (!string.IsNullOrWhiteSpace( faceCheck ) && (faceCheck == "1" || faceCheck == "true"));
                if (!hasFaceCheck( request ) && (useFaceCheck || _configuration.GetValue( "VerifiedID:useFaceCheck", false ))) {
                    AddFaceCheck( request, null, this.Request.Query["photoClaimName"] ); // when qp is null, appsettings value is used
                }
                AddClaimsConstrains( request );

                string jsonString = SerializeToJsonNoNull( request );

                string url = $"{_apiEndpoint}createPresentationRequest";
                _log.LogTrace( $"Request API {url}\n{jsonString}" );
                var client = _httpClientFactory.CreateClient();
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue( "Bearer", accessToken.token );
                HttpResponseMessage res = await client.PostAsync( url, new StringContent( jsonString, Encoding.UTF8, "application/json" ) );
                string response = await res.Content.ReadAsStringAsync();
                HttpStatusCode statusCode = res.StatusCode;

                if (statusCode == HttpStatusCode.Created) {
                    jsonString = HandleCreatedRequest( request.callback.state, response );
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

    } // cls
} // ns
