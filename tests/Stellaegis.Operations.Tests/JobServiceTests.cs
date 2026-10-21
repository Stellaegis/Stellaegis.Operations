using Stellaegis.Primitives;

namespace Stellaegis.Operations.Tests;

public abstract class JobServiceContract(PostgreSqlFixture? postgreSql) : StoreContract(postgreSql)
{

    [Fact]
    public async Task A_launched_job_is_pending_in_its_space_for_its_actor()
    {
        var job = await Harness.LaunchAsync("echo", input: new { name = "x" });

        Assert.Equal(JobStatus.Pending, job.Status);
        Assert.Equal(Space.Of(OperationsHarness.Acme), job.Space);
        Assert.Equal(OperationsHarness.Jane, job.Actor);
        Assert.Equal(JobVisibility.InitiatorOnly, job.Visibility);
        Assert.Equal(Harness.Clock.GetUtcNow() + TimeSpan.FromDays(7), job.ExpiresAt);
    }

    [Fact]
    public async Task An_unknown_job_type_is_refused_before_anything_is_stored()
    {
        var exception = await Assert.ThrowsAsync<UnknownJobTypeException>(() => Harness.LaunchAsync("nobody_runs_this"));

        Assert.Equal(JobErrors.UnknownType, exception.Error.Code);
        Assert.Empty(await Harness.Jobs.ListAsync(new JobQuery(), OperationsHarness.Bob));
    }

    [Fact]
    public async Task A_launch_replayed_with_its_idempotency_key_creates_nothing_twice()
    {
        var first = await Harness.LaunchAsync("echo", idempotencyKey: "import-42");
        var again = await Harness.LaunchAsync("echo", idempotencyKey: "import-42");
        var elsewhere = await Harness.LaunchAsync("echo", space: Space.Of(OperationsHarness.Globex), idempotencyKey: "import-42");

        Assert.Equal(first.JobUniqueId, again.JobUniqueId);
        Assert.NotEqual(first.JobUniqueId, elsewhere.JobUniqueId);
    }

    [Fact]
    public async Task A_job_is_read_in_its_space_only_and_overseen_from_any()
    {
        var job = await Harness.LaunchAsync("echo");
        var service = Harness.Service(out var scope);
        using (scope)
        {
            Assert.NotNull(await service.GetAsync(Space.Of(OperationsHarness.Acme), job.JobUniqueId, OperationsHarness.Jane));
            Assert.Null(await service.GetAsync(Space.Of(OperationsHarness.Globex), job.JobUniqueId, OperationsHarness.Jane));
            Assert.Null(await service.GetAsync(Space.Shared, job.JobUniqueId, OperationsHarness.Jane));
            Assert.NotNull(await service.FindAsync(job.JobUniqueId, OperationsHarness.Bob));
        }
    }

    [Fact]
    public async Task A_list_is_bound_to_its_space_whatever_the_query_says()
    {
        await Harness.LaunchAsync("echo");
        await Harness.LaunchAsync("echo", space: Space.Of(OperationsHarness.Globex));
        await Harness.LaunchAsync("echo", space: Space.Shared);
        var service = Harness.Service(out var scope);
        using (scope)
        {
            var acme = await service.ListAsync(Space.Of(OperationsHarness.Acme), new JobQuery { Space = Space.Of(OperationsHarness.Globex) }, OperationsHarness.Jane);
            var shared = await service.ListAsync(Space.Shared, new JobQuery(), OperationsHarness.Jane);
            var all = await service.OverseeAsync(new JobQuery(), OperationsHarness.Bob);

            Assert.Equal(Space.Of(OperationsHarness.Acme), Assert.Single(acme).Space);
            Assert.True(Assert.Single(shared).Space.IsShared);
            Assert.Equal(3, all.Count);
        }
    }

    [Fact]
    public async Task Only_a_pending_job_is_cancelled()
    {
        var pending = await Harness.LaunchAsync("echo");
        var done = await Harness.LaunchAsync("echo");
        await Harness.Jobs.ClaimAsync(1, TimeSpan.FromMinutes(1), JobWorker.Actor);
        var service = Harness.Service(out var scope);
        using (scope)
        {
            var running = (await service.FindAsync(pending.JobUniqueId, OperationsHarness.Jane))!;
            Assert.Equal(JobStatus.Running, running.Status);
            var exception = await Assert.ThrowsAsync<JobNotPendingException>(() => service.CancelAsync(running.Space, running.JobUniqueId, OperationsHarness.Jane));
            Assert.Equal(JobErrors.NotPending, exception.Error.Code);

            var cancelled = await service.CancelAsync(done.Space, done.JobUniqueId, OperationsHarness.Jane);
            Assert.Equal(JobStatus.Cancelled, cancelled!.Status);
            Assert.Null(await service.CancelAsync(Space.Of(OperationsHarness.Globex), done.JobUniqueId, OperationsHarness.Jane));
        }
    }

    [Fact]
    public async Task A_purge_deletes_the_jobs_and_schedules_of_a_compartment_and_is_replayable()
    {
        await Harness.LaunchAsync("echo");
        await Harness.LaunchAsync("echo", space: Space.Of(OperationsHarness.Globex));
        await Harness.Schedules.UpsertAsync(ScheduleContract.EveryMinute(Space.Of(OperationsHarness.Acme)), OperationsHarness.Bob);
        var service = Harness.Service(out var scope);
        using (scope)
        {
            Assert.Equal(2, await service.PurgeCompartmentAsync(OperationsHarness.Acme, OperationsHarness.Bob));
            Assert.Equal(0, await service.PurgeCompartmentAsync(OperationsHarness.Acme, OperationsHarness.Bob));
            Assert.Equal(Space.Of(OperationsHarness.Globex), Assert.Single(await service.OverseeAsync(new JobQuery(), OperationsHarness.Bob)).Space);
        }
    }

    [Fact]
    public void The_job_types_are_those_of_the_handlers()
    {
        var service = Harness.Service(out var scope);
        using (scope)
            Assert.Equal(["block", "crash", "echo", "refuse"], service.JobTypes.Order());
    }

}

public sealed class InMemoryJobServiceTests() : JobServiceContract(null);

public sealed class PostgreSqlJobServiceTests(PostgreSqlFixture postgreSql) : JobServiceContract(postgreSql), IClassFixture<PostgreSqlFixture>;
