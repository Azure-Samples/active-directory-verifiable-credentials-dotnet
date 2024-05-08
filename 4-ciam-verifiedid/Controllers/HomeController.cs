using Microsoft.AspNetCore.Mvc;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using CIAMVerifiedID.Models;

namespace CIAMVerifiedID
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
