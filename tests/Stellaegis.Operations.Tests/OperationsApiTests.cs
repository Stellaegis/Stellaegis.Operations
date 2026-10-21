using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Stellaegis.Operations.Tests;

public sealed class OperationsApiTests : IAsyncLifetime
{
    private OperationsHost _host = null!;
    private HttpClient _jane = null!;
    private HttpClient _bob = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _host = await OperationsHost.StartAsync();
        _jane = _host.Client("jane");
        _bob = _host.Client("bob");
    }

    public async ValueTask DisposeAsync()
    {
        _jane.Dispose();
        _bob.Dispose();
        await _host.DisposeAsync();
    }

    private static async Task<JobView> LaunchAsync(HttpClient client, string prefix, string jobType, object? input = null, string? visibility = null)
    {
        var response = await client.PostAsJsonAsync($"{prefix}/jobs", new { jobType, input, visibility }, Ct);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return await OperationsHost.ReadAsync<JobView>(response);
    }

    [Fact]
    public async Task A_launched_job_is_accepted_with_its_location_and_read_in_its_space_only()
    {
        var response = await _jane.PostAsJsonAsync("/api/t/acme/jobs", new { jobType = "echo", input = new { name = "x" } }, Ct);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var job = await OperationsHost.ReadAsync<JobView>(response);
        Assert.Equal($"/api/t/acme/jobs/{job.JobUniqueId}", response.Headers.Location!.ToString());
        Assert.Equal("pending", job.Status);
        Assert.Equal("acme", job.Compartment);
        Assert.Equal("jane", job.Actor);
        Assert.Equal("initiator_only", job.Visibility);

        Assert.Equal(HttpStatusCode.OK, (await _jane.GetAsync(response.Headers.Location, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _jane.GetAsync($"/api/t/globex/jobs/{job.JobUniqueId}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _jane.GetAsync($"/api/shared/jobs/{job.JobUniqueId}", Ct)).StatusCode);
    }

    [Fact]
    public async Task An_unknown_job_type_is_a_validation_problem()
    {
        var response = await _jane.PostAsJsonAsync("/api/t/acme/jobs", new { jobType = "nobody_runs_this" }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await OperationsHost.ReadAsync<JsonElement>(response);
        Assert.Contains("JobType.Unknown", problem.GetRawText());
    }

    [Fact]
    public async Task A_job_is_visible_to_its_initiator_or_to_its_space_as_its_visibility_says()
    {
        var mine = await LaunchAsync(_jane, "/api/t/acme", "echo");
        var ours = await LaunchAsync(_jane, "/api/t/acme", "echo", visibility: "space");

        Assert.Equal(HttpStatusCode.NotFound, (await _bob.GetAsync($"/api/t/acme/jobs/{mine.JobUniqueId}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _bob.GetAsync($"/api/t/acme/jobs/{ours.JobUniqueId}", Ct)).StatusCode);
        var bobSees = await OperationsHost.ReadAsync<List<JobView>>(await _bob.GetAsync("/api/t/acme/jobs", Ct));
        var janeSees = await OperationsHost.ReadAsync<List<JobView>>(await _jane.GetAsync("/api/t/acme/jobs", Ct));
        Assert.Equal(ours.JobUniqueId, Assert.Single(bobSees).JobUniqueId);
        Assert.Equal(2, janeSees.Count);
    }

    [Fact]
    public async Task A_pending_job_is_cancelled_once()
    {
        var job = await LaunchAsync(_jane, "/api/t/acme", "echo");

        var cancelled = await _jane.PostAsync($"/api/t/acme/jobs/{job.JobUniqueId}/cancel", null, Ct);
        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        Assert.Equal("cancelled", (await OperationsHost.ReadAsync<JobView>(cancelled)).Status);

        var again = await _jane.PostAsync($"/api/t/acme/jobs/{job.JobUniqueId}/cancel", null, Ct);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _bob.PostAsync($"/api/t/acme/jobs/{job.JobUniqueId}/cancel", null, Ct)).StatusCode);
    }

    [Fact]
    public async Task A_job_of_the_shared_space_has_no_compartment()
    {
        var job = await LaunchAsync(_jane, "/api/shared", "echo");

        Assert.Null(job.Compartment);
        Assert.Equal(HttpStatusCode.OK, (await _jane.GetAsync($"/api/shared/jobs/{job.JobUniqueId}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _jane.GetAsync($"/api/t/acme/jobs/{job.JobUniqueId}", Ct)).StatusCode);
    }

    [Fact]
    public async Task The_worker_runs_the_job_on_behalf_of_the_caller_and_the_route_answers_its_outcome()
    {
        var job = await LaunchAsync(_jane, "/api/t/acme", "echo", input: new { name = "x" });

        Assert.Equal(1, await _host.Worker.RunUntilIdleAsync(Ct));

        var done = await OperationsHost.ReadAsync<JobView>(await _jane.GetAsync($"/api/t/acme/jobs/{job.JobUniqueId}", Ct));
        Assert.Equal("succeeded", done.Status);
        Assert.Equal("acme", done.Result!.Value.GetProperty("space").GetString());
        Assert.Equal("jane", done.Result!.Value.GetProperty("actor").GetString());
        Assert.Equal("job:echo", done.Result!.Value.GetProperty("via").GetString());
    }

    [Fact]
    public async Task The_administration_oversees_every_space_and_purges_a_compartment()
    {
        var acme = await LaunchAsync(_jane, "/api/t/acme", "echo");
        await LaunchAsync(_jane, "/api/t/globex", "echo");
        await LaunchAsync(_jane, "/api/shared", "echo", visibility: "space");

        var all = await OperationsHost.ReadAsync<List<JobView>>(await _bob.GetAsync("/api/admin/jobs", Ct));
        var shared = await OperationsHost.ReadAsync<List<JobView>>(await _bob.GetAsync("/api/admin/jobs?compartment=_shared", Ct));
        var janes = await OperationsHost.ReadAsync<List<JobView>>(await _jane.GetAsync("/api/admin/jobs?compartment=acme", Ct));

        Assert.Single(all);
        Assert.Single(shared);
        Assert.Equal(acme.JobUniqueId, Assert.Single(janes).JobUniqueId);
        Assert.Equal(HttpStatusCode.OK, (await _jane.GetAsync($"/api/admin/jobs/{acme.JobUniqueId}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _bob.GetAsync($"/api/admin/jobs/{acme.JobUniqueId}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _bob.GetAsync("/api/admin/jobs?status=sleeping", Ct)).StatusCode);

        var purge = await _bob.DeleteAsync("/api/admin/compartments/acme/jobs", Ct);
        Assert.Equal(HttpStatusCode.OK, purge.StatusCode);
        Assert.Equal(1, (await OperationsHost.ReadAsync<JsonElement>(purge)).GetProperty("purged").GetInt32());
        Assert.Equal(0, (await OperationsHost.ReadAsync<JsonElement>(await _bob.DeleteAsync("/api/admin/compartments/acme/jobs", Ct))).GetProperty("purged").GetInt32());
        Assert.Empty(await OperationsHost.ReadAsync<List<JobView>>(await _jane.GetAsync("/api/admin/jobs?compartment=acme", Ct)));
    }

    [Fact]
    public async Task The_administration_creates_reads_changes_and_removes_schedules()
    {
        var created = await _bob.PostAsJsonAsync("/api/admin/schedules",
            new { name = "Nightly", cronExpression = "0 2 * * *", jobType = "echo", compartment = "acme", timeZoneId = "Europe/Paris", concurrencyPolicy = "replace" }, Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var schedule = await OperationsHost.ReadAsync<ScheduleView>(created);
        Assert.Equal($"/api/admin/schedules/{schedule.ScheduleUniqueId}", created.Headers.Location!.ToString());
        Assert.Equal("acme", schedule.Compartment);
        Assert.Equal("replace", schedule.ConcurrencyPolicy);
        Assert.Equal("bob", schedule.CreatedBy);

        Assert.Single(await OperationsHost.ReadAsync<List<ScheduleView>>(await _bob.GetAsync("/api/admin/schedules", Ct)));
        Assert.Empty(await OperationsHost.ReadAsync<List<ScheduleView>>(await _bob.GetAsync("/api/admin/schedules?compartment=globex", Ct)));

        var changed = await _bob.PutAsJsonAsync(created.Headers.Location,
            new { name = "Nightly, disabled", cronExpression = "0 3 * * *", jobType = "echo", compartment = "acme", enabled = false }, Ct);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        var view = await OperationsHost.ReadAsync<ScheduleView>(changed);
        Assert.False(view.Enabled);
        Assert.Equal("0 3 * * *", view.CronExpression);
        Assert.Equal("forbid", view.ConcurrencyPolicy);

        Assert.Equal(HttpStatusCode.NoContent, (await _bob.DeleteAsync(created.Headers.Location, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _bob.DeleteAsync(created.Headers.Location, Ct)).StatusCode);
    }

    [Fact]
    public async Task A_schedule_that_does_not_hold_is_a_validation_problem()
    {
        var badCron = await _bob.PostAsJsonAsync("/api/admin/schedules", new { name = "Bad", cronExpression = "whenever", jobType = "echo" }, Ct);
        var badType = await _bob.PostAsJsonAsync("/api/admin/schedules", new { name = "Bad", cronExpression = "* * * * *", jobType = "nobody_runs_this" }, Ct);
        var badPolicy = await _bob.PostAsJsonAsync("/api/admin/schedules", new { name = "Bad", cronExpression = "* * * * *", jobType = "echo", concurrencyPolicy = "maybe" }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, badCron.StatusCode);
        Assert.Contains("Schedule.Invalid", (await OperationsHost.ReadAsync<JsonElement>(badCron)).GetRawText());
        Assert.Equal(HttpStatusCode.BadRequest, badType.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, badPolicy.StatusCode);
    }
}
