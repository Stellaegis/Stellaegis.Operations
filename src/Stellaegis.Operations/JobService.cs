using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stellaegis.Primitives;

namespace Stellaegis.Operations;

/// <summary>
/// What the application does with the jobs: launch one, follow it, cancel it, in a space and for an actor; and,
/// on behalf of the administration, see the jobs of every space and purge a compartment. The core of the module,
/// consumed in process or behind the routes of <c>Stellaegis.Operations.AspNetCore</c>.
/// </summary>
public interface IJobService
{
    /// <summary>
    /// Launches a job: registers it pending, for a worker to claim. A request whose idempotency key is already
    /// recorded in its space answers the job it created the first time.
    /// </summary>
    /// <exception cref="UnknownJobTypeException">The job type names no registered handler.</exception>
    Task<JobRecord> LaunchAsync(JobRequest request, CancellationToken cancellationToken = default);

    /// <summary>The job, in the space, or <see langword="null"/>.</summary>
    Task<JobRecord?> GetAsync(Space space, Guid jobUniqueId, Actor actor, CancellationToken cancellationToken = default);

    /// <summary>The jobs of the space matching the query, the most recent first.</summary>
    Task<IReadOnlyList<JobRecord>> ListAsync(Space space, JobQuery query, Actor actor, CancellationToken cancellationToken = default);

    /// <summary>Cancels a pending job of the space. Answers the job, or <see langword="null"/> when the space holds none.</summary>
    /// <exception cref="JobNotPendingException">The job is not pending.</exception>
    Task<JobRecord?> CancelAsync(Space space, Guid jobUniqueId, Actor actor, CancellationToken cancellationToken = default);

    /// <summary>The job, whatever its space: the view of the administration.</summary>
    Task<JobRecord?> FindAsync(Guid jobUniqueId, Actor actor, CancellationToken cancellationToken = default);

    /// <summary>The jobs of every space matching the query: the view of the administration.</summary>
    Task<IReadOnlyList<JobRecord>> OverseeAsync(JobQuery query, Actor actor, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes every job and every schedule of the compartment, whatever their state. Replayable: a second call
    /// deletes nothing and answers 0. The actor is recorded in the log, at the start and at the end.
    /// </summary>
    Task<int> PurgeCompartmentAsync(Compartment compartment, Actor actor, CancellationToken cancellationToken = default);

    /// <summary>The job types the handlers of the application run.</summary>
    IReadOnlyCollection<string> JobTypes { get; }
}

/// <summary>The job types the application registered, read once from its handlers.</summary>
internal sealed class JobTypeCatalog(IEnumerable<IJobHandler> handlers)
{
    public IReadOnlyCollection<string> Types { get; } = handlers.Select(handler => handler.JobType).ToHashSet(StringComparer.Ordinal);

    public bool Contains(string jobType) => Types.Contains(jobType);
}

/// <inheritdoc cref="IJobService"/>
internal sealed class JobService(
    IJobStore jobs,
    IScheduleStore schedules,
    JobTypeCatalog catalog,
    IOptions<OperationsOptions> options,
    TimeProvider clock,
    ILogger<JobService> logger) : IJobService
{
    public IReadOnlyCollection<string> JobTypes => catalog.Types;

    public async Task<JobRecord> LaunchAsync(JobRequest request, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(request);
        Guard.NotNull(request.Space);
        Guard.NotNull(request.Actor);
        if (!NameRules.IsIdentifier(request.JobType) || !catalog.Contains(request.JobType))
            throw new UnknownJobTypeException(request.JobType);
        var expiresAt = request.ExpiresAt ?? clock.GetUtcNow() + options.Value.JobRetention;
        var id = await jobs.CreateAsync(request, expiresAt, cancellationToken).ConfigureAwait(false);
        return await jobs.GetAsync(request.Space, id, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"The job {id} was created and cannot be read back.");
    }

    public Task<JobRecord?> GetAsync(Space space, Guid jobUniqueId, Actor actor, CancellationToken cancellationToken = default)
        => jobs.GetAsync(Guard.NotNull(space), jobUniqueId, cancellationToken);

    public Task<IReadOnlyList<JobRecord>> ListAsync(Space space, JobQuery query, Actor actor, CancellationToken cancellationToken = default)
        => jobs.ListAsync(Guard.NotNull(query) with { Space = Guard.NotNull(space) }, cancellationToken);

    public Task<JobRecord?> CancelAsync(Space space, Guid jobUniqueId, Actor actor, CancellationToken cancellationToken = default)
        => jobs.CancelAsync(Guard.NotNull(space), jobUniqueId, Guard.NotNull(actor), cancellationToken);

    public Task<JobRecord?> FindAsync(Guid jobUniqueId, Actor actor, CancellationToken cancellationToken = default)
        => jobs.FindAsync(jobUniqueId, cancellationToken);

    public Task<IReadOnlyList<JobRecord>> OverseeAsync(JobQuery query, Actor actor, CancellationToken cancellationToken = default)
        => jobs.ListAsync(Guard.NotNull(query), cancellationToken);

    public async Task<int> PurgeCompartmentAsync(Compartment compartment, Actor actor, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(compartment);
        Guard.NotNull(actor);
        logger.LogInformation("Purge of the compartment {Compartment} started by {Actor}.", compartment, actor);
        var purged = await jobs.PurgeCompartmentAsync(compartment, actor, cancellationToken).ConfigureAwait(false)
            + await schedules.PurgeCompartmentAsync(compartment, actor, cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Purge of the compartment {Compartment} by {Actor}: {Count} jobs and schedules deleted.", compartment, actor, purged);
        return purged;
    }
}

/// <summary>The outcome a failed job records: the code, the message when it is meant for the caller, the log identifier otherwise.</summary>
public sealed record JobFailure(string Code, string? Message, string? LogId)
{
    /// <summary>The outcome as JSON.</summary>
    public JsonElement ToJson() => JsonSerializer.SerializeToElement(this, JobJson.Options);
}

/// <summary>The JSON options the module writes its outcomes with: camel case, no indentation.</summary>
public static class JobJson
{
    /// <summary>The options.</summary>
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web);
}
