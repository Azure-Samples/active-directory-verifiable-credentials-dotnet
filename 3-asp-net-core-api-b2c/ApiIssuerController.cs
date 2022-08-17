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
    [Route("api/issuer/[action]")]
    [ApiController]
    public class ApiIssuerController : ApiBaseVCController
    {
        public ApiIssuerController(IConfiguration configuration
                                 , IOptions<AppSettingsModel> appSettings
                                 , IMemoryCache memoryCache
                                 , IWebHostEnvironment env
                                 , ILogger<ApiIssuerController> log) : base(configuration, appSettings, memoryCache, env, log)
        {            
            GetIssuanceManifest();
        }

        protected string GetApiPath() {
            return string.Format("{0}/api/issuer", GetRequestHostName() );
        }

        protected JObject GetIssuanceManifest() {
            if ( GetCachedValue("manifestIssuance", out string json)) {
                return JObject.Parse(json); ;
            }
            // download manifest and cache it
            string contents;
            HttpStatusCode statusCode = HttpStatusCode.OK;
            var headers = new Dictionary<string, string>();
            headers.Add("x-ms-sign-contract", "false" );
            if (!HttpGet( this.AppSettings.DidManifest, out statusCode, out contents, headers )) {
                _log.LogError("HttpStatus {0} fetching manifest {1}", statusCode, this.AppSettings.DidManifest );
                return null;
            }
            CacheValueWithNoExpiery("manifestIssuance", contents);
            return JObject.Parse(contents);
        }

        protected Dictionary<string,string> GetSelfAssertedClaims( JObject manifest ) {
            Dictionary<string, string> claims = new Dictionary<string, string>();
            if (manifest["input"]["attestations"]["idTokens"][0]["id"].ToString() == "https://self-issued.me") {
                foreach (var claim in manifest["input"]["attestations"]["idTokens"][0]["claims"]) {
                    claims.Add(claim["claim"].ToString(), "");
                }
            }
            return claims;
        }

        /// ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
        /// REST APIs
        /// ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

        [HttpGet("/api/issuer/echo")]
        public ActionResult Echo() {
            TraceHttpRequest();
            try {
                JObject manifest = GetIssuanceManifest();
                Dictionary<string,string> claims = GetSelfAssertedClaims( manifest );
                var info = new
                {
                    date = DateTime.Now.ToString(),
                    host = GetRequestHostName(),
                    api = GetApiPath(),
                    didIssuer = manifest["input"]["issuer"], 
                    credentialType = this.AppSettings.CredentialType, 
                    displayCard = manifest["display"]["card"],
                    buttonColor = "#000080",
                    contract = manifest["display"]["contract"],
                    selfAssertedClaims = claims
                };
                return ReturnJson(JsonConvert.SerializeObject(info));
            } catch (Exception ex) {
                return ReturnErrorMessage(ex.Message);
            }
        }

        [HttpGet]
        [Route("/api/issuer/logo.png")]
        public ActionResult Logo() {
            TraceHttpRequest();
            JObject manifest = GetIssuanceManifest();
            return Redirect(manifest["display"]["card"]["logo"]["uri"].ToString());
        }

        /// <summary>
        /// API to start an issueance request. If it contains the query string parameter 'id', 
        /// it should be the same id as passed to the below api issuance-claims-b2c (see below)
        /// </summary>
        /// <returns></returns>
        [HttpGet("/api/issuer/issue-request")]
        public ActionResult IssuanceReference() {
            TraceHttpRequest();
            try {
                string stateId = Guid.NewGuid().ToString();
                string correlationId = this.Request.Query["id"];
                JObject cachedClaims = null;
                // if caller is passing and 'id' query string parameter, then we should have called issuance-claims-b2c and cached claims
                // if no 'id', then this is a standard issuance request
                if ( !string.IsNullOrWhiteSpace(correlationId) ) {
                    if (!GetCachedObject<JObject>(correlationId, out cachedClaims)) {
                        _log.LogError("No cached claims for correlationId {0}", correlationId);
                        return ReturnErrorMessage( "Invalid correlationId"  );
                    }
                } else {
                    correlationId = stateId;
                }
                VCIssuanceRequest request = new VCIssuanceRequest() {
                    includeQRCode = false,
                    authority = this.AppSettings.VerifierAuthority,
                    registration = new Registration() {
                        clientName = this.AppSettings.client_name
                    },
                    callback = new Callback() {
                        url = string.Format("{0}/issue-callback", GetApiPath()),
                        state = stateId,
                        headers = new Dictionary<string, string>() { { "api-key", this._apiKey } }
                    },
                    type = this.AppSettings.CredentialType,
                    manifest = this.AppSettings.DidManifest,
                    pin = null
                };

                // if pincode is required, set it up in the request
                if (this.AppSettings.IssuancePinCodeLength > 0) {
                    int pinCode = RandomNumberGenerator.GetInt32(1, int.Parse("".PadRight(this.AppSettings.IssuancePinCodeLength, '9')));
                    _log.LogTrace("pin={0}", pinCode);
                    request.pin = new Pin() {
                        length = this.AppSettings.IssuancePinCodeLength,
                        value = string.Format("{0:D" + this.AppSettings.IssuancePinCodeLength.ToString() + "}", pinCode)
                    };
                }

                // Get the manifest and check to see if there are any self asserted claims (id_token_hint flow)
                JObject manifest = GetIssuanceManifest();
                Dictionary<string, string> claims = GetSelfAssertedClaims(manifest);
                if (claims.Count > 0 ) {
                    request.claims = new Dictionary<string, string>();
                    // If we have received claims from B2C, use tham. Otherwise, pick the queryString params (for testing only)
                    if ( cachedClaims != null ) {
                        // for the claims mentioned in the issuance payload, get the values we cached from B2C
                        foreach (KeyValuePair<string, string> kvp in claims) {
                            if (cachedClaims.ContainsKey(kvp.Key) ) {
                                request.claims.Add(kvp.Key, cachedClaims[kvp.Key].ToString());
                            }
                        }
                        // if B2C gave us a pincode, then set it
                        if ( cachedClaims.ContainsKey("pinCode") ) {
                            string pinCode = cachedClaims["pinCode"].ToString();
                            _log.LogTrace("B2C pin={0}", pinCode);
                            request.pin = new Pin() { length = pinCode.Length, value = pinCode };
                        }
                    } else {
                        // set self-asserted claims passed as query string parameters
                        // This part assumes that ALL claims comes from the UX (and it should only be used in testing)
                        if (claims.Count > 0) {
                            foreach (KeyValuePair<string, string> kvp in claims) {
                                request.claims.Add(kvp.Key, this.Request.Query[kvp.Key].ToString());
                            }
                        }
                    }
                }

                string jsonString = JsonConvert.SerializeObject( request, Formatting.None, new JsonSerializerSettings {
                    NullValueHandling = NullValueHandling.Ignore
                });
                _log.LogTrace("VC Client API Request\n{0}", jsonString);

                string contents = "";
                HttpStatusCode statusCode = HttpStatusCode.OK;
                if ( !HttpPost( this.AppSettings.ApiEndpoint + "createIssuanceRequest", jsonString, out statusCode, out contents) ) {
                    _log.LogError("VC Client API Error Response\n{0}", contents);
                    return ReturnErrorMessage( contents );
                }
                // add the id and the pin to the response we give the browser since they need them
                JObject requestConfig = JObject.Parse(contents);
                if (this.AppSettings.IssuancePinCodeLength > 0) {
                    requestConfig["pin"] = request.pin.value;
                }
                requestConfig.Add(new JProperty("id", stateId));
                jsonString = JsonConvert.SerializeObject(requestConfig);
                _log.LogTrace("VC Client API Response\n{0}", jsonString);
                return ReturnJson( jsonString );
            }  catch (Exception ex)  {
                return ReturnErrorMessage( ex.Message );
            }
        }

        /// <summary>
        /// Callback from VC Request API
        /// </summary>
        /// <returns></returns>
        [HttpPost("/api/issuer/issue-callback")]
        public ActionResult IssuanceCallbackModel() {
            TraceHttpRequest();
            try {
                string body = GetRequestBody();
                _log.LogTrace(body);
                this.Request.Headers.TryGetValue("api-key", out var apiKey);
                if ( this._apiKey != apiKey ) {
                    return new ContentResult() { StatusCode = (int)HttpStatusCode.Unauthorized, Content = "api-key wrong or missing" };
                }
                VCCallbackEvent callback = JsonConvert.DeserializeObject<VCCallbackEvent>(body);
                CacheObjectWithExpiery(callback.state, callback);
                return new OkResult();
            } catch (Exception ex) {
                return ReturnErrorMessage(ex.Message);
            }
        }

        [HttpGet("/api/issuer/issue-response")]
        public ActionResult IssuanceResponseModel() {
            TraceHttpRequest();
            try {
                string correlationId = this.Request.Query["id"];
                if (string.IsNullOrEmpty(correlationId)) {
                    return ReturnErrorMessage("Missing argument 'id'");
                }
                if (GetCachedObject<VCCallbackEvent>(correlationId, out VCCallbackEvent callback)) {
                    if (callback.requestStatus == "request_retrieved") {
                        return ReturnJson(JsonConvert.SerializeObject(new { status = 1, message = "QR Code is scanned. Waiting for issuance to complete." }));
                    }
                    if (callback.requestStatus == "issuance_successful") {
                        RemoveCacheValue(correlationId);
                        return ReturnJson(JsonConvert.SerializeObject(new { status = 2, message = "Issuance process is completed" }));
                    }
                    if (callback.requestStatus == "issuance_error") {
                        RemoveCacheValue(correlationId);
                        return ReturnJson(JsonConvert.SerializeObject(new { status = 99, message = "Issuance process failed with reason: " + callback.error.message }));
                    }
                }
                return new OkResult();
            } catch (Exception ex) {
                return ReturnErrorMessage(ex.Message);
            }
        }

        /// <summary>
        /// Azure AD B2C REST API Endpoint for storing claims for future VC issuance request
        /// HTTP POST comes from Azure AD B2C 
        /// body : The InputClaims from the B2C policy. The 'id' is B2C's correlationId
        ///        Other claims are claims that may be used in the VC (see issue-request above)
        /// </summary>
        /// <returns>200 OK, 401 (api-key) or 404 (missing id/oid)</returns>
        [HttpPost("/api/issuer/issuance-claims-b2c")]
        public ActionResult IssuanceClaimsB2C() {
            TraceHttpRequest();
            try {
                string body = GetRequestBody();
                _log.LogTrace(body);
                // if the appSettings has an API key for B2C, make sure B2C passes it
                if ( !VerifyB2CApiKey() ) {
                    return ReturnUnauthorized( "invalid x-api-key" );
                }
                // make sure B2C passed the 'id' claim (correlationId) that we use for caching
                // (without it we will never be able to find these claims again)
                JObject b2cClaims = JObject.Parse(body);
                string correlationId = b2cClaims["id"].ToString();
                if (string.IsNullOrEmpty(correlationId)) {
                    return ReturnErrorMessage("Missing claim 'id'");
                }
                // make sure B2C atleast passes the oid claim as that is the key for identifying a B2C user from a VC
                string oid = b2cClaims["oid"].ToString();
                if (string.IsNullOrEmpty(oid)) {
                    return ReturnErrorMessage("Missing claim 'oid'");
                }
                CacheObjectWithExpiery(correlationId, b2cClaims );
                return new OkResult();
            } catch (Exception ex) {
                return ReturnErrorMessage(ex.Message);
            }
        }
    } // cls
} // ns
