using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging;

namespace AspNetCoreVerifiableCredentials.Pages
{
    [Authorize]
    public class IssuerModel : PageModel
    {
        private readonly ILogger<IssuerModel> _logger;
        public IssuerModel(ILogger<IssuerModel> logger)
        {
            _logger = logger;
        }
        public void OnGet()
        {
        }
    }
}
