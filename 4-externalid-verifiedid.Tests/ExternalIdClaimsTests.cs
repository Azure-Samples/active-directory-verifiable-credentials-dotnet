using System.Security.Claims;
using System;
using Xunit;

namespace ExternalIDVerifiedID.Tests {
    public class ExternalIdClaimsTests {
        private static ClaimsPrincipal User(params Claim[] claims) =>
            new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));

        [Theory]
        [InlineData("email", "member@example.com")]
        [InlineData(ClaimTypes.Email, "member@example.com")]
        [InlineData("emails", "member@example.com")]
        [InlineData("emails", "[\"member@example.com\"]")]
        public void ReadsSupportedEmailClaims(string type, string value) {
            Assert.Equal("member@example.com", ExternalIdClaims.Email(User(new Claim(type, value))));
        }

        [Fact]
        public void MissingEmailDoesNotInventAnAccountAddress() {
            var user = User(new Claim("preferred_username", "unverified@example.com"));
            Assert.Null(ExternalIdClaims.Email(user));
            Assert.Equal("Signed-in user", ExternalIdClaims.DisplayName(user));
        }

        [Fact]
        public void NameCanBeDisplayedWithoutEmail() {
            Assert.Equal("Demo member", ExternalIdClaims.DisplayName(User(new Claim("name", "Demo member"))));
        }

        [Theory]
        [InlineData("givenName", "surname")]
        [InlineData("given_name", "family_name")]
        [InlineData(ClaimTypes.GivenName, ClaimTypes.Surname)]
        public void IssuanceUsesTokenNamesAndSixDigitLoyalty(string givenType, string surnameType) {
            var claims = ExternalIdClaims.IssuanceClaims(User(
                new Claim(givenType, "Ada"), new Claim(surnameType, "Lovelace"),
                new Claim("email", "ada@example.com"), new Claim("name", "Ada Lovelace")), true);
            Assert.Equal("Ada", claims["givenName"]);
            Assert.Equal("Lovelace", claims["surname"]);
            Assert.Equal("ada@example.com", claims["email"]);
            Assert.Equal("Ada Lovelace", claims["displayName"]);
            Assert.Matches("^[1-9][0-9]{5}$", claims["loyalty"]);
            Assert.Equal(claims["membershipNo"], claims["loyalty"]);
            Assert.Equal("Diamond", claims["memberStatus"]);
        }

        [Theory]
        [InlineData("given_name", "Ada")]
        [InlineData("family_name", "Lovelace")]
        [InlineData("given_name", " ")]
        public void RequiredNamesAreNotInventedFromDisplayName(string type, string value) {
            var error = Assert.Throws<InvalidOperationException>(() => ExternalIdClaims.IssuanceClaims(User(
                new Claim(type, value), new Claim("name", "Ada Lovelace")), true));
            Assert.Contains("Token configuration > Add optional claim > ID", error.Message);
            Assert.Contains("given_name and family_name", error.Message);
            Assert.Contains("sign out and sign in again", error.Message);
        }

        [Fact]
        public void LegacyCredentialsCanIssueWithoutSeparateNames() {
            var claims = ExternalIdClaims.IssuanceClaims(User(new Claim("name", "Member")), false);
            Assert.False(claims.ContainsKey("givenName"));
            Assert.False(claims.ContainsKey("surname"));
            Assert.Matches("^[1-9][0-9]{5}$", claims["membershipNo"]);
        }
    }
}
