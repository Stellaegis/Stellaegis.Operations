using System.Text.Json;
using Stellaegis.Primitives;
using Stellaegis.Primitives.Data;

namespace Stellaegis.Operations.PostgreSQL;

/// <summary>
/// The jobs on PostgreSQL. Each call opens a unit of work in the space it acts in, under the actor it acts for;
/// the claim, the oversight and the cleaning open it in the shared space, where the queue as a whole is served.
/// </summary>
public sealed class PostgreSqlJobStore(ISpaceUnitOfWorkFactory<OperationsDatabase> factory, TimeProvider clock) : IJobStore
{
    /// <inheritdoc/>
    public async Task<Guid> CreateAsync(JobRequest request, DateTimeOffset expiresAt, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(request);
        await using var uow = await factory.OpenAsync(request.Space, request.Actor, cancellationToken).ConfigureAwait(false);
        var jobs = uow.Repo<IJobRepository>();
        var id = await jobs.InsertAsync(Guid.NewGuid(), request, clock.GetUtcNow(), expiresAt, cancellationToken).ConfigureAwait(false);
        if (id is { } inserted)
            return inserted;
        return await jobs.FindByIdempotencyKeyAsync(request.Space, request.IdempotencyKey, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The job was neither inserted nor found by its idempotency key.");
    }

    /// <inheritdoc/>
    public async Task<JobRecord?> GetAsync(Space space, Guid jobUniqueId, Actor actor, CancellationToken cancellationToken = default)
    {
        await using var uow = await factory.OpenAsync(Guard.NotNull(space), Guard.NotNull(actor), cancellationToken).ConfigureAwait(false);
        return await uow.Repo<IJobRepository>().GetAsync(space, jobUniqueId, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<JobRecord?> FindAsync(Guid jobUniqueId, Actor actor, CancellationToken cancellationToken = default)
    {
        await using var uow = await factory.OpenAsync(Space.Shared, Guard.NotNull(actor), cancellationToken).ConfigureAwait(false);
        return await uow.Repo<IJobRepository>().GetAsync(null, jobUniqueId, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<JobRecord>> ListAsync(JobQuery query, Actor actor, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(query);
        await using var uow = await factory.OpenAsync(query.Space ?? Space.Shared, Guard.NotNull(actor), cancellationToken).ConfigureAwait(false);
        return await uow.Repo<IJobRepository>().ListAsync(query, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<JobRecord>> ClaimAsync(int max, TimeSpan lease, Actor worker, CancellationToken cancellationToken = default)
    {
        await using var uow = await factory.OpenAsync(Space.Shared, Guard.NotNull(worker), cancellationToken).ConfigureAwait(false);
        return await uow.Repo<IJobRepository>().ClaimAsync(max, clock.GetUtcNow(), lease, worker, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task ReportProgressAsync(Guid jobUniqueId, int? percent, string? label, Actor actor, CancellationToken cancellationToken = default)
    {
        await using var uow = await factory.OpenAsync(Space.Shared, Guard.NotNull(actor), cancellationToken).ConfigureAwait(false);
        await uow.Repo<IJobRepository>().ReportProgressAsync(jobUniqueId, percent, label, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task CompleteAsync(Guid jobUniqueId, JobStatus status, JsonElement? result, Actor actor, CancellationToken cancellationToken = default)
    {
        await using var uow = await factory.OpenAsync(Space.Shared, Guard.NotNull(actor), cancellationToken).ConfigureAwait(false);
        await uow.Repo<IJobRepository>().CompleteAsync(jobUniqueId, status, result, clock.GetUtcNow(), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<JobRecord?> CancelAsync(Space space, Guid jobUniqueId, Actor actor, CancellationToken cancellationToken = default)
    {
        await using var uow = await factory.OpenAsync(Guard.NotNull(space), Guard.NotNull(actor), cancellationToken).ConfigureAwait(false);
        return await uow.Repo<IJobRepository>().CancelAsync(space, jobUniqueId, clock.GetUtcNow(), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<int> PurgeCompartmentAsync(Compartment compartment, Actor actor, CancellationToken cancellationToken = default)
    {
        await using var uow = await factory.OpenAsync(Space.Of(Guard.NotNull(compartment)), Guard.NotNull(actor), cancellationToken).ConfigureAwait(false);
        return await uow.Repo<IJobRepository>().DeleteOfCompartmentAsync(compartment, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<int> CleanExpiredAsync(Actor actor, CancellationToken cancellationToken = default)
    {
        await using var uow = await factory.OpenAsync(Space.Shared, Guard.NotNull(actor), cancellationToken).ConfigureAwait(false);
        return await uow.Repo<IJobRepository>().DeleteExpiredAsync(clock.GetUtcNow(), cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>The schedules on PostgreSQL, the evaluation and the claims of occurrences opened in the shared space.</summary>
public sealed class PostgreSqlScheduleStore(ISpaceUnitOfWorkFactory<OperationsDatabase> factory) : IScheduleStore
{
    /// <inheritdoc/>
    public async Task<IReadOnlyList<Schedule>> ListEnabledAsync(Actor actor, CancellationToken cancellationToken = default)
    {
        await using var uow = await factory.OpenAsync(Space.Shared, Guard.NotNull(actor), cancellationToken).ConfigureAwait(false);
        return await uow.Repo<IScheduleRepository>().ListAsync(null, enabledOnly: true, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Schedule>> ListAsync(Space? space, Actor actor, CancellationToken cancellationToken = default)
    {
        await using var uow = await factory.OpenAsync(space ?? Space.Shared, Guard.NotNull(actor), cancellationToken).ConfigureAwait(false);
        return await uow.Repo<IScheduleRepository>().ListAsync(space, enabledOnly: false, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<Schedule?> GetAsync(Guid scheduleUniqueId, Actor actor, CancellationToken cancellationToken = default)
    {
        await using var uow = await factory.OpenAsync(Space.Shared, Guard.NotNull(actor), cancellationToken).ConfigureAwait(false);
        return await uow.Repo<IScheduleRepository>().GetAsync(scheduleUniqueId, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task UpsertAsync(Schedule schedule, Actor actor, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(schedule);
        await using var uow = await factory.OpenAsync(schedule.Space, Guard.NotNull(actor), cancellationToken).ConfigureAwait(false);
        await uow.Repo<IScheduleRepository>().UpsertAsync(schedule, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<bool> RemoveAsync(Guid scheduleUniqueId, Actor actor, CancellationToken cancellationToken = default)
    {
        await using var uow = await factory.OpenAsync(Space.Shared, Guard.NotNull(actor), cancellationToken).ConfigureAwait(false);
        return await uow.Repo<IScheduleRepository>().DeleteAsync(scheduleUniqueId, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<bool> TryClaimOccurrenceAsync(Guid scheduleUniqueId, DateTimeOffset scheduledFor, Actor actor, CancellationToken cancellationToken = default)
    {
        await using var uow = await factory.OpenAsync(Space.Shared, Guard.NotNull(actor), cancellationToken).ConfigureAwait(false);
        return await uow.Repo<IScheduleRepository>().TryClaimOccurrenceAsync(scheduleUniqueId, scheduledFor, actor, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task MarkEvaluatedAsync(Guid scheduleUniqueId, DateTimeOffset through, Actor actor, CancellationToken cancellationToken = default)
    {
        await using var uow = await factory.OpenAsync(Space.Shared, Guard.NotNull(actor), cancellationToken).ConfigureAwait(false);
        await uow.Repo<IScheduleRepository>().MarkEvaluatedAsync(scheduleUniqueId, through, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<int> PurgeCompartmentAsync(Compartment compartment, Actor actor, CancellationToken cancellationToken = default)
    {
        await using var uow = await factory.OpenAsync(Space.Of(Guard.NotNull(compartment)), Guard.NotNull(actor), cancellationToken).ConfigureAwait(false);
        return await uow.Repo<IScheduleRepository>().DeleteOfCompartmentAsync(compartment, cancellationToken).ConfigureAwait(false);
    }
}
