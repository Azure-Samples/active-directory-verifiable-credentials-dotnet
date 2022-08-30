using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using AspNetCoreVerifiableCredentialsB2C.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using System.Collections.Generic;

namespace AspNetCoreVerifiableCredentialsB2C
{
    [Route("api/verifier/[action]")]
    [ApiController]
    public class ApiVerifierController : ApiBaseVCController
    {
        public ApiVerifierController( IConfiguration configuration
                                    , IOptions<AppSettingsModel> appSettings
                                    , IMemoryCache memoryCache
                                    , IWebHostEnvironment env
                                    , ILogger<ApiVerifierController> log) : base(configuration, appSettings, memoryCache, env, log)
        {
            GetPresentationManifest();
        }

        protected string GetApiPath() {
            return string.Format("{0}/api/verifier", GetRequestHostName());
        }

        protected JObject GetPresentationManifest() {
            if (GetCachedValue("manifestPresentation", out string json)) {
                return JObject.Parse(json); ;
            }
            // download manifest and cache it
            string contents;
            HttpStatusCode statusCode = HttpStatusCode.OK;
            var headers = new Dictionary<string, string>();
            headers.Add("x-ms-sign-contract", "false");
            if (!HttpGet( this.AppSettings.DidManifest, out statusCode, out contents, headers)) {
                _log.LogError("HttpStatus {0} fetching manifest {1}", statusCode, this.AppSettings.DidManifest );
                return null;
            }
            CacheValueWithNoExpiery("manifestPresentation", contents);
            return JObject.Parse(contents);
        }

        protected VCPresentationRequest CreatePresentationRequest( string correlationId ) {
            VCPresentationRequest request = new VCPresentationRequest() {
                includeQRCode = false,
                authority = this.AppSettings.VerifierAuthority,
                registration = new Registration() {
                    clientName = this.AppSettings.client_name,
                    purpose = this.AppSettings.Purpose
                },
                callback = new Callback() {
                    url = string.Format("{0}/presentation-callback", GetApiPath()),
                    state = correlationId,
                    headers = new Dictionary<string, string>() { { "api-key", this._apiKey } }
                },
                includeReceipt = false,
                requestedCredentials = new List<RequestedCredential>(),
                configuration = new Configuration() {
                    validation = new Validation() {
                        allowRevoked = false,
                        validateLinkedDomain = true
                    }
                }
            };
            request.requestedCredentials.Add(new RequestedCredential() {
                type = this.AppSettings.CredentialType,
                manifest = this.AppSettings.DidManifest,
                acceptedIssuers = new List<string>(new string[] { this.AppSettings.IssuerAuthority })
            });
            return request;
        }
        /// ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
        /// REST APIs
        /// ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

        [HttpGet("/api/verifier/echo")]
        public ActionResult Echo() {
            TraceHttpRequest();            
            try
            {
                JObject manifest = GetPresentationManifest();
                var info = new {
                    date = DateTime.Now.ToString(),
                    host = GetRequestHostName(),
                    api = GetApiPath(),
                    didIssuer = manifest["input"]["issuer"], 
                    didVerifier = manifest["input"]["issuer"], 
                    credentialType = this.AppSettings.CredentialType, 
                    displayCard = manifest["display"]["card"],
                    buttonColor = "#000080",
                    contract = manifest["display"]["contract"]
                };
                return ReturnJson(JsonConvert.SerializeObject(info));
            } catch (Exception ex) {
                return ReturnErrorMessage(ex.Message);
            }
        }
        [HttpGet]
        [Route("/api/verifier/logo.png")]
        public ActionResult Logo() {
            TraceHttpRequest();
            JObject manifest = GetPresentationManifest();
            return Redirect( manifest["display"]["card"]["logo"]["uri"].ToString() );
        }

        [HttpGet("/api/verifier/presentation-request")]
        public ActionResult PresentationReference() {
            TraceHttpRequest();
            try {
                // The 'state' variable is the identifier between the Browser session, this API and VC client API doing the validation.
                // It is passed back to the Browser as 'Id' so it can poll for status, and in the presentationCallback (presentation_verified)
                // we use it to correlate which verification that got completed, so we can update the cache and tell the correct Browser session
                // that they are done
                string correlationId = Guid.NewGuid().ToString();
                VCPresentationRequest request = CreatePresentationRequest( correlationId );
                string jsonString = JsonConvert.SerializeObject(request, Formatting.None, new JsonSerializerSettings {
                    NullValueHandling = NullValueHandling.Ignore
                });
                _log.LogTrace( "VC Client API Request\n{0}", jsonString );

                string contents = "";
                HttpStatusCode statusCode = HttpStatusCode.OK;
                if ( !HttpPost( this.AppSettings.ApiEndpoint + "createPresentationRequest", jsonString, out statusCode, out contents))  {
                    _log.LogError("VC Client API Error Response\n{0}", contents);
                    return ReturnErrorMessage( contents );
                }
                // pass the response to our caller (but add id)
                JObject apiResp = JObject.Parse(contents);
                apiResp.Add(new JProperty("id", correlationId));
                contents = JsonConvert.SerializeObject(apiResp);
                _log.LogTrace("VC Client API Response\n{0}", contents);
                return ReturnJson( contents );
            }  catch (Exception ex) {
                return ReturnErrorMessage(ex.Message);
            }
        }
        /// <summary>
        /// This method is called from the B2C HTML/javascript to get a QR code deeplink that 
        /// points back to this API instead of the VC Request Service API.
        /// You need to pass in QueryString parameters such as 'id' or 'StateProperties' which both
        /// are the B2C CorrelationId. StateProperties is a base64 encoded JSON structure.
        /// </summary>
        /// <returns>JSON deeplink to this API</returns>
        [HttpGet("/api/verifier/presentation-request-link")]
        public ActionResult StaticPresentationReferenceGet() {
            TraceHttpRequest();
            try {
                string correlationId = this.Request.Query["id"];
                string stateProp = this.Request.Query["StateProperties"]; // may come from SETTINGS.transId
                if (string.IsNullOrWhiteSpace(correlationId) && !string.IsNullOrWhiteSpace(stateProp) ) {
                    stateProp = stateProp.PadRight(stateProp.Length + (stateProp.Length % 4), '=');
                    JObject spJson = JObject.Parse( Encoding.UTF8.GetString(Convert.FromBase64String(stateProp)) );
                    correlationId = spJson["TID"].ToString();
                }
                if ( string.IsNullOrWhiteSpace(correlationId) ) {
                    correlationId = Guid.NewGuid().ToString();
                }
                RemoveCacheValue( correlationId );
                var resp = new { 
                    requestId = correlationId,
                    url = string.Format("openid://vc/?request_uri={0}/presentation-request-proxy?id={1}", GetApiPath(), correlationId),
                    expiry = (int)(DateTime.UtcNow.AddDays(1) - new DateTime(1970, 1, 1)).TotalSeconds,
                    id = correlationId
                };
                string respJson = JsonConvert.SerializeObject(resp);
                _log.LogTrace("API static request Response\n{0}", respJson );
                return ReturnJson( respJson );
            } catch (Exception ex) {
                return ReturnErrorMessage(ex.Message);
            }
        }
        /// <summary>
        /// This method get's called by the Microsoft Authenticator when it scans the QR code and 
        /// wants to retrieve the request. We call the VC Request Service API, get the request_uri 
        /// in the response, invoke that url and retrieve the response and pass it to the caller.
        /// This way this API acts as a proxy.
        /// </summary>
        /// <returns></returns>
        [HttpGet("/api/verifier/presentation-request-proxy")]
        public ActionResult StaticPresentationReferenceProxy() {
            TraceHttpRequest();
            try {
                // 1. Create a Presentation Request and call the Client API to get the 'real' request_uri
                string correlationId = this.Request.Query["id"];
                VCPresentationRequest request = CreatePresentationRequest( correlationId );
                string jsonString = JsonConvert.SerializeObject( request, Formatting.None, new JsonSerializerSettings {
                    NullValueHandling = NullValueHandling.Ignore
                });
                _log.LogTrace("VC Client API Request\n{0}", jsonString);
                string contents = "";
                HttpStatusCode statusCode = HttpStatusCode.OK;
                if (!HttpPost(this.AppSettings.ApiEndpoint + "createPresentationRequest", jsonString, out statusCode, out contents)) {
                    _log.LogError("VC Client API Error Response\n{0}", contents);
                    return ReturnErrorMessage(contents);
                }
                _log.LogTrace("VC Client API Response\n{0}", contents);

                // 2. Get the 'real' request_uri from the response and make a HTTP GET to it to retrieve the JWT Token for the Authenticator
                JObject apiResp = JObject.Parse(contents);
                string request_uri = apiResp["url"].ToString().Split("=")[1]; // openid://vc/?request_uri=<...url to retrieve request...>
                string response = null;
                string contentType = null;
                using (HttpClient client = new HttpClient()) {
                    string preferHeader = this.Request.Headers["prefer"].ToString();
                    if ( !string.IsNullOrWhiteSpace(preferHeader) ) {
                        client.DefaultRequestHeaders.Add("prefer", preferHeader); // JWT-interop-profile-0.0.1
                    }
                    HttpResponseMessage res = client.GetAsync(request_uri).Result;
                    response = res.Content.ReadAsStringAsync().Result;
                    statusCode = res.StatusCode;
                    contentType = res.Content.Headers.ContentType.ToString();
                    client.Dispose();
                }
                // 3. Return the response to the Authenticator
                _log.LogTrace("VC Client API GET Response\nStatusCode={0}\nContent-Type={1}\n{2}", statusCode, contentType, response);
                return new ContentResult { StatusCode = (int)statusCode, ContentType = contentType, Content = response };
            } catch (Exception ex) {
                return ReturnErrorMessage(ex.Message);
            }
        }

        [HttpPost("/api/verifier/presentation-callback")]
        public ActionResult PresentationCallback() {
            TraceHttpRequest();
            try {
                string body = GetRequestBody();
                _log.LogTrace(body);
                this.Request.Headers.TryGetValue("api-key", out var apiKey);
                if ( this._apiKey != apiKey) {
                    return new ContentResult() { StatusCode = (int)HttpStatusCode.Unauthorized, Content = "api-key wrong or missing" };
                }
                VCCallbackEvent callback = JsonConvert.DeserializeObject<VCCallbackEvent>(body);
                CacheObjectWithExpiery( callback.state, callback );
                return new OkResult();
            } catch (Exception ex) {
                return ReturnErrorMessage(ex.Message);
            }
        }

        [HttpGet("/api/verifier/presentation-response-status")]
        public ActionResult PresentationResponseStatus() {
            TraceHttpRequest();
            try {
                // This is out caller that call this to poll on the progress and result of the presentation
                string correlationId = this.Request.Query["id"];
                if (string.IsNullOrEmpty(correlationId)) {
                    return ReturnErrorMessage("Missing argument 'id'");
                }

                if ( GetCachedObject<VCCallbackEvent>(correlationId, out VCCallbackEvent callback) ) {
                    if ( callback.requestStatus == "request_retrieved" ) {
                        return ReturnJson(JsonConvert.SerializeObject(new { status = 1, message = "QR Code is scanned. Waiting for validation..." } ));
                    }
                    if (callback.requestStatus == "presentation_verified") {
                        string displayName = "";
                        if (callback.verifiedCredentialsData[0].claims.ContainsKey("displayName"))
                             displayName = callback.verifiedCredentialsData[0].claims["displayName"];
                        else displayName = string.Format("{0} {1}"
                                                        , callback.verifiedCredentialsData[0].claims["firstName"]
                                                        , callback.verifiedCredentialsData[0].claims["lastName"]
                                                        );
                        var obj = new { status = 2, message = displayName };
                        JObject resp = JObject.Parse(JsonConvert.SerializeObject( new { status = 2, message = displayName })  );
                        foreach (KeyValuePair<string, string> kvp in callback.verifiedCredentialsData[0].claims ) {
                            resp.Add(new JProperty(kvp.Key, kvp.Value));
                        }                        
                        return ReturnJson(JsonConvert.SerializeObject(resp));
                    }
                    if (callback.requestStatus == "presentation_error") {
                        return ReturnJson(JsonConvert.SerializeObject(new { status = 99, message = "Presentation failed: " + callback.error.message }));
                    }
                } else {
                    return ReturnJson(JsonConvert.SerializeObject(new { status = 0, message = "No data" }));
                }

                return new OkResult();
            } catch (Exception ex) {
                return ReturnErrorMessage( ex.Message );
            }
        }

        /// <summary>
        /// Azure AD B2C REST API Endpoint for retrieveing the VC presentation response
        /// HTTP POST comes from Azure AD B2C 
        /// body : The InputClaims from the B2C policy.It will only be one claim named 'id'
        /// </summary>
        /// <returns>returns a JSON structure with claims from the VC presented</returns>
        [HttpPost("/api/verifier/presentation-response-b2c")]
        public ActionResult PresentationResponseB2C() {
            TraceHttpRequest();
            try {
                string body = GetRequestBody();
                _log.LogTrace(body);
                // if the appSettings has an API key for B2C, make sure the caller passes it
                if (!VerifyB2CApiKey()) {
                    return ReturnUnauthorized("invalid x-api-key");
                }
                JObject b2cRequest = JObject.Parse(body);
                string correlationId = b2cRequest["id"].ToString();
                if (string.IsNullOrEmpty(correlationId)) {
                    return ReturnErrorMessage("Missing argument 'id'");
                }
                VCCallbackEvent callback = null;
                if (!GetCachedObject<VCCallbackEvent>(correlationId, out callback)) {
                    return ReturnErrorB2C("Verifiable Credentials not presented"); // 409
                }
                // remove cache data now, because if we crash, we don't want to get into an infinite loop of crashing 
                RemoveCacheValue(correlationId);
                // setup the response that we are returning to B2C
                var obj = new {
                    vcType = callback.verifiedCredentialsData[0].type[callback.verifiedCredentialsData[0].type.Length - 1], // last
                    vcIss = callback.verifiedCredentialsData[0].issuer,
                    vcSub = callback.subject,
                    // key is intended to be user in user's profile 'identities' collection as a signInName,
                    // and it can't have colons, therefor we modify the value (and clip at following :)
                    vcKey = callback.subject.Replace("did:ion:", "did.ion.").Split(":")[0]
                };
                JObject b2cResponse = JObject.Parse(JsonConvert.SerializeObject(obj));
                // add all the additional claims in the VC as claims to B2C
                foreach (KeyValuePair<string, string> kvp in callback.verifiedCredentialsData[0].claims) {
                    b2cResponse.Add(new JProperty(kvp.Key, kvp.Value));
                }
                string resp = JsonConvert.SerializeObject(b2cResponse);
                _log.LogTrace(resp);
                return ReturnJson( resp );
            } catch (Exception ex) {
                return ReturnErrorMessage(ex.Message);
            }
        }        
    } // cls
} // ns
