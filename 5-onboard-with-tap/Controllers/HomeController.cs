using Azure.Core;
using Azure.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Graph;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OnboardWithTAP.Helpers;
using OnboardWithTAP.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Web;

namespace OnboardWithTAP.Controllers {
    
    public class HomeController : Controller {
        private readonly ILogger<HomeController> _log;
        private readonly IConfiguration _configuration;
        protected IMemoryCache _cache;

        public HomeController( IConfiguration configuration, IMemoryCache cache, ILogger<HomeController> logger ) {
            _configuration = configuration;
            _log = logger;
            _cache = cache;
        }

        [AllowAnonymous]
        public IActionResult Index() {
            return View();
        }
        [AllowAnonymous]
        [ResponseCache( Duration = 0, Location = ResponseCacheLocation.None, NoStore = true )]
        public IActionResult Error() {
            return View( new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier } );
        }
        [AllowAnonymous]
        public IActionResult Privacy() {
            return View();
        }

    } // cls
} // ns
