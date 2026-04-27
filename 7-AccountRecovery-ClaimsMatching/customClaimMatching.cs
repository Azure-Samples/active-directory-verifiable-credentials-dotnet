using System.Net;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace account_recovery_claim_matching;

public class CustomClaimMatching
{
    private readonly ILogger<CustomClaimMatching> _logger;
    private readonly IClaimsValidator _claimsValidator;
    private readonly TokenValidationService _tokenValidator;

    public CustomClaimMatching(ILogger<CustomClaimMatching> logger, IClaimsValidator claimsValidator, TokenValidationService tokenValidator)
    {
        _logger = logger;
        _claimsValidator = claimsValidator;
        _tokenValidator = tokenValidator;
    }

    [Function("CustomClaimMatching")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post")] HttpRequest req)
    {
        _logger.LogInformation("C# HTTP trigger function processed a request.");

        // Validate Bearer token (required — OAuth-only authentication)
        var authHeader = req.Headers["Authorization"].FirstOrDefault();
        if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Missing Bearer token in request.");
            return new ObjectResult(new { error = "Unauthorized", detail = "Bearer token is required." }) { StatusCode = 401 };
        }

        var token = authHeader.Substring("Bearer ".Length).Trim();
        _logger.LogInformation("Bearer token: {Token}", token);
        var (isValid, errorMessage) = await _tokenValidator.ValidateTokenAsync(token);
        if (!isValid)
        {
            _logger.LogWarning("Bearer token validation failed: {Error}", errorMessage);
            return new ObjectResult(new { error = "Bearer token validation failed", detail = errorMessage }) { StatusCode = 401 };
        }
        
        string requestBody = await new StreamReader(req.Body).ReadToEndAsync();
        var request = JsonConvert.DeserializeObject<VerifiedIdClaimValidationRequest>(requestBody);

        if (request?.Data?.VerifiedIdClaimsContext == null)
        {
            _logger.LogError("Invalid request payload");
            return new BadRequestObjectResult("Invalid request payload");
        }

        // Extract user information from authentication context
        string? upn = request.Data.AuthenticationContext?.User?.UserPrincipalName;
        string? employeeId = request.Data.VerifiedIdClaimsContext.AdditionalInfo?.EmployeeId;

        // Extract Verified Credential claims (dynamic — any key/value pairs the caller sends)
        var claims = request.Data.VerifiedIdClaimsContext.Claims ?? new Dictionary<string, string>();

        // Extract authentication context
        string? correlationId = request.Data.AuthenticationContext?.CorrelationId;
        string? clientIp = request.Data.AuthenticationContext?.Client?.ClientIp;
        string? tenantId = request.Data.TenantId;

        _logger.LogInformation("Processing claim validation for UPN: {Upn}, CorrelationId: {CorrelationId}, ClaimCount: {Count}",
            upn, correlationId, claims.Count);

        // Validate claims against authoritative data source
        var matchResult = await _claimsValidator.ValidateClaimsAsync(
            upn: upn,
            employeeId: employeeId,
            claims: claims
        );

        _logger.LogInformation("Actual validation result: {Result}, FailedClaims: {FailedClaims}",
            matchResult.Result, matchResult.FailedClaims != null ? string.Join(", ", matchResult.FailedClaims) : "none");

        string validationResult = matchResult.Result;
        List<string>? failedClaims = matchResult.FailedClaims;

        // Build response
        var response = new VerifiedIdClaimValidationResponse(validationResult, failedClaims);

        return new OkObjectResult(response);
    }
}

#region Request Models

public class VerifiedIdClaimValidationRequest
{
    [JsonProperty("type")]
    public string? Type { get; set; }

    [JsonProperty("source")]
    public string? Source { get; set; }

    [JsonProperty("data")]
    public VerifiedIdClaimValidationData? Data { get; set; }
}

public class VerifiedIdClaimValidationData
{
    [JsonProperty("@odata.type")]
    public string? ODataType { get; set; }

    [JsonProperty("tenantId")]
    public string? TenantId { get; set; }

    [JsonProperty("authenticationEventListenerId")]
    public string? AuthenticationEventListenerId { get; set; }

    [JsonProperty("customAuthenticationExtensionId")]
    public string? CustomAuthenticationExtensionId { get; set; }

    [JsonProperty("verifiedIdClaimsContext")]
    public VerifiedIdClaimsContext? VerifiedIdClaimsContext { get; set; }

    [JsonProperty("authenticationContext")]
    public AuthenticationContext? AuthenticationContext { get; set; }
}

public class VerifiedIdClaimsContext
{
    [JsonProperty("identities")]
    public List<IdentityInfo>? Identities { get; set; }

    [JsonProperty("additionalInfo")]
    public AdditionalInfo? AdditionalInfo { get; set; }

    [JsonProperty("claims")]
    public Dictionary<string, string>? Claims { get; set; }
}

public class IdentityInfo
{
    [JsonProperty("issuer")]
    public string? Issuer { get; set; }

    [JsonProperty("issuerAssignedId")]
    public string? IssuerAssignedId { get; set; }

    [JsonProperty("signInType")]
    public string? SignInType { get; set; }
}

public class AdditionalInfo
{
    [JsonProperty("employeeId")]
    public string? EmployeeId { get; set; }
}

public class AuthenticationContext
{
    [JsonProperty("correlationId")]
    public string? CorrelationId { get; set; }

    [JsonProperty("protocol")]
    public string? Protocol { get; set; }

    [JsonProperty("client")]
    public ClientInfo? Client { get; set; }

    [JsonProperty("clientServicePrincipal")]
    public ServicePrincipalInfo? ClientServicePrincipal { get; set; }

    [JsonProperty("resourceServicePrincipal")]
    public ServicePrincipalInfo? ResourceServicePrincipal { get; set; }

    [JsonProperty("user")]
    public UserInfo? User { get; set; }
}

public class ClientInfo
{
    [JsonProperty("clientIp")]
    public string? ClientIp { get; set; }

    [JsonProperty("locale")]
    public string? Locale { get; set; }

    [JsonProperty("market")]
    public string? Market { get; set; }
}

public class ServicePrincipalInfo
{
    [JsonProperty("appId")]
    public string? AppId { get; set; }

    [JsonProperty("displayName")]
    public string? DisplayName { get; set; }

    [JsonProperty("id")]
    public string? Id { get; set; }
}

public class UserInfo
{
    [JsonProperty("id")]
    public string? Id { get; set; }

    [JsonProperty("userPrincipalName")]
    public string? UserPrincipalName { get; set; }

    [JsonProperty("givenName")]
    public string? GivenName { get; set; }

    [JsonProperty("surname")]
    public string? Surname { get; set; }

    [JsonProperty("mail")]
    public string? Mail { get; set; }

    [JsonProperty("onPremisesSamAccountName")]
    public string? OnPremisesSamAccountName { get; set; }

    [JsonProperty("userType")]
    public string? UserType { get; set; }

    [JsonProperty("createdDateTime")]
    public string? CreatedDateTime { get; set; }
}

#endregion

#region Response Models

public class VerifiedIdClaimValidationResponse
{
    [JsonPropertyName("data")]
    public VerifiedIdClaimValidationResponseData Data { get; set; }

    public VerifiedIdClaimValidationResponse(string result, List<string>? failedClaims = null)
    {
        Data = new VerifiedIdClaimValidationResponseData(result, failedClaims);
    }
}

public class VerifiedIdClaimValidationResponseData
{
    [JsonPropertyName("@odata.type")]
    public string ODataType { get; set; } = "microsoft.graph.onVerifiedIdClaimValidationResponseData";

    [JsonPropertyName("actions")]
    public List<VerifiedIdClaimsMatchingResultAction> Actions { get; set; }

    public VerifiedIdClaimValidationResponseData(string result, List<string>? failedClaims = null)
    {
        Actions = new List<VerifiedIdClaimsMatchingResultAction>
        {
            new VerifiedIdClaimsMatchingResultAction(result, failedClaims)
        };
    }
}

public class VerifiedIdClaimsMatchingResultAction
{
    [JsonPropertyName("@odata.type")]
    public string ODataType { get; set; }

    [JsonPropertyName("failedClaims")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? FailedClaims { get; set; }

    public VerifiedIdClaimsMatchingResultAction(string result, List<string>? failedClaims = null)
    {
        ODataType = string.Equals(result, "pass", StringComparison.OrdinalIgnoreCase)
            ? "microsoft.graph.verifiedIdClaimValidation.pass"
            : "microsoft.graph.verifiedIdClaimValidation.failed";
        FailedClaims = failedClaims;
    }
}

#endregion