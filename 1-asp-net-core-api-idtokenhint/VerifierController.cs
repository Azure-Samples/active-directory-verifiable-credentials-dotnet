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

namespace AspNetCoreVerifiableCredentials
{
    [Route("api/[controller]/[action]")]
    public class VerifierController : Controller
    {
        const string PRESENTATIONPAYLOAD = "presentation_request_config.json";
        //        const string PRESENTATIONPAYLOAD = "presentation_request_config - TrueIdentitySample.json";

        protected readonly AppSettingsModel AppSettings;
        protected IMemoryCache _cache;
        protected readonly ILogger<VerifierController> _log;
        private IHttpClientFactory _httpClientFactory;
        private string _apiKey;
        public VerifierController(IOptions<AppSettingsModel> appSettings, IMemoryCache memoryCache, ILogger<VerifierController> log, IHttpClientFactory httpClientFactory)
        {
            this.AppSettings = appSettings.Value;
            _cache = memoryCache;
            _log = log;
            _httpClientFactory = httpClientFactory;
            _apiKey = System.Environment.GetEnvironmentVariable("API-KEY");
        }

        /// <summary>
        /// This method is called from the UI to initiate the presentation of the verifiable credential
        /// </summary>
        /// <returns>JSON object with the address to the presentation request and optionally a QR code and a state value which can be used to check on the response status</returns>
        [HttpGet("/api/verifier/presentation-request")]
        public async Task<ActionResult> PresentationRequest()
        {
            try
            {
                if (!LoadPresentationRequestFile(out JObject payload, out String errorMessage))
                {
                    return BadRequest(new { error = "400", error_description = errorMessage });
                }

                string state = Guid.NewGuid().ToString();
                UpdatePresentationRequestPayload(payload, state);
                string jsonString = JsonConvert.SerializeObject(payload);

                 //CALL REST API WITH PAYLOAD
                HttpStatusCode statusCode = HttpStatusCode.OK;
                string response = null;
                try
                {
                    //The VC Request API is an authenticated API. We need to clientid and secret (or certificate) to create an access token which 
                    //needs to be send as bearer to the VC Request API
                    var accessToken = await GetAccessToken();
                    if (accessToken.Item1 == String.Empty)
                    {
                        _log.LogError(String.Format("failed to acquire accesstoken: {0} : {1}"), accessToken.error, accessToken.error_description);
                        return BadRequest(new { error = accessToken.error, error_description = accessToken.error_description });
                    }

                    _log.LogTrace( $"Request API payload: {jsonString}" );
                    var client = _httpClientFactory.CreateClient();
                    var defaultRequestHeaders = client.DefaultRequestHeaders;
                    defaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.token);

                    HttpResponseMessage res = await client.PostAsync(AppSettings.Endpoint + "verifiableCredentials/createPresentationRequest", new StringContent(jsonString, Encoding.UTF8, "application/json"));
                    response = await res.Content.ReadAsStringAsync();
                    statusCode = res.StatusCode;

                    if (statusCode == HttpStatusCode.Created)
                    {
                        _log.LogTrace("succesfully called Request API");
                        JObject requestConfig = JObject.Parse(response);
                        requestConfig.Add(new JProperty("id", state));
                        jsonString = JsonConvert.SerializeObject(requestConfig);

                        //We use in memory cache to keep state about the request. The UI will check the state when calling the presentationResponse method

                        var cacheData = new
                        {
                            status = "notscanned",
                            message = "Request ready, please scan with Authenticator",
                            expiry = requestConfig["expiry"].ToString()
                        };
                        _cache.Set(state, JsonConvert.SerializeObject(cacheData));

                        //the response from the VC Request API call is returned to the caller (the UI). It contains the URI to the request which Authenticator can download after
                        //it has scanned the QR code. If the payload requested the VC Request service to create the QR code that is returned as well
                        //the javascript in the UI will use that QR code to display it on the screen to the user.

                        return new ContentResult { ContentType = "application/json", Content = jsonString };
                    }
                    else
                    {
                        _log.LogError("Unsuccesfully called Request API");
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
        /// This method is called by the VC Request API when the user scans a QR code and presents a Verifiable Credential to the service
        /// </summary>
        /// <returns></returns>
        [HttpPost]
        public async Task<ActionResult> PresentationCallback()
        {
            try
            {
                string content = await new System.IO.StreamReader(this.Request.Body).ReadToEndAsync();
                _log.LogTrace("callback!: " + content);
                this.Request.Headers.TryGetValue("api-key", out var apiKey);
                if (this._apiKey != apiKey) 
                {
                    _log.LogTrace("api-key wrong or missing");
                    return new ContentResult() { StatusCode = (int)HttpStatusCode.Unauthorized, Content = "api-key wrong or missing" };
                }               
                JObject presentationResponse = JObject.Parse(content);
                var requestStatus = presentationResponse["requestStatus"].ToString();
                var state = presentationResponse["state"].ToString();
                Dictionary<string, object> cacheData = new Dictionary<string, object>{ { "status", requestStatus } };
                switch ( requestStatus ) 
                {
                    // Request is retrieved (QR code scanned)
                    case "request_retrieved":
                        cacheData.Add("message", "QR Code is scanned. Waiting for validation...");
                        break;
                    // VC is submitted to VerifiedID and verified
                    case "presentation_verified":
                        cacheData.Add("message", "Presentation verified");
                        cacheData.Add("subject", presentationResponse["subject"].ToString() );
                        cacheData.Add("payload", presentationResponse["verifiedCredentialsData"] );
                        //firstName = presentationResponse["verifiedCredentialsData"][0]["claims"]["firstName"].ToString(),
                        //lastName = presentationResponse["verifiedCredentialsData"][0]["claims"]["lastName"].ToString(),
                        cacheData.Add("presentationResponse", presentationResponse );
                        // get details on VC, when it was issued, when it expires, etc
                        if (presentationResponse.ContainsKey("receipt") )
                        {                                                        
                            JObject vpToken = GetJsonFromJwtToken(presentationResponse["receipt"]["vp_token"].ToString() );
                            JObject vc = GetJsonFromJwtToken(vpToken["vp"]["verifiableCredential"][0].ToString());
                            cacheData.Add("jti", vc["jti"].ToString() );
                            cacheData.Add("iat", vc["iat"].ToString());
                            cacheData.Add("exp", vc["exp"].ToString());
                        }
                        break;
                    // return error if unsupported request status
                    default:
                        _log.LogTrace($"Unsupported requestStatus {requestStatus}");
                        return new ContentResult() { StatusCode = (int)HttpStatusCode.BadRequest, Content = $"Unsupported requestStatus {requestStatus}" };
                }
                // return error if state is unknown
                if (!_cache.TryGetValue(state, out string buf))
                {
                    _log.LogTrace($"Unknown state {state}");
                    return new ContentResult() { StatusCode = (int)HttpStatusCode.BadRequest, Content = $"Unknown state {state}" };
                }
                _cache.Set(state, JsonConvert.SerializeObject(cacheData));
                return new OkResult();
            }
            catch (Exception ex)
            {
                return BadRequest(new { error = "400", error_description = ex.Message });
            }
        }
        //
        //this function is called from the UI polling for a response from the AAD VC Service.
        //when a callback is recieved at the presentationCallback service the session will be updated
        //this method will respond with the status so the UI can reflect if the QR code was scanned and with the result of the presentation
        //
        [HttpGet("/api/verifier/presentation-response")]
        public ActionResult PresentationResponse()
        {
            try
            {
                //the id is the state value initially created when the issuanc request was requested from the request API
                //the in-memory database uses this as key to get and store the state of the process so the UI can be updated
                string state = this.Request.Query["id"];
                if (string.IsNullOrEmpty(state))
                {
                    return BadRequest(new { error = "400", error_description = "Missing argument 'id'" });
                }
                JObject value = null;
                if (_cache.TryGetValue(state, out string buf))
                {
                    _log.LogTrace( $"id {state}, cache: {buf}");
                    value = JObject.Parse(buf);
                    // the browser doesn't need the full presentationResponse
                    if ( value.ContainsKey("presentationResponse") )
                    {
                        value.Remove("presentationResponse");
                    }
                    return new ContentResult { ContentType = "application/json", Content = JsonConvert.SerializeObject(value) };
                }
                return new OkResult();
            }
            catch (Exception ex)
            {
                return BadRequest(new { error = "400", error_description = ex.Message });
            }
        }
        //
        //this function is called from the UI to get some details to display in the UI about what
        //credential is being asked for
        //
        [HttpGet("/api/verifier/get-presentation-details")]
        public ActionResult getPresentationDetails() {
            try {
                if ( !LoadPresentationRequestFile( out JObject presentationRequest, out String errorMessage ))
                {
                    return BadRequest(new { error = "400", error_description = errorMessage });
                }
                UpdatePresentationRequestPayload( presentationRequest, "" );
                var details = new {
                    clientName = presentationRequest["registration"]["clientName"].ToString(),
                    purpose = presentationRequest["registration"]["purpose"].ToString(),
                    VerifierAuthority = AppSettings.VerifierAuthority,
                    type = presentationRequest["requestedCredentials"][0]["type"].ToString(),
                    acceptedIssuers = presentationRequest["requestedCredentials"][0]["acceptedIssuers"]
                };
                return new ContentResult { ContentType = "application/json", Content = JsonConvert.SerializeObject(details) };
            } catch (Exception ex) {
                return BadRequest(new { error = "400", error_description = ex.Message });
            }
        }

        //some helper functions
        protected async Task<(string token, string error, string error_description)> GetAccessToken()
        {
            // You can run this sample using ClientSecret or Certificate. The code will differ only when instantiating the IConfidentialClientApplication
            bool isUsingClientSecret = AppSettings.AppUsesClientSecret(AppSettings);

            // Since we are using application permissions this will be a confidential client application
            IConfidentialClientApplication app;
            if (isUsingClientSecret)
            {
                app = ConfidentialClientApplicationBuilder.Create(AppSettings.ClientId)
                    .WithClientSecret(AppSettings.ClientSecret)
                    .WithAuthority(new Uri(AppSettings.Authority))
                    .Build();
            }
            else
            {
                X509Certificate2 certificate = AppSettings.ReadCertificate(AppSettings.CertificateName);
                app = ConfidentialClientApplicationBuilder.Create(AppSettings.ClientId)
                    .WithCertificate(certificate)
                    .WithAuthority(new Uri(AppSettings.Authority))
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
            string[] scopes = new string[] { AppSettings.VCServiceScope };

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
        // load the presentation_request_config.json file
        public bool LoadPresentationRequestFile( out JObject payload, out string errorMessage ) 
        {
            payload = null;
            errorMessage = null;
            string payloadpath = Path.Combine(Path.GetDirectoryName(System.Reflection.Assembly.GetEntryAssembly().Location), PRESENTATIONPAYLOAD);
            _log.LogTrace("IssuanceRequest file: {0}", payloadpath);
            if (!System.IO.File.Exists(payloadpath)) {
                _log.LogError("File not found: {0}", payloadpath);
                errorMessage = PRESENTATIONPAYLOAD + " not found";
                return false;
            }
            String jsonString = System.IO.File.ReadAllText(payloadpath);
            if (string.IsNullOrEmpty(jsonString)) {
                _log.LogError("Error reading file: {0}", payloadpath);
                errorMessage = PRESENTATIONPAYLOAD + " error reading file";
                return false;
            }
            payload = JObject.Parse(jsonString);
            return true;
        }

        // update the loaded presentation_request_config.json file with config value
        private void UpdatePresentationRequestPayload(JObject presentationRequest, string state) 
        {
            //modify payload with new state, the state is used to be able to update the UI when callbacks are received from the VC Service
            if (presentationRequest["callback"]["state"] != null) {
                presentationRequest["callback"]["state"] = state;
            }

            //get the VerifierDID from the appsettings
            if (presentationRequest["authority"] != null) {
                presentationRequest["authority"] = AppSettings.VerifierAuthority;
            }

            //copy the issuerDID from the settings and fill in the trustedIssuer part of the payload
            //this means only that issuer should be trusted for the requested credentialtype
            //this value is an array in the payload, you can trust multiple issuers for the same credentialtype
            //very common to accept the test VCs and the Production VCs coming from different verifiable credential services
            if (presentationRequest["requestedCredentials"][0]["acceptedIssuers"][0] != null) {
                presentationRequest["requestedCredentials"][0]["acceptedIssuers"][0] = AppSettings.IssuerAuthority;
            }

            //modify the callback method to make it easier to debug with tools like ngrok since the URI changes all the time
            //this way you don't need to modify the callback URL in the payload every time ngrok changes the URI
            if (presentationRequest["callback"]["url"] != null) {
                //localhost hostname can't work for callbacks so we won't overwrite it.
                //this happens for example when testing with sign-in to an IDP and https://localhost is used as redirect URI
                //in that case the callback should be configured in the payload directly instead of being modified in the code here
                string host = GetRequestHostName();
                if (!host.Contains("//localhost")) {
                    presentationRequest["callback"]["url"] = String.Format("{0}:/api/verifier/presentationCallback", host);
                }
            }

            // set our api-key in the request so we can check it in the callbacks we receive
            if (presentationRequest["callback"]["headers"]["api-key"] != null) {
                presentationRequest["callback"]["headers"]["api-key"] = this._apiKey;
            }
        }

        public JObject GetJsonFromJwtToken(string jwtToken)
        {            
            jwtToken = jwtToken.Replace("_", "/").Replace("-", "+").Split(".")[1];
            jwtToken = jwtToken.PadRight(4 * ((jwtToken.Length + 3) / 4), '=');
            return JObject.Parse(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(jwtToken)) );
        }

    }
}
