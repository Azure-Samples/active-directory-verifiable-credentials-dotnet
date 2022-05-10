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
            if (!HttpGet( this.AppSettings.DidManifest, out statusCode, out contents)) {
                _log.LogError("HttpStatus {0} fetching manifest {1}", statusCode, this.AppSettings.DidManifest );
                return null;
            }
            CacheValueWithNoExpiery("manifestPresentation", contents);
            return JObject.Parse(contents);
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
                    credentialType = manifest["id"], 
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

                VCPresentationRequest request = new VCPresentationRequest() {
                    includeQRCode = false,
                    authority = this.AppSettings.VerifierAuthority,
                    registration = new Registration() {
                        clientName = this.AppSettings.client_name
                    },
                    callback = new Callback() {
                        url = string.Format("{0}/presentation-callback", GetApiPath()),
                        state = correlationId,
                        headers = new Dictionary<string, string>() { { "api-key", this._apiKey } }
                    },
                    presentation = new Presentation() {
                        includeReceipt = false,
                        requestedCredentials = new List<RequestedCredential>()
                    }
                };
                request.presentation.requestedCredentials.Add(new RequestedCredential() {
                    type = this.AppSettings.CredentialType,
                    manifest = this.AppSettings.DidManifest,
                    purpose = this.AppSettings.Purpose,
                    acceptedIssuers = new List<string>(new string[] { this.AppSettings.IssuerAuthority })
                });

                string jsonString = JsonConvert.SerializeObject(request, Formatting.None, new JsonSerializerSettings {
                    NullValueHandling = NullValueHandling.Ignore
                });
                _log.LogTrace( "VC Client API Request\n{0}", jsonString );

                string contents = "";
                HttpStatusCode statusCode = HttpStatusCode.OK;
                if ( !HttpPost(jsonString, out statusCode, out contents))  {
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
                    if ( callback.code == "request_retrieved" ) {
                        return ReturnJson(JsonConvert.SerializeObject(new { status = 1, message = "QR Code is scanned. Waiting for validation..." } ));
                    }
                    if (callback.code == "presentation_verified") {
                        string displayName = "";
                        if (callback.issuers[0].claims.ContainsKey("displayName"))
                             displayName = callback.issuers[0].claims["displayName"];
                        else displayName = string.Format("{0} {1}", callback.issuers[0].claims["firstName"], callback.issuers[0].claims["lastName"]);
                        var obj = new { status = 2, message = displayName };
                        JObject resp = JObject.Parse(JsonConvert.SerializeObject( new { status = 2, message = displayName })  );
                        foreach (KeyValuePair<string, string> kvp in callback.issuers[0].claims ) {
                            resp.Add(new JProperty(kvp.Key, kvp.Value));
                        }                        
                        return ReturnJson(JsonConvert.SerializeObject(resp));
                    }                    
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
                    vcType = callback.issuers[0].type[callback.issuers[0].type.Length - 1], // last
                    vcIss = callback.issuers[0].authority,
                    vcSub = callback.subject,
                    // key is intended to be user in user's profile 'identities' collection as a signInName,
                    // and it can't have colons, therefor we modify the value (and clip at following :)
                    vcKey = callback.subject.Replace("did:ion:", "did.ion.").Split(":")[0]
                };
                JObject b2cResponse = JObject.Parse(JsonConvert.SerializeObject(obj));
                // add all the additional claims in the VC as claims to B2C
                foreach (KeyValuePair<string, string> kvp in callback.issuers[0].claims) {
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
