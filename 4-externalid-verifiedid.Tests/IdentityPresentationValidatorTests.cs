using System;
using System.Collections.Generic;
using ExternalIDVerifiedID;
using ExternalIDVerifiedID.Models;
using Xunit;

namespace ExternalIDVerifiedID.Tests {
    public class IdentityPresentationValidatorTests {
        private static CallbackEvent Loyalty() => new CallbackEvent {
            requestStatus = "presentation_verified",
            verifiedCredentialsData = new[] { new ClaimsIssuer {
                type = new[] { "NorthstarLoyalty" }, issuer = "did:web:loyalty",
                credentialState = new CredentialState { revocationStatus = "VALID" },
                expirationDate = DateTimeOffset.UtcNow.AddDays(3).ToString("O"),
                claims = new Dictionary<string, string> {
                    ["email"] = "member@example.com", ["membershipNo"] = "123456789"
                }
            } }
        };

        private static CallbackEvent Identity() => new CallbackEvent {
            requestStatus = "presentation_verified",
            verifiedCredentialsData = new[] { new ClaimsIssuer {
                type = new[] { "TrueIdentity" }, issuer = "did:web:did.woodgrovedemo.com",
                credentialState = new CredentialState { revocationStatus = "VALID" },
                expirationDate = DateTimeOffset.UtcNow.AddDays(3).ToString("O")
            } }
        };

        private static bool ValidateLoyalty(CallbackEvent callback) =>
            IdentityPresentationValidator.TryValidateAccountCredential(callback,
                "did:web:loyalty", "NorthstarLoyalty", "email", "email", "member@example.com", "membershipNo", out _);

        private static bool ValidateIdentity(CallbackEvent callback) =>
            IdentityPresentationValidator.TryValidateIdentity(callback,
                "did:web:did.woodgrovedemo.com", out _);

        [Fact]
        public void TrustedLoyaltyCredentialPasses() {
            Assert.True(IdentityPresentationValidator.TryValidateAccountCredential(Loyalty(),
                "did:web:loyalty", "NorthstarLoyalty", "email", "email", "MEMBER@example.com", "membershipNo", out var number));
            Assert.Equal("123456789", number);
        }

        [Fact]
        public void TrueIdentityCredentialPasses() {
            Assert.True(IdentityPresentationValidator.TryValidateIdentity(Identity(),
                "did:web:did.woodgrovedemo.com", out var expiration));
            Assert.True(DateTimeOffset.Parse(expiration) > DateTimeOffset.UtcNow);
        }

        [Fact]
        public void WrongLoyaltyIssuerFails() {
            var callback = Loyalty();
            callback.verifiedCredentialsData[0].issuer = "did:web:attacker";
            Assert.False(ValidateLoyalty(callback));
        }

        [Fact]
        public void WrongAccountFails() {
            var callback = Loyalty();
            callback.verifiedCredentialsData[0].claims["email"] = "other@example.com";
            Assert.False(ValidateLoyalty(callback));
        }

        [Fact]
        public void MissingMembershipNumberFails() {
            var callback = Loyalty();
            callback.verifiedCredentialsData[0].claims.Remove("membershipNo");
            Assert.False(ValidateLoyalty(callback));
        }

        [Fact]
        public void NorthstarLoyaltyRequiresThePublishedLoyaltyMapping() {
            var callback = Loyalty();
            callback.verifiedCredentialsData[0].claims.Remove("membershipNo");
            callback.verifiedCredentialsData[0].claims["loyalty"] = "123456";
            Assert.False(IdentityPresentationValidator.TryValidateAccountCredential(callback,
                "did:web:loyalty", "NorthstarLoyalty", "email", "email",
                "member@example.com", "membershipNo", out _, out var error));
            Assert.Contains("Verification:ProfileValueClaim", error);
            Assert.DoesNotContain("member@example.com", error);
            Assert.DoesNotContain("123456", error);
            Assert.True(IdentityPresentationValidator.TryValidateAccountCredential(callback,
                "did:web:loyalty", "NorthstarLoyalty", "email", "email",
                "member@example.com", "loyalty", out var value, out error));
            Assert.Equal("123456", value);
            Assert.Null(error);
        }

        [Theory]
        [InlineData("issuer", "issuer")]
        [InlineData("type", "type")]
        [InlineData("email", "does not match")]
        [InlineData("missingEmail", "account-binding claim")]
        [InlineData("revocation", "revocation")]
        [InlineData("expiration", "expiration")]
        public void RejectionExplainsTheFailedCheckWithoutClaimValues(string invalid, string expected) {
            var callback = Loyalty();
            var credential = callback.verifiedCredentialsData[0];
            switch (invalid) {
                case "issuer": credential.issuer = "did:web:wrong"; break;
                case "type": credential.type = new[] { "Other" }; break;
                case "email": credential.claims["email"] = "private@example.com"; break;
                case "missingEmail": credential.claims.Remove("email"); break;
                case "revocation": credential.credentialState.revocationStatus = "REVOKED"; break;
                case "expiration": credential.expirationDate = null; break;
            }
            Assert.False(IdentityPresentationValidator.TryValidateAccountCredential(callback,
                "did:web:loyalty", "NorthstarLoyalty", "email", "email",
                "member@example.com", "membershipNo", out _, out var error));
            Assert.Contains(expected, error);
            Assert.DoesNotContain("@example.com", error);
            Assert.DoesNotContain("123456789", error);
        }

        [Fact]
        public void WrongIdentityIssuerFails() {
            var callback = Identity();
            callback.verifiedCredentialsData[0].issuer = "did:web:attacker";
            Assert.False(ValidateIdentity(callback));
        }

        [Fact]
        public void WrongIdentityTypeFails() {
            var callback = Identity();
            callback.verifiedCredentialsData[0].type = new[] { "VerifiedIdentity" };
            Assert.False(ValidateIdentity(callback));
        }

        [Fact]
        public void RevokedIdentityFails() {
            var callback = Identity();
            callback.verifiedCredentialsData[0].credentialState.revocationStatus = "REVOKED";
            Assert.False(ValidateIdentity(callback));
        }

        [Fact]
        public void ExpiredLoyaltyFails() {
            var callback = Loyalty();
            callback.verifiedCredentialsData[0].expirationDate = DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O");
            Assert.False(ValidateLoyalty(callback));
        }

        [Fact]
        public void ExpiredIdentityFails() {
            var callback = Identity();
            callback.verifiedCredentialsData[0].expirationDate = DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O");
            Assert.False(ValidateIdentity(callback));
        }

        [Fact]
        public void CombinedCredentialRequestIsNotAcceptedForEitherStage() {
            var loyalty = Loyalty();
            loyalty.verifiedCredentialsData = new[] { loyalty.verifiedCredentialsData[0], Identity().verifiedCredentialsData[0] };
            Assert.False(ValidateLoyalty(loyalty));
            Assert.False(ValidateIdentity(loyalty));
        }

        [Fact]
        public void NonLoyaltyCredentialUsesConfiguredClaims() {
            var callback = Loyalty();
            callback.verifiedCredentialsData[0].type = new[] { "Employee" };
            callback.verifiedCredentialsData[0].claims = new Dictionary<string, string> {
                ["workEmail"] = "MEMBER@example.com", ["employeeNumber"] = "E42"
            };
            Assert.True(IdentityPresentationValidator.TryValidateAccountCredential(callback, "did:web:loyalty",
                "Employee", "workEmail", "email", "member@example.com", "employeeNumber", out var value));
            Assert.Equal("E42", value);
        }

        [Theory]
        [InlineData("22222222-2222-2222-2222-222222222222", true)]
        [InlineData("33333333-3333-3333-3333-333333333333", false)]
        [InlineData("not-an-object-id", false)]
        public void ObjectIdBindingRequiresTheSameValidObjectId(string actual, bool expected) {
            var callback = Loyalty();
            callback.verifiedCredentialsData[0].claims["accountId"] = actual;
            Assert.Equal(expected, IdentityPresentationValidator.TryValidateAccountCredential(callback,
                "did:web:loyalty", "NorthstarLoyalty", "accountId", "oid",
                "22222222-2222-2222-2222-222222222222", "membershipNo", out _));
        }

        [Fact]
        public void MissingMappedClaimDoesNotFallBackToLoyaltyClaims() {
            Assert.False(IdentityPresentationValidator.TryValidateAccountCredential(Loyalty(),
                "did:web:loyalty", "NorthstarLoyalty", "workEmail", "email",
                "member@example.com", "membershipNo", out _));
            Assert.False(IdentityPresentationValidator.TryValidateAccountCredential(Loyalty(),
                "did:web:loyalty", "NorthstarLoyalty", "email", "email",
                "member@example.com", "employeeNumber", out _));
        }

        [Fact]
        public void InvalidBindingSourceFailsClosed() {
            Assert.False(IdentityPresentationValidator.TryValidateAccountCredential(Loyalty(),
                "did:web:loyalty", "NorthstarLoyalty", "email", "name",
                "member@example.com", "membershipNo", out _));
        }
    }
}
