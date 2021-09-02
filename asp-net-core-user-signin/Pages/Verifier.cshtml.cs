using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace asp_net_core_user_signin.Pages
{
    public class VerifierModel : PageModel
    {
        private readonly ILogger<VerifierModel> _logger;

        public VerifierModel(ILogger<VerifierModel> logger)
        {
            _logger = logger;
        }

        public void OnGet()
        {

        }
    }
}
