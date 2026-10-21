using System.Text.Json;
using Stellaegis.Primitives;

namespace Stellaegis.Operations;

/// <summary>The life of a job: registered, claimed by a worker, then succeeded, failed or cancelled.</summary>
public enum JobStatus
{
    /// <summary>Registered, waiting for a worker.</summary>
    Pending,

    /// <summary>Claimed by a worker, running.</summary>
    Running,

    /// <summary>Completed by its handler.</summary>
    Succeeded,

    /// <summary>Failed: the result carries the code of the failure, and its message when it is meant for the caller.</summary>
    Failed,

    /// <summary>Cancelled before a worker claimed it.</summary>
    Cancelled,
}

/// <summary>
/// Who may see a job among the users of its space: a fact about the job, which the application's authorization
/// handler on the resource reads (<c>AuthorizeResourceAsync</c>). The module decides nothing from it.
/// </summary>
public enum JobVisibility
{
    /// <summary>The actor who launched it.</summary>
    InitiatorOnly,

    /// <summary>Every user of the space.</summary>
    Space,
}

/// <summary>
/// What a schedule does when an occurrence is due while a job of a previous occurrence is still pending or
/// running.
/// </summary>
public enum ConcurrencyPolicy
{
    /// <summary>Skip the occurrence.</summary>
    Forbid,

    /// <summary>Launch the occurrence anyway.</summary>
    Allow,

    /// <summary>Cancel the pending jobs of the schedule — a running one runs to its end —, then launch the occurrence.</summary>
    Replace,
}

/// <summary>
/// A job as the store holds it: where it acts, who asked for it, its state and its outcome. Immutable; the store
/// returns a new snapshot at each read.
/// </summary>
public sealed record JobRecord
{
    /// <summary>The public identifier of the job.</summary>
    public required Guid JobUniqueId { get; init; }

    /// <summary>The type of job, an identifier such as <c>tenant_provision</c>, naming the handler that runs it.</summary>
    public required string JobType { get; init; }

    /// <summary>The state of the job.</summary>
    public required JobStatus Status { get; init; }

    /// <summary>Who may see it among the users of its space.</summary>
    public required JobVisibility Visibility { get; init; }

    /// <summary>
    /// Where the job acts: a compartment, or the shared space for a job of the assembly that acts on spaces, its
    /// targets being part of its input.
    /// </summary>
    public required Space Space { get; init; }

    /// <summary>Who asked for the job, and answers for it; its handler runs through the worker on this actor's behalf.</summary>
    public required Actor Actor { get; init; }

    /// <summary>The input handed to the handler, opaque to the module.</summary>
    public JsonElement? Input { get; init; }

    /// <summary>The outcome: what the handler returned, or the code and message of a failure.</summary>
    public JsonElement? Result { get; init; }

    /// <summary>The progress reported by the handler, 0 to 100, or <see langword="null"/>.</summary>
    public int? Progress { get; init; }

    /// <summary>The label of the progress reported by the handler.</summary>
    public string ProgressLabel { get; init; } = string.Empty;

    /// <summary>When the job was registered.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>When a worker claimed it.</summary>
    public DateTimeOffset? StartedAt { get; init; }

    /// <summary>When it reached a terminal state.</summary>
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>When a completed job may be cleaned.</summary>
    public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>Until when the claim of the running worker holds; a running job past it may be claimed again.</summary>
    public DateTimeOffset? ClaimedUntil { get; init; }

    /// <summary>The key the caller gave so that a replayed launch creates nothing twice, or empty.</summary>
    public string IdempotencyKey { get; init; } = string.Empty;

    /// <summary>The schedule that launched it, or <see langword="null"/> when a caller did.</summary>
    public Guid? ScheduleUniqueId { get; init; }

    /// <summary>The occurrence of the schedule that launched it.</summary>
    public DateTimeOffset? ScheduledFor { get; init; }

    /// <summary>Whether the job is pending or running.</summary>
    public bool IsInFlight => Status is JobStatus.Pending or JobStatus.Running;
}

/// <summary>What a caller gives to launch a job.</summary>
public sealed record JobRequest
{
    /// <summary>The type of job, an identifier naming a registered handler.</summary>
    public required string JobType { get; init; }

    /// <summary>Where the job acts.</summary>
    public required Space Space { get; init; }

    /// <summary>Who asks for it.</summary>
    public required Actor Actor { get; init; }

    /// <summary>The input handed to the handler.</summary>
    public JsonElement? Input { get; init; }

    /// <summary>Who may see it among the users of its space.</summary>
    public JobVisibility Visibility { get; init; } = JobVisibility.InitiatorOnly;

    /// <summary>When a completed job may be cleaned; the module's retention unless given.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>A key of the caller's, unique within the space: a replayed launch with the same key returns the same job.</summary>
    public string IdempotencyKey { get; init; } = string.Empty;

    /// <summary>The schedule launching it, when one does.</summary>
    public Guid? ScheduleUniqueId { get; init; }

    /// <summary>The occurrence of the schedule launching it.</summary>
    public DateTimeOffset? ScheduledFor { get; init; }
}

/// <summary>The filters of a list of jobs. A filter left <see langword="null"/> does not apply.</summary>
public sealed record JobQuery
{
    /// <summary>The space of the jobs, or <see langword="null"/> for every space — the administration's view.</summary>
    public Space? Space { get; init; }

    /// <summary>The actor who asked for them.</summary>
    public Actor? Actor { get; init; }

    /// <summary>Their state.</summary>
    public JobStatus? Status { get; init; }

    /// <summary>Their type.</summary>
    public string? JobType { get; init; }

    /// <summary>The schedule that launched them.</summary>
    public Guid? ScheduleUniqueId { get; init; }

    /// <summary>At most this many, the most recent first.</summary>
    public int MaxResults { get; init; } = 100;
}

/// <summary>The codes of the errors the module lets out, <c>Entity.Reason</c>.</summary>
public static class JobErrors
{
    /// <summary>The job type names no registered handler.</summary>
    public const string UnknownType = "JobType.Unknown";

    /// <summary>The job is not pending: only a pending job is cancelled.</summary>
    public const string NotPending = "Job.NotPending";

    /// <summary>The handler threw an exception not meant for the caller; the log holds it under the identifier the result gives.</summary>
    public const string Failed = "Job.Failed";

    /// <summary>The schedule does not hold: its cron expression, its time zone or its job type.</summary>
    public const string InvalidSchedule = "Schedule.Invalid";
}

/// <summary>The job type of a launch names no registered handler.</summary>
public sealed class UnknownJobTypeException(string jobType)
    : UserFacingException(Error.Validation(JobErrors.UnknownType, $"No handler is registered for the job type '{jobType}'."))
{
    /// <summary>The job type.</summary>
    public string JobType { get; } = jobType;
}

/// <summary>The job is not pending, and only a pending job is cancelled: a running one runs to its end.</summary>
public sealed class JobNotPendingException(Guid jobUniqueId, JobStatus status)
    : UserFacingException(Error.Conflict(JobErrors.NotPending, $"The job {jobUniqueId} is {status}, not pending: it cannot be cancelled."))
{
    /// <summary>The job.</summary>
    public Guid JobUniqueId { get; } = jobUniqueId;
}

/// <summary>A schedule does not hold: its cron expression, its time zone or its job type.</summary>
public sealed class InvalidScheduleException(string message)
    : UserFacingException(Error.Validation(JobErrors.InvalidSchedule, message));
