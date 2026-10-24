using System;
using System.IO;
using System.Net.Http;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using ExternalIDVerifiedID;
using ExternalIDVerifiedID.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ExternalIDVerifiedID.Tests {
    public class IdentityVerificationControllerTests {
        private const string Oid = "40af969b-2d48-4d4c-a3ac-cb95b6938e9a";

        private static (IdentityVerificationController controller, IdentityVerificationState state)
            Create(System.Collections.Generic.Dictionary<string, string> overrides = null) {
            var state = new IdentityVerificationState(new MemoryCache(new MemoryCacheOptions()));
            var config = new ConfigurationBuilder().AddInMemoryCollection(
                new System.Collections.Generic.Dictionary<string, string> {
                    ["IdentityVerification:CallbackApiKey"] = "test-only-key",
                    ["IdentityVerification:PublicBaseUrl"] = "https://example.com/",
                    ["IdentityVerification:ProviderUrl"] = "https://trueidentity.example/enroll",
                    ["VerifiedID:DidAuthority"] = "did:web:loyalty",
                    ["VerifiedID:CredentialType"] = "NorthstarLoyalty",
                    ["IdentityVerification:IdentityIssuer"] = "did:web:did.woodgrovedemo.com"
                }).Build();
            if (overrides != null) {
                foreach (var pair in overrides) config[pair.Key] = pair.Value;
            }
            var clients = new TestHttpClientFactory();
            var controller = new IdentityVerificationController(state, config,
                clients, NullLogger<IdentityVerificationController>.Instance,
                new IssuanceCredentialCatalog(config, new MemoryCache(new MemoryCacheOptions()), clients));
            controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
            controller.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(new[] {
                new Claim("oid", Oid), new Claim("email", "member@example.com")
            }, "test"));
            return (controller, state);
        }

        private static async Task<IActionResult> Callback(IdentityVerificationController controller,
            string json, string key = "test-only-key") {
            controller.Request.Headers["api-key"] = key;
            controller.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));
            return await controller.Callback();
        }

        [Fact]
        public void MissingEmailShowsConfigurationGuidanceInsteadOfStartingFlow() {
            var (controller, _) = Create();
            controller.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(new[] {
                new Claim("oid", Oid)
            }, "test"));
            var result = Assert.IsType<ViewResult>(controller.Start());
            Assert.Equal("Index", result.ViewName);
            Assert.Equal(400, controller.Response.StatusCode);
            var missing = Assert.IsType<System.Collections.Generic.List<string>>(
                result.ViewData["MissingAccountClaims"]);
            Assert.Equal(new[] { "email (or emails)" }, missing);
            Assert.Null(result.ViewData["Transaction"]);
        }

        [Fact]
        public void MissingObjectIdIsDistinguishedFromMissingEmail() {
            var (controller, _) = Create();
            controller.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(new[] {
                new Claim("email", "member@example.com")
            }, "test"));
            var result = Assert.IsType<ViewResult>(controller.Index(null));
            var missing = Assert.IsType<System.Collections.Generic.List<string>>(
                result.ViewData["MissingAccountClaims"]);
            Assert.Equal(new[] { "oid (a valid External ID object ID)" }, missing);
        }

        [Fact]
        public void EmailsArrayAllowsStartingVerification() {
            var (controller, _) = Create();
            controller.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(new[] {
                new Claim("oid", Oid), new Claim("emails", "[\"member@example.com\"]")
            }, "test"));
            Assert.IsType<RedirectToActionResult>(controller.Start());
        }

        [Fact]
        public void ProviderCannotStartBeforeLoyaltyVerification() {
            var (controller, state) = Create();
            var id = Guid.NewGuid().ToString("N");
            state.Add(id, new IdentityVerificationState.Transaction { FlowId = id, UserId = Oid, Email = "member@example.com" });
            Assert.IsType<ConflictObjectResult>(controller.Provider(id));
        }

        [Fact]
        public async Task IdentityPresentationCannotStartBeforeLoyaltyVerification() {
            var (controller, state) = Create();
            var id = Guid.NewGuid().ToString("N");
            state.Add(id, new IdentityVerificationState.Transaction { FlowId = id, UserId = Oid, Email = "member@example.com" });
            Assert.IsType<ConflictObjectResult>(await controller.Presentation(id, "identity"));
        }

        [Fact]
        public void ProviderReturnDoesNotVerifyIdentity() {
            var (controller, state) = Create();
            var id = Guid.NewGuid().ToString("N");
            var transaction = new IdentityVerificationState.Transaction {
                FlowId = id, UserId = Oid, Email = "member@example.com", Status = "credential_verified", ProfileValue = "123"
            };
            state.Add(id, transaction);
            Assert.IsType<RedirectToActionResult>(controller.Return(id));
            Assert.Equal("credential_verified", transaction.Status);
            Assert.True(transaction.ProviderReturnSeen);
        }

        [Fact]
        public void ProviderReturnUrlUsesPathStateAndNeverMarksIdentityVerified() {
            var (controller, state) = Create();
            var flow = Guid.NewGuid().ToString("N");
            var transaction = new IdentityVerificationState.Transaction {
                FlowId = flow, UserId = Oid, Email = "member@example.com", Status = "credential_verified", ProfileValue = "123"
            };
            state.Add(flow, transaction);
            var redirect = Assert.IsType<RedirectResult>(controller.Provider(flow));
            var provider = new Uri(redirect.Url);
            Assert.Equal("https://trueidentity.example", provider.GetLeftPart(UriPartial.Authority));
            Assert.Equal($"https://example.com/identity/return/{flow}",
                System.Web.HttpUtility.ParseQueryString(provider.Query)["returnUrl"]);
            Assert.Equal("credential_verified", transaction.Status);
        }

        [Fact]
        public void ProviderReturnCannotUseAnotherAccountState() {
            var (controller, state) = Create();
            var id = Guid.NewGuid().ToString("N");
            state.Add(id, new IdentityVerificationState.Transaction {
                FlowId = id, UserId = Guid.NewGuid().ToString(), Email = "other@example.com", Status = "credential_verified"
            });
            Assert.IsType<NotFoundResult>(controller.Return(id));
        }

        [Fact]
        public void RequestStateCannotBeUsedAsAFlowId() {
            var (controller, state) = Create();
            var flow = Guid.NewGuid().ToString("N");
            var requestId = Guid.NewGuid().ToString("N");
            state.Add(requestId, new IdentityVerificationState.Transaction {
                FlowId = flow, UserId = Oid, Email = "member@example.com",
                RequestId = requestId, Status = "credential_verified", ProfileValue = "123"
            });
            Assert.IsType<NotFoundResult>(controller.Return(requestId));
        }

        [Fact]
        public async Task CallbackRejectsWrongKey() {
            var (controller, _) = Create();
            Assert.IsType<UnauthorizedResult>(await Callback(controller, "{}", "wrong"));
        }

        [Fact]
        public async Task CallbackRejectsUnknownState() {
            var (controller, _) = Create();
            Assert.IsType<NotFoundResult>(await Callback(controller,
                "{\"state\":\"missing\",\"requestStatus\":\"presentation_verified\"}"));
        }

        [Fact]
        public async Task TrueIdentityCallbackCannotSkipLoyaltyStep() {
            var (controller, state) = Create();
            var id = Guid.NewGuid().ToString("N");
            var transaction = new IdentityVerificationState.Transaction {
                UserId = Oid, Email = "member@example.com", RequestId = id, Status = "credential_request_created"
            };
            state.Add(id, transaction);
            var json = $"{{\"state\":\"{id}\",\"requestStatus\":\"presentation_verified\"," +
                "\"verifiedCredentialsData\":[{\"type\":[\"TrueIdentity\"],\"issuer\":\"did:web:did.woodgrovedemo.com\"," +
                "\"expirationDate\":\"2099-01-01T00:00:00Z\",\"credentialState\":{\"revocationStatus\":\"VALID\"}}]}";
            Assert.IsType<BadRequestObjectResult>(await Callback(controller, json));
            Assert.Equal("failed", transaction.Status);
        }

        [Fact]
        public async Task InvalidTrueIdentityCallbackDoesNotCompleteVerification() {
            var (controller, state) = Create();
            var id = Guid.NewGuid().ToString("N");
            var transaction = new IdentityVerificationState.Transaction {
                UserId = Oid, Email = "member@example.com", RequestId = id,
                Status = "identity_request_created", ProfileValue = "123456"
            };
            state.Add(id, transaction);
            var json = $"{{\"state\":\"{id}\",\"requestStatus\":\"presentation_verified\"," +
                "\"verifiedCredentialsData\":[{\"type\":[\"TrueIdentity\"],\"issuer\":\"did:web:attacker\"," +
                "\"expirationDate\":\"2099-01-01T00:00:00Z\",\"credentialState\":{\"revocationStatus\":\"VALID\"}}]}";
            Assert.IsType<BadRequestObjectResult>(await Callback(controller, json));
            Assert.Equal("failed", transaction.Status);
        }

        [Fact]
        public async Task WalletScanDoesNotMarkLoyaltyVerified() {
            var (controller, state) = Create();
            var id = Guid.NewGuid().ToString("N");
            var transaction = new IdentityVerificationState.Transaction {
                UserId = Oid, Email = "member@example.com", RequestId = id, Status = "credential_request_created"
            };
            state.Add(id, transaction);
            Assert.IsType<OkResult>(await Callback(controller,
                $"{{\"state\":\"{id}\",\"requestStatus\":\"request_retrieved\"}}"));
            Assert.Equal("credential_request_retrieved", transaction.Status);
        }

        [Fact]
        public async Task LoyaltyCallbackUnlocksProviderOnlyAfterValidation() {
            var (controller, state) = Create();
            var flow = Guid.NewGuid().ToString("N");
            var id = Guid.NewGuid().ToString("N");
            var transaction = new IdentityVerificationState.Transaction {
                FlowId = flow, UserId = Oid, Email = "member@example.com",
                RequestId = id, Status = "credential_request_created",
                CredentialType = "NorthstarLoyalty", CredentialIssuer = "did:web:loyalty",
                AccountBindingSource = "email", AccountBindingClaim = "email",
                ExpectedAccountValue = "member@example.com", ProfileValueClaim = "membershipNo"
            };
            state.Add(flow, transaction);
            state.Add(id, transaction);
            var json = $"{{\"state\":\"{id}\",\"requestStatus\":\"presentation_verified\"," +
                "\"verifiedCredentialsData\":[{\"type\":[\"NorthstarLoyalty\"],\"issuer\":\"did:web:loyalty\"," +
                "\"expirationDate\":\"2099-01-01T00:00:00Z\",\"credentialState\":{\"revocationStatus\":\"VALID\"}," +
                "\"claims\":{\"email\":\"member@example.com\",\"membershipNo\":\"123456\"}}]}";
            Assert.IsType<OkResult>(await Callback(controller, json));
            Assert.Equal("credential_verified", transaction.Status);
            Assert.Equal("123456", transaction.ProfileValue);
            Assert.IsType<RedirectResult>(controller.Provider(flow));
        }

        private static System.Collections.Generic.Dictionary<string, string> EmployeeConfig() => new() {
            ["VerifiedID:Credentials:0:Id"] = "employee",
            ["VerifiedID:Credentials:0:Name"] = "Employee card",
            ["VerifiedID:Credentials:0:Type"] = "Employee",
            ["VerifiedID:Credentials:0:Manifest"] = "https://example.com/manifest",
            ["VerifiedID:Credentials:0:Verification:AccountBindingClaim"] = "accountId",
            ["VerifiedID:Credentials:0:Verification:AccountBindingSource"] = "oid",
            ["VerifiedID:Credentials:0:Verification:ProfileValueClaim"] = "employeeNumber"
        };

        [Theory]
        [InlineData("loyalty", true)]
        [InlineData("membershipNo", false)]
        public async Task NorthstarCallbackUsesExplicitMappingAndReportsMissingProfileClaim(string mapping, bool succeeds) {
            var (controller, state) = Create(new() {
                ["VerifiedID:Verification:AccountBindingClaim"] = "email",
                ["VerifiedID:Verification:AccountBindingSource"] = "email",
                ["VerifiedID:Verification:ProfileValueClaim"] = mapping
            });
            var redirect = Assert.IsType<RedirectToActionResult>(controller.Start());
            var flow = (string)redirect.RouteValues["flow"];
            Assert.True(state.TryGet(flow, out var transaction));
            transaction.RequestId = Guid.NewGuid().ToString("N");
            transaction.Status = "credential_request_created";
            state.Add(transaction.RequestId, transaction);
            var json = $"{{\"state\":\"{transaction.RequestId}\",\"requestStatus\":\"presentation_verified\"," +
                "\"verifiedCredentialsData\":[{\"type\":[\"NorthstarLoyalty\"],\"issuer\":\"did:web:loyalty\"," +
                "\"expirationDate\":\"2099-01-01T00:00:00Z\",\"credentialState\":{\"revocationStatus\":\"VALID\"}," +
                "\"claims\":{\"email\":\"member@example.com\",\"loyalty\":\"123456\"}}]}";
            var result = await Callback(controller, json);
            if (succeeds) {
                Assert.IsType<OkResult>(result);
                Assert.Equal("credential_verified", transaction.Status);
                Assert.Equal("123456", transaction.ProfileValue);
                Assert.IsType<RedirectResult>(controller.Provider(flow));
            } else {
                Assert.IsType<BadRequestObjectResult>(result);
                Assert.Equal("failed", transaction.Status);
                Assert.Contains("Verification:ProfileValueClaim", transaction.Message);
                Assert.Null(transaction.ProfileValue);
                Assert.IsType<ConflictObjectResult>(controller.Provider(flow));
            }
        }

        [Fact]
        public void EmployeeStartCapturesSelectedTypeMappingsAndOidWithoutRequiringEmail() {
            var (controller, state) = Create(EmployeeConfig());
            controller.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("oid", Oid) }, "test"));
            var redirect = Assert.IsType<RedirectToActionResult>(controller.Start("employee"));
            Assert.True(state.TryGet((string)redirect.RouteValues["flow"], out var transaction));
            Assert.Equal("Employee", transaction.CredentialType);
            Assert.Equal("Employee card", transaction.CredentialName);
            Assert.Equal("did:web:loyalty", transaction.CredentialIssuer);
            Assert.Equal("oid", transaction.AccountBindingSource);
            Assert.Equal(Oid, transaction.ExpectedAccountValue);
            Assert.Equal("accountId", transaction.AccountBindingClaim);
            Assert.Equal("employeeNumber", transaction.ProfileValueClaim);
        }

        [Theory]
        [InlineData("employeeNumber", true)]
        [InlineData("membershipNo", false)]
        public async Task SelectedEmployeeCallbackUsesSnapshotInsteadOfDefaultLoyalty(string profileClaim, bool succeeds) {
            var (controller, state) = Create(EmployeeConfig());
            var redirect = Assert.IsType<RedirectToActionResult>(controller.Start("employee"));
            string flow = (string)redirect.RouteValues["flow"];
            Assert.True(state.TryGet(flow, out var transaction));
            var id = Guid.NewGuid().ToString("N");
            transaction.RequestId = id;
            transaction.Status = "credential_request_created";
            state.Add(id, transaction);
            var json = $"{{\"state\":\"{id}\",\"requestStatus\":\"presentation_verified\"," +
                "\"verifiedCredentialsData\":[{\"type\":[\"Employee\"],\"issuer\":\"did:web:loyalty\"," +
                "\"expirationDate\":\"2099-01-01T00:00:00Z\",\"credentialState\":{\"revocationStatus\":\"VALID\"}," +
                $"\"claims\":{{\"accountId\":\"{Oid}\",\"{profileClaim}\":\"E42\"}}}}]}}";
            var result = await Callback(controller, json);
            if (succeeds) {
                Assert.IsType<OkResult>(result);
                Assert.Equal("credential_verified", transaction.Status);
                Assert.Equal("E42", transaction.ProfileValue);
                Assert.IsType<RedirectResult>(controller.Provider(flow));
            } else {
                Assert.IsType<BadRequestObjectResult>(result);
                Assert.Equal("failed", transaction.Status);
                Assert.Null(transaction.ProfileValue);
                Assert.IsType<ConflictObjectResult>(controller.Provider(flow));
            }
        }

        [Theory]
        [InlineData("missing")]
        [InlineData("employee")]
        public void UnknownOrUnmappedCredentialCannotStart(string credentialId) {
            var config = EmployeeConfig();
            foreach (var key in new[] { "AccountBindingClaim", "AccountBindingSource", "ProfileValueClaim" })
                config.Remove("VerifiedID:Credentials:0:Verification:" + key);
            var (controller, _) = Create(config);
            Assert.IsType<BadRequestObjectResult>(controller.Start(credentialId));
        }

        [Theory]
        [InlineData("AccountBindingSource", "name")]
        [InlineData("AccountBindingClaim", "")]
        [InlineData("ProfileValueClaim", "")]
        public void InvalidMappingCannotStart(string key, string value) {
            var config = EmployeeConfig();
            config["VerifiedID:Credentials:0:Verification:" + key] = value;
            var (controller, _) = Create(config);
            Assert.IsType<BadRequestObjectResult>(controller.Start("employee"));
        }

        [Fact]
        public void DefaultSelectionPreservesEmailAndMembershipMappings() {
            var (controller, state) = Create();
            var redirect = Assert.IsType<RedirectToActionResult>(controller.Start());
            Assert.True(state.TryGet((string)redirect.RouteValues["flow"], out var transaction));
            Assert.Equal("NorthstarLoyalty", transaction.CredentialType);
            Assert.Equal("email", transaction.AccountBindingClaim);
            Assert.Equal("member@example.com", transaction.ExpectedAccountValue);
            Assert.Equal("membershipNo", transaction.ProfileValueClaim);
        }

        [Fact]
        public void VerificationPageUsesSelectedMappingAndRequiresNoEmailForOidBinding() {
            var (controller, _) = Create(EmployeeConfig());
            controller.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("oid", Oid) }, "test"));
            var view = Assert.IsType<ViewResult>(controller.Index(null, "employee"));
            Assert.Equal("employee", view.ViewData["CredentialId"]);
            Assert.Empty(Assert.IsType<System.Collections.Generic.List<string>>(view.ViewData["MissingAccountClaims"]));
        }

        [Fact]
        public async Task MissingCallbackKeyBlocksIdentityRequestBeforeAnyNetworkCall() {
            var (controller, state) = Create(new() { ["IdentityVerification:CallbackApiKey"] = "" });
            var flow = Guid.NewGuid().ToString("N");
            state.Add(flow, new IdentityVerificationState.Transaction {
                FlowId = flow, UserId = Oid, Status = "credential_verified", ProfileValue = "E42"
            });
            Assert.IsType<BadRequestObjectResult>(await controller.Presentation(flow, "identity"));
        }

        [Fact]
        public void ConfigurationChangesDoNotChangeAnExistingFlowSelectionOrMappings() {
            var config = new ConfigurationBuilder().AddInMemoryCollection(EmployeeConfig()).Build();
            config["VerifiedID:DidAuthority"] = "did:web:employee.example";
            var state = new IdentityVerificationState(new MemoryCache(new MemoryCacheOptions()));
            var clients = new TestHttpClientFactory();
            var controller = new IdentityVerificationController(state, config, clients,
                NullLogger<IdentityVerificationController>.Instance,
                new IssuanceCredentialCatalog(config, new MemoryCache(new MemoryCacheOptions()), clients));
            controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
            controller.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("oid", Oid) }, "test"));
            var redirect = Assert.IsType<RedirectToActionResult>(controller.Start("employee"));
            Assert.True(state.TryGet((string)redirect.RouteValues["flow"], out var transaction));
            config["VerifiedID:Credentials:0:Type"] = "Different";
            config["VerifiedID:Credentials:0:Verification:ProfileValueClaim"] = "differentClaim";
            config["VerifiedID:DidAuthority"] = "did:web:different.example";
            Assert.Equal("Employee", transaction.CredentialType);
            Assert.Equal("employeeNumber", transaction.ProfileValueClaim);
            Assert.Equal("did:web:employee.example", transaction.CredentialIssuer);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ValidTrueIdentityCompletesWithoutGraphConfigurationOrOutboundCalls(bool providerReturned) {
            var (controller, state) = Create();
            var id = Guid.NewGuid().ToString("N");
            var transaction = new IdentityVerificationState.Transaction {
                UserId = Oid, RequestId = id, Status = "identity_request_created",
                ProfileValue = "123456", ProviderReturnSeen = providerReturned
            };
            state.Add(id, transaction);
            var json = $"{{\"state\":\"{id}\",\"requestStatus\":\"presentation_verified\"," +
                "\"verifiedCredentialsData\":[{\"type\":[\"VerifiableCredential\",\"TrueIdentity\"]," +
                "\"issuer\":\"did:web:did.woodgrovedemo.com\"," +
                "\"expirationDate\":\"2099-01-01T00:00:00Z\",\"credentialState\":{\"revocationStatus\":\"VALID\"}}]}";
            Assert.IsType<OkResult>(await Callback(controller, json));
            Assert.Equal("verified", transaction.Status);
            Assert.Contains("profile was not changed", transaction.Message);
            Assert.IsType<ConflictObjectResult>(await Callback(controller, json));
            Assert.Equal("verified", transaction.Status);
        }

        [Fact]
        public async Task ValidTrueIdentityCannotCompleteWithoutValidatedAccountCredential() {
            var (controller, state) = Create();
            var id = Guid.NewGuid().ToString("N");
            var transaction = new IdentityVerificationState.Transaction {
                UserId = Oid, RequestId = id, Status = "identity_request_created"
            };
            state.Add(id, transaction);
            var json = $"{{\"state\":\"{id}\",\"requestStatus\":\"presentation_verified\"," +
                "\"verifiedCredentialsData\":[{\"type\":[\"TrueIdentity\"]," +
                "\"issuer\":\"did:web:did.woodgrovedemo.com\"," +
                "\"expirationDate\":\"2099-01-01T00:00:00Z\",\"credentialState\":{\"revocationStatus\":\"VALID\"}}]}";
            Assert.IsType<BadRequestObjectResult>(await Callback(controller, json));
            Assert.Equal("failed", transaction.Status);
        }

        private sealed class TestHttpClientFactory : IHttpClientFactory {
            public HttpClient CreateClient(string name) =>
                throw new InvalidOperationException("Unexpected outbound HTTP call.");
        }
    }
}
