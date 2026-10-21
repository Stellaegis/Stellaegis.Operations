using System.Text.Json;
using Stellaegis.Primitives;

namespace Stellaegis.Operations;

/// <summary>
/// The jobs, as the module keeps them: the port of its storage. Every call says where it acts and on whose behalf:
/// a read or a write in a space takes the space, so that the store never serves a job of another; the claim of the
/// workers and the oversight of the administration act on every space, the store filtering itself. The actor is
/// the one the call is made for — the user of a request, the worker, the scheduler —, which the store records in
/// its session.
/// </summary>
public interface IJobStore
{
    /// <summary>
    /// Registers a job, pending, and returns its identifier. A request whose idempotency key is already
    /// recorded in its space returns the job it created the first time, and creates nothing.
    /// </summary>
    Task<Guid> CreateAsync(JobRequest request, DateTimeOffset expiresAt, CancellationToken cancellationToken = default);

    /// <summary>The job, in its space, or <see langword="null"/> when the space holds none by this identifier.</summary>
    Task<JobRecord?> GetAsync(Space space, Guid jobUniqueId, Actor actor, CancellationToken cancellationToken = default);

    /// <summary>The job, whatever its space — the view of the administration —, or <see langword="null"/>.</summary>
    Task<JobRecord?> FindAsync(Guid jobUniqueId, Actor actor, CancellationToken cancellationToken = default);

    /// <summary>The jobs matching the query, the most recent first.</summary>
    Task<IReadOnlyList<JobRecord>> ListAsync(JobQuery query, Actor actor, CancellationToken cancellationToken = default);

    /// <summary>
    /// Claims up to <paramref name="max"/> jobs for <paramref name="worker"/>: the pending ones, and the running
    /// ones whose claim expired, each marked running and claimed until now plus <paramref name="lease"/>. Atomic:
    /// two workers never claim the same job.
    /// </summary>
    Task<IReadOnlyList<JobRecord>> ClaimAsync(int max, TimeSpan lease, Actor worker, CancellationToken cancellationToken = default);

    /// <summary>Records the progress a handler reports.</summary>
    Task ReportProgressAsync(Guid jobUniqueId, int? percent, string? label, Actor actor, CancellationToken cancellationToken = default);

    /// <summary>Records the outcome of a job: a terminal status and what it produced.</summary>
    Task CompleteAsync(Guid jobUniqueId, JobStatus status, JsonElement? result, Actor actor, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancels a pending job of the space. Returns the job, or <see langword="null"/> when the space holds none by
    /// this identifier.
    /// </summary>
    /// <exception cref="JobNotPendingException">The job is not pending.</exception>
    Task<JobRecord?> CancelAsync(Space space, Guid jobUniqueId, Actor actor, CancellationToken cancellationToken = default);

    /// <summary>Deletes every job of the compartment, whatever its state. Replayable: answers how many were deleted.</summary>
    Task<int> PurgeCompartmentAsync(Compartment compartment, Actor actor, CancellationToken cancellationToken = default);

    /// <summary>Deletes the completed jobs past their expiry. Answers how many were deleted.</summary>
    Task<int> CleanExpiredAsync(Actor actor, CancellationToken cancellationToken = default);
}

/// <summary>The schedules, as the module keeps them, and the occurrences claimed from them.</summary>
public interface IScheduleStore
{
    /// <summary>The enabled schedules, those a worker evaluates.</summary>
    Task<IReadOnlyList<Schedule>> ListEnabledAsync(Actor actor, CancellationToken cancellationToken = default);

    /// <summary>Every schedule, enabled or not, in one space or in every one when <paramref name="space"/> is <see langword="null"/>.</summary>
    Task<IReadOnlyList<Schedule>> ListAsync(Space? space, Actor actor, CancellationToken cancellationToken = default);

    /// <summary>The schedule, or <see langword="null"/>.</summary>
    Task<Schedule?> GetAsync(Guid scheduleUniqueId, Actor actor, CancellationToken cancellationToken = default);

    /// <summary>Creates the schedule, or replaces the one of the same identifier, keeping its evaluation mark.</summary>
    Task UpsertAsync(Schedule schedule, Actor actor, CancellationToken cancellationToken = default);

    /// <summary>Removes the schedule and its claimed occurrences. Answers whether it existed.</summary>
    Task<bool> RemoveAsync(Guid scheduleUniqueId, Actor actor, CancellationToken cancellationToken = default);

    /// <summary>
    /// Claims an occurrence of a schedule: <see langword="true"/> for the one worker that claims it first,
    /// <see langword="false"/> for every other. Atomic across workers.
    /// </summary>
    Task<bool> TryClaimOccurrenceAsync(Guid scheduleUniqueId, DateTimeOffset scheduledFor, Actor actor, CancellationToken cancellationToken = default);

    /// <summary>Records up to when the occurrences of the schedule were evaluated, so that a restart resumes there.</summary>
    Task MarkEvaluatedAsync(Guid scheduleUniqueId, DateTimeOffset through, Actor actor, CancellationToken cancellationToken = default);

    /// <summary>Deletes every schedule of the compartment, and their occurrences. Answers how many schedules were deleted.</summary>
    Task<int> PurgeCompartmentAsync(Compartment compartment, Actor actor, CancellationToken cancellationToken = default);
}
