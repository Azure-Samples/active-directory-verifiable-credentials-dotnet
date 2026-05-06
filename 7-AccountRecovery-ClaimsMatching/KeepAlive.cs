using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace account_recovery_claim_matching;

public class KeepAlive
{
    private readonly ILogger<KeepAlive> _logger;

    public KeepAlive(ILogger<KeepAlive> logger)
    {
        _logger = logger;
    }

    [Function("KeepAlive")]
    public void Run([TimerTrigger("0 */4 * * * *")] TimerInfo timer)
    {
        _logger.LogInformation("Keep-alive ping at {Time}", DateTime.UtcNow);
    }
}
