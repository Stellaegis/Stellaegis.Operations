using System.Text.Json;
using Stellaegis.Primitives;

namespace Stellaegis.Operations.InMemory;

/// <summary>
/// The jobs in memory, for development and tests: lost when the process stops, and shared by nothing else. The
/// claim is atomic within the process.
/// </summary>
public sealed class InMemoryJobStore(TimeProvider clock) : IJobStore
{
    private readonly Lock _lock = new();
    private readonly Dictionary<Guid, JobRecord> _jobs = [];

    /// <inheritdoc/>
    public Task<Guid> CreateAsync(JobRequest request, DateTimeOffset expiresAt, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(request);
        lock (_lock)
        {
            if (request.IdempotencyKey.Length > 0)
            {
                var existing = _jobs.Values.FirstOrDefault(job => job.Space == request.Space && job.IdempotencyKey == request.IdempotencyKey);
                if (existing is not null)
                    return Task.FromResult(existing.JobUniqueId);
            }
            var job = new JobRecord
            {
                JobUniqueId = Guid.NewGuid(),
                JobType = request.JobType,
                Status = JobStatus.Pending,
                Visibility = request.Visibility,
                Space = request.Space,
                Actor = request.Actor,
                Input = request.Input,
                CreatedAt = clock.GetUtcNow(),
                ExpiresAt = expiresAt,
                IdempotencyKey = request.IdempotencyKey,
                ScheduleUniqueId = request.ScheduleUniqueId,
                ScheduledFor = request.ScheduledFor,
            };
            _jobs[job.JobUniqueId] = job;
            return Task.FromResult(job.JobUniqueId);
        }
    }

    /// <inheritdoc/>
    public Task<JobRecord?> GetAsync(Space space, Guid jobUniqueId, Actor actor, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(space);
        lock (_lock)
            return Task.FromResult(_jobs.TryGetValue(jobUniqueId, out var job) && job.Space == space ? job : null);
    }

    /// <inheritdoc/>
    public Task<JobRecord?> FindAsync(Guid jobUniqueId, Actor actor, CancellationToken cancellationToken = default)
    {
        lock (_lock)
            return Task.FromResult(_jobs.GetValueOrDefault(jobUniqueId));
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<JobRecord>> ListAsync(JobQuery query, Actor actor, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(query);
        lock (_lock)
        {
            IReadOnlyList<JobRecord> list = _jobs.Values
                .Where(job => query.Space is null || job.Space == query.Space)
                .Where(job => query.Actor is null || job.Actor.Value == query.Actor.Value)
                .Where(job => query.Status is null || job.Status == query.Status)
                .Where(job => query.JobType is null || job.JobType == query.JobType)
                .Where(job => query.ScheduleUniqueId is null || job.ScheduleUniqueId == query.ScheduleUniqueId)
                .OrderByDescending(job => job.CreatedAt)
                .Take(query.MaxResults)
                .ToList();
            return Task.FromResult(list);
        }
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<JobRecord>> ClaimAsync(int max, TimeSpan lease, Actor worker, CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();
        lock (_lock)
        {
            var claimed = _jobs.Values
                .Where(job => job.Status == JobStatus.Pending || (job.Status == JobStatus.Running && job.ClaimedUntil < now))
                .OrderBy(job => job.CreatedAt)
                .Take(max)
                .Select(job => job with { Status = JobStatus.Running, StartedAt = job.StartedAt ?? now, ClaimedUntil = now + lease })
                .ToList();
            foreach (var job in claimed)
                _jobs[job.JobUniqueId] = job;
            return Task.FromResult<IReadOnlyList<JobRecord>>(claimed);
        }
    }

    /// <inheritdoc/>
    public Task ReportProgressAsync(Guid jobUniqueId, int? percent, string? label, Actor actor, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (_jobs.TryGetValue(jobUniqueId, out var job))
                _jobs[jobUniqueId] = job with { Progress = percent ?? job.Progress, ProgressLabel = label ?? job.ProgressLabel };
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task CompleteAsync(Guid jobUniqueId, JobStatus status, JsonElement? result, Actor actor, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (_jobs.TryGetValue(jobUniqueId, out var job))
                _jobs[jobUniqueId] = job with { Status = status, Result = result, CompletedAt = clock.GetUtcNow(), ClaimedUntil = null };
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<JobRecord?> CancelAsync(Space space, Guid jobUniqueId, Actor actor, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(space);
        lock (_lock)
        {
            if (!_jobs.TryGetValue(jobUniqueId, out var job) || job.Space != space)
                return Task.FromResult<JobRecord?>(null);
            if (job.Status != JobStatus.Pending)
                throw new JobNotPendingException(jobUniqueId, job.Status);
            var cancelled = job with { Status = JobStatus.Cancelled, CompletedAt = clock.GetUtcNow() };
            _jobs[jobUniqueId] = cancelled;
            return Task.FromResult<JobRecord?>(cancelled);
        }
    }

    /// <inheritdoc/>
    public Task<int> PurgeCompartmentAsync(Compartment compartment, Actor actor, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(compartment);
        lock (_lock)
        {
            var ids = _jobs.Values.Where(job => job.Space.Compartment == compartment).Select(job => job.JobUniqueId).ToList();
            foreach (var id in ids)
                _jobs.Remove(id);
            return Task.FromResult(ids.Count);
        }
    }

    /// <inheritdoc/>
    public Task<int> CleanExpiredAsync(Actor actor, CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();
        lock (_lock)
        {
            var ids = _jobs.Values.Where(job => !job.IsInFlight && job.ExpiresAt < now).Select(job => job.JobUniqueId).ToList();
            foreach (var id in ids)
                _jobs.Remove(id);
            return Task.FromResult(ids.Count);
        }
    }
}

/// <summary>The schedules in memory, for development and tests.</summary>
public sealed class InMemoryScheduleStore : IScheduleStore
{
    private readonly Lock _lock = new();
    private readonly Dictionary<Guid, Schedule> _schedules = [];
    private readonly HashSet<(Guid, DateTimeOffset)> _claimed = [];

    /// <inheritdoc/>
    public Task<IReadOnlyList<Schedule>> ListEnabledAsync(Actor actor, CancellationToken cancellationToken = default)
    {
        lock (_lock)
            return Task.FromResult<IReadOnlyList<Schedule>>(_schedules.Values.Where(schedule => schedule.Enabled).OrderBy(s => s.CreatedAt).ToList());
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<Schedule>> ListAsync(Space? space, Actor actor, CancellationToken cancellationToken = default)
    {
        lock (_lock)
            return Task.FromResult<IReadOnlyList<Schedule>>(_schedules.Values.Where(s => space is null || s.Space == space).OrderBy(s => s.CreatedAt).ToList());
    }

    /// <inheritdoc/>
    public Task<Schedule?> GetAsync(Guid scheduleUniqueId, Actor actor, CancellationToken cancellationToken = default)
    {
        lock (_lock)
            return Task.FromResult(_schedules.GetValueOrDefault(scheduleUniqueId));
    }

    /// <inheritdoc/>
    public Task UpsertAsync(Schedule schedule, Actor actor, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(schedule);
        lock (_lock)
        {
            var kept = _schedules.TryGetValue(schedule.ScheduleUniqueId, out var existing) ? existing.LastEvaluatedAt : null;
            _schedules[schedule.ScheduleUniqueId] = schedule with { LastEvaluatedAt = schedule.LastEvaluatedAt ?? kept };
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<bool> RemoveAsync(Guid scheduleUniqueId, Actor actor, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            _claimed.RemoveWhere(claim => claim.Item1 == scheduleUniqueId);
            return Task.FromResult(_schedules.Remove(scheduleUniqueId));
        }
    }

    /// <inheritdoc/>
    public Task<bool> TryClaimOccurrenceAsync(Guid scheduleUniqueId, DateTimeOffset scheduledFor, Actor actor, CancellationToken cancellationToken = default)
    {
        lock (_lock)
            return Task.FromResult(_claimed.Add((scheduleUniqueId, scheduledFor)));
    }

    /// <inheritdoc/>
    public Task MarkEvaluatedAsync(Guid scheduleUniqueId, DateTimeOffset through, Actor actor, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (_schedules.TryGetValue(scheduleUniqueId, out var schedule))
                _schedules[scheduleUniqueId] = schedule with { LastEvaluatedAt = through };
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<int> PurgeCompartmentAsync(Compartment compartment, Actor actor, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(compartment);
        lock (_lock)
        {
            var ids = _schedules.Values.Where(s => s.Space.Compartment == compartment).Select(s => s.ScheduleUniqueId).ToList();
            foreach (var id in ids)
            {
                _schedules.Remove(id);
                _claimed.RemoveWhere(claim => claim.Item1 == id);
            }
            return Task.FromResult(ids.Count);
        }
    }
}
