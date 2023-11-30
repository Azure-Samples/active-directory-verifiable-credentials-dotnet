using Azure.Identity;
using Azure.Security.KeyVault.Keys.Cryptography;
using Azure.Security.KeyVault.Keys;
using Newtonsoft.Json.Linq;
using System.Security.Cryptography;
using System;
using Microsoft.Extensions.Configuration;
using System.Text;
using Microsoft.AspNetCore.DataProtection.KeyManagement;

namespace OnboardWithTAP.Helpers {
    public class KeyVaultHelper {

        private static CryptographyClient GetKeyVaultClient( IConfiguration configuration, out string signingKeyVersion ) {
            ClientSecretCredential clientSecretCredential = new ClientSecretCredential( configuration["AzureAd:TenantId"]
                                                                                        , configuration["AzureAd:ClientId"]
                                                                                        , configuration["AzureAd:ClientSecret"]
                                                                                      );
            string keyIdentifier = configuration["AppSettings:KeyIdentifier"];
            string[] parts = keyIdentifier.Split( "/" );
            string keyVaultURI = $"{parts[0]}//{parts[2]}";
            string signingKeyName = parts[4];
            signingKeyVersion = parts[5];
            var keyClient = new KeyClient( new System.Uri( keyVaultURI ), clientSecretCredential );
            KeyVaultKey key = keyClient.GetKey( signingKeyName, signingKeyVersion );
            CryptographyClient cryptoClient = new CryptographyClient( key.Id, clientSecretCredential );
            return cryptoClient;
        }

        public static string SignPayload( IConfiguration configuration, string payload ) {
            var cryptoClient = GetKeyVaultClient( configuration, out string signingKeyVersion );
            var header = "{\"alg\":\"RS256\",\"typ\":\"JWT\", \"kid\":\"" + signingKeyVersion + "\"}";
            var headerAndPayload = Base64Encode( header ) + "." + Base64Encode( payload );
            byte[] data = Encoding.UTF8.GetBytes( headerAndPayload );
            byte[] digest = null;
            using (HashAlgorithm haslAlgo = SHA256.Create()) { digest = haslAlgo.ComputeHash( data ); }
            SignResult signResult = cryptoClient.Sign( SignatureAlgorithm.RS256, digest );
            var token = headerAndPayload + "." + System.Convert.ToBase64String( signResult.Signature );
            return token;
        }
        public static bool ValidateJwt( IConfiguration configuration, string jwtToken, out JObject payload, out string error ) {
            error = null;
            payload = null;
            var cryptoClient = GetKeyVaultClient( configuration, out string signingKeyVersion );

            var jwtParts = jwtToken.Split( '.' );
            string header = jwtParts[0];
            var signature = Convert.FromBase64String( Base64Pad( jwtParts[2] ) );

            JObject jsonHeader = JObject.Parse( Base64Decode( header ) );
            string alg = jsonHeader["alg"].ToString();
            string type = jsonHeader["typ"].ToString();
            string kid = jsonHeader["kid"].ToString();

            long now = ((DateTimeOffset)(DateTime.UtcNow)).ToUnixTimeSeconds();
            payload = JObject.Parse( Base64Decode( jwtParts[1] ) );
            long iat = int.Parse( payload["iat"].ToString() );
            long nbf = int.Parse( payload["iat"].ToString() );
            long exp = int.Parse( payload["iat"].ToString() );
            // expired
            if (nbf >= now) {
                error = "Token not valid yet";
                return false;
            }
            if (exp > now) {
                error = "Token expired";
                return false;
            }

            var byteData = Encoding.UTF8.GetBytes( $"{header}.{jwtParts[1]}" );
            byte[] digest = null;
            using (HashAlgorithm haslAlgo = SHA256.Create()) { digest = haslAlgo.ComputeHash( byteData ); }
            VerifyResult result = cryptoClient.Verify( (SignatureAlgorithm)alg, digest, signature );
            if (!result.IsValid) {
                error = "Invalid signature";
            }
            return result.IsValid;
        }
        public static string Base64Encode( string plainText ) {
            var plainTextBytes = System.Text.Encoding.UTF8.GetBytes( plainText );
            string s = System.Convert.ToBase64String( plainTextBytes );
            s = s.Split( '=' )[0]; // Remove any trailing '='s
            s = s.Replace( '+', '-' ); // 62nd char of encoding
            s = s.Replace( '/', '_' ); // 63rd char of encoding
            return s;
        }
        public static string Base64Pad( string arg ) {
            switch (arg.Length % 4) // Pad with trailing '='s
            {
                case 0: break; // No pad chars in this case
                case 1: arg += "==="; break;
                case 2: arg += "=="; break;
                case 3: arg += "="; break;
            }
            return arg;
        }

        public static string Base64Decode( string arg ) {
            string s = arg;
            s = s.Replace( '-', '+' ); // 62nd char of encoding
            s = s.Replace( '_', '/' ); // 63rd char of encoding
            switch (s.Length % 4) // Pad with trailing '='s
            {
                case 0: break; // No pad chars in this case
                case 2: s += "=="; break; // Two pad chars
                case 3: s += "="; break; // One pad char
                default: throw new Exception( "Illegal base64url string!" );
            }
            return System.Text.Encoding.UTF8.GetString( Convert.FromBase64String( s ) );
        }

        // JWT decode helpers
        public static JObject GetJwtPart( string jwtToken, int part ) {
            return JObject.Parse( GetJwtPartAsString( jwtToken, part ) );
        }
        public static string GetJwtPartAsString( string jwtToken, int part ) {
            if (!(part == 0 || part == 1))
                throw new ArgumentOutOfRangeException( "part", "Must be 0 or 1" );
            jwtToken = jwtToken.Replace( "_", "/" ).Replace( "-", "+" ); // node.js does emit non-standard base64 values :-(
            string[] parts = jwtToken.Split( "." );
            if (parts.Length < 3)
                throw new ArgumentOutOfRangeException( "jwtToken", "token must have 3 parts separated by a '.'" );
            parts[part] = parts[part].PadRight( 4 * ((parts[part].Length + 3) / 4), '=' );
            return System.Text.Encoding.UTF8.GetString( Convert.FromBase64String( parts[part] ) );
        }




    }
}
