// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace B2CVerifiedID {
    public class IssuanceRequest {
        public string authority { get; set; }
        public bool includeQRCode { get; set; }
        public Registration registration { get; set; }
        public Callback callback { get; set; }
        public string type { get; set; }
        public string manifest { get; set; }
        [JsonProperty( NullValueHandling = NullValueHandling.Ignore )]
        public Pin pin { get; set; }
        [JsonProperty( NullValueHandling = NullValueHandling.Ignore )]
        public Dictionary<string, string> claims;
        [JsonProperty( NullValueHandling = NullValueHandling.Ignore )]
        public string expirationDate { get; set; } // format "2024-10-20T14:52:39.043Z"
    }

    /// <summary>
    /// VC Presentation
    /// </summary>
    public class PresentationRequest {
        public string authority { get; set; }
        public bool includeQRCode { get; set; }
        public Registration registration { get; set; }
        public Callback callback { get; set; }
        //public Presentation presentation { get; set; }
        public bool includeReceipt { get; set; }
        public List<RequestedCredential> requestedCredentials { get; set; }
    }

    /// <summary>
    /// Configuration - presentation validation configuration
    /// </summary>
    public class Configuration {
        public Validation validation { get; set; }
    }
    /// <summary>
    /// Validation - presentation validation configuration
    /// </summary>
    public class Validation {
        public bool allowRevoked { get; set; } // default false
        public bool validateLinkedDomain { get; set; } // default false
        [JsonProperty( NullValueHandling = NullValueHandling.Ignore )]
        public FaceCheck faceCheck { get; set; }
    }

    /// <summary>
    /// FaceCheck - if to ask for face check and what claim + score you want
    /// </summary>
    public class FaceCheck {
        public string sourcePhotoClaimName { get; set; }
        public int matchConfidenceThreshold { get; set; }
    }

    /// <summary>
    /// Registration - used in both issuance and presentation to give the app a display name
    /// </summary>
    public class Registration {
        public string clientName { get; set; }
        public string purpose { get; set; }
    }

    /// <summary>
    /// Callback - defines where and how we want our callback.
    /// url - points back to us
    /// state - something we pass that we get back in the callback event. We use it as a correlation id
    /// headers - any additional HTTP headers you want to pass to the VC Client API. 
    /// The values you pass will be returned, as HTTP Headers, in the callback
    public class Callback {
        public string url { get; set; }
        public string state { get; set; }
        public Dictionary<string, string> headers { get; set; }
    }

    /// <summary>
    /// Pin - if issuance involves the use of a pin code. The 'value' attribute is a string so you can have values like "0907"
    /// </summary>
    public class Pin {
        public string value { get; set; }
        public int length { get; set; }
    }

    /// <summary>
    /// Presentation can involve asking for multiple VCs
    /// </summary>
    public class RequestedCredential {
        public string type { get; set; }
        [JsonProperty( NullValueHandling = NullValueHandling.Ignore )]
        public List<string> acceptedIssuers { get; set; }
        [JsonProperty( NullValueHandling = NullValueHandling.Ignore )]
        public Configuration configuration { get; set; }
        [JsonProperty( NullValueHandling = NullValueHandling.Ignore )]
        public List<Constraint> constraints { get; set; }

    }

    public class Constraint {
        public string claimName { get; set; }
        [JsonProperty( NullValueHandling = NullValueHandling.Ignore )]
        public List<string> values { get; set; }
        [JsonProperty( NullValueHandling = NullValueHandling.Ignore )]
        public string contains { get; set; }
        [JsonProperty( NullValueHandling = NullValueHandling.Ignore )]
        public string startsWith { get; set; }
    }

    /// <summary>
    /// VC Client API callback
    /// </summary>
    public class CallbackEvent {
        public string requestId { get; set; }
        public string requestStatus { get; set; }
        public Error error { get; set; }
        public string state { get; set; }
        public string subject { get; set; }
        public ClaimsIssuer[] verifiedCredentialsData { get; set; }
        public Receipt receipt { get; set; }
        public string photo { get; set; }

    }

    /// <summary>
    /// Receipt - returned when VC presentation is verified. The id_token contains the full VC id_token
    /// the state is not to be confused with the VCCallbackEvent.state and is something internal to the VC Client API
    /// </summary>
    public class Receipt {
        //public string vp_token { get; set; }
        [JsonProperty( NullValueHandling = NullValueHandling.Ignore )]
        [JsonConverter( typeof( VpTokenJsonConverter<string> ) )]
        public List<string> vp_token { get; set; }
    }
    internal class VpTokenJsonConverter<T> : JsonConverter {
        public override bool CanConvert( Type objectType ) {
            return (objectType == typeof( List<T> ));
        }

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer ) {
            JToken token = JToken.Load( reader );
            if (token.Type == JTokenType.Array)
                return token.ToObject<List<T>>();
            return new List<T> { token.ToObject<T>() };
        }

        public override bool CanWrite {
            get {
                return false;
            }
        }
        public override void WriteJson( JsonWriter writer, object value, JsonSerializer serializer ) {
            throw new NotImplementedException();
        }
    }
     /// <summary>
     /// Error - in case the VC Client API returns an error
     /// </summary>
    public class Error {
        public string code { get; set; }
        public string message { get; set; }
    }

    /// <summary>
    /// ClaimsIssuer - details of each VC that was presented (usually just one)
    /// authority gives you who issued the VC and the claims is a collection of the VC's claims, like givenName, etc
    /// </summary>
    public class ClaimsIssuer {
        public string issuer { get; set; }
        public string domain { get; set; }
        public string verified { get; set; }
        public string[] type { get; set; }
        public IDictionary<string, string> claims { get; set; }
        public CredentialState credentialState { get; set; }
        public FaceCheckResult faceCheck { get; set; }
        public DomainValidation domainValidation { get; set; }
        public string expirationDate {get;set; }
        public string issuanceDate {get; set; }
    }

    public class CredentialState {
        public string revocationStatus { get; set; }
        [JsonIgnore]
        public bool isValid { get {return revocationStatus == "VALID"; } }
    }

    public class DomainValidation {
        public string url { get; set; }
    }

    public class FaceCheckResult {
        public double matchConfidenceScore { get; set; }
    }

}
