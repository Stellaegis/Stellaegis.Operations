using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Stellaegis.Operations.PostgreSQL;
using Stellaegis.Primitives;
using Stellaegis.Primitives.Hosting;

namespace Stellaegis.Operations.Tests;

/// <summary>The module on one of its stores, with the test handlers, driven by a test clock.</summary>
public sealed class OperationsHarness : IDisposable
{
    public static readonly Compartment Acme = Compartment.Parse("acme");
    public static readonly Compartment Globex = Compartment.Parse("globex");
    public static readonly Actor Jane = Actor.Parse("jane");
    public static readonly Actor Bob = Actor.Parse("bob");

    /// <param name="postgreSql">The connection string of a PostgreSQL store, or <see langword="null"/> for the in-memory one.</param>
    /// <param name="settings">The settings of <c>Stellaegis:Operations</c>.</param>
    public OperationsHarness(string? postgreSql = null, IReadOnlyDictionary<string, string?>? settings = null)
    {
        var values = new Dictionary<string, string?>(settings ?? new Dictionary<string, string?>());
        if (postgreSql is not null)
            values["Stellaegis:Operations:PostgreSQL:ConnectionString"] = postgreSql;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        var services = new ServiceCollection()
            .AddLogging()
            .AddStellaegisFrame(PostgreSqlFixture.Frame)
            .AddSingleton<TimeProvider>(Clock)
            .AddSingleton<Gate>();
        if (postgreSql is null)
            services.AddStellaegisOperationsInMemory(configuration, handlers => handlers.AddFromAssemblyOf<OperationsHarness>());
        else
            services.AddStellaegisOperationsPostgreSQL(configuration, handlers => handlers.AddFromAssemblyOf<OperationsHarness>());
        Provider = services.BuildServiceProvider();
    }

    public TestClock Clock { get; } = new();
    public ServiceProvider Provider { get; }
    public IJobStore Jobs => Provider.GetRequiredService<IJobStore>();
    public IScheduleStore Schedules => Provider.GetRequiredService<IScheduleStore>();
    public JobWorker Worker => Provider.GetRequiredService<JobWorker>();
    public ScheduleWorker Scheduler => Provider.GetRequiredService<ScheduleWorker>();
    public Gate Gate => Provider.GetRequiredService<Gate>();

    public IJobService Service(out IServiceScope scope)
    {
        scope = Provider.CreateScope();
        return scope.ServiceProvider.GetRequiredService<IJobService>();
    }

    public async Task<JobRecord> LaunchAsync(string jobType, Space? space = null, Actor? actor = null, object? input = null, string idempotencyKey = "")
    {
        using var scope = Provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IJobService>();
        return await service.LaunchAsync(new JobRequest
        {
            JobType = jobType,
            Space = space ?? Space.Of(Acme),
            Actor = actor ?? Jane,
            Input = input is null ? null : JsonSerializer.SerializeToElement(input),
            IdempotencyKey = idempotencyKey,
        });
    }

    public void Dispose() => Provider.Dispose();
}

/// <summary>
/// A test class run on each store: the in-memory one, and the PostgreSQL one when a server answers. The tests
/// of a derived class use <see cref="Harness"/>, built fresh for each test.
/// </summary>
public abstract class StoreContract : IAsyncLifetime
{
    private readonly PostgreSqlFixture? _postgreSql;

    protected StoreContract(PostgreSqlFixture? postgreSql) => _postgreSql = postgreSql;

    protected OperationsHarness Harness { get; private set; } = null!;

    /// <summary>The settings of <c>Stellaegis:Operations</c> for the class.</summary>
    protected virtual IReadOnlyDictionary<string, string?>? Settings => null;

    public async ValueTask InitializeAsync()
    {
        if (_postgreSql is null)
        {
            Harness = new OperationsHarness(settings: Settings);
            return;
        }
        Assert.SkipWhen(_postgreSql.Unavailable is not null, _postgreSql.Unavailable ?? string.Empty);
        await _postgreSql.ResetAsync();
        Harness = new OperationsHarness(_postgreSql.AppConnectionString, Settings);
    }

    public ValueTask DisposeAsync()
    {
        Harness?.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>What the blocking handler waits on, released by the test.</summary>
public sealed class Gate
{
    private readonly TaskCompletionSource _source = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _entered;

    public int Entered => Volatile.Read(ref _entered);

    public Task WaitAsync()
    {
        Interlocked.Increment(ref _entered);
        return _source.Task;
    }

    public void Open() => _source.TrySetResult();
}

/// <summary>Answers with where it ran and on whose behalf.</summary>
public sealed class EchoJob : IJobHandler
{
    public string JobType => "echo";

    public async Task ExecuteAsync(JobContext job, CancellationToken cancellationToken)
    {
        await job.Progress.ReportAsync(50, "Halfway", cancellationToken);
        job.Result = new { space = job.Space.ToSegment(), actor = job.Actor.Value, via = job.Actor.Via?.Value, input = job.Input };
    }
}

/// <summary>Fails with an error meant for the caller.</summary>
public sealed class RefusingJob : IJobHandler
{
    public string JobType => "refuse";

    public Task ExecuteAsync(JobContext job, CancellationToken cancellationToken)
        => throw new UserFacingException(Error.Validation("Quota.Exceeded", "The quota of the space is exceeded."));
}

/// <summary>Fails with an exception nobody meant for the caller.</summary>
public sealed class CrashingJob : IJobHandler
{
    public string JobType => "crash";

    public Task ExecuteAsync(JobContext job, CancellationToken cancellationToken)
        => throw new InvalidOperationException("secret connection string in the message");
}

/// <summary>Runs until the gate opens.</summary>
public sealed class BlockingJob(Gate gate) : IJobHandler
{
    public string JobType => "block";

    public Task ExecuteAsync(JobContext job, CancellationToken cancellationToken) => gate.WaitAsync();
}
