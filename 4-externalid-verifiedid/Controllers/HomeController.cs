using Microsoft.AspNetCore.Mvc;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using ExternalIDVerifiedID.Models;

namespace ExternalIDVerifiedID
{
    public class HomeController : Controller
    {
        protected readonly ILogger<HomeController> _log;
        private IConfiguration _configuration;
        private readonly IssuanceCredentialCatalog _credentials;
        public HomeController(IConfiguration configuration, ILogger<HomeController> log, IssuanceCredentialCatalog credentials)
        {
            _configuration = configuration;
            _log = log;
            _credentials = credentials;
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
        public IActionResult Issuer(string credentialId = "default") {
            CheckLocalhost();
            try {
                var credentials = _credentials.GetCredentials();
                if (!System.Linq.Enumerable.Any(credentials, c => c.Id == credentialId)) {
                    return BadRequest("Unknown issuance credential.");
                }
                ViewData["CredentialId"] = credentialId;
                ViewData["DidAuthority"] = _configuration["VerifiedID:DidAuthority"];
                return View(credentials);
            } catch (System.InvalidOperationException ex) {
                _log.LogError(ex, "Invalid issuance credential catalog.");
                return Problem("Invalid issuance credential configuration. Check the server configuration.");
            }
        }

        [Authorize]
        public IActionResult Profile() {
            return View();
        }

        [AllowAnonymous]
        public IActionResult Privacy() {
            return View();
        }

        [AllowAnonymous]
        [ResponseCache( Duration = 0, Location = ResponseCacheLocation.None, NoStore = true )]
        public IActionResult Error() {
            return View( new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier } );
        }

    } // cls
} // ns
