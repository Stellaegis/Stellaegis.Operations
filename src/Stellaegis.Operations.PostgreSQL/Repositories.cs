using System.Data.Common;
using System.Text.Json;
using Dapper;
using Stellaegis.Primitives;
using Stellaegis.Primitives.Data;

namespace Stellaegis.Operations.PostgreSQL;

/// <summary>A row of <c>ops.jobs</c>, as Dapper reads it.</summary>
internal sealed class JobRow
{
    public Guid JobUniqueId { get; set; }
    public string JobType { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Visibility { get; set; } = string.Empty;
    public string? Compartment { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
    public string? Via { get; set; }
    public string? Input { get; set; }
    public string? Result { get; set; }
    public short? Progress { get; set; }
    public string ProgressLabel { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? ClaimedUntil { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
    public Guid? ScheduleUniqueId { get; set; }
    public DateTime? ScheduledFor { get; set; }

    public JobRecord ToRecord() => new()
    {
        JobUniqueId = JobUniqueId,
        JobType = JobType,
        Status = Words.ParseStatus(Status),
        Visibility = Words.ParseVisibility(Visibility),
        Space = Words.SpaceOf(Compartment),
        Actor = Words.ActorOf(CreatedBy, Via),
        Input = Words.Json(Input),
        Result = Words.Json(Result),
        Progress = Progress,
        ProgressLabel = ProgressLabel,
        CreatedAt = Words.Utc(CreatedAt),
        StartedAt = Words.Utc(StartedAt),
        CompletedAt = Words.Utc(CompletedAt),
        ExpiresAt = Words.Utc(ExpiresAt),
        ClaimedUntil = Words.Utc(ClaimedUntil),
        IdempotencyKey = IdempotencyKey,
        ScheduleUniqueId = ScheduleUniqueId,
        ScheduledFor = Words.Utc(ScheduledFor),
    };
}

/// <summary>A row of <c>ops.schedules</c>, as Dapper reads it.</summary>
internal sealed class ScheduleRow
{
    public Guid ScheduleUniqueId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string CronExpression { get; set; } = string.Empty;
    public string TimeZoneId { get; set; } = "UTC";
    public string JobType { get; set; } = string.Empty;
    public string? Compartment { get; set; }
    public string? Input { get; set; }
    public string ConcurrencyPolicy { get; set; } = "forbid";
    public bool Enabled { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime? LastEvaluatedAt { get; set; }

    public Schedule ToSchedule() => new()
    {
        ScheduleUniqueId = ScheduleUniqueId,
        Name = Name,
        CronExpression = CronExpression,
        TimeZoneId = TimeZoneId,
        JobType = JobType,
        Space = Words.SpaceOf(Compartment),
        Input = Words.Json(Input),
        ConcurrencyPolicy = Words.ParsePolicy(ConcurrencyPolicy),
        Enabled = Enabled,
        CreatedBy = Actor.Parse(CreatedBy),
        CreatedAt = Words.Utc(CreatedAt),
        LastEvaluatedAt = Words.Utc(LastEvaluatedAt),
    };
}

/// <summary>The words the tables hold and the values of the module, each way.</summary>
internal static class Words
{
    public static string Of(JobStatus status) => status switch
    {
        JobStatus.Pending => "pending",
        JobStatus.Running => "running",
        JobStatus.Succeeded => "succeeded",
        JobStatus.Failed => "failed",
        JobStatus.Cancelled => "cancelled",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };

    public static JobStatus ParseStatus(string word) => word switch
    {
        "pending" => JobStatus.Pending,
        "running" => JobStatus.Running,
        "succeeded" => JobStatus.Succeeded,
        "failed" => JobStatus.Failed,
        "cancelled" => JobStatus.Cancelled,
        _ => throw new InvalidOperationException($"'{word}' is not a job status the module knows."),
    };

    public static string Of(JobVisibility visibility) => visibility switch
    {
        JobVisibility.InitiatorOnly => "initiator_only",
        JobVisibility.Space => "space",
        _ => throw new ArgumentOutOfRangeException(nameof(visibility), visibility, null),
    };

    public static JobVisibility ParseVisibility(string word) => word switch
    {
        "initiator_only" => JobVisibility.InitiatorOnly,
        "space" => JobVisibility.Space,
        _ => throw new InvalidOperationException($"'{word}' is not a job visibility the module knows."),
    };

    public static string Of(ConcurrencyPolicy policy) => policy switch
    {
        ConcurrencyPolicy.Forbid => "forbid",
        ConcurrencyPolicy.Allow => "allow",
        ConcurrencyPolicy.Replace => "replace",
        _ => throw new ArgumentOutOfRangeException(nameof(policy), policy, null),
    };

    public static ConcurrencyPolicy ParsePolicy(string word) => word switch
    {
        "forbid" => ConcurrencyPolicy.Forbid,
        "allow" => ConcurrencyPolicy.Allow,
        "replace" => ConcurrencyPolicy.Replace,
        _ => throw new InvalidOperationException($"'{word}' is not a concurrency policy the module knows."),
    };

    public static Space SpaceOf(string? compartment) => compartment is null ? Space.Shared : Space.Of(Compartment.Parse(compartment));

    public static Actor ActorOf(string value, string? via) => via is null ? Actor.Parse(value) : Actor.Parse(value).Through(Actor.Parse(via));

    public static JsonElement? Json(string? text) => text is null ? null : JsonDocument.Parse(text).RootElement.Clone();

    public static string? Text(JsonElement? json) => json?.GetRawText();

    public static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    public static DateTimeOffset? Utc(DateTime? value) => value is null ? null : Utc(value.Value);
}

/// <summary>The repository of the jobs: the SQL of <c>ops.jobs</c>, run on the connection of a unit of work.</summary>
internal interface IJobRepository
{
    Task<Guid?> InsertAsync(Guid jobUniqueId, JobRequest request, DateTimeOffset createdAt, DateTimeOffset expiresAt, CancellationToken ct);
    Task<Guid?> FindByIdempotencyKeyAsync(Space space, string key, CancellationToken ct);
    Task<JobRecord?> GetAsync(Space? space, Guid jobUniqueId, CancellationToken ct);
    Task<IReadOnlyList<JobRecord>> ListAsync(JobQuery query, CancellationToken ct);
    Task<IReadOnlyList<JobRecord>> ClaimAsync(int max, DateTimeOffset now, TimeSpan lease, Actor worker, CancellationToken ct);
    Task ReportProgressAsync(Guid jobUniqueId, int? percent, string? label, CancellationToken ct);
    Task CompleteAsync(Guid jobUniqueId, JobStatus status, JsonElement? result, DateTimeOffset completedAt, CancellationToken ct);
    Task<JobRecord?> CancelAsync(Space space, Guid jobUniqueId, DateTimeOffset cancelledAt, CancellationToken ct);
    Task<int> DeleteOfCompartmentAsync(Compartment compartment, CancellationToken ct);
    Task<int> DeleteExpiredAsync(DateTimeOffset now, CancellationToken ct);
}

/// <summary>The repository of the schedules: the SQL of <c>ops.schedules</c> and <c>ops.schedule_runs</c>.</summary>
internal interface IScheduleRepository
{
    Task<IReadOnlyList<Schedule>> ListAsync(Space? space, bool enabledOnly, CancellationToken ct);
    Task<Schedule?> GetAsync(Guid scheduleUniqueId, CancellationToken ct);
    Task UpsertAsync(Schedule schedule, CancellationToken ct);
    Task<bool> DeleteAsync(Guid scheduleUniqueId, CancellationToken ct);
    Task<bool> TryClaimOccurrenceAsync(Guid scheduleUniqueId, DateTimeOffset scheduledFor, Actor worker, CancellationToken ct);
    Task MarkEvaluatedAsync(Guid scheduleUniqueId, DateTimeOffset through, CancellationToken ct);
    Task<int> DeleteOfCompartmentAsync(Compartment compartment, CancellationToken ct);
}

internal sealed class JobRepository(DbConnection connection, Func<DbTransaction?> transaction) : IJobRepository
{
    private const string Columns = """
        job_unique_id, job_type, status, visibility, compartment, created_by, via, input::text as input, result::text as result,
        progress, progress_label, created_at, started_at, completed_at, expires_at, claimed_until, idempotency_key,
        schedule_unique_id, scheduled_for
        """;

    private CommandDefinition Command(string sql, object? parameters, CancellationToken ct) => new(sql, parameters, transaction(), cancellationToken: ct);

    public async Task<Guid?> InsertAsync(Guid jobUniqueId, JobRequest request, DateTimeOffset createdAt, DateTimeOffset expiresAt, CancellationToken ct)
    {
        // A replayed launch hits the unique index of the idempotency key and inserts nothing: the caller then
        // reads the job of the first launch.
        var inserted = await connection.ExecuteAsync(Command("""
            insert into ops.jobs (job_unique_id, job_type, status, visibility, compartment, created_by, via, input, created_at, expires_at,
                                  idempotency_key, schedule_unique_id, scheduled_for)
            values (@Id, @JobType, 'pending', @Visibility, @Compartment, @CreatedBy, @Via, @Input::jsonb, @CreatedAt, @ExpiresAt,
                    @IdempotencyKey, @ScheduleUniqueId, @ScheduledFor)
            on conflict do nothing
            """, new
        {
            Id = jobUniqueId,
            request.JobType,
            Visibility = Words.Of(request.Visibility),
            Compartment = request.Space.Compartment?.Value,
            CreatedBy = request.Actor.Value,
            Via = request.Actor.Via?.Value,
            Input = Words.Text(request.Input),
            CreatedAt = createdAt,
            ExpiresAt = expiresAt,
            request.IdempotencyKey,
            request.ScheduleUniqueId,
            request.ScheduledFor,
        }, ct)).ConfigureAwait(false);
        return inserted == 1 ? jobUniqueId : null;
    }

    public Task<Guid?> FindByIdempotencyKeyAsync(Space space, string key, CancellationToken ct)
        => connection.QuerySingleOrDefaultAsync<Guid?>(Command(
            "select job_unique_id from ops.jobs where coalesce(compartment, '_shared') = @Space and idempotency_key = @Key",
            new { Space = space.ToSegment(), Key = key }, ct));

    public async Task<JobRecord?> GetAsync(Space? space, Guid jobUniqueId, CancellationToken ct)
    {
        var row = await connection.QuerySingleOrDefaultAsync<JobRow>(Command($"""
            select {Columns} from ops.jobs
            where job_unique_id = @Id and (@Space is null or coalesce(compartment, '_shared') = @Space)
            """, new { Id = jobUniqueId, Space = space?.ToSegment() }, ct)).ConfigureAwait(false);
        return row?.ToRecord();
    }

    public async Task<IReadOnlyList<JobRecord>> ListAsync(JobQuery query, CancellationToken ct)
    {
        var rows = await connection.QueryAsync<JobRow>(Command($"""
            select {Columns} from ops.jobs
            where (@Space is null or coalesce(compartment, '_shared') = @Space)
              and (@Actor is null or created_by = @Actor)
              and (@Status is null or status = @Status)
              and (@JobType is null or job_type = @JobType)
              and (@Schedule is null or schedule_unique_id = @Schedule)
            order by created_at desc
            limit @Max
            """, new
        {
            Space = query.Space?.ToSegment(),
            Actor = query.Actor?.Value,
            Status = query.Status is { } status ? Words.Of(status) : null,
            query.JobType,
            Schedule = query.ScheduleUniqueId,
            Max = query.MaxResults,
        }, ct)).ConfigureAwait(false);
        return rows.Select(row => row.ToRecord()).ToList();
    }

    public async Task<IReadOnlyList<JobRecord>> ClaimAsync(int max, DateTimeOffset now, TimeSpan lease, Actor worker, CancellationToken ct)
    {
        // The claim is one statement: the candidates are locked and skipped when locked by another worker, so
        // that two workers never claim the same job, and never wait for each other.
        var rows = await connection.QueryAsync<JobRow>(Command($"""
            with candidates as (
                select job_unique_id from ops.jobs
                where status = 'pending' or (status = 'running' and claimed_until < @Now)
                order by created_at
                limit @Max
                for update skip locked)
            update ops.jobs j
            set status = 'running', started_at = coalesce(j.started_at, @Now), claimed_until = @Until, claimed_by = @Worker
            from candidates c
            where j.job_unique_id = c.job_unique_id
            returning {Columns.Replace("job_unique_id,", "j.job_unique_id,", StringComparison.Ordinal)}
            """, new { Now = now, Until = now + lease, Max = max, Worker = worker.Value }, ct)).ConfigureAwait(false);
        return rows.Select(row => row.ToRecord()).OrderBy(job => job.CreatedAt).ToList();
    }

    public Task ReportProgressAsync(Guid jobUniqueId, int? percent, string? label, CancellationToken ct)
        => connection.ExecuteAsync(Command("""
            update ops.jobs set progress = coalesce(@Percent, progress), progress_label = coalesce(@Label, progress_label)
            where job_unique_id = @Id
            """, new { Id = jobUniqueId, Percent = (short?)percent, Label = label }, ct));

    public Task CompleteAsync(Guid jobUniqueId, JobStatus status, JsonElement? result, DateTimeOffset completedAt, CancellationToken ct)
        => connection.ExecuteAsync(Command("""
            update ops.jobs set status = @Status, result = @Result::jsonb, completed_at = @CompletedAt, claimed_until = null
            where job_unique_id = @Id
            """, new { Id = jobUniqueId, Status = Words.Of(status), Result = Words.Text(result), CompletedAt = completedAt }, ct));

    public async Task<JobRecord?> CancelAsync(Space space, Guid jobUniqueId, DateTimeOffset cancelledAt, CancellationToken ct)
    {
        var current = await GetAsync(space, jobUniqueId, ct).ConfigureAwait(false);
        if (current is null)
            return null;
        var cancelled = await connection.ExecuteAsync(Command("""
            update ops.jobs set status = 'cancelled', completed_at = @CancelledAt
            where job_unique_id = @Id and status = 'pending'
            """, new { Id = jobUniqueId, CancelledAt = cancelledAt }, ct)).ConfigureAwait(false);
        if (cancelled == 0)
        {
            // Claimed between the read and the update, or already terminal: the state read again says which.
            var now = await GetAsync(space, jobUniqueId, ct).ConfigureAwait(false);
            throw new JobNotPendingException(jobUniqueId, now?.Status ?? current.Status);
        }
        return await GetAsync(space, jobUniqueId, ct).ConfigureAwait(false);
    }

    public Task<int> DeleteOfCompartmentAsync(Compartment compartment, CancellationToken ct)
        => connection.ExecuteAsync(Command("delete from ops.jobs where compartment = @Compartment", new { Compartment = compartment.Value }, ct));

    public Task<int> DeleteExpiredAsync(DateTimeOffset now, CancellationToken ct)
        => connection.ExecuteAsync(Command(
            "delete from ops.jobs where status in ('succeeded', 'failed', 'cancelled') and expires_at < @Now", new { Now = now }, ct));
}

internal sealed class ScheduleRepository(DbConnection connection, Func<DbTransaction?> transaction) : IScheduleRepository
{
    private const string Columns = """
        schedule_unique_id, name, cron_expression, time_zone_id, job_type, compartment, input::text as input, concurrency_policy,
        enabled, created_by, created_at, last_evaluated_at
        """;

    private CommandDefinition Command(string sql, object? parameters, CancellationToken ct) => new(sql, parameters, transaction(), cancellationToken: ct);

    public async Task<IReadOnlyList<Schedule>> ListAsync(Space? space, bool enabledOnly, CancellationToken ct)
    {
        var rows = await connection.QueryAsync<ScheduleRow>(Command($"""
            select {Columns} from ops.schedules
            where (@Space is null or coalesce(compartment, '_shared') = @Space) and (not @EnabledOnly or enabled)
            order by created_at
            """, new { Space = space?.ToSegment(), EnabledOnly = enabledOnly }, ct)).ConfigureAwait(false);
        return rows.Select(row => row.ToSchedule()).ToList();
    }

    public async Task<Schedule?> GetAsync(Guid scheduleUniqueId, CancellationToken ct)
    {
        var row = await connection.QuerySingleOrDefaultAsync<ScheduleRow>(Command(
            $"select {Columns} from ops.schedules where schedule_unique_id = @Id", new { Id = scheduleUniqueId }, ct)).ConfigureAwait(false);
        return row?.ToSchedule();
    }

    public Task UpsertAsync(Schedule schedule, CancellationToken ct)
        => connection.ExecuteAsync(Command("""
            insert into ops.schedules (schedule_unique_id, name, cron_expression, time_zone_id, job_type, compartment, input,
                                       concurrency_policy, enabled, created_by, created_at, last_evaluated_at)
            values (@Id, @Name, @CronExpression, @TimeZoneId, @JobType, @Compartment, @Input::jsonb,
                    @ConcurrencyPolicy, @Enabled, @CreatedBy, @CreatedAt, @LastEvaluatedAt)
            on conflict (schedule_unique_id) do update set
                name = excluded.name, cron_expression = excluded.cron_expression, time_zone_id = excluded.time_zone_id,
                job_type = excluded.job_type, compartment = excluded.compartment, input = excluded.input,
                concurrency_policy = excluded.concurrency_policy, enabled = excluded.enabled, created_by = excluded.created_by,
                last_evaluated_at = coalesce(excluded.last_evaluated_at, ops.schedules.last_evaluated_at)
            """, new
        {
            Id = schedule.ScheduleUniqueId,
            schedule.Name,
            schedule.CronExpression,
            schedule.TimeZoneId,
            schedule.JobType,
            Compartment = schedule.Space.Compartment?.Value,
            Input = Words.Text(schedule.Input),
            ConcurrencyPolicy = Words.Of(schedule.ConcurrencyPolicy),
            schedule.Enabled,
            CreatedBy = schedule.CreatedBy.Value,
            schedule.CreatedAt,
            schedule.LastEvaluatedAt,
        }, ct));

    public async Task<bool> DeleteAsync(Guid scheduleUniqueId, CancellationToken ct)
        => await connection.ExecuteAsync(Command("delete from ops.schedules where schedule_unique_id = @Id", new { Id = scheduleUniqueId }, ct)).ConfigureAwait(false) == 1;

    public async Task<bool> TryClaimOccurrenceAsync(Guid scheduleUniqueId, DateTimeOffset scheduledFor, Actor worker, CancellationToken ct)
        => await connection.ExecuteAsync(Command("""
            insert into ops.schedule_runs (schedule_unique_id, scheduled_for, claimed_by) values (@Id, @ScheduledFor, @Worker)
            on conflict do nothing
            """, new { Id = scheduleUniqueId, ScheduledFor = scheduledFor, Worker = worker.Value }, ct)).ConfigureAwait(false) == 1;

    public Task MarkEvaluatedAsync(Guid scheduleUniqueId, DateTimeOffset through, CancellationToken ct)
        => connection.ExecuteAsync(Command("update ops.schedules set last_evaluated_at = @Through where schedule_unique_id = @Id",
            new { Id = scheduleUniqueId, Through = through }, ct));

    public Task<int> DeleteOfCompartmentAsync(Compartment compartment, CancellationToken ct)
        => connection.ExecuteAsync(Command("delete from ops.schedules where compartment = @Compartment", new { Compartment = compartment.Value }, ct));
}

/// <summary>The repositories of the database, registered once for its unit of work.</summary>
internal static class OperationsRepositories
{
    private static int _mapped;

    public static RepositoryRegistry<OperationsDatabase> Registry()
    {
        if (Interlocked.Exchange(ref _mapped, 1) == 0)
        {
            SnakeCaseTypeMap<JobRow>.Register();
            SnakeCaseTypeMap<ScheduleRow>.Register();
        }
        return new RepositoryRegistry<OperationsDatabase>()
            .Add<IJobRepository>((connection, transaction) => new JobRepository(connection, transaction))
            .Add<IScheduleRepository>((connection, transaction) => new ScheduleRepository(connection, transaction));
    }
}
