using Microsoft.Extensions.Configuration;
using Microsoft.Identity.Client;
using Microsoft.Identity.Web;
using Microsoft.Extensions.DependencyInjection;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using System;
using Microsoft.Extensions.Logging;

namespace AspNetCoreVerifiableCredentials {
    public class MsalAccessTokenHandler {
        private static X509Certificate2 ReadCertificate( string certificateName ) {
            if (string.IsNullOrWhiteSpace( certificateName )) {
                throw new ArgumentException( "certificateName should not be empty. Please set the CertificateName setting in the appsettings.json", "certificateName" );
            }
            CertificateDescription certificateDescription = CertificateDescription.FromStoreWithDistinguishedName( certificateName );
            DefaultCertificateLoader defaultCertificateLoader = new DefaultCertificateLoader();
            defaultCertificateLoader.LoadIfNeeded( certificateDescription );
            return certificateDescription.Certificate;
        }

        public static async Task<(string token, string error, string error_description)> GetAccessToken( IConfiguration configuration) {
            // You can run this sample using ClientSecret or Certificate. The code will differ only when instantiating the IConfidentialClientApplication
            string authority = $"{configuration["VerifiedID:Authority"]}{configuration["VerifiedID:TenantId"]}";
            string clientSecret = configuration.GetValue( "VerifiedID:ClientSecret", "" );
            // Since we are using application permissions this will be a confidential client application
            IConfidentialClientApplication app;
            if (!string.IsNullOrWhiteSpace( clientSecret )) {
                app = ConfidentialClientApplicationBuilder.Create( configuration["VerifiedID:ClientId"] )
                    .WithClientSecret( clientSecret )
                    .WithAuthority( new Uri( authority ) )
                    .Build();
            } else {
                X509Certificate2 certificate = ReadCertificate( configuration["VerifiedID:CertificateName"] );
                app = ConfidentialClientApplicationBuilder.Create( configuration["VerifiedID:ClientId"] )
                    .WithCertificate( certificate )
                    .WithAuthority( new Uri( authority ) )
                    .Build();
            }

            //configure in memory cache for the access tokens. The tokens are typically valid for 60 seconds,
            //so no need to create new ones for every web request
            app.AddDistributedTokenCache( services => {
                services.AddDistributedMemoryCache();
                services.AddLogging( configure => configure.AddConsole() )
                .Configure<LoggerFilterOptions>( options => options.MinLevel = Microsoft.Extensions.Logging.LogLevel.Debug );
            } );

            // With client credentials flows the scopes is ALWAYS of the shape "resource/.default", as the 
            // application permissions need to be set statically (in the portal or by PowerShell), and then granted by
            // a tenant administrator. 
            string[] scopes = new string[] { configuration["VerifiedID:scope"] };

            AuthenticationResult result = null;
            try {
                result = await app.AcquireTokenForClient( scopes )
                    .ExecuteAsync();
            } catch (MsalServiceException ex) when (ex.Message.Contains( "AADSTS70011" )) {
                // Invalid scope. The scope has to be of the form "https://resourceurl/.default"
                // Mitigation: change the scope to be as expected
                return (string.Empty, "500", "Scope provided is not supported");
                //return BadRequest(new { error = "500", error_description = "Scope provided is not supported" });
            } catch (MsalServiceException ex) {
                // general error getting an access token
                return (String.Empty, "500", "Something went wrong getting an access token for the client API:" + ex.Message);
                //return BadRequest(new { error = "500", error_description = "Something went wrong getting an access token for the client API:" + ex.Message });
            }

            return (result.AccessToken, String.Empty, String.Empty);
        }

    }
}
