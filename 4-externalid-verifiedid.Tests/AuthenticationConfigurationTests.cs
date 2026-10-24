using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Web;
using Xunit;

namespace ExternalIDVerifiedID.Tests {
    public class AuthenticationConfigurationTests {
        [Fact]
        public void SignInUsesAuthorizationCodeWithPkceAndConfiguredSecret() {
            var configuration = new ConfigurationBuilder()
                .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "sample-appsettings.json"))
                .AddInMemoryCollection(new Dictionary<string, string> {
                    ["EEID:Authority"] = "https://example.ciamlogin.com/",
                    ["EEID:TenantId"] = "11111111-1111-1111-1111-111111111111",
                    ["EEID:ClientId"] = "22222222-2222-2222-2222-222222222222",
                    ["EEID:ClientSecret"] = "test-only-secret"
                }).Build();
            var services = new ServiceCollection();
            services.AddSingleton<IConfiguration>(configuration);
            services.AddLogging();
            services.AddAuthentication(OpenIdConnectDefaults.AuthenticationScheme)
                .AddMicrosoftIdentityWebApp(configuration.GetSection("EEID"));
            using var provider = services.BuildServiceProvider();
            var options = provider.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
                .Get(OpenIdConnectDefaults.AuthenticationScheme);

            Assert.Equal("code", options.ResponseType);
            Assert.True(options.UsePkce);
            Assert.False(options.MapInboundClaims);
            Assert.Equal("test-only-secret", options.ClientSecret);
            Assert.Null(configuration["EEID:ClientCredentials:0:ClientSecret"]);
        }
    }
}
