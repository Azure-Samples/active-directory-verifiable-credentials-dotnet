using System.IdentityModel.Tokens.Jwt;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace account_recovery_claim_matching;

/// <summary>
/// Validates JWT Bearer tokens issued by Entra ID via the OAuth 2.0 client credentials flow.
/// Used when the function is called by an Entra custom authentication extension.
/// When EntraId settings are not configured, token validation is skipped (testing mode).
/// </summary>
public class TokenValidationService
{
    private readonly ILogger<TokenValidationService> _logger;
    private readonly bool _isEnabled;
    private readonly ConfigurationManager<OpenIdConnectConfiguration>? _configManager;
    private readonly TokenValidationParameters? _validationParameters;
    private readonly HashSet<string> _authorizedClientAppIds;

    public TokenValidationService(IConfiguration configuration, ILogger<TokenValidationService> logger)
    {
        _logger = logger;

        var tenantId = configuration["EntraId:TenantId"];
        var clientId = configuration["EntraId:ClientId"];

        _isEnabled = !string.IsNullOrEmpty(tenantId) && !string.IsNullOrEmpty(clientId);

        // Build the list of authorized calling app IDs (azp claim).
        // EntraId:AuthorizedClientAppIds is a semicolon-separated list; defaults to Entra custom auth extension app.
        var authorizedApps = configuration["EntraId:AuthorizedClientAppIds"] ?? "99045fe1-7639-4a75-9d4a-577b6ca3810f";
        _authorizedClientAppIds = new HashSet<string>(
            authorizedApps.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.OrdinalIgnoreCase);

        if (!_isEnabled)
        {
            _logger.LogInformation("EntraId settings not configured — Bearer token validation is disabled.");
            return;
        }

        _logger.LogInformation("Authorized client app IDs (azp): {AppIds}", string.Join(", ", _authorizedClientAppIds));

        var authority = $"https://login.microsoftonline.com/{tenantId}/v2.0";

        _configManager = new ConfigurationManager<OpenIdConnectConfiguration>(
            $"{authority}/.well-known/openid-configuration",
            new OpenIdConnectConfigurationRetriever());

        _validationParameters = new TokenValidationParameters
        {
            ValidAudiences = new[] { clientId, $"api://{clientId}" },
            ValidIssuers = new[]
            {
                $"https://login.microsoftonline.com/{tenantId}/v2.0",
                $"https://sts.windows.net/{tenantId}/"
            },
            ValidateAudience = true,
            ValidateIssuer = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true
        };
    }

    public bool IsEnabled => _isEnabled;

    public async Task<(bool IsValid, string? ErrorMessage)> ValidateTokenAsync(string token)
    {
        if (!_isEnabled)
        {
            return (true, null);
        }

        try
        {
            var config = await _configManager!.GetConfigurationAsync(CancellationToken.None);
            _validationParameters!.IssuerSigningKeys = config.SigningKeys;

            var handler = new JwtSecurityTokenHandler();
            handler.ValidateToken(token, _validationParameters, out var validatedToken);

            // Verify the azp (authorized party) claim — ensures only allowed client apps can call this API
            var jwt = (JwtSecurityToken)validatedToken;
            var azp = jwt.Claims.FirstOrDefault(c => c.Type == "azp")?.Value
                   ?? jwt.Claims.FirstOrDefault(c => c.Type == "appid")?.Value;

            if (string.IsNullOrEmpty(azp))
            {
                _logger.LogWarning("Bearer token missing azp/appid claim.");
                return (false, "Token does not contain an authorized party (azp) claim.");
            }

            if (!_authorizedClientAppIds.Contains(azp))
            {
                _logger.LogWarning("Unauthorized client app ID (azp): {Azp}", azp);
                return (false, $"Client app '{azp}' is not authorized to call this API.");
            }

            _logger.LogInformation("Bearer token validated successfully. azp={Azp}", azp);
            return (true, null);
        }
        catch (SecurityTokenException ex)
        {
            _logger.LogWarning("Bearer token validation failed: {Message}", ex.Message);
            return (false, ex.Message);
        }
    }
}
