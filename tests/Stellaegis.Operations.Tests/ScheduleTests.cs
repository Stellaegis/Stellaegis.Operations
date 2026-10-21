using Microsoft.Extensions.Configuration;
using Stellaegis.Primitives;

namespace Stellaegis.Operations.Tests;

public sealed class ScheduleTests : IDisposable
{
    private readonly OperationsHarness _harness = new(configuration: new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["Stellaegis:Operations:MaxOccurrencesPerEvaluation"] = "10" })
        .Build());

    public static Schedule EveryMinute(Space space, ConcurrencyPolicy policy = ConcurrencyPolicy.Forbid, string jobType = "echo") => new()
    {
        ScheduleUniqueId = Guid.NewGuid(),
        Name = "Every minute",
        CronExpression = "* * * * *",
        JobType = jobType,
        Space = space,
        ConcurrencyPolicy = policy,
        CreatedBy = OperationsHarness.Bob,
        CreatedAt = new DateTimeOffset(2026, 10, 21, 9, 0, 0, TimeSpan.Zero),
    };

    [Fact]
    public void A_schedule_says_what_keeps_it_from_working()
    {
        var good = EveryMinute(Space.Shared);
        Assert.Null(good.FindError());
        Assert.Contains("cron", (good with { CronExpression = "every day" }).FindError());
        Assert.Contains("time zone", (good with { TimeZoneId = "Mars/Olympus" }).FindError());
        Assert.Contains("job type", (good with { JobType = "Not An Identifier" }).FindError());
        Assert.Null((good with { CronExpression = "*/15 * * * * *", TimeZoneId = "Europe/Paris" }).FindError());
    }

    [Fact]
    public void The_occurrences_between_two_instants_exclude_the_first_and_include_the_last()
    {
        var from = new DateTimeOffset(2026, 10, 21, 10, 0, 0, TimeSpan.Zero);

        var occurrences = CronSchedule.OccurrencesBetween(EveryMinute(Space.Shared), from, from + TimeSpan.FromMinutes(3), 10);

        Assert.Equal([from + TimeSpan.FromMinutes(1), from + TimeSpan.FromMinutes(2), from + TimeSpan.FromMinutes(3)], occurrences);
    }

    [Fact]
    public async Task The_first_evaluation_launches_nothing_and_marks_where_it_stood()
    {
        var schedule = EveryMinute(Space.Of(OperationsHarness.Acme));
        await _harness.Schedules.UpsertAsync(schedule);

        Assert.Equal(0, await _harness.Scheduler.EvaluateOnceAsync());

        Assert.Equal(_harness.Clock.GetUtcNow(), (await _harness.Schedules.GetAsync(schedule.ScheduleUniqueId))!.LastEvaluatedAt);
    }

    [Fact]
    public async Task A_due_occurrence_launches_a_job_of_the_schedule_in_its_space_under_the_scheduler()
    {
        var schedule = EveryMinute(Space.Of(OperationsHarness.Acme));
        await _harness.Schedules.UpsertAsync(schedule);
        await _harness.Scheduler.EvaluateOnceAsync();
        _harness.Clock.Advance(TimeSpan.FromMinutes(1));

        Assert.Equal(1, await _harness.Scheduler.EvaluateOnceAsync());

        var job = Assert.Single(await _harness.Jobs.ListAsync(new JobQuery()));
        Assert.Equal(Space.Of(OperationsHarness.Acme), job.Space);
        Assert.Equal(ScheduleWorker.Actor, job.Actor);
        Assert.Equal(schedule.ScheduleUniqueId, job.ScheduleUniqueId);
        Assert.Equal(_harness.Clock.GetUtcNow(), job.ScheduledFor);
        Assert.Equal(JobVisibility.Space, job.Visibility);
    }

    [Fact]
    public async Task The_occurrences_missed_while_the_host_was_down_are_caught_up_from_the_persisted_mark()
    {
        var schedule = EveryMinute(Space.Shared, ConcurrencyPolicy.Allow);
        await _harness.Schedules.UpsertAsync(schedule);
        await _harness.Scheduler.EvaluateOnceAsync();
        _harness.Clock.Advance(TimeSpan.FromMinutes(15));

        Assert.Equal(10, await _harness.Scheduler.EvaluateOnceAsync());
        Assert.Equal(5, await _harness.Scheduler.EvaluateOnceAsync());
        Assert.Equal(0, await _harness.Scheduler.EvaluateOnceAsync());

        Assert.Equal(15, (await _harness.Jobs.ListAsync(new JobQuery { MaxResults = 100 })).Count);
    }

    [Fact]
    public async Task An_occurrence_is_claimed_by_one_worker_only()
    {
        var schedule = EveryMinute(Space.Shared);
        await _harness.Schedules.UpsertAsync(schedule);
        var due = _harness.Clock.GetUtcNow();

        Assert.True(await _harness.Schedules.TryClaimOccurrenceAsync(schedule.ScheduleUniqueId, due));
        Assert.False(await _harness.Schedules.TryClaimOccurrenceAsync(schedule.ScheduleUniqueId, due));
    }

    [Fact]
    public async Task Forbid_skips_an_occurrence_while_a_job_of_the_schedule_is_in_flight()
    {
        await _harness.Schedules.UpsertAsync(EveryMinute(Space.Shared, ConcurrencyPolicy.Forbid));
        await _harness.Scheduler.EvaluateOnceAsync();
        _harness.Clock.Advance(TimeSpan.FromMinutes(1));
        await _harness.Scheduler.EvaluateOnceAsync();
        _harness.Clock.Advance(TimeSpan.FromMinutes(1));

        Assert.Equal(0, await _harness.Scheduler.EvaluateOnceAsync());

        await _harness.Worker.RunUntilIdleAsync();
        _harness.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(1, await _harness.Scheduler.EvaluateOnceAsync());
    }

    [Fact]
    public async Task Replace_cancels_the_pending_jobs_of_the_schedule_before_launching()
    {
        await _harness.Schedules.UpsertAsync(EveryMinute(Space.Shared, ConcurrencyPolicy.Replace));
        await _harness.Scheduler.EvaluateOnceAsync();
        _harness.Clock.Advance(TimeSpan.FromMinutes(1));
        await _harness.Scheduler.EvaluateOnceAsync();
        _harness.Clock.Advance(TimeSpan.FromMinutes(1));

        Assert.Equal(1, await _harness.Scheduler.EvaluateOnceAsync());

        var jobs = await _harness.Jobs.ListAsync(new JobQuery());
        Assert.Equal(2, jobs.Count);
        Assert.Single(jobs, job => job.Status == JobStatus.Cancelled);
        Assert.Single(jobs, job => job.Status == JobStatus.Pending);
    }

    [Fact]
    public async Task A_disabled_or_invalid_schedule_launches_nothing()
    {
        await _harness.Schedules.UpsertAsync(EveryMinute(Space.Shared) with { Enabled = false });
        await _harness.Schedules.UpsertAsync(EveryMinute(Space.Shared) with { CronExpression = "nonsense" });
        await _harness.Scheduler.EvaluateOnceAsync();
        _harness.Clock.Advance(TimeSpan.FromMinutes(1));

        Assert.Equal(0, await _harness.Scheduler.EvaluateOnceAsync());
    }

    public void Dispose() => _harness.Dispose();
}
