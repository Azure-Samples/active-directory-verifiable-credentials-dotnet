using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using ExternalIDVerifiedID.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace ExternalIDVerifiedID {
    [Route("identity")]
    public sealed class IdentityVerificationController : Controller {
        private readonly IdentityVerificationState _state;
        private readonly IConfiguration _config;
        private readonly IHttpClientFactory _clients;
        private readonly ILogger<IdentityVerificationController> _log;
        private readonly IssuanceCredentialCatalog _credentials;

        public IdentityVerificationController(IdentityVerificationState state, IConfiguration config,
            IHttpClientFactory clients, ILogger<IdentityVerificationController> log,
            IssuanceCredentialCatalog credentials) {
            _state = state;
            _config = config;
            _clients = clients;
            _log = log;
            _credentials = credentials;
        }

        private string UserId => User.FindFirst("oid")?.Value ??
            User.FindFirst("http://schemas.microsoft.com/identity/claims/objectidentifier")?.Value;
        private string Email => ExternalIdClaims.Email(User);

        private List<string> MissingAccountClaims(string bindingSource = "email") {
            var missing = new List<string>();
            if (!Guid.TryParse(UserId, out _)) missing.Add("oid (a valid External ID object ID)");
            if (bindingSource == "email" && string.IsNullOrWhiteSpace(Email)) missing.Add("email (or emails)");
            return missing;
        }

        private bool TryGetFlow(string flow, out IdentityVerificationState.Transaction transaction) {
            transaction = null;
            return !string.IsNullOrWhiteSpace(flow) &&
                _state.TryGet(flow, out transaction) &&
                transaction.FlowId == flow && transaction.UserId == UserId;
        }

        private Uri PublicBaseUrl() {
            var url = _config["IdentityVerification:PublicBaseUrl"];
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
                uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
                throw new InvalidOperationException("IdentityVerification:PublicBaseUrl must be an HTTPS origin ending in /.");
            return uri;
        }

        [Authorize]
        [HttpGet("")]
        public IActionResult Index([FromQuery] string flow, [FromQuery] string credentialId = "default") {
            if (!string.IsNullOrEmpty(flow) && !TryGetFlow(flow, out _)) return NotFound();
            var credentials = _credentials.GetCredentials();
            var selected = credentials.SingleOrDefault(c => c.Id == credentialId);
            if (selected == null) {
                _log.LogWarning("Unknown verification credential ID.");
                return BadRequest("Unknown verification credential.");
            }
            ViewData["Flow"] = flow;
            ViewData["Transaction"] = TryGetFlow(flow, out var transaction) ? transaction : null;
            ViewData["Credentials"] = credentials;
            ViewData["CredentialId"] = transaction?.CredentialId ?? selected.Id;
            ViewData["ProviderConfigured"] = Uri.TryCreate(_config["IdentityVerification:ProviderUrl"],
                UriKind.Absolute, out var provider) && provider.Scheme == Uri.UriSchemeHttps;
            ViewData["MissingAccountClaims"] = MissingAccountClaims(transaction?.AccountBindingSource ??
                selected.Verification?.AccountBindingSource);
            return View();
        }

        [Authorize]
        [HttpPost("start")]
        [ValidateAntiForgeryToken]
        public IActionResult Start([FromForm] string credentialId = "default") {
            var selected = _credentials.GetCredentials().SingleOrDefault(c => c.Id == credentialId);
            if (selected == null || selected.Verification == null) {
                _log.LogWarning("Selected credential has no verification mappings.");
                return BadRequest("Select a configured credential with verification mappings.");
            }
            var mapping = selected.Verification;
            try {
                mapping.Validate();
            } catch (InvalidOperationException ex) {
                _log.LogError(ex, "Invalid credential verification mapping.");
                return BadRequest(ex.Message);
            }
            if (string.IsNullOrWhiteSpace(selected.Type) || string.IsNullOrWhiteSpace(_config["VerifiedID:DidAuthority"])) {
                _log.LogWarning("Verification credential type or issuer is not configured.");
                return BadRequest("Configure the selected credential type and VerifiedID:DidAuthority.");
            }
            var missing = MissingAccountClaims(mapping.AccountBindingSource);
            if (missing.Count > 0) {
                _log.LogWarning("Verification cannot start: missing required claims {Claims}", string.Join(", ", missing));
                var result = (ViewResult)Index(null, credentialId);
                result.ViewName = "Index";
                Response.StatusCode = 400;
                return result;
            }
            var flow = Guid.NewGuid().ToString("N");
            _state.Add(flow, new IdentityVerificationState.Transaction {
                FlowId = flow, UserId = UserId, Email = Email,
                CredentialId = selected.Id, CredentialName = selected.Name, CredentialType = selected.Type,
                CredentialIssuer = _config["VerifiedID:DidAuthority"],
                AccountBindingClaim = mapping.AccountBindingClaim, AccountBindingSource = mapping.AccountBindingSource,
                ExpectedAccountValue = mapping.AccountBindingSource == "oid" ? UserId : Email,
                ProfileValueClaim = mapping.ProfileValueClaim
            });
            return RedirectToAction(nameof(Index), new { flow });
        }

        [Authorize]
        [HttpGet("provider")]
        public IActionResult Provider([FromQuery] string flow) {
            if (!TryGetFlow(flow, out var transaction)) return NotFound();
            if (transaction.Status != "credential_verified") return Conflict("Verify the selected credential first.");
            var provider = _config["IdentityVerification:ProviderUrl"];
            if (!Uri.TryCreate(provider, UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.Fragment))
                return BadRequest("Configure an HTTPS IdentityVerification:ProviderUrl before starting the provider handoff.");

            var returnUrl = new Uri(PublicBaseUrl(), $"identity/return/{Uri.EscapeDataString(flow)}");
            var separator = string.IsNullOrEmpty(uri.Query) ? "?" : "&";
            return Redirect(uri + separator + "returnUrl=" + Uri.EscapeDataString(returnUrl.ToString()));
        }

        [Authorize]
        [HttpGet("return/{state}")]
        public IActionResult Return(string state) {
            if (!TryGetFlow(state, out var transaction)) return NotFound();
            if (transaction.Status != "credential_verified") return Conflict("Verify the selected credential first.");
            transaction.ProviderReturnSeen = true;
            return RedirectToAction(nameof(Index), new { flow = state });
        }

        [Authorize]
        [HttpPost("presentation")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Presentation([FromForm] string flow, [FromForm] string stage) {
            if (!TryGetFlow(flow, out var transaction)) return NotFound();
            if (stage != "credential" && stage != "identity") return BadRequest("Unknown presentation stage.");
            var expectedStatus = stage == "credential" ? "ready" : "credential_verified";
            lock (transaction) {
                if (transaction.Status != expectedStatus)
                    return Conflict("Finish the preceding verification step first.");
                transaction.Status = stage + "_starting";
            }

            var key = _config["IdentityVerification:CallbackApiKey"];
            var credentialType = stage == "credential" ? transaction.CredentialType : "TrueIdentity";
            var issuer = stage == "credential" ? transaction.CredentialIssuer :
                _config["IdentityVerification:IdentityIssuer"];
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(issuer) ||
                string.IsNullOrWhiteSpace(credentialType)) {
                transaction.Status = expectedStatus;
                _log.LogWarning("Verification issuer, credential type, or callback key is not configured.");
                return BadRequest("Configure callback authentication and credential issuers/types.");
            }

            Uri baseUrl;
            try {
                baseUrl = PublicBaseUrl();
            } catch (InvalidOperationException ex) {
                transaction.Status = expectedStatus;
                _log.LogError(ex, "Invalid public callback URL");
                return BadRequest(ex.Message);
            }
            var token = await MsalAccessTokenHandler.GetAccessToken(_config);
            if (string.IsNullOrEmpty(token.token)) {
                transaction.Status = expectedStatus;
                _log.LogError("Verified ID token acquisition failed: {Error}", token.error_description);
                return StatusCode(502, "Could not authenticate to Verified ID.");
            }

            var id = Guid.NewGuid().ToString("N");
            var payload = new PresentationRequest {
                authority = _config["VerifiedID:DidAuthority"],
                includeQRCode = true,
                registration = new Registration {
                    clientName = _config["VerifiedID:client_name"],
                    purpose = stage == "credential" ? $"Verify your {transaction.CredentialName} credential" : "Verify your TrueIdentity credential"
                },
                callback = new Callback {
                    url = new Uri(baseUrl, "identity/callback").ToString(),
                    state = id,
                    headers = new Dictionary<string, string> { ["api-key"] = key }
                },
                requestedCredentials = new List<RequestedCredential> {
                    new RequestedCredential {
                        type = credentialType,
                        acceptedIssuers = new List<string> { issuer },
                        configuration = new Configuration { validation = new Validation {
                            allowRevoked = false, validateLinkedDomain = true
                        } }
                    }
                }
            };
            lock (transaction) {
                transaction.RequestId = id;
                transaction.Status = stage + "_request_created";
                transaction.Message = "Waiting for " + stage + " credential presentation";
                _state.Add(id, transaction, TimeSpan.FromMinutes(10));
            }

            using var request = new HttpRequestMessage(HttpMethod.Post,
                (_config["VerifiedID:ApiEndpoint"] ?? "https://verifiedid.did.msidentity.com/v1.0/verifiableCredentials/").TrimEnd('/') +
                "/createPresentationRequest");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.token);
            request.Content = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");
            try {
                using var response = await _clients.CreateClient().SendAsync(request);
                if (response.StatusCode != HttpStatusCode.Created) {
                    _log.LogError("Verified ID presentation request failed with {Status}", response.StatusCode);
                    ResetRequest(transaction, id, expectedStatus);
                    return StatusCode(502, "Verified ID did not create a presentation request.");
                }
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                var root = document.RootElement;
                if (!root.TryGetProperty("url", out var link) || link.ValueKind != JsonValueKind.String ||
                    !root.TryGetProperty("expiry", out var expiry) || !expiry.TryGetInt64(out var expirySeconds) ||
                    !Uri.TryCreate(link.GetString(), UriKind.Absolute, out var deepLink)) {
                    ResetRequest(transaction, id, expectedStatus);
                    return StatusCode(502, "Verified ID returned an invalid presentation request.");
                }
                return Json(new { id, url = deepLink.ToString(), expiry = expirySeconds });
            } catch (HttpRequestException ex) {
                ResetRequest(transaction, id, expectedStatus);
                _log.LogError(ex, "Verified ID presentation request failed");
                return StatusCode(502, "Could not reach Verified ID.");
            } catch (System.Text.Json.JsonException ex) {
                ResetRequest(transaction, id, expectedStatus);
                _log.LogError(ex, "Invalid Verified ID presentation response");
                return StatusCode(502, "Verified ID returned an invalid presentation request.");
            }
        }

        private void ResetRequest(IdentityVerificationState.Transaction transaction, string id, string status) {
            lock (transaction) {
                if (transaction.RequestId == id && transaction.Status == (status == "ready" ?
                    "credential_request_created" : "identity_request_created")) {
                    transaction.Status = status;
                    transaction.Message = status == "ready" ?
                        "Present your selected credential" : "Credential verified. Continue to TrueIdentity.";
                    transaction.RequestId = null;
                    _state.Remove(id);
                }
            }
        }

        [AllowAnonymous]
        [HttpPost("callback")]
        [RequestSizeLimit(1024 * 1024)]
        public async Task<IActionResult> Callback() {
            var configuredKey = _config["IdentityVerification:CallbackApiKey"];
            var suppliedKey = Request.Headers["api-key"].ToString();
            if (string.IsNullOrEmpty(configuredKey) || string.IsNullOrEmpty(suppliedKey) ||
                !CryptographicOperations.FixedTimeEquals(
                    SHA256.HashData(Encoding.UTF8.GetBytes(configuredKey)),
                    SHA256.HashData(Encoding.UTF8.GetBytes(suppliedKey)))) return Unauthorized();

            CallbackEvent callback;
            try {
                using var reader = new System.IO.StreamReader(Request.Body);
                callback = JsonConvert.DeserializeObject<CallbackEvent>(await reader.ReadToEndAsync());
            } catch (Newtonsoft.Json.JsonException) {
                return BadRequest("Invalid callback JSON.");
            }
            if (callback == null || string.IsNullOrWhiteSpace(callback.state) ||
                !_state.TryGet(callback.state, out var transaction)) return NotFound();

            string profileValue;
            lock (transaction) {
                if (transaction.RequestId != callback.state) return Conflict("Request is no longer current.");
                if (transaction.Status is "credential_verified" or "verified" or "failed")
                    return Conflict("Transaction has already completed.");
                if (transaction.Status is not ("credential_request_created" or "identity_request_created" or
                    "credential_request_retrieved" or "identity_request_retrieved"))
                    return Conflict("Request is not awaiting a presentation.");
                var stage = transaction.Status.StartsWith("credential_", StringComparison.Ordinal) ? "credential" : "identity";
                if (callback.requestStatus == "request_retrieved") {
                    transaction.Status = stage + "_request_retrieved";
                    transaction.Message = "Waiting for wallet presentation";
                    return Ok();
                }
                if (callback.requestStatus == "presentation_error") {
                    transaction.Status = "failed";
                    transaction.Message = "Verified ID rejected the presentation";
                    return Ok();
                }
                if (callback.requestStatus != "presentation_verified") return BadRequest("Unknown callback status.");
                if (stage == "credential") {
                    if (!IdentityPresentationValidator.TryValidateAccountCredential(callback,
                        transaction.CredentialIssuer, transaction.CredentialType,
                        transaction.AccountBindingClaim, transaction.AccountBindingSource,
                        transaction.ExpectedAccountValue, transaction.ProfileValueClaim, out profileValue, out var validationError)) {
                        transaction.Status = "failed";
                        transaction.Message = validationError;
                        _log.LogWarning("Account credential validation failed: {Reason}", validationError);
                        return BadRequest(transaction.Message);
                    }
                    transaction.ProfileValue = profileValue;
                    transaction.Status = "credential_verified";
                    transaction.Message = $"{transaction.CredentialName} credential verified. Continue to TrueIdentity.";
                    return Ok();
                }
                if (string.IsNullOrWhiteSpace(transaction.ProfileValue) ||
                    !IdentityPresentationValidator.TryValidateIdentity(callback,
                        _config["IdentityVerification:IdentityIssuer"], out _)) {
                    transaction.Status = "failed";
                    transaction.Message = "TrueIdentity credential did not validate";
                    return BadRequest(transaction.Message);
                }
                transaction.Status = "verified";
                transaction.Message = "TrueIdentity credential validated. Verification complete; your External ID profile was not changed.";
                return Ok();
            }
        }

        [Authorize]
        [HttpGet("status/{id}")]
        public IActionResult Status(string id) {
            if (!_state.TryGet(id, out var transaction) ||
                transaction.UserId != UserId || transaction.RequestId != id) return NotFound();
            return Json(new { status = transaction.Status, message = transaction.Message });
        }
    }
}
