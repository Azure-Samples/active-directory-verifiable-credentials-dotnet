using System;

namespace ExternalIDVerifiedID.Models {
    public static class IdentityPresentationValidator {
        private static readonly string[] StandalonePresentationTypes = {
            "VerifiableCredential",
            "TrueIdentity",
            "VerifiedIdentity"
        };

        private static bool TryGetCredential(CallbackEvent callback, string type, string issuer,
            out ClaimsIssuer credential, out string error) {
            credential = null;
            error = "Expected one verified credential presentation.";
            if (callback?.requestStatus != "presentation_verified" ||
                callback.verifiedCredentialsData?.Length != 1 ||
                string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(issuer))
                return false;

            credential = callback.verifiedCredentialsData[0];
            if (credential?.type == null || !Array.Exists(credential.type, value => value == type)) {
                error = "The presented credential type does not match the selected credential.";
                return false;
            }
            if (credential.issuer != issuer) {
                error = "The presented credential issuer does not match the configured issuer DID.";
                return false;
            }
            if (credential.credentialState?.revocationStatus != "VALID") {
                error = "The credential does not have a valid revocation status.";
                return false;
            }
            if (!DateTimeOffset.TryParse(credential.expirationDate, out var expires) ||
                expires <= DateTimeOffset.UtcNow) {
                error = "The credential expiration is missing, invalid or in the past.";
                return false;
            }
            error = null;
            return true;
        }

        public static bool TryValidateStandalonePresentation(CallbackEvent callback, out string error) {
            error = "Expected one verified credential presentation.";
            if (callback?.requestStatus != "presentation_verified" ||
                callback.verifiedCredentialsData?.Length != 1) {
                return false;
            }

            var credential = callback.verifiedCredentialsData[0];
            if (credential.type == null ||
                !Array.Exists(credential.type, type => Array.Exists(StandalonePresentationTypes,
                    allowedType => string.Equals(type, allowedType, StringComparison.Ordinal)))) {
                error = "The presented credential does not include a supported type.";
                return false;
            }

            if (credential.credentialState?.revocationStatus != null &&
                credential.credentialState.revocationStatus != "VALID") {
                error = "The presented credential has an invalid revocation status.";
                return false;
            }
            if (!string.IsNullOrWhiteSpace(credential.expirationDate) &&
                (!DateTimeOffset.TryParse(credential.expirationDate, out var expires) ||
                 expires <= DateTimeOffset.UtcNow)) {
                error = "The presented credential is expired or has an invalid expiration date.";
                return false;
            }

            error = null;
            return true;
        }

        public static bool TryValidateFaceCheck(CallbackEvent callback, int threshold, out string error) {
            double? score = callback?.verifiedCredentialsData?.Length == 1
                ? callback.verifiedCredentialsData[0]?.faceCheck?.matchConfidenceScore : null;
            if (!score.HasValue || double.IsNaN(score.Value) || double.IsInfinity(score.Value) ||
                score.Value < threshold || score.Value > 100) {
                error = "The requested Face Check result is missing or did not meet the required confidence threshold.";
                return false;
            }
            error = null;
            return true;
        }

        public static bool TryValidateAccountCredential(CallbackEvent callback, string issuer, string type,
            string bindingClaim, string bindingSource, string expectedValue, string profileClaim, out string profileValue) {
            return TryValidateAccountCredential(callback, issuer, type, bindingClaim, bindingSource,
                expectedValue, profileClaim, out profileValue, out _);
        }

        public static bool TryValidateAccountCredential(CallbackEvent callback, string issuer, string type,
            string bindingClaim, string bindingSource, string expectedValue, string profileClaim,
            out string profileValue, out string error) {
            profileValue = null;
            if (!TryGetCredential(callback, type, issuer, out var credential, out error)) return false;
            if (string.IsNullOrWhiteSpace(expectedValue) ||
                string.IsNullOrWhiteSpace(bindingClaim) || string.IsNullOrWhiteSpace(profileClaim) ||
                (bindingSource != "email" && bindingSource != "oid")) {
                error = "Account-binding or profile-claim mappings are not configured.";
                return false;
            }
            if (credential.claims == null ||
                !credential.claims.TryGetValue(bindingClaim, out var actualValue) ||
                string.IsNullOrWhiteSpace(actualValue)) {
                error = "The credential is missing the configured account-binding claim. Check Verification:AccountBindingClaim.";
                return false;
            }
            if (!credential.claims.TryGetValue(profileClaim, out var value) || string.IsNullOrWhiteSpace(value)) {
                error = "The credential is missing the configured profile-value claim. Check Verification:ProfileValueClaim (Northstar uses loyalty; the sample contract uses membershipNo), then start a new flow.";
                return false;
            }
            if (bindingSource == "oid") {
                if (!Guid.TryParse(expectedValue, out var expectedId) ||
                    !Guid.TryParse(actualValue, out var actualId) || actualId != expectedId) {
                    error = "The credential belongs to a different account or has an invalid object ID.";
                    return false;
                }
            } else if (!string.Equals(actualValue, expectedValue, StringComparison.OrdinalIgnoreCase)) {
                error = "The credential email does not match your signed-in account. Use the credential issued for this account.";
                return false;
            }
            profileValue = value;
            error = null;
            return true;
        }

        public static bool TryValidateIdentity(CallbackEvent callback, string issuer,
            out string expiration) {
            expiration = null;
            if (!TryGetCredential(callback, "TrueIdentity", issuer, out var credential, out _) ||
                !DateTimeOffset.TryParse(credential.expirationDate, out var expires))
                return false;
            expiration = expires.ToUniversalTime().ToString("O");
            return true;
        }
    }
}
