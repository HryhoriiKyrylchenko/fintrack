using FinTrack.Shared.Infrastructure.Outbox;
using Mediator;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace FinTrack.Api.Outbox;

/// <summary>
/// Timer: delivers outbox messages that were not delivered right after their commit (ARCHITECTURE
/// 2.3). The schedule is the app setting <c>OutboxSweepSchedule</c> (an NCRONTAB expression); the
/// host keeps the timer's state in storage, which is why Azurite runs locally.
/// </summary>
public sealed partial class SweepOutboxFunction(ISender sender, ILogger<SweepOutboxFunction> logger)
{
    /// <summary>The function name.</summary>
    public const string Name = "SweepOutbox";

    /// <summary>Runs one sweep.</summary>
    [Function(Name)]
    public async Task Run([TimerTrigger("%OutboxSweepSchedule%")] TimerInfo timer, CancellationToken cancellationToken)
    {
        var result = await sender.Send(new DispatchPendingOutbox(), cancellationToken).ConfigureAwait(false);
        LogSwept(result.ValueOrDefault);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Outbox sweep delivered {Delivered} message(s).")]
    private partial void LogSwept(int delivered);
}
