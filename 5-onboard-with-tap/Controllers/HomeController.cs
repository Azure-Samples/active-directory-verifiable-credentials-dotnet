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
using System.Diagnostics;
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

        ////////////////////////////////////////////////////////////////////////////////////////////////
        // Helpers
        ////////////////////////////////////////////////////////////////////////////////////////////////
        protected string GetRequestHostName() {
            string scheme = "https";// : this.Request.Scheme;
            string originalHost = this.Request.Headers["x-original-host"];
            string hostname = "";
            if (!string.IsNullOrEmpty( originalHost ))
                hostname = string.Format( "{0}://{1}", scheme, originalHost );
            else hostname = string.Format( "{0}://{1}", scheme, this.Request.Host );
            return hostname;
        }
        private string? GetOidOfCurrentUser() {
            Claim? oidClaim = User?.Claims.Where( x => x.Type == "oid" ).FirstOrDefault();
            if (null == oidClaim) {
                oidClaim = User?.Claims.Where( x => x.Type == "uid" ).FirstOrDefault();
            }
            if (null == oidClaim) {
                oidClaim = User?.Claims.Where( x => x.Type == "http://schemas.microsoft.com/identity/claims/objectidentifier" ).FirstOrDefault();
            }
            if (null == oidClaim) {
                return null;
            } else {
                return oidClaim.Value;
            }
        }

        protected Microsoft.Graph.GraphServiceClient GetGraphClient() {
            var clientSecretCredential = new ClientSecretCredential( _configuration["AzureAd:TenantId"]
                                                                    , _configuration["AzureAd:ClientId"], _configuration["AzureAd:ClientSecret"]
                                                                    , new TokenCredentialOptions { AuthorityHost = AzureAuthorityHosts.AzurePublicCloud }
                                                                    );
            var scopes = new[] { "https://graph.microsoft.com/.default" };
            return new Microsoft.Graph.GraphServiceClient( clientSecretCredential, scopes );
        }

        private List<NewHireProfileClaim> CreateUserClaimsList( string objectId = "", string mail = ""
                                                                , string displayName = "", string givenName = "", string surname = ""
                                                                , string mailNickname = ""
                                                                , string employeeId = "", string employeeHireDate = "", string employeeType = ""
                                                                , string costCenter = "", string division = ""
                                                              ) {
            bool readOnly = !string.IsNullOrWhiteSpace(objectId);
            List<NewHireProfileClaim> profile = new List<NewHireProfileClaim>();
            profile.Add( new NewHireProfileClaim() { Label = "Private email", InternalName = "mail", ReadOnly = false, Type = "text", Value = mail, Placeholder = "private email address" } );
            profile.Add( new NewHireProfileClaim() { Label = "DisplayName", InternalName = "displayName", ReadOnly = false, Type = "text", Value = displayName, Placeholder = "John Doe" } );
            profile.Add( new NewHireProfileClaim() { Label = "Given Name", InternalName = "givenName", ReadOnly = false, Type = "text", Value = givenName, Placeholder ="Name given at birth" } );
            profile.Add( new NewHireProfileClaim() { Label = "Surname", InternalName = "surname", ReadOnly = false, Type = "text", Value = surname, Placeholder = "Family name" } );
            profile.Add( new NewHireProfileClaim() { Label = "Mail Nickname", InternalName = "mailNickname", ReadOnly = readOnly, Type = "text", Value = mailNickname, Placeholder = "john.doe" } );
            profile.Add( new NewHireProfileClaim() { Label = "Employee No", InternalName = "employeeId", ReadOnly = false, Type = "text", Value = employeeId, Placeholder = "111222333" } );
            profile.Add( new NewHireProfileClaim() { Label = "Hire Date", InternalName = "employeeHireDate", ReadOnly = false, Type = "date", Value = employeeHireDate, Placeholder = "YYYY-MM-DD" } );
            profile.Add( new NewHireProfileClaim() { Label = "Employee Type", InternalName = "employeeType", ReadOnly = false, Type = "text", Value = employeeType, Placeholder = "Employee, Contractor, Consultant, or Vendor" } );
            profile.Add( new NewHireProfileClaim() { Label = "Cost Center", InternalName = "costCenter", ReadOnly = false, Type = "text", Value = costCenter, Placeholder = "" } );
            profile.Add( new NewHireProfileClaim() { Label = "Division", InternalName = "division", ReadOnly = false, Type = "text", Value = division, Placeholder = "" } );
            profile.Add( new NewHireProfileClaim() { Label = "objectId", InternalName = "id", ReadOnly = true, Type = "text", Value = objectId } );
            return profile;
        }
        ////////////////////////////////////////////////////////////////////////////////////////////////
        // public actions
        ////////////////////////////////////////////////////////////////////////////////////////////////
        public IActionResult Index() {
            return View();
        }

        public IActionResult Privacy() {
            return View();
        }

        [Authorize]
        public IActionResult RegisterNewHire() {
            ViewData["userProfile"] = CreateUserClaimsList();
            return View();
        }

        [AllowAnonymous]
        [ResponseCache( Duration = 0, Location = ResponseCacheLocation.None, NoStore = true )]
        public IActionResult Error() {
            return View( new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier } );
        }

        /// <summary>
        /// Creates or updates a user profile
        /// </summary>
        /// <param name="id">objectId of existing user. Null if new user</param>
        /// <param name="mail"></param>
        /// <param name="displayName"></param>
        /// <param name="givenName"></param>
        /// <param name="surname"></param>
        /// <param name="mailNickname"></param>
        /// <param name="employeeId"></param>
        /// <param name="employeeHireDate"></param>
        /// <returns></returns>
        [Authorize]
        public async Task<IActionResult> SaveProfile( string id, string mail, string displayName, string givenName, string surname, string mailNickname
                                                    , string employeeId, string employeeHireDate, string employeeType, string costCenter, string division ) {
            _log.LogTrace( this.HttpContext.Request.GetDisplayUrl() );
            if ( string.IsNullOrWhiteSpace(employeeId) ) {
                employeeId = RandomNumberGenerator.GetInt32( 1, int.Parse( "".PadRight( 9, '9' ) ) ).ToString();
            }
            if (string.IsNullOrWhiteSpace( employeeType )) {
                employeeType = "Employee";
            }
            DateTime hireDate = DateTime.Now.Date;
            DateTime.TryParse(employeeHireDate, out hireDate);
            string objectId = "";
            string username = User?.Claims.Where( x => x.Type == "preferred_username" ).FirstOrDefault().Value;
            string domain = username.Split("@")[1];
            Microsoft.Graph.User user = new Microsoft.Graph.User {
                Id = id,
                AccountEnabled = true,
                DisplayName = displayName,
                GivenName = givenName,
                Surname = surname,
                EmployeeId = employeeId,
                EmployeeHireDate = hireDate,
                EmployeeType = employeeType,
                EmployeeOrgData = new EmployeeOrgData() { CostCenter = costCenter, Division = division },
                Department = "Created via OnboardTap sample - delete when done testing",
                OtherMails = new List<string> { mail }
            };
            try {
                var mgClient = GetGraphClient();
                // new or existing user?
                if (!string.IsNullOrWhiteSpace( id )) {
                    var res = mgClient.Users[id].Request().UpdateAsync( user ).Result;
                    ViewData["message"] = "Profile updated";
                    objectId = id;
                } else {
                    mailNickname = mailNickname.Replace( " ", "." ).ToLowerInvariant();
                    user.UserPrincipalName = $"{mailNickname}@{domain}";
                    user.MailNickname = mailNickname;
                    user.PasswordProfile = new Microsoft.Graph.PasswordProfile {
                        ForceChangePasswordNextSignIn = true,
                        Password = Guid.NewGuid().ToString(),
                    };
                    user = mgClient.Users.Request().AddAsync( user ).Result;
                    ViewData["message"] = "Profile created";
                    objectId = user.Id;
                }
                // Assign manager 
                string oid = GetOidOfCurrentUser();
                await mgClient.Users[user.Id].Manager.Reference.Request().PutResponseAsync( oid );

                // Add user to TAP-group to enable use of TAP code
                string tapGroupName = _configuration["AzureAd:TapGroupName"];
                var groups = await mgClient.Groups.Request().Filter( $"displayName eq '{tapGroupName}'" ).GetAsync();
                if (groups != null && groups.Count >= 1) {
                    var usersGroups = await mgClient.Users[user.Id].MemberOf.Request().GetAsync();
                    if (!usersGroups.Any( g => g is Group && g.Id == groups[0].Id )) {
                        // User is not a member of the group, add them.
                        await mgClient.Groups[groups[0].Id].Members.References.Request().AddAsync( user );
                        ViewData["message"] = ViewData["message"] + $". User added to group {tapGroupName}";
                    }
                } else {
                    ViewData["message"] = ViewData["message"] + $". TAP group {tapGroupName} does not exist";
                }
                ViewData["userExists"] = true;
            } catch (Exception ex) {
                ViewData["message"] = $"Error saving user profile - {ex.Message}";
            }

            List<NewHireProfileClaim> profile = CreateUserClaimsList( objectId, mail, displayName, givenName, surname, mailNickname
                                                                    , employeeId, employeeHireDate, employeeType, costCenter, division );
            ViewData["userProfile"] = profile;

            return View( "RegisterNewHire" );
        }

        /// <summary>
        /// Looks up a user profile in the tenant based on email == otherMails
        /// </summary>
        /// <param name="mail"></param>
        /// <returns></returns>
        [Authorize]
        public async Task<IActionResult> FindProfile( string mail ) {
            _log.LogTrace( this.HttpContext.Request.GetDisplayUrl() );
            List<NewHireProfileClaim> profile = CreateUserClaimsList();
            var mgClient = GetGraphClient();
            try {
                var users = await mgClient.Users.Request().Filter( $"otherMails/any(id:id eq '{mail}')" ).GetAsync();
                if (users == null || (null != users && users.Count == 0)) {
                    ViewData["message"] = "User not found";
                }
                if ( null != users ) {
                    if ( users.Count > 1 ) {
                        ViewData["message"] = "Multiple users found. Retrieving first user.";
                    } else {
                        ViewData["message"] = "User retrieved";
                    }                
                    profile = CreateUserClaimsList( users[0].Id, string.Join(", ", users[0].OtherMails.ToArray())
                                                , users[0].DisplayName, users[0].GivenName, users[0].Surname
                                                , users[0].MailNickname, users[0].EmployeeId, users[0].EmployeeHireDate?.ToString( "yyyy-MM-dd")
                                                , users[0].EmployeeType, users[0].EmployeeOrgData.CostCenter, users[0].EmployeeOrgData.Division );
                }
                ViewData["userExists"] = true;
            } catch( Exception ex) { 
                ViewData["message"] = $"Error retrieving user profile - {ex.Message}";
            }
            ViewData["userProfile"] = profile;
            return View( "RegisterNewHire" );
        }

        /// <summary>
        /// Generate a link that can be emailed to the new hire for onboarding.
        /// The link contains a signed JWT token including the private email as a claim
        /// </summary>
        /// <param name="mail"></param>
        /// <returns></returns>
        [Authorize]
        public async Task<IActionResult> GetOnboardingLink( string mail ) {
            _log.LogTrace( this.HttpContext.Request.GetDisplayUrl() );
            ViewData["link"] = "";
            List<NewHireProfileClaim> profile = CreateUserClaimsList();
            try {
                if (string.IsNullOrEmpty( mail )) {
                    ViewData["message"] = "Must specify email to generate link";
                } else {
                    var mgClient = GetGraphClient();
                    var users = await mgClient.Users.Request().Filter( $"otherMails/any(id:id eq '{mail}')" ).GetAsync();
                    if (users == null || (null != users && users.Count == 0)) {
                        ViewData["message"] = "User not found. Please fill in details and save the user profile first.";
                    }
                    if (null != users && users.Count >= 1) {
                        profile = CreateUserClaimsList( users[0].Id, string.Join( ", ", users[0].OtherMails.ToArray() )
                                                    , users[0].DisplayName, users[0].GivenName, users[0].Surname
                                                    , users[0].MailNickname, users[0].EmployeeId, users[0].EmployeeHireDate?.ToString( "yyyy-MM-dd" ) );
                        long now = ((DateTimeOffset)(DateTime.UtcNow)).ToUnixTimeSeconds();
                        Dictionary<string, object> claims = new Dictionary<string, object> {
                            { "exp", ((DateTimeOffset)(DateTime.UtcNow.AddHours(6))).ToUnixTimeSeconds() },
                            { "iat", now },
                            { "nbf", now },
                            { "email", mail },
                            { "tid", _configuration["AzureAd:TenantId"] }
                        };
                        string token = JsonConvert.SerializeObject( claims );
                        string jwtToken = KeyVaultHelper.SignPayload( _configuration, token );
                        _log.LogTrace( jwtToken );
                        string urlToken = HttpUtility.UrlEncode( jwtToken );
                        _log.LogTrace( urlToken );
                        string link = $"{GetRequestHostName()}/Home/Onboarding?token={urlToken}";
                        ViewData["link"] = link;
                        ViewData["mail"] = mail;
                        ViewData["company"] = _configuration["AppSettings:CompanyName"];
                    }
                }
                ViewData["userProfile"] = profile;
            } catch (Exception ex) {
                _log.LogTrace( ex.Message );
                ViewData["message"] = ex.Message;
            }
            return View( "RegisterNewHire" );
        }

        /// <summary>
        /// Onboarding landing page for a new hire user
        /// </summary>
        /// <returns></returns>
        [AllowAnonymous]
        public IActionResult Onboarding() {
            _log.LogTrace( this.HttpContext.Request.GetDisplayUrl() );
            ViewData["email"] = "";
            if (!this.Request.Query.ContainsKey( "token" )) {
                ViewData["message"] = "Required parameter 'token' is missing. Cannot perform onboarding. You should have received an email with a link";
                return View();
            }
            // TrueIdentity adds the parameter with a "?" not checking if there already is a QP
            bool trueIdVerified = false;
            string token = this.Request.Query["token"];
            if ( token.Contains( "?trueIdVerified=true" )) {
                trueIdVerified = true;
                token = token.Replace( "?trueIdVerified=true", "" );
            }
            _log.LogTrace( token );
            if (!KeyVaultHelper.ValidateJwt( _configuration, token, out JObject payload, out string errmsg )) {
                ViewData["message"] = $"Invalid 'token' - {errmsg}";
                return View();
            }

            string mail = payload["email"].ToString();
            var mgClient = GetGraphClient();
            var users = mgClient.Users.Request().Filter( $"otherMails/any(id:id eq '{mail}')" ).GetAsync().Result;
            if (users == null || (null != users && users.Count == 0)) {
                ViewData["message"] = $"User not found with email {mail}";
                return View();
            }

            ViewData["email"] = mail;
            ViewData["displayName"] = users[0].DisplayName;
            string idvUrl = _configuration.GetSection( "AppSettings" )["IdvUrl"];
            string returnUrl = HttpUtility.UrlEncode( $"{this.HttpContext.Request.GetDisplayUrl()}" );
            string link = $"{idvUrl}?returnUrl={returnUrl}&firstName={users[0].GivenName}&lastName={users[0].Surname}";
            ViewData["idvLink"] = link;

            return View();
        }

        [AllowAnonymous]
        [HttpPost( "/api/createtap/{id}" )]
        public async Task<ActionResult> CreateTap( string id ) {
            try {
                _log.LogTrace( this.HttpContext.Request.GetDisplayUrl() );
                //the id is the state value initially created when the issuanc request was requested from the request API
                //the in-memory database uses this as key to get and store the state of the process so the UI can be updated
                /**/
                if (string.IsNullOrEmpty( id )) {
                    _log.LogTrace( $"Missing argument 'id'" );
                    return BadRequest( new { error = "400", error_description = "Missing argument 'id'" } );
                }
                if (!_cache.TryGetValue( id, out string buf )) {
                    _log.LogTrace( $"Cached data not found for id: {id}" );
                    return new NotFoundResult();
                }
                JObject cachedData = JObject.Parse( buf );
                CallbackEvent callback = JsonConvert.DeserializeObject<CallbackEvent>( cachedData["callback"].ToString() );
                if (callback.requestStatus != "presentation_verified") {
                    return BadRequest( new { error = "400", error_description = $"Wrong status in cached data" } );
                }

                // Find the Verified ID credential we asked for and get the first- and last name
                string firstName = null;
                string lastName = null;
                foreach (var vc in callback.verifiedCredentialsData) {
                    if (vc.type.Contains( _configuration["VerifiedID:CredentialType"] )) {
                        if (vc.claims.ContainsKey( "firstName" ) && vc.claims.ContainsKey( "lastName" )) {
                            firstName = vc.claims["firstName"].ToString();
                            lastName = vc.claims["lastName"].ToString();
                        }
                    }
                }
                if (string.IsNullOrWhiteSpace( firstName ) || string.IsNullOrWhiteSpace( lastName )) {
                    return BadRequest( new { error = "400", error_description = $"firstName/lastName missing in presented credential" } );
                }

                var mgClient = GetGraphClient();
                var users = await mgClient.Users.Request().Filter( $"givenName eq '{firstName}' and surname eq '{lastName}'" ).GetAsync();

                if (users == null || (null != users && users.Count == 0)) {
                    return BadRequest( new { error = "400", error_description = $"No user found with givenName '{firstName}' and surname '{lastName}'" } );
                }
                if (users == null || (null != users && users.Count > 1)) {
                    return BadRequest( new { error = "400", error_description = $"Multiple users found with givenName '{firstName}' and surname '{lastName}'" } );
                }
                var userObjectId = users[0].Id;
                var userUPN = users[0].UserPrincipalName;

                // delete any old TAPs so we can create a new one
                var existingTap = mgClient.Users[userObjectId].Authentication.TemporaryAccessPassMethods.Request().GetAsync();
                if (existingTap != null && existingTap.Result != null && existingTap.Result.Count > 1) {
                    foreach (var eTap in existingTap.Result) {
                        await mgClient.Users[userObjectId].Authentication.TemporaryAccessPassMethods[eTap.Id].Request().DeleteAsync();
                    }
                }
                // now create a new TAP code
                TemporaryAccessPassAuthenticationMethod tap = new TemporaryAccessPassAuthenticationMethod();
                tap.LifetimeInMinutes = _configuration.GetValue( "AppSettings:tapLifetimeInMinutes", 60 );
                var tapResult = await mgClient.Users[userObjectId].Authentication.TemporaryAccessPassMethods.Request().AddAsync( tap );
                tap = tapResult;

                var cacheData = new {
                    status = "tap_created",
                    message = $"Welcome aboard!",
                    userFirstName = firstName,
                    userLastName = lastName,
                    userUPN = userUPN,
                    userObjectId = userObjectId,
                    tap = tap.TemporaryAccessPass,
                    expiresUtc = DateTime.UtcNow.AddMinutes( (double)tap.LifetimeInMinutes ),
                    payload = $"userUPN={userUPN}, objectId={userObjectId}, tap={tap.TemporaryAccessPass}"
                };
                _log.LogTrace( $"{cacheData.message}.objectId={userObjectId}, UPN={userUPN}" );
                _cache.Set( id, JsonConvert.SerializeObject( cacheData ) );
                //return new OkResult();
                return new ContentResult { StatusCode = (int)HttpStatusCode.Created, ContentType = "application/json", Content = JsonConvert.SerializeObject( cacheData ) };
            } catch (Exception ex) {
                return BadRequest( new { error = "400", error_description = ex.Message } );
            }
        }

        [AllowAnonymous]
        [HttpGet( "/api/verifier/account-set-up" )]
        public async Task<ActionResult> AccountSetupDone() {
            try {
                _log.LogTrace( this.HttpContext.Request.GetDisplayUrl() );
                //retrieve the right cache from state
                string state = this.Request.Query["id"];
                if (string.IsNullOrEmpty( state )) {
                    return BadRequest( new { error = "400", error_description = "Missing argument 'id'" } );
                }
                JObject value = null;
                if (_cache.TryGetValue( state, out string buf )) {
                    value = JObject.Parse( buf );
                    string targetUserObjectId = value["userObjectId"].ToString(); //TODO: Figure this out

                    var mgClient = GetGraphClient();
                    var authenticatorAppResult = await mgClient.Users[targetUserObjectId].Authentication.MicrosoftAuthenticatorMethods.Request().GetAsync();

                    //TODO: after authenticator app is added to the user, you can consider manipulating group membership to 
                    //'unblock' the account from 
                    if (authenticatorAppResult != null && authenticatorAppResult.Count > 0) {
                        var cacheData = new {
                            status = "account_setup_done",
                            message = $"Authenticator App Phone Sign In configured in Device: {authenticatorAppResult[0].DisplayName} - Platform: {authenticatorAppResult[0].DeviceTag}",
                            deviceDisplayName = authenticatorAppResult[0].DisplayName,
                            deviceTag = authenticatorAppResult[0].DeviceTag,
                            phoneAppVersion = authenticatorAppResult[0].PhoneAppVersion
                        };
                        _log.LogTrace( cacheData.message );
                        return new ContentResult { ContentType = "application/json", Content = JsonConvert.SerializeObject( cacheData ) };
                    } else {
                        var cacheData = new {
                            status = "account_setup_in_progress",
                            message = "Waiting for account set up"
                        };
                        _log.LogTrace( cacheData.message );
                        return new ContentResult { ContentType = "application/json", Content = JsonConvert.SerializeObject( cacheData ) };
                    }
                }
                return new OkResult();
            } catch (Exception ex) {
                _log.LogTrace( ex.Message );
                return BadRequest( new { error = "400", error_description = ex.Message } );
            }
        }

    } // cls
} // ns
