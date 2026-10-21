using System.Text.Json;
using Stellaegis.Primitives;

namespace Stellaegis.Operations;

/// <summary>What a caller sends to launch a job on the routes of the module; the visibility is <c>initiator_only</c> or <c>space</c>.</summary>
public sealed record LaunchJobRequest(string? JobType, JsonElement? Input = null, string? Visibility = null, string? IdempotencyKey = null);

/// <summary>A job as the routes of the module answer it.</summary>
public sealed record JobView(
    Guid JobUniqueId,
    string JobType,
    string Status,
    string Visibility,
    string? Compartment,
    string Actor,
    JsonElement? Input,
    JsonElement? Result,
    int? Progress,
    string ProgressLabel,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    Guid? ScheduleUniqueId,
    DateTimeOffset? ScheduledFor)
{
    /// <summary>The view of a job.</summary>
    public static JobView Of(JobRecord job) => new(
        Guard.NotNull(job).JobUniqueId, job.JobType, Lower(job.Status), Lower(job.Visibility), job.Space.Compartment?.Value, job.Actor.Value,
        job.Input, job.Result, job.Progress, job.ProgressLabel, job.CreatedAt, job.StartedAt, job.CompletedAt, job.ScheduleUniqueId, job.ScheduledFor);

    internal static string Lower<T>(T value) where T : struct, Enum
        => string.Concat(value.ToString().Select((c, i) => i > 0 && char.IsUpper(c) ? "_" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()));
}

/// <summary>What the administration sends to create or change a schedule; the policy is <c>forbid</c>, <c>allow</c> or <c>replace</c>, the compartment <see langword="null"/> for the shared space.</summary>
public sealed record ScheduleRequest(
    string? Name,
    string? CronExpression,
    string? JobType,
    string? Compartment = null,
    string? TimeZoneId = null,
    JsonElement? Input = null,
    string? ConcurrencyPolicy = null,
    bool? Enabled = null);

/// <summary>A schedule as the routes of the module answer it.</summary>
public sealed record ScheduleView(
    Guid ScheduleUniqueId,
    string Name,
    string CronExpression,
    string TimeZoneId,
    string JobType,
    string? Compartment,
    JsonElement? Input,
    string ConcurrencyPolicy,
    bool Enabled,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastEvaluatedAt)
{
    /// <summary>The view of a schedule.</summary>
    public static ScheduleView Of(Schedule schedule) => new(
        Guard.NotNull(schedule).ScheduleUniqueId, schedule.Name, schedule.CronExpression, schedule.TimeZoneId, schedule.JobType,
        schedule.Space.Compartment?.Value, schedule.Input, JobView.Lower(schedule.ConcurrencyPolicy), schedule.Enabled,
        schedule.CreatedBy.Value, schedule.CreatedAt, schedule.LastEvaluatedAt);
}
