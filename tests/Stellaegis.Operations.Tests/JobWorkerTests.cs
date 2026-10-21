using System.Text.Json;
using Stellaegis.Primitives;

namespace Stellaegis.Operations.Tests;

public abstract class JobWorkerContract(PostgreSqlFixture? postgreSql) : StoreContract(postgreSql)
{
    protected override IReadOnlyDictionary<string, string?> Settings => new Dictionary<string, string?> { ["Stellaegis:Operations:MaxConcurrentJobs"] = "2" };

    [Fact]
    public async Task A_handler_runs_in_the_space_of_the_job_on_behalf_of_its_actor_through_the_worker()
    {
        var job = await Harness.LaunchAsync("echo", input: new { name = "x" });

        Assert.Equal(1, await Harness.Worker.RunUntilIdleAsync());

        var done = (await Harness.Jobs.FindAsync(job.JobUniqueId, OperationsHarness.Bob))!;
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
        var job = await Harness.LaunchAsync("refuse");

        await Harness.Worker.RunUntilIdleAsync();

        var failed = (await Harness.Jobs.FindAsync(job.JobUniqueId, OperationsHarness.Bob))!;
        Assert.Equal(JobStatus.Failed, failed.Status);
        var failure = failed.Result!.Value.Deserialize<JobFailure>(JobJson.Options)!;
        Assert.Equal("Quota.Exceeded", failure.Code);
        Assert.Equal("The quota of the space is exceeded.", failure.Message);
        Assert.Null(failure.LogId);
    }

    [Fact]
    public async Task Any_other_failure_keeps_only_a_code_and_a_log_identifier()
    {
        var job = await Harness.LaunchAsync("crash");

        await Harness.Worker.RunUntilIdleAsync();

        var failed = (await Harness.Jobs.FindAsync(job.JobUniqueId, OperationsHarness.Bob))!;
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
            await Harness.LaunchAsync("block");

        await Harness.Worker.RunOnceAsync();
        await Harness.Worker.RunOnceAsync();

        Assert.Equal(2, (await Harness.Jobs.ListAsync(new JobQuery { Status = JobStatus.Running }, OperationsHarness.Bob)).Count);
        Assert.Single(await Harness.Jobs.ListAsync(new JobQuery { Status = JobStatus.Pending }, OperationsHarness.Bob));
        await WaitForAsync(() => Task.FromResult(Harness.Gate.Entered == 2));

        Harness.Gate.Open();
        await WaitForAsync(async () => (await Harness.Jobs.ListAsync(new JobQuery { Status = JobStatus.Succeeded }, OperationsHarness.Bob)).Count == 2);
        await WaitForAsync(async () =>
        {
            await Harness.Worker.RunOnceAsync();
            return (await Harness.Jobs.ListAsync(new JobQuery { Status = JobStatus.Succeeded }, OperationsHarness.Bob)).Count == 3;
        });
    }

    [Fact]
    public async Task Concurrent_claims_never_hand_the_same_job_twice()
    {
        for (var i = 0; i < 20; i++)
            await Harness.LaunchAsync("echo");

        var claims = await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => Task.Run(() => Harness.Jobs.ClaimAsync(1, TimeSpan.FromMinutes(1), JobWorker.Actor))));

        var ids = claims.SelectMany(claimed => claimed).Select(job => job.JobUniqueId).ToList();
        Assert.Equal(20, ids.Count);
        Assert.Equal(20, ids.Distinct().Count());
    }

    [Fact]
    public async Task A_running_job_whose_claim_expired_is_claimed_again()
    {
        var job = await Harness.LaunchAsync("echo");
        await Harness.Jobs.ClaimAsync(1, TimeSpan.FromMinutes(10), JobWorker.Actor);

        Assert.Empty(await Harness.Jobs.ClaimAsync(1, TimeSpan.FromMinutes(10), JobWorker.Actor));
        Harness.Clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Equal(job.JobUniqueId, Assert.Single(await Harness.Jobs.ClaimAsync(1, TimeSpan.FromMinutes(10), JobWorker.Actor)).JobUniqueId);
    }

    [Fact]
    public async Task Completed_jobs_past_their_retention_are_cleaned()
    {
        var done = await Harness.LaunchAsync("echo");
        await Harness.Worker.RunUntilIdleAsync();
        var pending = await Harness.LaunchAsync("echo");

        Harness.Clock.Advance(TimeSpan.FromDays(8));

        Assert.Equal(1, await Harness.Jobs.CleanExpiredAsync(OperationsHarness.Bob));
        Assert.Null(await Harness.Jobs.FindAsync(done.JobUniqueId, OperationsHarness.Bob));
        Assert.NotNull(await Harness.Jobs.FindAsync(pending.JobUniqueId, OperationsHarness.Bob));
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

}

public sealed class InMemoryJobWorkerTests() : JobWorkerContract(null);

public sealed class PostgreSqlJobWorkerTests(PostgreSqlFixture postgreSql) : JobWorkerContract(postgreSql), IClassFixture<PostgreSqlFixture>;
