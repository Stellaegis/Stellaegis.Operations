using System.Data;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Stellaegis.Primitives;
using Stellaegis.Primitives.Data;

namespace Stellaegis.Operations.PostgreSQL;
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

internal sealed class JobRepository(NpgsqlConnection connection, Func<NpgsqlTransaction?> transaction) : IJobRepository
{
    private const string Columns = """
        job_unique_id, job_type, status, visibility, compartment, created_by, via, input::text as input, result::text as result,
        progress, progress_label, created_at, started_at, completed_at, expires_at, claimed_until, idempotency_key,
        schedule_unique_id, scheduled_for
        """;

    public async Task<Guid?> InsertAsync(Guid jobUniqueId, JobRequest request, DateTimeOffset createdAt, DateTimeOffset expiresAt, CancellationToken ct)
    {
        // A replayed launch hits the unique index of the idempotency key and inserts nothing: the caller then
        // reads the job of the first launch.
        await using var command = new NpgsqlCommand("""
            insert into ops.jobs (job_unique_id, job_type, status, visibility, compartment, created_by, via, input, created_at, expires_at,
                                  idempotency_key, schedule_unique_id, scheduled_for)
            values (@Id, @JobType, 'pending', @Visibility, @Compartment, @CreatedBy, @Via, @Input::jsonb, @CreatedAt, @ExpiresAt,
                    @IdempotencyKey, @ScheduleUniqueId, @ScheduledFor)
            on conflict do nothing
            """, connection, transaction());
        command.Parameters.Add("Id", NpgsqlDbType.Uuid).Value = jobUniqueId;
        command.Parameters.Add("JobType", NpgsqlDbType.Text).Value = request.JobType;
        command.Parameters.Add("Visibility", NpgsqlDbType.Text).Value = Words.Of(request.Visibility);
        command.Parameters.Add("Compartment", NpgsqlDbType.Text).Value = (object?)request.Space.Compartment?.Value ?? DBNull.Value;
        command.Parameters.Add("CreatedBy", NpgsqlDbType.Text).Value = request.Actor.Value;
        command.Parameters.Add("Via", NpgsqlDbType.Text).Value = (object?)request.Actor.Via?.Value ?? DBNull.Value;
        command.Parameters.Add("Input", NpgsqlDbType.Text).Value = (object?)Words.Text(request.Input) ?? DBNull.Value;
        command.Parameters.Add("CreatedAt", NpgsqlDbType.TimestampTz).Value = createdAt;
        command.Parameters.Add("ExpiresAt", NpgsqlDbType.TimestampTz).Value = expiresAt;
        command.Parameters.Add("IdempotencyKey", NpgsqlDbType.Text).Value = request.IdempotencyKey;
        command.Parameters.Add("ScheduleUniqueId", NpgsqlDbType.Uuid).Value = (object?)request.ScheduleUniqueId ?? DBNull.Value;
        command.Parameters.Add("ScheduledFor", NpgsqlDbType.TimestampTz).Value = (object?)request.ScheduledFor ?? DBNull.Value;
        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1 ? jobUniqueId : null;
    }

    public async Task<Guid?> FindByIdempotencyKeyAsync(Space space, string key, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(
            "select job_unique_id from ops.jobs where coalesce(compartment, '_shared') = @Space and idempotency_key = @Key",
            connection, transaction());
        command.Parameters.Add("Space", NpgsqlDbType.Text).Value = space.ToSegment();
        command.Parameters.Add("Key", NpgsqlDbType.Text).Value = key;
        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) as Guid?;
    }

    public async Task<JobRecord?> GetAsync(Space? space, Guid jobUniqueId, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand($"""
            select {Columns} from ops.jobs
            where job_unique_id = @Id and (@Space is null or coalesce(compartment, '_shared') = @Space)
            """, connection, transaction());
        command.Parameters.Add("Id", NpgsqlDbType.Uuid).Value = jobUniqueId;
        command.Parameters.Add("Space", NpgsqlDbType.Text).Value = (object?)space?.ToSegment() ?? DBNull.Value;
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? Read(reader) : null;
    }

    public async Task<IReadOnlyList<JobRecord>> ListAsync(JobQuery query, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand($"""
            select {Columns} from ops.jobs
            where (@Space is null or coalesce(compartment, '_shared') = @Space)
              and (@Actor is null or created_by = @Actor)
              and (@Status is null or status = @Status)
              and (@JobType is null or job_type = @JobType)
              and (@Schedule is null or schedule_unique_id = @Schedule)
            order by created_at desc
            limit @Max
            """, connection, transaction());
        command.Parameters.Add("Space", NpgsqlDbType.Text).Value = (object?)query.Space?.ToSegment() ?? DBNull.Value;
        command.Parameters.Add("Actor", NpgsqlDbType.Text).Value = (object?)query.Actor?.Value ?? DBNull.Value;
        command.Parameters.Add("Status", NpgsqlDbType.Text).Value = query.Status is { } status ? Words.Of(status) : DBNull.Value;
        command.Parameters.Add("JobType", NpgsqlDbType.Text).Value = (object?)query.JobType ?? DBNull.Value;
        command.Parameters.Add("Schedule", NpgsqlDbType.Uuid).Value = (object?)query.ScheduleUniqueId ?? DBNull.Value;
        command.Parameters.Add("Max", NpgsqlDbType.Integer).Value = query.MaxResults;
        return await ReadAllAsync(command, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<JobRecord>> ClaimAsync(int max, DateTimeOffset now, TimeSpan lease, Actor worker, CancellationToken ct)
    {
        // The claim is one statement: the candidates are locked and skipped when locked by another worker, so
        // that two workers never claim the same job, and never wait for each other.
        await using var command = new NpgsqlCommand($"""
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
            """, connection, transaction());
        command.Parameters.Add("Now", NpgsqlDbType.TimestampTz).Value = now;
        command.Parameters.Add("Until", NpgsqlDbType.TimestampTz).Value = now + lease;
        command.Parameters.Add("Max", NpgsqlDbType.Integer).Value = max;
        command.Parameters.Add("Worker", NpgsqlDbType.Text).Value = worker.Value;
        var claimed = await ReadAllAsync(command, ct).ConfigureAwait(false);
        return claimed.OrderBy(job => job.CreatedAt).ToList();
    }

    public async Task ReportProgressAsync(Guid jobUniqueId, int? percent, string? label, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            update ops.jobs set progress = coalesce(@Percent, progress), progress_label = coalesce(@Label, progress_label)
            where job_unique_id = @Id
            """, connection, transaction());
        command.Parameters.Add("Id", NpgsqlDbType.Uuid).Value = jobUniqueId;
        command.Parameters.Add("Percent", NpgsqlDbType.Smallint).Value = (object?)(short?)percent ?? DBNull.Value;
        command.Parameters.Add("Label", NpgsqlDbType.Text).Value = (object?)label ?? DBNull.Value;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task CompleteAsync(Guid jobUniqueId, JobStatus status, JsonElement? result, DateTimeOffset completedAt, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            update ops.jobs set status = @Status, result = @Result::jsonb, completed_at = @CompletedAt, claimed_until = null
            where job_unique_id = @Id
            """, connection, transaction());
        command.Parameters.Add("Id", NpgsqlDbType.Uuid).Value = jobUniqueId;
        command.Parameters.Add("Status", NpgsqlDbType.Text).Value = Words.Of(status);
        command.Parameters.Add("Result", NpgsqlDbType.Text).Value = (object?)Words.Text(result) ?? DBNull.Value;
        command.Parameters.Add("CompletedAt", NpgsqlDbType.TimestampTz).Value = completedAt;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<JobRecord?> CancelAsync(Space space, Guid jobUniqueId, DateTimeOffset cancelledAt, CancellationToken ct)
    {
        var current = await GetAsync(space, jobUniqueId, ct).ConfigureAwait(false);
        if (current is null)
            return null;
        int cancelled;
        await using (var command = new NpgsqlCommand("""
            update ops.jobs set status = 'cancelled', completed_at = @CancelledAt
            where job_unique_id = @Id and status = 'pending'
            """, connection, transaction()))
        {
            command.Parameters.Add("Id", NpgsqlDbType.Uuid).Value = jobUniqueId;
            command.Parameters.Add("CancelledAt", NpgsqlDbType.TimestampTz).Value = cancelledAt;
            cancelled = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        if (cancelled == 0)
        {
            // Claimed between the read and the update, or already terminal: the state read again says which.
            var now = await GetAsync(space, jobUniqueId, ct).ConfigureAwait(false);
            throw new JobNotPendingException(jobUniqueId, now?.Status ?? current.Status);
        }
        return await GetAsync(space, jobUniqueId, ct).ConfigureAwait(false);
    }

    public async Task<int> DeleteOfCompartmentAsync(Compartment compartment, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("delete from ops.jobs where compartment = @Compartment", connection, transaction());
        command.Parameters.Add("Compartment", NpgsqlDbType.Text).Value = compartment.Value;
        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<int> DeleteExpiredAsync(DateTimeOffset now, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(
            "delete from ops.jobs where status in ('succeeded', 'failed', 'cancelled') and expires_at < @Now", connection, transaction());
        command.Parameters.Add("Now", NpgsqlDbType.TimestampTz).Value = now;
        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<JobRecord>> ReadAllAsync(NpgsqlCommand command, CancellationToken ct)
    {
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var jobs = new List<JobRecord>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            jobs.Add(Read(reader));
        return jobs;
    }

    private static JobRecord Read(NpgsqlDataReader row) => new()
    {
        JobUniqueId = row.GetGuid("job_unique_id"),
        JobType = row.GetString("job_type"),
        Status = Words.ParseStatus(row.GetString("status")),
        Visibility = Words.ParseVisibility(row.GetString("visibility")),
        Space = Words.SpaceOf(row.IsDBNull("compartment") ? null : row.GetString("compartment")),
        Actor = Words.ActorOf(row.GetString("created_by"), row.IsDBNull("via") ? null : row.GetString("via")),
        Input = Words.Json(row.IsDBNull("input") ? null : row.GetString("input")),
        Result = Words.Json(row.IsDBNull("result") ? null : row.GetString("result")),
        Progress = row.IsDBNull("progress") ? null : row.GetInt16("progress"),
        ProgressLabel = row.GetString("progress_label"),
        CreatedAt = row.GetFieldValue<DateTimeOffset>("created_at"),
        StartedAt = row.IsDBNull("started_at") ? null : row.GetFieldValue<DateTimeOffset>("started_at"),
        CompletedAt = row.IsDBNull("completed_at") ? null : row.GetFieldValue<DateTimeOffset>("completed_at"),
        ExpiresAt = row.GetFieldValue<DateTimeOffset>("expires_at"),
        ClaimedUntil = row.IsDBNull("claimed_until") ? null : row.GetFieldValue<DateTimeOffset>("claimed_until"),
        IdempotencyKey = row.GetString("idempotency_key"),
        ScheduleUniqueId = row.IsDBNull("schedule_unique_id") ? null : row.GetGuid("schedule_unique_id"),
        ScheduledFor = row.IsDBNull("scheduled_for") ? null : row.GetFieldValue<DateTimeOffset>("scheduled_for"),
    };
}

internal sealed class ScheduleRepository(NpgsqlConnection connection, Func<NpgsqlTransaction?> transaction) : IScheduleRepository
{
    private const string Columns = """
        schedule_unique_id, name, cron_expression, time_zone_id, job_type, compartment, input::text as input, concurrency_policy,
        enabled, created_by, created_at, last_evaluated_at
        """;

    public async Task<IReadOnlyList<Schedule>> ListAsync(Space? space, bool enabledOnly, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand($"""
            select {Columns} from ops.schedules
            where (@Space is null or coalesce(compartment, '_shared') = @Space) and (not @EnabledOnly or enabled)
            order by created_at
            """, connection, transaction());
        command.Parameters.Add("Space", NpgsqlDbType.Text).Value = (object?)space?.ToSegment() ?? DBNull.Value;
        command.Parameters.Add("EnabledOnly", NpgsqlDbType.Boolean).Value = enabledOnly;
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var schedules = new List<Schedule>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            schedules.Add(Read(reader));
        return schedules;
    }

    public async Task<Schedule?> GetAsync(Guid scheduleUniqueId, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(
            $"select {Columns} from ops.schedules where schedule_unique_id = @Id", connection, transaction());
        command.Parameters.Add("Id", NpgsqlDbType.Uuid).Value = scheduleUniqueId;
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? Read(reader) : null;
    }

    public async Task UpsertAsync(Schedule schedule, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            insert into ops.schedules (schedule_unique_id, name, cron_expression, time_zone_id, job_type, compartment, input,
                                       concurrency_policy, enabled, created_by, created_at, last_evaluated_at)
            values (@Id, @Name, @CronExpression, @TimeZoneId, @JobType, @Compartment, @Input::jsonb,
                    @ConcurrencyPolicy, @Enabled, @CreatedBy, @CreatedAt, @LastEvaluatedAt)
            on conflict (schedule_unique_id) do update set
                name = excluded.name, cron_expression = excluded.cron_expression, time_zone_id = excluded.time_zone_id,
                job_type = excluded.job_type, compartment = excluded.compartment, input = excluded.input,
                concurrency_policy = excluded.concurrency_policy, enabled = excluded.enabled, created_by = excluded.created_by,
                last_evaluated_at = coalesce(excluded.last_evaluated_at, ops.schedules.last_evaluated_at)
            """, connection, transaction());
        command.Parameters.Add("Id", NpgsqlDbType.Uuid).Value = schedule.ScheduleUniqueId;
        command.Parameters.Add("Name", NpgsqlDbType.Text).Value = schedule.Name;
        command.Parameters.Add("CronExpression", NpgsqlDbType.Text).Value = schedule.CronExpression;
        command.Parameters.Add("TimeZoneId", NpgsqlDbType.Text).Value = schedule.TimeZoneId;
        command.Parameters.Add("JobType", NpgsqlDbType.Text).Value = schedule.JobType;
        command.Parameters.Add("Compartment", NpgsqlDbType.Text).Value = (object?)schedule.Space.Compartment?.Value ?? DBNull.Value;
        command.Parameters.Add("Input", NpgsqlDbType.Text).Value = (object?)Words.Text(schedule.Input) ?? DBNull.Value;
        command.Parameters.Add("ConcurrencyPolicy", NpgsqlDbType.Text).Value = Words.Of(schedule.ConcurrencyPolicy);
        command.Parameters.Add("Enabled", NpgsqlDbType.Boolean).Value = schedule.Enabled;
        command.Parameters.Add("CreatedBy", NpgsqlDbType.Text).Value = schedule.CreatedBy.Value;
        command.Parameters.Add("CreatedAt", NpgsqlDbType.TimestampTz).Value = schedule.CreatedAt;
        command.Parameters.Add("LastEvaluatedAt", NpgsqlDbType.TimestampTz).Value = (object?)schedule.LastEvaluatedAt ?? DBNull.Value;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<bool> DeleteAsync(Guid scheduleUniqueId, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("delete from ops.schedules where schedule_unique_id = @Id", connection, transaction());
        command.Parameters.Add("Id", NpgsqlDbType.Uuid).Value = scheduleUniqueId;
        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    public async Task<bool> TryClaimOccurrenceAsync(Guid scheduleUniqueId, DateTimeOffset scheduledFor, Actor worker, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            insert into ops.schedule_runs (schedule_unique_id, scheduled_for, claimed_by) values (@Id, @ScheduledFor, @Worker)
            on conflict do nothing
            """, connection, transaction());
        command.Parameters.Add("Id", NpgsqlDbType.Uuid).Value = scheduleUniqueId;
        command.Parameters.Add("ScheduledFor", NpgsqlDbType.TimestampTz).Value = scheduledFor;
        command.Parameters.Add("Worker", NpgsqlDbType.Text).Value = worker.Value;
        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    public async Task MarkEvaluatedAsync(Guid scheduleUniqueId, DateTimeOffset through, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(
            "update ops.schedules set last_evaluated_at = @Through where schedule_unique_id = @Id", connection, transaction());
        command.Parameters.Add("Id", NpgsqlDbType.Uuid).Value = scheduleUniqueId;
        command.Parameters.Add("Through", NpgsqlDbType.TimestampTz).Value = through;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<int> DeleteOfCompartmentAsync(Compartment compartment, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("delete from ops.schedules where compartment = @Compartment", connection, transaction());
        command.Parameters.Add("Compartment", NpgsqlDbType.Text).Value = compartment.Value;
        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static Schedule Read(NpgsqlDataReader row) => new()
    {
        ScheduleUniqueId = row.GetGuid("schedule_unique_id"),
        Name = row.GetString("name"),
        CronExpression = row.GetString("cron_expression"),
        TimeZoneId = row.GetString("time_zone_id"),
        JobType = row.GetString("job_type"),
        Space = Words.SpaceOf(row.IsDBNull("compartment") ? null : row.GetString("compartment")),
        Input = Words.Json(row.IsDBNull("input") ? null : row.GetString("input")),
        ConcurrencyPolicy = Words.ParsePolicy(row.GetString("concurrency_policy")),
        Enabled = row.GetBoolean("enabled"),
        CreatedBy = Actor.Parse(row.GetString("created_by")),
        CreatedAt = row.GetFieldValue<DateTimeOffset>("created_at"),
        LastEvaluatedAt = row.IsDBNull("last_evaluated_at") ? null : row.GetFieldValue<DateTimeOffset>("last_evaluated_at"),
    };
}

/// <summary>The repositories of the database, registered once for its unit of work.</summary>
internal static class OperationsRepositories
{
    public static RepositoryRegistry<OperationsDatabase> Registry()
        => new RepositoryRegistry<OperationsDatabase>()
            .Add<IJobRepository>((connection, transaction) => new JobRepository((NpgsqlConnection)connection, () => (NpgsqlTransaction?)transaction()))
            .Add<IScheduleRepository>((connection, transaction) => new ScheduleRepository((NpgsqlConnection)connection, () => (NpgsqlTransaction?)transaction()));
}
