﻿using Microsoft.AspNetCore.Mvc;
using System;
using System.IO;
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

namespace Verifiable_credentials_DotNet
{
    [Route("api/[controller]/[action]")]
    [ApiController]
    public class IssuerController : ControllerBase
    {
        const string ISSUANCEPAYLOAD = "issuance_request_config.json";

        protected readonly AppSettingsModel AppSettings;
        protected IMemoryCache _cache;
        protected readonly ILogger<IssuerController> _log;

        public IssuerController(IOptions<AppSettingsModel> appSettings, IMemoryCache memoryCache, ILogger<IssuerController> log)
        {
            this.AppSettings = appSettings.Value;
            _cache = memoryCache;
            _log = log;
        }

        /// <summary>
        /// This method is called from the UI to initiate the issuance of the verifiable credential
        /// </summary>
        /// <returns>JSON object with the address to the presentation request and optionally a QR code and a state value which can be used to check on the response status</returns>
        [HttpGet("/api/issuer/issuance-request")]
        public async Task<ActionResult> issuanceRequest()
        {
            try
            {
                //they payload template is loaded from disk and modified in the code below to make it easier to get started
                //and having all config in a central location appsettings.json. 
                //if you want to manually change the payload in the json file make sure you comment out the code below which will modify it automatically
                //
                string jsonString = null;
                string newpin = null;

                string payloadpath = Path.Combine(Path.GetDirectoryName(System.Reflection.Assembly.GetEntryAssembly().Location), ISSUANCEPAYLOAD);
                _log.LogTrace("IssuanceRequest file: {0}", payloadpath);
                if (!System.IO.File.Exists(payloadpath))
                {
                    _log.LogError("File not found: {0}", payloadpath);
                    return BadRequest(new { error = "400", error_description = ISSUANCEPAYLOAD + " not found" });
                }
                jsonString = System.IO.File.ReadAllText(payloadpath);
                if (string.IsNullOrEmpty(jsonString))
                {
                    _log.LogError("Error reading file: {0}", payloadpath);
                    return BadRequest(new { error = "400", error_description = ISSUANCEPAYLOAD + " error reading file" });
                }

                //check if pin is required, if found make sure we set a new random pin
                //pincode is only used when the payload contains claim value pairs which results in an IDTokenhint
                JObject payload = JObject.Parse(jsonString);
                if (payload["issuance"]["pin"] != null)
                {
                    _log.LogTrace("pin element found in JSON payload, modifying to a random number of the specific length");
                    var length = (int)payload["issuance"]["pin"]["length"];
                    var pinMaxValue = (int)Math.Pow(10, length) - 1;
                    var randomNumber = RandomNumberGenerator.GetInt32(1, pinMaxValue);
                    newpin = string.Format("{0:D" + length.ToString() + "}", randomNumber);
                    payload["issuance"]["pin"]["value"] = newpin;
                }

                string state = Guid.NewGuid().ToString();

                //modify payload with new state, the state is used to be able to update the UI when callbacks are received from the VC Service
                if (payload["callback"]["state"] != null)
                {
                    payload["callback"]["state"] = state;
                }

                //get the IssuerDID from the appsettings
                if (payload["authority"] != null)
                {
                    payload["authority"] = AppSettings.IssuerAuthority;
                }

                //modify the callback method to make it easier to debug 
                //with tools like ngrok since the URI changes all the time
                //this way you don't need to modify the callback URL in the payload every time
                //ngrok changes the URI
                
                if (payload["callback"]["url"] != null)
                {
                    //localhost hostname can't work for callbacks so we won't overwrite it.
                    //this happens for example when testing with sign-in to an IDP and https://localhost is used as redirect URI
                    //in that case the callback should be configured in the payload directly instead of being modified in the code here
                    string host = GetRequestHostName();
                    if (!host.Contains("//localhost"))
                    {
                        payload["callback"]["url"] = String.Format("{0}:/api/issuer/issuanceCallback", host);
                    }
                }

                //get the manifest from the appsettings, this is the URL to the credential created in the azure portal. 
                //the display and rules file to create the credential can be dound in the credentialfiles directory
                //make sure the credentialtype in the issuance payload matches with the rules file
                //for this sample it should be VerifiedCredentialExpert
                if (payload["issuance"]["manifest"] != null)
                {
                    payload["issuance"]["manifest"] = AppSettings.CredentialManifest;
                }

                //here you could change the payload manifest and change the firstname and lastname
                payload["issuance"]["claims"]["given_name"] = "Megan";
                payload["issuance"]["claims"]["family_name"] = "Bowen";

                jsonString = JsonConvert.SerializeObject(payload);

                //CALL REST API WITH PAYLOAD
                HttpStatusCode statusCode = HttpStatusCode.OK;
                string response = null;

                try
                {
                    //The VC Request API is an authenticated API. We need to clientid and secret (or certificate) to create an access token which 
                    //needs to be send as bearer to the VC Request API
                    var accessToken = GetAccessToken().Result;
                    if (accessToken.Item1 == String.Empty)
                    {
                        _log.LogError(String.Format("failed to acquire accesstoken: {0} : {1}"),accessToken.error, accessToken.error_description);
                        return BadRequest(new { error = accessToken.error, error_description = accessToken.error_description });
                    }


                    HttpClient client = new HttpClient();
                    var defaultRequestHeaders = client.DefaultRequestHeaders;
                    defaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.token);

                    HttpResponseMessage res = client.PostAsync(AppSettings.ApiEndpoint, new StringContent(jsonString, Encoding.UTF8, "application/json")).Result;
                    response = res.Content.ReadAsStringAsync().Result;
                    client.Dispose();
                    statusCode = res.StatusCode;

                    if (statusCode == HttpStatusCode.Created)
                    {
                        _log.LogTrace("succesfully called Request API");
                        JObject requestConfig = JObject.Parse(response);
                        if (newpin != null) { requestConfig["pin"] = newpin; }
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
        /// This method is called by the VC Request API when the user scans a QR code and accepts the issued Verifiable Credential
        /// </summary>
        /// <returns></returns>
        [HttpPost]
        public async Task<ActionResult> issuanceCallback()
        {
            try
            {
                string content = new System.IO.StreamReader(this.Request.Body).ReadToEndAsync().Result;
                _log.LogTrace("callback!: " + content);
                JObject issuanceResponse = JObject.Parse(content);
                var state = issuanceResponse["state"].ToString();

                //there are 2 different callbacks. 1 if the QR code is scanned (or deeplink has been followed)
                //Scanning the QR code makes Authenticator download the specific request from the server
                //the request will be deleted from the server immediately.
                //That's why it is so important to capture this callback and relay this to the UI so the UI can hide
                //the QR code to prevent the user from scanning it twice (resulting in an error since the request is already deleted)
                if (issuanceResponse["code"].ToString() == "request_retrieved")
                {
                    var cacheData = new
                    {
                        status = "request_retrieved",
                        message = "QR Code is scanned. Waiting for issuance...",
                    };
                    _cache.Set(state, JsonConvert.SerializeObject(cacheData));
                }

                //
                //This callback is called when issuance is completed.
                //
                if (issuanceResponse["code"].ToString() == "issuance_successful")
                {
                    var cacheData = new
                    {
                        status = "issuance_succesful",
                        message = "Credential successfully issued",
                    };
                    _cache.Set(state, JsonConvert.SerializeObject(cacheData));
                }
                //
                //We capture if something goes wrong during issuance. See documentation with the different error codes
                //
                if (issuanceResponse["code"].ToString() == "issuance_error")
                {
                    var cacheData = new
                    {
                        status = "issuance_failed",
                        message = "Credential issuance failed",
                        payload = issuanceResponse["code"].ToString()
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
        //when a callback is recieved at the issuanceCallback service the session will be updated
        //this method will respond with the status so the UI can reflect if the QR code was scanned and with the result of the issuance process
        //
        [HttpGet("/api/issuer/issuance-response")]
        public async Task<ActionResult> issuanceResponse()
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
                    value = JObject.Parse(buf);

                    Debug.WriteLine("check if there was a response yet: " + value);
                    return new ContentResult { ContentType = "application/json", Content = JsonConvert.SerializeObject(value) };
                }

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
            // TODO: Setup the proper access token cache for client credentials
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
