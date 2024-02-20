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
using B2CVerifiedID.Models;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authentication;
using Microsoft.IdentityModel.Tokens;
using System.Linq;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace B2CVerifiedID
{
    public class HomeController : Controller
    {
        protected readonly ILogger<HomeController> _log;
        private IConfiguration _configuration;
        public HomeController(IConfiguration configuration, ILogger<HomeController> log)
        {
            _configuration = configuration;
            _log = log;
        }

        private void CheckLocalhost() {
            if ("localhost" == this.Request.Host.Host || "127.0.0.1" == this.Request.Host.Host) {
                ViewData["message"] = "=== You can not run Verified ID on localhost. Use ngrok or similar proxy, or deploy to Azure AppServices ===";
            } else {
                ViewData["message"] = "";
            }
        }

        [AllowAnonymous]
        public IActionResult Index() {
            CheckLocalhost();
            return View();
        }

        [AllowAnonymous]
        public IActionResult Verifier() {
            CheckLocalhost();

            ViewData["CredentialType"] = _configuration["VerifiedID:CredentialType"];
            ViewData["acceptedIssuers"] = new string[] { _configuration["VerifiedID:DidAuthority"] };
            ViewData["useFaceCheck"] = false;

            if (this.Request.Query.ContainsKey( "photoClaimName" )) {
                ViewData["PhotoClaimName"] = this.Request.Query["photoClaimName"].ToString(); // could be empty/null for no-photo
            } else {
                ViewData["PhotoClaimName"] = _configuration.GetValue( "VerifiedID:PhotoClaimName", "" );
            }

            return View();
        }
        [AllowAnonymous]
        public IActionResult PresentationVerified() {
            return View();
        }

        [Authorize]
        public IActionResult Issuer() {
            CheckLocalhost();
            return View();
        }

        [Authorize]
        public async Task<IActionResult> SensitivePage() {
            CheckLocalhost();

            var claimMatchConfidenceScore = User.FindFirst( "VCMatchConfidenceScore" );
            if (claimMatchConfidenceScore == null) {
                string b2cMfaPolicyId = _configuration["AzureAdB2C:MFAPolicyId"];
                _log.LogTrace( $"B2C MFA PolicyId = {b2cMfaPolicyId}" );
                if ( string.IsNullOrWhiteSpace( b2cMfaPolicyId ) ) {
                    ViewData["message"] = "No MFA B2C Custom Policy is configured.";
                } else {
                    // challenge the user via invoking the MFA policy
                    await HttpContext.ChallengeAsync( OpenIdConnectDefaults.AuthenticationScheme, 
                        new AuthenticationProperties {
                            RedirectUri = Url.Action( nameof( HomeController.SensitivePage ), "Home" ),
                            Items = { { "policy", b2cMfaPolicyId } }
                        } );
                }
            } else {
                _log.LogTrace( $"Face Check matchConfidenceScore = {claimMatchConfidenceScore.Value}" );
                ViewData["matchConfidenceScore"] = claimMatchConfidenceScore.Value;
            }
            return View();
        }

        [AllowAnonymous]
        [ResponseCache( Duration = 0, Location = ResponseCacheLocation.None, NoStore = true )]
        public IActionResult Error() {
            return View( new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier } );
        }

    } // cls
} // ns
