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
using Microsoft.Extensions.Configuration;
using System.Security.Policy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Extensions;
using Azure.Core;
using Microsoft.AspNetCore.Http;

namespace B2CVerifiedID
{
    [Route("api/[action]")]
    [ApiController]
    public class CallbackController : Controller
    {
        private enum RequestType {
            Unknown,
            Presentation,
            Issuance,
            Selfie
        };
        protected IMemoryCache _cache;
        protected readonly ILogger<CallbackController> _log;
        private string _apiKey;
        private IConfiguration _configuration;
        public CallbackController(IConfiguration configuration, IMemoryCache memoryCache, ILogger<CallbackController> log)
        {
            _configuration = configuration;
            _cache = memoryCache;
            _log = log;
            _apiKey = System.Environment.GetEnvironmentVariable("API-KEY");
        }

        private async Task<ActionResult> HandleRequestCallback( RequestType requestType, string body ) {
            try {
                this.Request.Headers.TryGetValue( "api-key", out var apiKey );
                if (requestType != RequestType.Selfie && this._apiKey != apiKey) {
                    _log.LogTrace( "api-key wrong or missing" );
                    return new ContentResult() { StatusCode = (int)HttpStatusCode.Unauthorized, Content = "api-key wrong or missing" };
                }
                if ( body == null) {
                    body = await new System.IO.StreamReader( this.Request.Body ).ReadToEndAsync();
                    _log.LogTrace( body );
                }

                bool rc = false;
                string errorMessage = null;
                List<string> presentationStatus = new List<string>() { "request_retrieved", "presentation_verified", "presentation_error" };
                List<string> issuanceStatus = new List<string>() { "request_retrieved", "issuance_successful", "issuance_error" };
                List<string> selfieStatus = new List<string>() { "selfie_taken" };

                CallbackEvent callback = JsonConvert.DeserializeObject<CallbackEvent>( body );

                if (   (requestType == RequestType.Presentation && presentationStatus.Contains( callback.requestStatus ))
                    || (requestType == RequestType.Issuance && issuanceStatus.Contains( callback.requestStatus ))
                    || (requestType == RequestType.Selfie && selfieStatus.Contains( callback.requestStatus ))) {
                    if (!_cache.TryGetValue( callback.state, out string requestState )) {
                        errorMessage = $"Invalid state '{callback.state}'";
                    } else {
                        callback.state = callback.state.Trim();
                        JObject reqState = JObject.Parse( requestState );
                        reqState["state"] = callback.state;
                        reqState["status"] = callback.requestStatus;
                        if (reqState.ContainsKey( "callback" )) {
                            reqState["callback"] = body;
                        } else {
                            reqState.Add( "callback", body );
                        }
                        _cache.Set( callback.state, JsonConvert.SerializeObject( reqState )
                            , DateTimeOffset.Now.AddSeconds( _configuration.GetValue<int>( "AppSettings:CacheExpiresInSeconds", 300 ) ) );
                        rc = true;
                    }
                } else {
                    errorMessage = $"Unknown request status '{callback.requestStatus}'";
                }
                if (!rc ) {
                    return BadRequest( new { error = "400", error_description = errorMessage } );
                }
                return new OkResult();
            } catch (Exception ex) {
                return BadRequest( new { error = "400", error_description = ex.Message } );
            }
        }

        [AllowAnonymous]
        [HttpPost( "/api/issuer/issuecallback" )]
        public async Task<ActionResult> IssuanceCallback() {
            _log.LogTrace( this.Request.GetDisplayUrl() );
            return await HandleRequestCallback( RequestType.Issuance, null );
        }

        [AllowAnonymous]
        [HttpPost( "/api/verifier/presentationcallback" )]
        public async Task<ActionResult> PresentationCallback() {
            _log.LogTrace( this.Request.GetDisplayUrl() );
            return await HandleRequestCallback( RequestType.Presentation, null );
        }

        [AllowAnonymous]
        [HttpGet( "/api/request-status" )]
        public ActionResult RequestStatus() {
            _log.LogTrace( this.Request.GetDisplayUrl() );
            try {
                if (! PollRequestStatus( out JObject response )) {
                    return BadRequest( new { error = "400", error_description = JsonConvert.SerializeObject( response ) } );
                }
                return new ContentResult { ContentType = "application/json", Content = JsonConvert.SerializeObject( response ) };
            } catch (Exception ex) {
                return BadRequest( new { error = "400", error_description = ex.Message } );
            }
        }
        public bool PollRequestStatus( out JObject result ) {
            result = null;
            string state = this.Request.Query["id"];
            if (string.IsNullOrEmpty( state )) {
                result = JObject.FromObject( new { status = "error", message = "Missing argument 'id'" } );
                return false;
            }
            state = state.Trim();
            bool rc = true;
            if (_cache.TryGetValue( state, out string requestState )) {
                JObject reqState = JObject.Parse(requestState);
                string requestStatus = reqState["status"].ToString();
                CallbackEvent callback = null;
                switch ( requestStatus ) {
                    case "request_created":
                        result = JObject.FromObject( new { status = requestStatus, message = "Waiting to scan QR code" } );
                        break;
                    case "request_retrieved":
                        result = JObject.FromObject( new { status = requestStatus, message = "QR code is scanned. Waiting for user action..." } );
                        break;
                    case "issuance_error":
                        callback = JsonConvert.DeserializeObject<CallbackEvent>( reqState["callback"].ToString() );
                        result = JObject.FromObject( new { status = requestStatus, message = "Issuance failed: " + callback.error.message } );
                        break;
                    case "issuance_successful":
                        result = JObject.FromObject( new { status = requestStatus, message = "Issuance successful" } );
                        break;
                    case "presentation_error":
                        callback = JsonConvert.DeserializeObject<CallbackEvent>( reqState["callback"].ToString() );                        
                        result = JObject.FromObject( new { status = requestStatus, message = "Presentation failed:" + callback.error.message } );
                        break;
                    case "presentation_verified":
                        callback = JsonConvert.DeserializeObject<CallbackEvent>(reqState["callback"].ToString() );
                        JObject resp = JObject.Parse( JsonConvert.SerializeObject( new {
                                                                                    status = requestStatus,
                                                                                    message = "Presentation verified",
                                                                                    type = callback.verifiedCredentialsData[0].type,
                                                                                    claims = callback.verifiedCredentialsData[0].claims,
                                                                                    subject = callback.subject,
                                                                                    payload = callback.verifiedCredentialsData,
                                                                                }, Newtonsoft.Json.Formatting.None, new JsonSerializerSettings {
                                                                                                                NullValueHandling = NullValueHandling.Ignore
                                                                            } ) );
                        if (null != callback.receipt && null != callback.receipt.vp_token ) {
                            JObject vpToken = GetJsonFromJwtToken( callback.receipt.vp_token[0] );
                            JObject vc = GetJsonFromJwtToken( vpToken["vp"]["verifiableCredential"][0].ToString() );
                            resp.Add( new JProperty( "jti", vc["jti"].ToString() ) );
                        }
                        if (!string.IsNullOrWhiteSpace( callback.verifiedCredentialsData[0].expirationDate )) {
                            resp.Add( new JProperty( "expirationDate", callback.verifiedCredentialsData[0].expirationDate ) );
                        }
                        if (!string.IsNullOrWhiteSpace( callback.verifiedCredentialsData[0].issuanceDate )) {
                            resp.Add( new JProperty( "issuanceDate", callback.verifiedCredentialsData[0].issuanceDate ) );
                        }
                        result = resp;
                        break;
                    case "selfie_taken":
                        callback = JsonConvert.DeserializeObject<CallbackEvent>( reqState["callback"].ToString() );
                        result = JObject.FromObject( new { status = requestStatus, message = "Selfie taken", photo = callback.photo } );
                        break;
                    default:
                        result = JObject.FromObject( new { status = "error", message = $"Invalid requestStatus '{requestStatus}'" } );
                        rc = false;
                        break;
                }
            } else {
                result = JObject.FromObject( new { status = "request_not_created", message = "No data" } );
                rc = false;
            }
            return rc;
        }

        [AllowAnonymous]
        [HttpPost( "/api/issuer/selfie/{id}" )]
        public async Task<ActionResult> setSelfie( string id ) {
            _log.LogTrace( this.Request.GetDisplayUrl() );
            try {
                string body = new System.IO.StreamReader( this.Request.Body ).ReadToEndAsync().Result;
                _log.LogTrace( body );
                string dataImage = "data:image/jpeg;base64,";
                int idx = body.IndexOf( ";base64," );
                if (-1 == idx) {
                    return BadRequest( new { error = "400", error_description = $"Image must be {dataImage}" } );
                }
                string photo = body.Substring( idx + 8 );
                CallbackEvent callback = new CallbackEvent() {
                    requestId = id,
                    state = id,
                    requestStatus = "selfie_taken",
                    photo = photo
                };
                return await HandleRequestCallback( RequestType.Selfie, JsonConvert.SerializeObject( callback ) );
            } catch (Exception ex) {
                return BadRequest( new { error = "400", error_description = ex.Message } );
            }
        }

        public JObject GetJsonFromJwtToken(string jwtToken)
        {            
            jwtToken = jwtToken.Replace("_", "/").Replace("-", "+").Split(".")[1];
            jwtToken = jwtToken.PadRight(4 * ((jwtToken.Length + 3) / 4), '=');
            return JObject.Parse(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(jwtToken)) );
        }

    } // cls
} // ns
