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
using Microsoft.Identity.Client;
using System.Collections.Generic;

namespace AspNetCoreVerifiableCredentialsB2C
{
    public class ApiBaseVCController : ControllerBase
    {
        protected IMemoryCache _cache;
        protected readonly IWebHostEnvironment _env;
        protected readonly ILogger<ApiBaseVCController> _log;
        protected readonly AppSettingsModel AppSettings;
        protected readonly IConfiguration _configuration;
        private string _authority;
        public string _apiKey;

        public ApiBaseVCController(IConfiguration configuration
                                 , IOptions<AppSettingsModel> appSettings
                                 , IMemoryCache memoryCache
                                 , IWebHostEnvironment env
                                 , ILogger<ApiBaseVCController> log)
        {
            this.AppSettings = appSettings.Value;
            _cache = memoryCache;
            _env = env;
            _log = log;
            _configuration = configuration;

            _authority = string.Format(this.AppSettings.Authority, this.AppSettings.TenantId);
            _apiKey = System.Environment.GetEnvironmentVariable("INMEM-API-KEY");
        }

        /// ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
        /// Helpers
        /// ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

        protected string GetRequestHostName() {
            string scheme = "https";// : this.Request.Scheme;
            string originalHost = this.Request.Headers["x-original-host"];
            string hostname = "";
            if (!string.IsNullOrEmpty(originalHost))
                 hostname = string.Format("{0}://{1}", scheme, originalHost);
            else hostname = string.Format("{0}://{1}", scheme, this.Request.Host);
            return hostname;
        }
        // return 400 error-message
        protected ActionResult ReturnErrorMessage(string errorMessage) {
            return BadRequest(new { error = "400", error_description = errorMessage });
        }
        // return 200 json 
        protected ActionResult ReturnJson( string json ) {
            return new ContentResult { ContentType = "application/json", Content = json };
        }
        protected async Task<(string, string)> GetAccessToken() {
            IConfidentialClientApplication app = ConfidentialClientApplicationBuilder.Create( this.AppSettings.ClientId )
                                                        .WithClientSecret( this.AppSettings.ClientSecret )
                                                        .WithAuthority( new Uri( _authority ) )
                                                        .Build();
            string[] scopes = new string[] { this.AppSettings.scope };
            AuthenticationResult result = null;
            try {
                result = await app.AcquireTokenForClient( scopes ).ExecuteAsync();
            } catch ( Exception ex) {
                return (String.Empty, ex.Message);
            }
            _log.LogTrace( result.AccessToken );
            return (result.AccessToken, String.Empty);
        }
        protected ActionResult ReturnErrorB2C(string message) {
            var msg = new {
                version = "1.0.0",
                status = 400,
                userMessage = message
            };
            return new ContentResult { StatusCode = 409, ContentType = "application/json", Content = JsonConvert.SerializeObject(msg) };
        }

        protected bool VerifyB2CApiKey() {
            bool rc = true;
            // if the appSettings has an API key for B2C, make sure the caller passes it
            if (!string.IsNullOrWhiteSpace(this.AppSettings.B2C1ARestApiKey)) {
                string xApiKey = this.Request.Headers["x-api-key"];
                if (string.IsNullOrWhiteSpace(xApiKey)) {
                    _log.LogError("Missing header: x-api-key");
                    rc = false;
                }
                else if (xApiKey != this.AppSettings.B2C1ARestApiKey) {
                    _log.LogError("invalid x-api-key: {0}", xApiKey);
                    rc = false;
                }
            }
            return rc;
        }
        protected ActionResult ReturnUnauthorized( string errorMessage ) {
            return new ContentResult() { StatusCode = (int)HttpStatusCode.Unauthorized, Content = errorMessage };
        }

        // POST to VC Client API
        protected bool HttpPost(string url, string body, out HttpStatusCode statusCode, out string response) {
            response = null;            
            var accessToken = GetAccessToken( ).Result;            
            if (accessToken.Item1 == String.Empty ) {
                statusCode = HttpStatusCode.Unauthorized;
                response = accessToken.Item2;
                return false;
            }
            HttpClient client = new HttpClient();
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken.Item1 );
            HttpResponseMessage res = client.PostAsync(  url, new StringContent(body, Encoding.UTF8, "application/json") ).Result;
            response = res.Content.ReadAsStringAsync().Result;
            client.Dispose();
            statusCode = res.StatusCode;
            return res.IsSuccessStatusCode;
        }
        protected bool HttpGet(string url, out HttpStatusCode statusCode, out string response, Dictionary<string, string> headers) {
            response = null;
            HttpClient client = new HttpClient();
            if ( headers != null ) {
                foreach (KeyValuePair<string, string> kvp in headers) {
                    client.DefaultRequestHeaders.Add( kvp.Key, kvp.Value );
                }
            }            
            HttpResponseMessage res = client.GetAsync( url ).Result;
            response = res.Content.ReadAsStringAsync().Result;
            client.Dispose();
            statusCode = res.StatusCode;            
            return res.IsSuccessStatusCode;
        }

        protected string GetClientIpAddr() {
            string ipaddr = "";
            string xForwardedFor = this.Request.Headers["X-Forwarded-For"];
            if (!string.IsNullOrEmpty(xForwardedFor))
                 ipaddr = xForwardedFor;
            else ipaddr = HttpContext.Connection.RemoteIpAddress.ToString();
            return ipaddr;
        }
        protected void TraceHttpRequest() {
            string ipaddr = GetClientIpAddr();
            StringBuilder sb = new StringBuilder();
            foreach( var header in this.Request.Headers ) {
                sb.AppendFormat( "      {0}: {1}\n", header.Key, header.Value );
            }
            _log.LogTrace("{0} {1}\n      {2} {3}://{4}{5}{6}\n{7}", DateTime.UtcNow.ToString("o"), ipaddr
                    , this.Request.Method, this.Request.Scheme, this.Request.Host, this.Request.Path, this.Request.QueryString, sb.ToString() );
        }
        protected string GetRequestBody() {
            return new System.IO.StreamReader(this.Request.Body).ReadToEndAsync().Result;
        }

        protected bool GetCachedObject<T>(string key, out T Object) {
            Object = default(T);
            object val = null;
            bool rc;
            if ( (rc = _cache.TryGetValue(key, out val) ) ) {
                Object = (T)Convert.ChangeType(val, typeof(T));
            }
            return rc;
        }
        protected bool GetCachedValue(string key, out string value) {
            return _cache.TryGetValue(key, out value);
        }
        protected void CacheObjectWithExpiery(string key, object Object) {
            _cache.Set(key, Object, DateTimeOffset.Now.AddSeconds(this.AppSettings.CacheExpiresInSeconds));
        }

        protected void CacheValueWithNoExpiery(string key, string value) {
            _cache.Set(key, value );
        }
        protected void RemoveCacheValue( string key ) {
            _cache.Remove(key);
        }
    } // cls
} // ns
