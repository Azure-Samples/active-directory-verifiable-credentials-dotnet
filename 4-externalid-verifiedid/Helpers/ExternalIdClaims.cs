using System;
using System.Linq;
using System.Collections.Generic;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;

namespace ExternalIDVerifiedID {
    public static class ExternalIdClaims {
        public static string Email(ClaimsPrincipal user) {
            var email = user.FindAll("email").Concat(user.FindAll(ClaimTypes.Email))
                .Select(claim => claim.Value).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
            if (email != null) return email;

            foreach (var claim in user.FindAll("emails")) {
                if (string.IsNullOrWhiteSpace(claim.Value)) continue;
                if (!claim.Value.TrimStart().StartsWith("[", StringComparison.Ordinal))
                    return claim.Value;
                try {
                    using var array = JsonDocument.Parse(claim.Value);
                    foreach (var item in array.RootElement.EnumerateArray()) {
                        if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                            return item.GetString();
                    }
                } catch (JsonException ex) {
                    throw new InvalidOperationException("The External ID emails claim is malformed.", ex);
                }
            }
            return null;
        }

        public static string DisplayName(ClaimsPrincipal user) =>
            user.FindFirst("name")?.Value ?? user.Identity?.Name ?? "Signed-in user";

        public static Dictionary<string, string> IssuanceClaims(ClaimsPrincipal user, bool requireNames) {
            string Read(params string[] types) => types.SelectMany(user.FindAll)
                .Select(claim => claim.Value).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
            string givenName = Read("givenName", "given_name", ClaimTypes.GivenName);
            string surname = Read("surname", "family_name", ClaimTypes.Surname);
            if (requireNames && (givenName == null || surname == null)) {
                throw new InvalidOperationException(
                    "The credential requires givenName and surname. In the External ID app registration, open Token configuration > Add optional claim > ID, add given_name and family_name, and save. Ensure your user profile has both names, then sign out and sign in again. Confirm the populated claims on Account claims; refreshing alone does not refresh your token.");
            }
            string membershipNo = RandomNumberGenerator.GetInt32(100000, 1000000).ToString();
            var claims = new Dictionary<string, string> {
                ["email"] = Email(user),
                ["name"] = Read("name") ?? user.Identity?.Name,
                ["displayName"] = Read("displayName", "name") ?? user.Identity?.Name,
                ["memberStatus"] = "Diamond",
                ["membershipNo"] = membershipNo,
                ["loyalty"] = membershipNo
            };
            if (givenName != null) claims["givenName"] = givenName;
            if (surname != null) claims["surname"] = surname;
            return claims;
        }
    }
}
