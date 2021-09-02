using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace asp_net_core_user_signin.Pages
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
