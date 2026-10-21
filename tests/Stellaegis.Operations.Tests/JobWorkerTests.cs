using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Stellaegis.Primitives;

namespace Stellaegis.Operations.Tests;

public sealed class JobWorkerTests : IDisposable
{
    private readonly OperationsHarness _harness = new(configuration: new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["Stellaegis:Operations:MaxConcurrentJobs"] = "2" })
        .Build());

    [Fact]
    public async Task A_handler_runs_in_the_space_of_the_job_on_behalf_of_its_actor_through_the_worker()
    {
        var job = await _harness.LaunchAsync("echo", input: new { name = "x" });

        Assert.Equal(1, await _harness.Worker.RunUntilIdleAsync());

        var done = (await _harness.Jobs.FindAsync(job.JobUniqueId))!;
        Assert.Equal(JobStatus.Succeeded, done.Status);
        Assert.Equal("acme", done.Result!.Value.GetProperty("space").GetString());
        Assert.Equal("jane", done.Result!.Value.GetProperty("actor").GetString());
        Assert.Equal("job:echo", done.Result!.Value.GetProperty("via").GetString());
        Assert.Equal("x", done.Result!.Value.GetProperty("input").GetProperty("name").GetString());
        Assert.Equal(50, done.Progress);
        Assert.Equal("Halfway", done.ProgressLabel);
        Assert.NotNull(done.StartedAt);
        Assert.NotNull(done.CompletedAt);
        Assert.Null(done.ClaimedUntil);
    }

    [Fact]
    public async Task A_failure_meant_for_the_caller_keeps_its_code_and_message()
    {
        var job = await _harness.LaunchAsync("refuse");

        await _harness.Worker.RunUntilIdleAsync();

        var failed = (await _harness.Jobs.FindAsync(job.JobUniqueId))!;
        Assert.Equal(JobStatus.Failed, failed.Status);
        var failure = failed.Result!.Value.Deserialize<JobFailure>(JobJson.Options)!;
        Assert.Equal("Quota.Exceeded", failure.Code);
        Assert.Equal("The quota of the space is exceeded.", failure.Message);
        Assert.Null(failure.LogId);
    }

    [Fact]
    public async Task Any_other_failure_keeps_only_a_code_and_a_log_identifier()
    {
        var job = await _harness.LaunchAsync("crash");

        await _harness.Worker.RunUntilIdleAsync();

        var failed = (await _harness.Jobs.FindAsync(job.JobUniqueId))!;
        Assert.Equal(JobStatus.Failed, failed.Status);
        var failure = failed.Result!.Value.Deserialize<JobFailure>(JobJson.Options)!;
        Assert.Equal(JobErrors.Failed, failure.Code);
        Assert.Null(failure.Message);
        Assert.NotNull(failure.LogId);
        Assert.DoesNotContain("secret", failed.Result!.Value.GetRawText());
    }

    [Fact]
    public async Task The_worker_runs_no_more_jobs_at_once_than_configured()
    {
        for (var i = 0; i < 3; i++)
            await _harness.LaunchAsync("block");

        await _harness.Worker.RunOnceAsync();
        await _harness.Worker.RunOnceAsync();

        Assert.Equal(2, (await _harness.Jobs.ListAsync(new JobQuery { Status = JobStatus.Running })).Count);
        Assert.Single(await _harness.Jobs.ListAsync(new JobQuery { Status = JobStatus.Pending }));
        await WaitForAsync(() => Task.FromResult(_harness.Gate.Entered == 2));

        _harness.Gate.Open();
        await WaitForAsync(async () => (await _harness.Jobs.ListAsync(new JobQuery { Status = JobStatus.Succeeded })).Count == 2);
        await WaitForAsync(async () =>
        {
            await _harness.Worker.RunOnceAsync();
            return (await _harness.Jobs.ListAsync(new JobQuery { Status = JobStatus.Succeeded })).Count == 3;
        });
    }

    [Fact]
    public async Task Concurrent_claims_never_hand_the_same_job_twice()
    {
        for (var i = 0; i < 20; i++)
            await _harness.LaunchAsync("echo");

        var claims = await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => Task.Run(() => _harness.Jobs.ClaimAsync(1, TimeSpan.FromMinutes(1), JobWorker.Actor))));

        var ids = claims.SelectMany(claimed => claimed).Select(job => job.JobUniqueId).ToList();
        Assert.Equal(20, ids.Count);
        Assert.Equal(20, ids.Distinct().Count());
    }

    [Fact]
    public async Task A_running_job_whose_claim_expired_is_claimed_again()
    {
        var job = await _harness.LaunchAsync("echo");
        await _harness.Jobs.ClaimAsync(1, TimeSpan.FromMinutes(10), JobWorker.Actor);

        Assert.Empty(await _harness.Jobs.ClaimAsync(1, TimeSpan.FromMinutes(10), JobWorker.Actor));
        _harness.Clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Equal(job.JobUniqueId, Assert.Single(await _harness.Jobs.ClaimAsync(1, TimeSpan.FromMinutes(10), JobWorker.Actor)).JobUniqueId);
    }

    [Fact]
    public async Task Completed_jobs_past_their_retention_are_cleaned()
    {
        var done = await _harness.LaunchAsync("echo");
        await _harness.Worker.RunUntilIdleAsync();
        var pending = await _harness.LaunchAsync("echo");

        _harness.Clock.Advance(TimeSpan.FromDays(8));

        Assert.Equal(1, await _harness.Jobs.CleanExpiredAsync());
        Assert.Null(await _harness.Jobs.FindAsync(done.JobUniqueId));
        Assert.NotNull(await _harness.Jobs.FindAsync(pending.JobUniqueId));
    }

    private static async Task WaitForAsync(Func<Task<bool>> condition)
    {
        for (var i = 0; i < 200; i++)
        {
            if (await condition())
                return;
            await Task.Delay(25);
        }
        Assert.Fail("The condition was not met in time.");
    }

    public void Dispose() => _harness.Dispose();
}
