using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Stellaegis.Primitives;
using Stellaegis.Primitives.AspNetCore;

namespace Stellaegis.Operations.AspNetCore;

/// <summary>
/// The routes of the module. The application maps them onto the groups it chooses, declares who may call each
/// operation, and answers for each job it loads with an <c>AuthorizationHandler&lt;OperationRequirement, JobRecord&gt;</c>
/// that reads its <see cref="JobRecord.Visibility"/> — or declares <c>AllowAnyResource()</c>.
/// <code>
/// app.MapGroup("/api/t/{compartment}").MapOperationsEndpoints().RequireAuthorizationPerOperation(op => op.ToString());
/// app.MapGroup("/api/shared").MapOperationsEndpoints().InSharedSpace().RequireAuthorization("assembly");
/// app.MapGroup("/api/admin").MapOperationsAdminEndpoints().RequireAuthorization("operator");
/// </code>
/// </summary>
public static class OperationsEndpoints
{
    /// <summary>
    /// Maps the routes of a space under <c>/jobs</c>: launch a job, read one, list them, cancel one. The space is
    /// the one of the request context; the actor, the one of the request.
    /// <list type="bullet">
    /// <item><c>POST /jobs</c> — <c>jobs.launch</c>: 202 with the job and its <c>Location</c>.</item>
    /// <item><c>GET /jobs</c> — <c>jobs.list</c>: the jobs the caller may see, <c>?status=</c>, <c>?jobType=</c>, <c>?max=</c>.</item>
    /// <item><c>GET /jobs/{jobUniqueId}</c> — <c>jobs.read</c>: the job, or 404.</item>
    /// <item><c>POST /jobs/{jobUniqueId}/cancel</c> — <c>jobs.cancel</c>: the job cancelled, 404, or 409 when it is not pending.</item>
    /// </list>
    /// </summary>
    public static RouteGroupBuilder MapOperationsEndpoints(this IEndpointRouteBuilder routes)
    {
        Guard.NotNull(routes);
        var group = routes.MapGroup("/jobs").BelongsTo<OperationsModule>().RequireRequestContext().AddErrorTranslation();

        group.MapPost("", async (LaunchJobRequest request, IRequestContext context, IJobService jobs, HttpContext http, CancellationToken ct) =>
        {
            if (context.Space is not { } space)
                return Results.NotFound();
            Guard.NotNull(request);
            var job = await jobs.LaunchAsync(new JobRequest
            {
                JobType = request.JobType ?? string.Empty,
                Space = space,
                Actor = context.Actor,
                Input = request.Input,
                Visibility = Parse.Visibility(request.Visibility) ?? JobVisibility.InitiatorOnly,
                IdempotencyKey = request.IdempotencyKey ?? string.Empty,
            }, ct);
            return Results.Accepted($"{http.Request.PathBase}{http.Request.Path}/{job.JobUniqueId}", JobView.Of(job));
        }).WithMetadata(JobOperations.Launch).LoadsNoResource();

        group.MapGet("", async (IRequestContext context, IJobService jobs, HttpContext http, string? status, string? jobType, int? max, CancellationToken ct) =>
        {
            if (context.Space is not { } space)
                return Results.NotFound();
            var query = new JobQuery { Status = Parse.Status(status), JobType = jobType, MaxResults = max ?? 100 };
            var views = new List<JobView>();
            foreach (var job in await jobs.ListAsync(space, query, context.Actor, ct))
            {
                if (await http.AuthorizeResourceAsync(job))
                    views.Add(JobView.Of(job));
            }
            return Results.Ok(views);
        }).WithMetadata(JobOperations.List);

        group.MapGet("/{jobUniqueId:guid}", async (Guid jobUniqueId, IRequestContext context, IJobService jobs, HttpContext http, CancellationToken ct) =>
        {
            if (context.Space is not { } space)
                return Results.NotFound();
            var job = await jobs.GetAsync(space, jobUniqueId, context.Actor, ct);
            return job is null || !await http.AuthorizeResourceAsync(job) ? Results.NotFound() : Results.Ok(JobView.Of(job));
        }).WithMetadata(JobOperations.Read);

        group.MapPost("/{jobUniqueId:guid}/cancel", async (Guid jobUniqueId, IRequestContext context, IJobService jobs, HttpContext http, CancellationToken ct) =>
        {
            if (context.Space is not { } space)
                return Results.NotFound();
            var job = await jobs.GetAsync(space, jobUniqueId, context.Actor, ct);
            if (job is null || !await http.AuthorizeResourceAsync(job))
                return Results.NotFound();
            var cancelled = await jobs.CancelAsync(space, jobUniqueId, context.Actor, ct);
            return cancelled is null ? Results.NotFound() : Results.Ok(JobView.Of(cancelled));
        }).WithMetadata(JobOperations.Cancel);

        return group;
    }

    /// <summary>
    /// Maps the routes of the administration, which act on spaces rather than in one: the space is the object of
    /// each route, read from its query, its path or its body.
    /// <list type="bullet">
    /// <item><c>GET /jobs</c> — <c>jobs.oversee</c>: the jobs of every space, <c>?compartment=</c> (<c>_shared</c> for the shared space), <c>?status=</c>, <c>?jobType=</c>, <c>?actor=</c>, <c>?max=</c>.</item>
    /// <item><c>GET /jobs/{jobUniqueId}</c> — <c>jobs.oversee</c>: the job, whatever its space.</item>
    /// <item><c>DELETE /compartments/{compartment}/jobs</c> — <c>jobs.purge</c>: every job and schedule of the compartment; replayable.</item>
    /// <item><c>GET /schedules</c>, <c>GET /schedules/{scheduleUniqueId}</c> — <c>schedules.read</c>.</item>
    /// <item><c>POST /schedules</c>, <c>PUT /schedules/{scheduleUniqueId}</c> — <c>schedules.write</c>: 201 or 200 with the schedule.</item>
    /// <item><c>DELETE /schedules/{scheduleUniqueId}</c> — <c>schedules.delete</c>: 204, or 404.</item>
    /// </list>
    /// </summary>
    public static RouteGroupBuilder MapOperationsAdminEndpoints(this IEndpointRouteBuilder routes)
    {
        Guard.NotNull(routes);
        var group = routes.MapGroup(string.Empty).BelongsTo<OperationsModule>()
            .RequireRequestContext(SharedSpaceAcceptance.Refused).ActsOnSpaces().AddErrorTranslation();

        group.MapGet("/jobs", async (IRequestContext context, IJobService jobs, HttpContext http,
            string? compartment, string? status, string? jobType, string? actor, int? max, CancellationToken ct) =>
        {
            var query = new JobQuery
            {
                Space = Parse.Space(compartment),
                Status = Parse.Status(status),
                JobType = jobType,
                Actor = actor is null ? null : Parse.Actor(actor),
                MaxResults = max ?? 100,
            };
            var views = new List<JobView>();
            foreach (var job in await jobs.OverseeAsync(query, context.Actor, ct))
            {
                if (await http.AuthorizeResourceAsync(job))
                    views.Add(JobView.Of(job));
            }
            return Results.Ok(views);
        }).WithMetadata(JobOperations.Oversee);

        group.MapGet("/jobs/{jobUniqueId:guid}", async (Guid jobUniqueId, IRequestContext context, IJobService jobs, HttpContext http, CancellationToken ct) =>
        {
            var job = await jobs.FindAsync(jobUniqueId, context.Actor, ct);
            return job is null || !await http.AuthorizeResourceAsync(job) ? Results.NotFound() : Results.Ok(JobView.Of(job));
        }).WithMetadata(JobOperations.Oversee);

        group.MapDelete("/compartments/{compartment}/jobs", async (string compartment, IRequestContext context, IJobService jobs, CancellationToken ct) =>
        {
            if (!Compartment.TryParse(compartment, out var parsed))
                return Results.NotFound();
            var purged = await jobs.PurgeCompartmentAsync(parsed, context.Actor, ct);
            return Results.Ok(new PurgeResult(purged));
        }).WithMetadata(JobOperations.Purge).LoadsNoResource();

        group.MapGet("/schedules", async (IRequestContext context, IScheduleStore schedules, HttpContext http, string? compartment, CancellationToken ct) =>
        {
            var views = new List<ScheduleView>();
            foreach (var schedule in await schedules.ListAsync(Parse.Space(compartment), context.Actor, ct))
            {
                if (await http.AuthorizeResourceAsync(schedule))
                    views.Add(ScheduleView.Of(schedule));
            }
            return Results.Ok(views);
        }).WithMetadata(JobOperations.ReadSchedules);

        group.MapGet("/schedules/{scheduleUniqueId:guid}", async (Guid scheduleUniqueId, IRequestContext context, IScheduleStore schedules, HttpContext http, CancellationToken ct) =>
        {
            var schedule = await schedules.GetAsync(scheduleUniqueId, context.Actor, ct);
            return schedule is null || !await http.AuthorizeResourceAsync(schedule) ? Results.NotFound() : Results.Ok(ScheduleView.Of(schedule));
        }).WithMetadata(JobOperations.ReadSchedules);

        group.MapPost("/schedules", async (ScheduleRequest request, IRequestContext context, IScheduleStore schedules, IJobService jobs, TimeProvider clock, HttpContext http, CancellationToken ct) =>
        {
            var schedule = Build(Guid.NewGuid(), request, context.Actor, clock.GetUtcNow(), jobs);
            if (!await http.AuthorizeResourceAsync(schedule))
                return Results.NotFound();
            await schedules.UpsertAsync(schedule, context.Actor, ct);
            return Results.Created($"{http.Request.PathBase}{http.Request.Path}/{schedule.ScheduleUniqueId}", ScheduleView.Of(schedule));
        }).WithMetadata(JobOperations.WriteSchedules);

        group.MapPut("/schedules/{scheduleUniqueId:guid}", async (Guid scheduleUniqueId, ScheduleRequest request, IRequestContext context, IScheduleStore schedules, IJobService jobs, TimeProvider clock, HttpContext http, CancellationToken ct) =>
        {
            var existing = await schedules.GetAsync(scheduleUniqueId, context.Actor, ct);
            if (existing is null || !await http.AuthorizeResourceAsync(existing))
                return Results.NotFound();
            var schedule = Build(scheduleUniqueId, request, context.Actor, existing.CreatedAt, jobs);
            if (!await http.AuthorizeResourceAsync(schedule))
                return Results.NotFound();
            await schedules.UpsertAsync(schedule, context.Actor, ct);
            return Results.Ok(ScheduleView.Of((await schedules.GetAsync(scheduleUniqueId, context.Actor, ct))!));
        }).WithMetadata(JobOperations.WriteSchedules);

        group.MapDelete("/schedules/{scheduleUniqueId:guid}", async (Guid scheduleUniqueId, IRequestContext context, IScheduleStore schedules, HttpContext http, CancellationToken ct) =>
        {
            var schedule = await schedules.GetAsync(scheduleUniqueId, context.Actor, ct);
            if (schedule is null || !await http.AuthorizeResourceAsync(schedule))
                return Results.NotFound();
            return await schedules.RemoveAsync(scheduleUniqueId, context.Actor, ct) ? Results.NoContent() : Results.NotFound();
        }).WithMetadata(JobOperations.DeleteSchedules);

        return group;
    }

    private static Schedule Build(Guid id, ScheduleRequest request, Actor actor, DateTimeOffset createdAt, IJobService jobs)
    {
        Guard.NotNull(request);
        var schedule = new Schedule
        {
            ScheduleUniqueId = id,
            Name = request.Name ?? string.Empty,
            CronExpression = request.CronExpression ?? string.Empty,
            TimeZoneId = string.IsNullOrWhiteSpace(request.TimeZoneId) ? "UTC" : request.TimeZoneId,
            JobType = request.JobType ?? string.Empty,
            Space = Parse.Space(request.Compartment) ?? Space.Shared,
            Input = request.Input,
            ConcurrencyPolicy = Parse.Policy(request.ConcurrencyPolicy) ?? ConcurrencyPolicy.Forbid,
            Enabled = request.Enabled ?? true,
            CreatedBy = actor,
            CreatedAt = createdAt,
        };
        if (schedule.FindError() is { } error)
            throw new InvalidScheduleException(error);
        if (!jobs.JobTypes.Contains(schedule.JobType))
            throw new UnknownJobTypeException(schedule.JobType);
        return schedule;
    }

    /// <summary>What a purge answers.</summary>
    public sealed record PurgeResult(int Purged);

    private static class Parse
    {
        public static JobStatus? Status(string? word) => word is null ? null
            : Enum.TryParse<JobStatus>(word, ignoreCase: true, out var status) ? status
            : throw new UserFacingException(Error.Validation("Job.InvalidStatus", $"'{word}' is not a job status: pending, running, succeeded, failed or cancelled."));

        public static JobVisibility? Visibility(string? word) => word is null ? null
            : Enum.TryParse<JobVisibility>(word.Replace("_", string.Empty, StringComparison.Ordinal), ignoreCase: true, out var visibility) ? visibility
            : throw new UserFacingException(Error.Validation("Job.InvalidVisibility", $"'{word}' is not a job visibility: initiator_only or space."));

        public static ConcurrencyPolicy? Policy(string? word) => word is null ? null
            : Enum.TryParse<ConcurrencyPolicy>(word, ignoreCase: true, out var policy) ? policy
            : throw new UserFacingException(Error.Validation("Schedule.InvalidPolicy", $"'{word}' is not a concurrency policy: forbid, allow or replace."));

        public static Space? Space(string? compartment) => compartment is null ? null
            : compartment == Primitives.Space.SharedSegment ? Primitives.Space.Shared
            : Compartment.TryParse(compartment, out var parsed) ? Primitives.Space.Of(parsed)
            : throw new UserFacingException(Error.Validation("Compartment.Invalid", $"'{compartment}' is not a compartment, nor {Primitives.Space.SharedSegment}."));

        public static Actor Actor(string value) => Primitives.Actor.TryParse(value, out var actor) ? actor
            : throw new UserFacingException(Error.Validation("Actor.Invalid", $"'{value}' is not an actor."));
    }
}
