using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace AspNetCoreVerifiableCredentialsB2C.Models
{
    /// <summary>
    /// VC Issuance
    /// </summary>
    public class VCIssuanceRequest
    {
        public string authority { get; set; }
        public bool includeQRCode { get; set; }
        public Registration registration { get; set; }
        public Callback callback { get; set; }
        public string type { get; set; }
        public string manifest { get; set; }
        public Pin pin { get; set; }
        public Dictionary<string, string> claims;
    }

    /// <summary>
    /// VC Presentation
    /// </summary>
    public class VCPresentationRequest
    {
        public string authority { get; set; }
        public bool includeQRCode { get; set; }
        public Registration registration { get; set; }
        public Callback callback { get; set; }
        //public Presentation presentation { get; set; }
        public bool includeReceipt { get; set; }
        public List<RequestedCredential> requestedCredentials { get; set; }
        public Configuration configuration { get; set; }

    }

    /// <summary>
    /// Configuration - presentation validation configuration
    /// </summary>
    public class Configuration
    {
        public Validation validation { get; set; }
    }
    /// <summary>
    /// Validation - presentation validation configuration
    /// </summary>
    public class Validation
    {
        public bool allowRevoked { get; set; } // default false
        public bool validateLinkedDomain { get; set; } // default false
    }

    /// <summary>
    /// Registration - used in both issuance and presentation to give the app a display name
    /// </summary>
    public class Registration
    {
        public string clientName { get; set; }
        public string purpose { get; set; }
    }

    /// <summary>
    /// Callback - defines where and how we want our callback.
    /// url - points back to us
    /// state - something we pass that we get back in the callback event. We use it as a correlation id
    /// headers - any additional HTTP headers you want to pass to the VC Client API. 
    /// The values you pass will be returned, as HTTP Headers, in the callback
    public class Callback
    {
        public string url { get; set; }
        public string state { get; set; }
        public Dictionary<string, string> headers { get; set; }
    }

    /// <summary>
    /// Pin - if issuance involves the use of a pin code. The 'value' attribute is a string so you can have values like "0907"
    /// </summary>
    public class Pin
    {
        public string value { get; set; }
        public int length { get; set; }
    }

    /// <summary>
    /// Issuance - the specific details when you do VC issuance
    /// </summary>
    /*
    public class Issuance
    {
        public string type { get; set; }
        public string manifest { get; set; }
        public Pin pin { get; set; }
        public Dictionary<string,string> claims;
    }
    */
    /// <summary>
    /// Presentation - the specific details when you do VC presentation
    /// </summary>
    /*
    public class Presentation
    {
        public bool includeReceipt { get; set; }
        public List<RequestedCredential> requestedCredentials { get; set; }
    }
    */
    /// <summary>
    /// Presentation can involve asking for multiple VCs
    /// </summary>
    public class RequestedCredential
    {
        public string type { get; set; }
        public string manifest { get; set; }
        public List<string> acceptedIssuers { get; set; }
    }

    /// <summary>
    /// VC Client API callback
    /// </summary>
    public class VCCallbackEvent
    {
        public string requestId { get; set; }
        public string requestStatus { get; set; }
        public Error error { get; set; }
        public string state { get; set; }
        public string subject { get; set; }
        public ClaimsIssuer[] verifiedCredentialsData { get; set; }
        public Receipt receipt { get; set; }
    }

    /// <summary>
    /// Error - in case the VC Client API returns an error
    /// </summary>
    public class Error
    {
        public string code { get; set; }
        public string message { get; set; }
    }

    /// <summary>
    /// Receipt - returned when VC presentation is verified. The id_token contains the full VC id_token
    /// the state is not to be confused with the VCCallbackEvent.state and is something internal to the VC Client API
    /// </summary>
    public class Receipt
    {
        public string id_token { get; set; }
        public string state { get; set; }
    }

    /// <summary>
    /// ClaimsIssuer - details of each VC that was presented (usually just one)
    /// authority gives you who issued the VC and the claims is a collection of the VC's claims, like givenName, etc
    /// </summary>
    public class ClaimsIssuer
    {
        public string issuer { get; set; }
        public string domain { get; set; }
        public string verified { get; set; }
        public string[] type { get; set; }
        public IDictionary<string, string> claims { get; set; }
    }

} // ns
