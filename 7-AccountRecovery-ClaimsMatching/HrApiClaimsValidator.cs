using System.Net.Http.Headers;
using System.Text;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace account_recovery_claim_matching;

/// <summary>
/// Validates Verified ID claims against an external HR API.
/// 
/// Supports two authentication modes (set via HrApi:AuthMode):
///   "apikey"  — sends a static key in the x-api-key header
///   "oauth"   — acquires an OAuth 2.0 bearer token via DefaultAzureCredential
///               (managed identity in Azure, VS/CLI credentials locally)
/// </summary>
public class HrApiClaimsValidator : IClaimsValidator
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<HrApiClaimsValidator> _logger;
    private readonly string _baseUrl;
    private readonly string _authMode;
    private readonly TokenCredential? _tokenCredential;
    private readonly string? _oauthScope;

    public HrApiClaimsValidator(
        HttpClient httpClient,
        IConfiguration configuration,
        ILogger<HrApiClaimsValidator> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        _baseUrl = configuration["HrApi:BaseUrl"]
            ?? throw new InvalidOperationException("HrApi:BaseUrl configuration is required.");

        _authMode = configuration["HrApi:AuthMode"] ?? "apikey";

        if (string.Equals(_authMode, "oauth", StringComparison.OrdinalIgnoreCase))
        {
            _oauthScope = configuration["HrApi:OAuthScope"]
                ?? throw new InvalidOperationException("HrApi:OAuthScope is required when AuthMode is 'oauth'.");
            _tokenCredential = new DefaultAzureCredential();
        }
        else
        {
            var apiKey = configuration["HrApi:ApiKey"];
            if (!string.IsNullOrEmpty(apiKey))
            {
                _httpClient.DefaultRequestHeaders.Add("x-api-key", apiKey);
            }
        }
    }

    public async Task<ClaimMatchResult> ValidateClaimsAsync(
        string? upn,
        string? employeeId,
        Dictionary<string, string> claims)
    {
        _logger.LogInformation("Calling HR API for UPN={Upn}, EmployeeId={EmployeeId}, ClaimCount={Count}",
            upn, employeeId, claims.Count);

        var payload = new Dictionary<string, object?>
        {
            ["upn"] = upn,
            ["employeeId"] = employeeId,
            ["claims"] = claims
        };

        var content = new StringContent(
            JsonConvert.SerializeObject(payload),
            Encoding.UTF8,
            "application/json");

        // Acquire OAuth bearer token if using OAuth mode
        if (string.Equals(_authMode, "oauth", StringComparison.OrdinalIgnoreCase)
            && _tokenCredential != null && _oauthScope != null)
        {
            var tokenResult = await _tokenCredential.GetTokenAsync(
                new TokenRequestContext(new[] { _oauthScope }), CancellationToken.None);
            _httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", tokenResult.Token);
        }

        var response = await _httpClient.PostAsync($"{_baseUrl.TrimEnd('/')}/validate", content);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("HR API returned {StatusCode}: {Reason}", response.StatusCode, response.ReasonPhrase);
            return new ClaimMatchResult
            {
                Result = "fail",
                FailedClaims = new List<string> { "hrApiError" }
            };
        }

        var responseBody = await response.Content.ReadAsStringAsync();
        var result = JsonConvert.DeserializeObject<ClaimMatchResult>(responseBody);

        if (result == null)
        {
            _logger.LogError("HR API returned an unparseable response.");
            return new ClaimMatchResult
            {
                Result = "fail",
                FailedClaims = new List<string> { "hrApiError" }
            };
        }

        _logger.LogInformation("HR API validation result: {Result}", result.Result);
        return result;
    }
}
