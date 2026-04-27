namespace account_recovery_claim_matching;

/// <summary>
/// Abstraction for claim validation. Implementations can call HR APIs,
/// query databases, read Excel files, etc.
/// The claims dictionary is dynamic — callers can send any set of key/value
/// pairs and the validator will compare whichever claims are present.
/// </summary>
public interface IClaimsValidator
{
    Task<ClaimMatchResult> ValidateClaimsAsync(
        string? upn,
        string? employeeId,
        Dictionary<string, string> claims);
}

public class ClaimMatchResult
{
    public string Result { get; set; } = "pass";
    public List<string>? FailedClaims { get; set; }
}
