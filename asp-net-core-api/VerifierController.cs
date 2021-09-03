using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Identity.Client;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Security.Cryptography.X509Certificates;
using System.Net.Http.Headers;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;

namespace Verifiable_credentials_DotNet
{
    [Route("api/[controller]/[action]")]
    public class VerifierController : Controller
    {
        const string PRESENTATIONPAYLOAD = "presentation_request_config.json";
//        const string PRESENTATIONPAYLOAD = "presentation_request_config - TrueIdentitySample.json";

        protected readonly AppSettingsModel AppSettings;
        protected IMemoryCache _cache;
        protected readonly ILogger<VerifierController> _log;

        public VerifierController(IOptions<AppSettingsModel> appSettings,IMemoryCache memoryCache, ILogger<VerifierController> log)
        {
            this.AppSettings = appSettings.Value;
            _cache = memoryCache;
            _log = log;
        }


        [HttpGet("/api/verifier/presentation-request")]
        public async Task<ActionResult> presentationRequest()
        {
            try
            {

                string jsonString = null;

                string payloadpath = Path.Combine(Path.GetDirectoryName(System.Reflection.Assembly.GetEntryAssembly().Location), PRESENTATIONPAYLOAD);
                _log.LogTrace("IssuanceRequest file: {0}", payloadpath);
                if (!System.IO.File.Exists(payloadpath))
                {
                    _log.LogError("File not found: {0}", payloadpath);
                    return BadRequest(new { error = "400", error_description = PRESENTATIONPAYLOAD + " not found" }); 
                }
                jsonString = System.IO.File.ReadAllText(payloadpath);
                if (string.IsNullOrEmpty(jsonString)) 
                {
                    _log.LogError("Error reading file: {0}", payloadpath);
                    return BadRequest(new { error = "400", error_description = PRESENTATIONPAYLOAD + " error reading file" }); 
                }

                string state = Guid.NewGuid().ToString();

                //modify payload with new state
                JObject payload = JObject.Parse(jsonString);
                if (payload["callback"]["state"] != null)
                {
                    payload["callback"]["state"] = state;
                }

                //get the VerifierDID from the appsettings
                if (payload["authority"] != null)
                {
                    payload["authority"] = AppSettings.VerifierAuthority;
                }

                //copy the issuerDID from the settings and fill in the trustedIssuer part of the payload
                //this means only that issuer should be trusted for the request credentialtype
                //this value is an array in the payload, you can trust multiple issuers for the same credentialtype
                //very common to accept the test VCs and the Production VCs coming from different verifiable credential services
                if (payload["presentation"]["requestedCredentials"][0]["trustedIssuers"][0] != null)
                {
                    payload["presentation"]["requestedCredentials"][0]["trustedIssuers"][0] = AppSettings.IssuerAuthority;
                }

                //modify the callback method to make it easier to debug 
                //with tools like ngrok since the URI changes all the time
                //this way you don't need to modify the callback URL in the payload every time
                //ngrok changes the URI
                if (payload["callback"]["url"] != null)
                {
                    string host = GetRequestHostName();
                    payload["callback"]["url"] = String.Format("{0}/api/verifier/presentationCallback", host);
                }

                jsonString = JsonConvert.SerializeObject(payload);


                //CALL REST API WITH PAYLOAD
                HttpStatusCode statusCode = HttpStatusCode.OK;
                string response = null;
                try
                {
                    var accessToken = GetAccessToken().Result;
                    if (accessToken.Item1 == String.Empty)
                    {
                        _log.LogError(String.Format("failed to acquire accesstoken: {0} : {1}"), accessToken.error, accessToken.error_description);
                        return BadRequest(new { error = accessToken.error, error_description = accessToken.error_description });
                    }

                    HttpClient client = new HttpClient();
                    var defaultRequestHeaders = client.DefaultRequestHeaders;
                    defaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.token);

                    HttpResponseMessage res = client.PostAsync(AppSettings.ApiEndpoint, new StringContent(jsonString, Encoding.UTF8, "application/json")).Result;
                    response = res.Content.ReadAsStringAsync().Result;
                    _log.LogTrace("succesfully called Request API");
                    client.Dispose();
                    statusCode = res.StatusCode;

                    JObject requestConfig = JObject.Parse(response);
                    requestConfig.Add(new JProperty("id", state));
                    jsonString = JsonConvert.SerializeObject(requestConfig);

                    var cacheData = new
                    {
                        status = "notscanned",
                        message = "Request ready, please scan with Authenticator",
                        expiry = requestConfig["expiry"].ToString()
                    };
                    _cache.Set(state, JsonConvert.SerializeObject(cacheData));

                    return new ContentResult { ContentType = "application/json", Content = jsonString };
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


        [HttpPost]
        public async Task<ActionResult> presentationCallback()
        {
            try
            {
                string content = new System.IO.StreamReader(this.Request.Body).ReadToEndAsync().Result;
                Debug.WriteLine("callback!: " + content);
                JObject presentationResponse = JObject.Parse(content);
                var state = presentationResponse["state"].ToString();

                if (presentationResponse["code"].ToString() == "request_retrieved")
                {
                    var cacheData = new
                    {
                        status = "request_retrieved",
                        message = "QR Code is scanned. Waiting for validation...",
                    };
                    _cache.Set(state, JsonConvert.SerializeObject(cacheData));
                }

                if (presentationResponse["code"].ToString() == "presentation_verified")
                {
                    var cacheData = new
                    {
                        status = "presentation_verified",
                        message = "Presentation received",
                        payload = presentationResponse["issuers"].ToString(),
                        subject = presentationResponse["subject"].ToString()
                    };
                    _cache.Set(state, JsonConvert.SerializeObject(cacheData));

                }

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
        public async Task<ActionResult> presentationResponse()
        {
            
            try
            {
                string state = this.Request.Query["id"];
                if (string.IsNullOrEmpty(state))
                {
                    return BadRequest(new { error = "400", error_description = "Missing argument 'id'" });
                }
                JObject value = null;
                if (_cache.TryGetValue(state, out string buf))
                {
                    value = JObject.Parse(buf);

                    Debug.WriteLine("check if there was a response yet: " + value);
                    return new ContentResult { ContentType = "application/json", Content = JsonConvert.SerializeObject(value) }; 
                }

                //JObject cacheData = null;
                //if (GetCachedJsonObject(state, out cacheData))
                //{
                //    _log.LogTrace("Have VC validation result");
                //    //RemoveCacheValue( state ); // if you're not using B2C integration, uncomment this line
                //    return ReturnJson(TransformCacheDataToBrowserResponse(cacheData));
                //}
                //else
                //{
                //    string requestId = this.Request.Query["requestId"];
                //    if (!string.IsNullOrEmpty(requestId) && GetCachedJsonObject(requestId, out cacheData))
                //    {
                //        _log.LogTrace("Have 1st callback");
                //        RemoveCacheValue(requestId);
                //        return ReturnJson(TransformCacheDataToBrowserResponse(cacheData));
                //    }
                //}
                return new OkResult();
            }
            catch (Exception ex)
            {
                return BadRequest(new { error = "400", error_description = ex.Message });
            }


        }

        //some helper functions
        protected async Task<(string token, string error, string error_description)> GetAccessToken()
        {
            //
            //TODO Setup the proper access token cache for client credentials
            //
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
    }
}
