#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json.Linq;

namespace ExternalIDVerifiedID {
    public sealed class IssuanceCredential {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Type { get; set; } = "";
        public string Manifest { get; set; } = "";
        public CredentialVerificationMapping? Verification { get; set; }
    }

    public sealed class CredentialVerificationMapping {
        public string AccountBindingClaim { get; set; } = "";
        public string AccountBindingSource { get; set; } = "email";
        public string ProfileValueClaim { get; set; } = "";

        public void Validate() {
            if (string.IsNullOrWhiteSpace(AccountBindingClaim) || string.IsNullOrWhiteSpace(ProfileValueClaim) ||
                (AccountBindingSource != "email" && AccountBindingSource != "oid")) {
                throw new InvalidOperationException("Verification mappings require AccountBindingClaim, AccountBindingSource (email or oid), and ProfileValueClaim.");
            }
        }
    }

    public sealed class IssuanceManifest {
        public IssuanceCredential Credential { get; init; } = new();
        public JObject Document { get; init; } = new();
        public string? PhotoClaimName { get; init; }
        public IReadOnlyList<ManifestClaim> Claims { get; init; } = Array.Empty<ManifestClaim>();
    }

    public sealed class IssuanceCredentialCatalog {
        private readonly IConfiguration configuration;
        private readonly IMemoryCache cache;
        private readonly IHttpClientFactory clients;

        public IssuanceCredentialCatalog(IConfiguration configuration, IMemoryCache cache, IHttpClientFactory clients) {
            this.configuration = configuration;
            this.cache = cache;
            this.clients = clients;
        }

        public IReadOnlyList<IssuanceCredential> GetCredentials() {
            var credentials = new List<IssuanceCredential> {
                new() {
                    Id = "default",
                    Name = configuration["VerifiedID:CredentialType"] ?? "",
                    Type = configuration["VerifiedID:CredentialType"] ?? "",
                    Manifest = configuration["VerifiedID:CredentialManifest"] ?? "",
                    Verification = configuration.GetSection("VerifiedID:Verification").Get<CredentialVerificationMapping>()
                        ?? new CredentialVerificationMapping {
                            AccountBindingClaim = "email", ProfileValueClaim = "membershipNo"
                        }
                }
            };
            foreach (var section in configuration.GetSection("VerifiedID:Credentials").GetChildren()) {
                var credential = section.Get<IssuanceCredential>()
                    ?? throw new InvalidOperationException("Invalid issuance credential configuration.");
                if (string.IsNullOrWhiteSpace(credential.Id) || string.IsNullOrWhiteSpace(credential.Name) ||
                    string.IsNullOrWhiteSpace(credential.Type) || string.IsNullOrWhiteSpace(credential.Manifest) ||
                    credentials.Any(c => string.Equals(c.Id, credential.Id, StringComparison.Ordinal))) {
                    throw new InvalidOperationException("Each additional issuance credential needs a unique Id, Name, Type, and Manifest.");
                }
                credentials.Add(credential);
            }
            return credentials;
        }

        public async Task<IssuanceManifest> GetManifestAsync(string? credentialId) {
            var credential = GetCredentials().SingleOrDefault(c => c.Id == (credentialId ?? "default"))
                ?? throw new ArgumentException("Unknown issuance credential.");
            if (!Uri.TryCreate(credential.Manifest, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) {
                throw new InvalidOperationException("Configure an HTTPS manifest URL for the selected credential.");
            }
            string tenantId = configuration["VerifiedID:TenantId"] ?? configuration["EEID:TenantId"] ?? "";
            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            int tenantSegment = Array.IndexOf(segments, "tenants");
            if (tenantSegment < 0 || tenantSegment + 1 >= segments.Length ||
                !string.Equals(segments[tenantSegment + 1], tenantId, StringComparison.OrdinalIgnoreCase)) {
                throw new InvalidOperationException("The selected manifest URL must belong to the configured Verified ID tenant.");
            }

            string key = $"issuance-manifest:{credential.Manifest}";
            if (!cache.TryGetValue(key, out string? json)) {
                using var response = await clients.CreateClient().GetAsync(uri);
                response.EnsureSuccessStatusCode();
                var envelope = JObject.Parse(await response.Content.ReadAsStringAsync());
                string token = (string?)envelope["token"] ?? throw new InvalidOperationException("Manifest response has no token.");
                var parts = token.Split('.');
                if (parts.Length != 3) {
                    throw new InvalidOperationException("Manifest response is not a JWT.");
                }
                string payload = parts[1].Replace('-', '+').Replace('_', '/');
                json = Encoding.UTF8.GetString(Convert.FromBase64String(payload.PadRight((payload.Length + 3) / 4 * 4, '=')));
                // Cache only the downloaded document; recheck issuer/type against current configuration on every use.
                cache.Set(key, json, TimeSpan.FromMinutes(5));
            }
            return ReadManifest(credential, JObject.Parse(json!), configuration["VerifiedID:DidAuthority"] ?? "",
                configuration["VerifiedID:PhotoClaimName"] ?? "photo");
        }

        public static IssuanceManifest ReadManifest(IssuanceCredential credential, JObject document,
            string didAuthority, string photoClaimName = "photo") {
            if (string.IsNullOrWhiteSpace(didAuthority) ||
                !string.Equals((string?)document["input"]?["issuer"], didAuthority, StringComparison.Ordinal)) {
                throw new InvalidOperationException("The selected credential manifest issuer does not match VerifiedID:DidAuthority.");
            }
            var types = (document["vcTypes"] ?? document["input"]?["credential"]?["type"]) as JArray;
            if (string.IsNullOrWhiteSpace(credential.Type) || types == null ||
                types.Any(t => t.Type != JTokenType.String) ||
                !types.Values<string>().Contains(credential.Type, StringComparer.Ordinal)) {
                throw new InvalidOperationException("The selected credential type does not match its published manifest.");
            }
            var displayClaims = document["display"]?["claims"] as JObject
                ?? throw new InvalidOperationException("The selected manifest has no display claims.");
            var claims = displayClaims.Properties().Select(p => new ManifestClaim {
                name = p.Name.StartsWith("vc.credentialSubject.", StringComparison.Ordinal)
                    ? p.Name.Substring("vc.credentialSubject.".Length) : p.Name,
                type = (string?)p.Value["type"] ?? "",
                label = (string?)p.Value["label"] ?? p.Name
            }).ToArray();
            string? photo = claims.FirstOrDefault(c =>
                string.Equals(c.name, photoClaimName, StringComparison.OrdinalIgnoreCase) &&
                (c.type.StartsWith("image/jpeg", StringComparison.OrdinalIgnoreCase) ||
                 c.type.StartsWith("image/jpg", StringComparison.OrdinalIgnoreCase)))?.name;
            return new IssuanceManifest { Credential = credential, Document = document, Claims = claims, PhotoClaimName = photo };
        }
    }
}
