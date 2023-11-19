using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Policy;
using System.Threading.Tasks;
using Azure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;

namespace AspNetCoreVerifiableCredentials.Pages
{
    public class VerifierModel : PageModel
    {
        private IConfiguration _configuration;
        public VerifierModel( IConfiguration configuration ) {
            _configuration = configuration;
        }
        public void OnGet()
        {
            ViewData["Message"] = "";
            ViewData["CredentialType"] = _configuration["VerifiedID:CredentialType"];
            ViewData["acceptedIssuers"] = new string[] { _configuration["VerifiedID:DidAuthority"] };
            ViewData["useFaceCheck"] = false;
            ViewData["useConstraints"] = false;
            ViewData["constraintName"] = "";
            ViewData["constraintValue"] = "";
            ViewData["constraintOp"] = "value";

            if (this.Request.Query.ContainsKey( "photoClaimName" )) {
                ViewData["PhotoClaimName"] = this.Request.Query["photoClaimName"].ToString(); // could be empty/null for no-photo
            } else {
                ViewData["PhotoClaimName"] = _configuration.GetValue( "VerifiedID:PhotoClaimName", "" );
            }

            HttpContext.Session.Remove( "presentationRequestTemplate" );
            string templateLink = this.Request.Query["template"];
            string jsonTemplate = null;

            // URL?
            if ( !string.IsNullOrWhiteSpace( templateLink ) && templateLink.StartsWith("https://") ) {
                HttpClient client = new HttpClient();
                HttpResponseMessage res = null;
                try {
                    res = client.GetAsync( templateLink ).Result;
                } catch( Exception ex ) {
                    client.Dispose();
                    ViewData["Message"] = $"Error getting template link: {templateLink}. {ex.Message}";
                    return;
                }
                jsonTemplate = res.Content.ReadAsStringAsync().Result;
                client.Dispose();
                if ( HttpStatusCode.OK != res.StatusCode ) {
                    ViewData["Message"] = $"{res.StatusCode.ToString()} - Template link not found: {templateLink}";
                    return;
                }
            }

            // local file?
            if (!string.IsNullOrWhiteSpace( templateLink ) 
                && (templateLink.StartsWith( "file://" ) || templateLink.Substring(1,2) == ":\\")) {
                if (templateLink.StartsWith( "file://" ) ) {
                    templateLink = templateLink.Substring(8).Replace("/", "\\" );
                }
                try {
                    jsonTemplate = System.IO.File.ReadAllText( templateLink );
                } catch( Exception ex ) {
                    ViewData["Message"] = $"Error getting template link: {ex.Message}";
                }
            }

            if ( !string.IsNullOrWhiteSpace( jsonTemplate ) ) {
                PresentationRequest request = null;
                try {
                    request = JsonConvert.DeserializeObject<PresentationRequest>( jsonTemplate );
                } catch( Exception ex ) {
                    ViewData["Message"] = $"Error parsing template link: {templateLink}. {ex.Message}";
                    return;
                }
                if ( null == request.requestedCredentials ) {
                    ViewData["Message"] = $"Template link is not a presentation request: {templateLink}.";
                    return;
                }
                if ( string.IsNullOrWhiteSpace( request.requestedCredentials[0].type) ) {
                    ViewData["Message"] = $"Template link does not have a credential type: {templateLink}.";
                    return;
                }
                ViewData["CredentialType"] = request.requestedCredentials[0].type;
                ViewData["acceptedIssuers"] = request.requestedCredentials[0].acceptedIssuers.ToArray();

                // template uses FaceCheck?
                if ( null != request.requestedCredentials[0].configuration && null != request.requestedCredentials[0].configuration.validation.faceCheck) {
                        ViewData["useFaceCheck"] = true;
                        ViewData["PhotoClaimName"] = request.requestedCredentials[0].configuration.validation.faceCheck.sourcePhotoClaimName;
                }

                // template uses constraints?
                if (null != request.requestedCredentials[0].constraints ) {
                    ViewData["useConstraints"] = true;
                    ViewData["constraintName"] = request.requestedCredentials[0].constraints[0].claimName;
                    if (  request.requestedCredentials[0].constraints[0].values != null ) {
                        ViewData["constraintOp"] = "value";
                        ViewData["constraintValue"] = string.Join(";", request.requestedCredentials[0].constraints[0].values );
                    }
                    if (request.requestedCredentials[0].constraints[0].contains != null) {
                        ViewData["constraintOp"] = "contains";
                        ViewData["constraintValue"] = request.requestedCredentials[0].constraints[0].contains;
                    }
                    if (request.requestedCredentials[0].constraints[0].startsWith != null) {
                        ViewData["constraintOp"] = "startsWith";
                        ViewData["constraintValue"] = request.requestedCredentials[0].constraints[0].startsWith;
                    }
                }
                HttpContext.Session.SetString("presentationRequestTemplate", jsonTemplate );
            }
        }
    }
}
