using Microsoft.Extensions.Configuration;
using Microsoft.Identity.Client;
using Microsoft.Identity.Web;
using Microsoft.Extensions.DependencyInjection;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using System;
using Microsoft.Extensions.Logging;
using Azure.Identity;

namespace B2CVerifiedID.Helpers {
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

        public static async Task<(string token, string error, string error_description)> GetAccessToken( IConfiguration configuration ) {
            // You can run this sample using ClientSecret or Certificate or MSI .
            // The code will differ only when instantiating the IConfidentialClientApplication
            bool useManagedIdentity = configuration.GetValue( "VerifiedID:ManagedIdentity", false );
            string[] scopes = new string[] { configuration["VerifiedID:scope"] };

            if (useManagedIdentity) {
                try {
                    var credential = new ChainedTokenCredential( new ManagedIdentityCredential(), new EnvironmentCredential() );
                    var token = credential.GetToken( new Azure.Core.TokenRequestContext( scopes ) );
                    return (token.Token, string.Empty, string.Empty);
                } catch (Exception ex) {
                    return (string.Empty, "500", "Error acquiring an access token via MSI: " + ex.Message);
                }
            }

            string tenantId = configuration.GetValue( "VerifiedID:tenantId", configuration["AzureAd:TenantId"] );
            string clientId = configuration.GetValue( "VerifiedID:ClientId", configuration["AzureAd:ClientId"] );
            string authority = $"{configuration["VerifiedID:Authority"]}{tenantId}";
            string clientSecret = configuration.GetValue( "VerifiedID:ClientSecret", configuration.GetValue( "AzureAd:ClientSecret", "" ) );

            // Since we are using application permissions this will be a confidential client application
            IConfidentialClientApplication app;
            if (!string.IsNullOrWhiteSpace( clientSecret )) {
                app = ConfidentialClientApplicationBuilder.Create( clientId )
                    .WithClientSecret( clientSecret )
                    .WithAuthority( new Uri( authority ) )
                    .Build();
            } else {
                X509Certificate2 certificate = ReadCertificate( configuration["VerifiedID:CertificateName"] );
                app = ConfidentialClientApplicationBuilder.Create( clientId )
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

            AuthenticationResult result = null;
            try {
                result = await app.AcquireTokenForClient( scopes ).ExecuteAsync();
            } catch (MsalServiceException ex) when (ex.Message.Contains( "AADSTS70011" )) {
                return (string.Empty, "500", "Scope provided is not supported");
            } catch (MsalServiceException ex) {
                return (string.Empty, "500", "Something went wrong getting an access token for the client API:" + ex.Message);
            }

            return (result.AccessToken, string.Empty, string.Empty);
        }

    }
}
