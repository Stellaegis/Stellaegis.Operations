using Stellaegis.Primitives.Hosting;

namespace Stellaegis.Operations;

/// <summary>
/// The options of the module, bound from <c>Stellaegis:Operations</c>: how the workers poll, how many jobs run at
/// once, how long a claim holds, how long a completed job is kept.
/// <code>
/// Stellaegis:Operations:PollingInterval         = 00:00:05
/// Stellaegis:Operations:MaxConcurrentJobs       = 4
/// Stellaegis:Operations:ClaimLease              = 00:10:00
/// Stellaegis:Operations:JobRetention            = 7.00:00:00
/// Stellaegis:Operations:SchedulePollingInterval = 00:00:30
/// </code>
/// </summary>
public sealed class OperationsOptions : IValidatableOptions
{
    /// <summary>The configuration section the options are bound from.</summary>
    public const string SectionName = "Stellaegis:Operations";

    /// <summary>How often the job worker looks for pending jobs. Five seconds unless set.</summary>
    public TimeSpan PollingInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How many jobs one worker runs at once. Four unless set.</summary>
    public int MaxConcurrentJobs { get; init; } = 4;

    /// <summary>
    /// How long the claim of a worker holds: a running job past it is claimed again, by this worker or another,
    /// which is how a job survives the crash of the process running it. Ten minutes unless set; longer than the
    /// longest job.
    /// </summary>
    public TimeSpan ClaimLease { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>How long a completed job is kept before the cleaning removes it. Seven days unless set.</summary>
    public TimeSpan JobRetention { get; init; } = TimeSpan.FromDays(7);

    /// <summary>How often the schedule worker evaluates the schedules. Thirty seconds unless set; cron has the minute.</summary>
    public TimeSpan SchedulePollingInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How many occurrences of one schedule are launched in one evaluation, when a stop left many due: the
    /// rest wait for the next evaluation. Ten unless set.
    /// </summary>
    public int MaxOccurrencesPerEvaluation { get; init; } = 10;

    /// <inheritdoc/>
    public string? FindError()
    {
        if (PollingInterval <= TimeSpan.Zero)
            return "PollingInterval must be positive.";
        if (MaxConcurrentJobs < 1)
            return "MaxConcurrentJobs must be at least 1.";
        if (ClaimLease <= TimeSpan.Zero)
            return "ClaimLease must be positive.";
        if (JobRetention <= TimeSpan.Zero)
            return "JobRetention must be positive.";
        if (SchedulePollingInterval <= TimeSpan.Zero)
            return "SchedulePollingInterval must be positive.";
        if (MaxOccurrencesPerEvaluation < 1)
            return "MaxOccurrencesPerEvaluation must be at least 1.";
        return null;
    }
}
