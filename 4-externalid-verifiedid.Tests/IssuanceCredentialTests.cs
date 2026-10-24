using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ExternalIDVerifiedID;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace ExternalIDVerifiedID.Tests {
    public class IssuanceCredentialTests {
        private const string Did = "did:web:issuer.example";
        private const string Tenant = "11111111-1111-1111-1111-111111111111";
        private const string Oid = "22222222-2222-2222-2222-222222222222";
        private static readonly string ManifestUrl = $"https://verifiedid.did.msidentity.com/v1.0/tenants/{Tenant}/verifiableCredentials/contracts/loyalty/manifest";

        private static JObject Manifest(string type = "Loyalty", string issuer = Did, bool photo = true) {
            var claims = new JObject {
                ["vc.credentialSubject.email"] = new JObject { ["type"] = "String", ["label"] = "Email" },
                ["vc.credentialSubject.membershipQRcode"] = new JObject { ["type"] = "image/jpg;base64url" }
            };
            if (photo) {
                claims["vc.credentialSubject.photo"] = new JObject { ["type"] = "image/jpeg;base64url", ["label"] = "Photo" };
            }
            return new JObject {
                ["vcTypes"] = new JArray("VerifiableCredential", type),
                ["input"] = new JObject {
                    ["issuer"] = issuer
                },
                ["display"] = new JObject {
                    ["claims"] = claims,
                    ["card"] = new JObject { ["title"] = type, ["issuedBy"] = "Example" }
                }
            };
        }

        private static IConfiguration Config(Dictionary<string, string> overrides = null) {
            var values = new Dictionary<string, string> {
                ["EEID:TenantId"] = Tenant,
                ["VerifiedID:DidAuthority"] = Did,
                ["VerifiedID:CredentialType"] = "Loyalty",
                ["VerifiedID:CredentialManifest"] = ManifestUrl,
                ["VerifiedID:Credentials:0:Id"] = "other",
                ["VerifiedID:Credentials:0:Name"] = "Another credential",
                ["VerifiedID:Credentials:0:Type"] = "Other",
                ["VerifiedID:Credentials:0:Manifest"] = ManifestUrl.Replace("loyalty", "other")
            };
            if (overrides != null) {
                foreach (var pair in overrides) values[pair.Key] = pair.Value;
            }
            return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        }

        private static VerifiedIDController Controller(IConfiguration config, IMemoryCache cache, IHttpClientFactory clients) {
            var controller = new VerifiedIDController(config, cache, NullLogger<VerifiedIDController>.Instance, clients);
            controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
            controller.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(new[] {
                new Claim("oid", Oid), new Claim("email", "member@example.com"), new Claim("name", "Member")
            }, "test"));
            return controller;
        }

        [Fact]
        public void PublishedManifestUsesTopLevelVcTypes() {
            var manifest = Manifest();
            Assert.Null(manifest["input"]["credential"]);
            var result = IssuanceCredentialCatalog.ReadManifest(
                new IssuanceCredential { Type = "Loyalty" }, manifest, Did);
            Assert.Equal("Loyalty", result.Credential.Type);
            Assert.Equal("photo", result.PhotoClaimName);
        }

        [Fact]
        public void LegacyNestedCredentialTypesRemainSupported() {
            var manifest = Manifest();
            manifest["input"]["credential"] = new JObject { ["type"] = manifest["vcTypes"].DeepClone() };
            manifest.Remove("vcTypes");
            var result = IssuanceCredentialCatalog.ReadManifest(
                new IssuanceCredential { Type = "Loyalty" }, manifest, Did);
            Assert.Equal("Loyalty", result.Credential.Type);
        }

        [Theory]
        [InlineData("null")]
        [InlineData("\"Loyalty\"")]
        [InlineData("{}")]
        [InlineData("[\"VerifiableCredential\", 123]")]
        [InlineData("[\"VerifiableCredential\", \"Other\"]")]
        [InlineData("[\"VerifiableCredential\", \"loyalty\"]")]
        public void InvalidPublishedTypesDoNotFallBackToNestedTypes(string typesJson) {
            var manifest = Manifest();
            manifest["vcTypes"] = JToken.Parse(typesJson);
            manifest["input"]["credential"] = new JObject {
                ["type"] = new JArray("VerifiableCredential", "Loyalty")
            };
            Assert.Throws<InvalidOperationException>(() => IssuanceCredentialCatalog.ReadManifest(
                new IssuanceCredential { Type = "Loyalty" }, manifest, Did));
        }

        [Fact]
        public void MissingCredentialTypesAreRejected() {
            var manifest = Manifest();
            manifest.Remove("vcTypes");
            Assert.Throws<InvalidOperationException>(() => IssuanceCredentialCatalog.ReadManifest(
                new IssuanceCredential { Type = "Loyalty" }, manifest, Did));
        }

        [Fact]
        public void PhotoIsNotInferredFromQrCodeImage() {
            var result = IssuanceCredentialCatalog.ReadManifest(new IssuanceCredential { Type = "Loyalty" },
                Manifest(photo: false), Did);
            Assert.Null(result.PhotoClaimName);
        }

        [Fact]
        public void PhotoRequiresNamedImageClaimNotStringOrLabel() {
            var manifest = Manifest();
            manifest["display"]["claims"]["vc.credentialSubject.photo"]["type"] = "String";
            var result = IssuanceCredentialCatalog.ReadManifest(new IssuanceCredential { Type = "Loyalty" }, manifest, Did);
            Assert.Null(result.PhotoClaimName);
        }

        [Fact]
        public void ConfiguredPhotoNameIsUsed() {
            var manifest = Manifest(photo: false);
            manifest["display"]["claims"]["vc.credentialSubject.portrait"] = new JObject { ["type"] = "image/jpg;base64url" };
            var result = IssuanceCredentialCatalog.ReadManifest(new IssuanceCredential { Type = "Loyalty" }, manifest, Did, "portrait");
            Assert.Equal("portrait", result.PhotoClaimName);
        }

        [Theory]
        [InlineData("Other", Did)]
        [InlineData("Loyalty", "did:web:wrong.example")]
        public void WrongIssuerOrTypeIsRejected(string type, string issuer) {
            Assert.Throws<InvalidOperationException>(() => IssuanceCredentialCatalog.ReadManifest(
                new IssuanceCredential { Type = "Loyalty" }, Manifest(type, issuer), Did));
        }

        [Fact]
        public void DuplicateCredentialIdsAreRejected() {
            var catalog = new IssuanceCredentialCatalog(Config(new Dictionary<string, string> {
                ["VerifiedID:Credentials:0:Id"] = "default"
            }), new MemoryCache(new MemoryCacheOptions()), new ManifestClient());
            Assert.Throws<InvalidOperationException>(() => catalog.GetCredentials());
        }

        [Fact]
        public async Task SelectionCannotSupplyArbitraryManifestUrl() {
            var client = new ManifestClient();
            var catalog = new IssuanceCredentialCatalog(Config(), new MemoryCache(new MemoryCacheOptions()), client);
            await Assert.ThrowsAsync<ArgumentException>(() => catalog.GetManifestAsync("https://untrusted.example/manifest"));
            Assert.Equal(0, client.Requests);
        }

        [Fact]
        public async Task WrongTenantIsRejectedBeforeDownload() {
            var client = new ManifestClient();
            var catalog = new IssuanceCredentialCatalog(Config(new Dictionary<string, string> {
                ["VerifiedID:CredentialManifest"] = ManifestUrl.Replace(Tenant, Oid)
            }), new MemoryCache(new MemoryCacheOptions()), client);
            await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.GetManifestAsync("default"));
            Assert.Equal(0, client.Requests);
        }

        [Fact]
        public async Task SelectionCachesAreIsolatedAndDefaultRemainsAvailable() {
            var client = new ManifestClient();
            var catalog = new IssuanceCredentialCatalog(Config(), new MemoryCache(new MemoryCacheOptions()), client);
            Assert.Equal(2, catalog.GetCredentials().Count);
            Assert.Equal("photo", (await catalog.GetManifestAsync(null)).PhotoClaimName);
            Assert.Null((await catalog.GetManifestAsync("other")).PhotoClaimName);
            Assert.Equal("Loyalty", (await catalog.GetManifestAsync("default")).Credential.Type);
            Assert.Equal(2, client.Requests);
        }

        [Fact]
        public async Task NoPhotoManifestRejectsUploadAndSelfieOnServer() {
            var controller = Controller(Config(), new MemoryCache(new MemoryCacheOptions()), new ManifestClient());
            Assert.IsType<BadRequestObjectResult>(await controller.SetUserPhoto("other"));
            Assert.IsType<BadRequestObjectResult>(await controller.SelfieRequest("other"));
        }

        [Fact]
        public async Task UploadedPhotoIsScopedToUserAndCredential() {
            var cache = new MemoryCache(new MemoryCacheOptions());
            var controller = Controller(Config(), cache, new ManifestClient());
            controller.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("data:image/jpeg;base64,/9j/"));
            Assert.Equal(201, Assert.IsType<ContentResult>(await controller.SetUserPhoto()).StatusCode);
            Assert.Equal("/9j/", cache.Get<string>($"issuance-photo:{Oid}:default"));
            Assert.Null(cache.Get<string>($"issuance-photo:{Oid}:other"));
        }

        [Fact]
        public async Task NonJpegUploadIsRejected() {
            var controller = Controller(Config(), new MemoryCache(new MemoryCacheOptions()), new ManifestClient());
            controller.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("data:image/jpeg;base64,aGVsbG8="));
            Assert.IsType<BadRequestObjectResult>(await controller.SetUserPhoto());
        }

        [Fact]
        public async Task ManifestEndpointUsesSelectedDocumentAndPhotoMetadata() {
            var controller = Controller(Config(), new MemoryCache(new MemoryCacheOptions()), new ManifestClient());
            var result = Assert.IsType<ContentResult>(await controller.getManifest("other"));
            var json = JObject.Parse(result.Content);
            Assert.Equal("Other", (string)json["manifest"]["display"]["card"]["title"]);
            Assert.Equal(JTokenType.Null, json["photoClaimName"].Type);
        }

        [Fact]
        public async Task ManifestDownloadFailureIsNotSuccess() {
            var controller = Controller(Config(), new MemoryCache(new MemoryCacheOptions()), new ManifestClient { Fail = true });
            Assert.IsType<BadRequestObjectResult>(await controller.getManifest());
        }

        private sealed class ManifestClient : HttpMessageHandler, IHttpClientFactory {
            public int Requests { get; private set; }
            public bool Fail { get; set; }
            public HttpClient CreateClient(string name) => new HttpClient(this, false);
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
                Requests++;
                var manifest = request.RequestUri.AbsolutePath.Contains("/other/")
                    ? Manifest("Other", photo: false) : Manifest();
                string payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(manifest.ToString()))
                    .TrimEnd('=').Replace('+', '-').Replace('/', '_');
                return Task.FromResult(new HttpResponseMessage(Fail ? HttpStatusCode.BadGateway : HttpStatusCode.OK) {
                    Content = new StringContent(new JObject { ["token"] = $"header.{payload}.signature" }.ToString())
                });
            }
        }
    }
}
