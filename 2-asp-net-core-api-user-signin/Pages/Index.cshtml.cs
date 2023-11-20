using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;

namespace AspNetCoreVerifiableCredentials.Pages
{
    public class IndexModel : PageModel
    {
        private IConfiguration _configuration;
        public IndexModel( IConfiguration configuration ) {
            _configuration = configuration;
        }
        public void OnGet()
        {
            ViewData["AzureAd:ClientId"] = _configuration["AzureAd:ClientId"];

            if (User.Identity.IsAuthenticated) {
                ViewData["username"] = HttpContext.User.Claims.FirstOrDefault( c => c.Type == "preferred_username" )?.Value;
                ViewData["name"] = HttpContext.User.Claims.FirstOrDefault( c => c.Type == "name" )?.Value;
                string given_name = HttpContext.User.Claims.FirstOrDefault( c => c.Type == ClaimTypes.GivenName )?.Value;
                string family_name = HttpContext.User.Claims.FirstOrDefault( c => c.Type == ClaimTypes.Surname )?.Value;
                if ( string.IsNullOrWhiteSpace( given_name ) ) {
                    ViewData["given_name"] = "(not available in ID token)";
                } else {
                    ViewData["given_name"] = given_name;
                }
                if (string.IsNullOrWhiteSpace( family_name )) {
                    ViewData["family_name"] = "(not available in ID token)";
                } else {
                    ViewData["family_name"] = family_name;
                }
            }

        }
    }
}
