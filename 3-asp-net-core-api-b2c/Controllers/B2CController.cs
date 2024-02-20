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
using Microsoft.AspNetCore.Cors;

namespace B2CVerifiedID
{
    public class B2CController : Controller
    {
        protected readonly ILogger<B2CController> _log;
        private IConfiguration _configuration;
        public B2CController(IConfiguration configuration, ILogger<B2CController> log)
        {
            _configuration = configuration;
            _log = log;
        }

        /////////////////////////////////////////////////////////////////////////////////////
        // Helpers
        /////////////////////////////////////////////////////////////////////////////////////
        protected string GetRequestHostName() {
            string scheme = this.Request.Scheme;
            string originalHost = this.Request.Headers["x-original-host"];
            string hostname = "";
            if (!string.IsNullOrEmpty( originalHost ))
                hostname = string.Format( "{0}://{1}", scheme, originalHost );
            else hostname = string.Format( "{0}://{1}", scheme, this.Request.Host );
            return hostname;
        }

        [EnableCors( "B2CCorsCustomHtml" )]
        [AllowAnonymous]
        public IActionResult selfAsserted() {
            ViewData["apiUrl"] = GetRequestHostName();
            return View();
        }
        [EnableCors( "B2CCorsCustomHtml" )]
        [AllowAnonymous]
        public IActionResult unified() {
            ViewData["apiUrl"] = GetRequestHostName();
            return View();
        }
        [EnableCors( "B2CCorsCustomHtml" )]
        [AllowAnonymous]
        public IActionResult unifiedquick() {
            ViewData["apiUrl"] = GetRequestHostName();
            return View();
        }

    } // cls
} // ns
