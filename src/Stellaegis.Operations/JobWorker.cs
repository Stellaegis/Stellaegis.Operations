using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stellaegis.Primitives;

namespace Stellaegis.Operations;

/// <summary>
/// Claims the pending jobs and runs them, each in a scope of its own, through the handler of its type. One
/// worker per host that registers the module; several hosts share the store safely, the claim being atomic.
/// </summary>
/// <remarks>
/// The worker acts as <c>job:operations</c>. A handler runs on behalf of the actor who asked for the job, through
/// the worker: <c>jane</c> through <c>job:tenant_provision</c>. A job whose handler is not registered on this host
/// is released for another: its claim expires.
/// </remarks>
public sealed class JobWorker(
    IServiceScopeFactory scopes,
    IOptions<OperationsOptions> options,
    TimeProvider clock,
    ILogger<JobWorker> logger) : BackgroundService
{
    /// <summary>The actor the worker claims and cleans under.</summary>
    public static Actor Actor { get; } = Actor.Job("operations");

    private readonly OperationsOptions _options = options.Value;
    private readonly SemaphoreSlim _slots = new(options.Value.MaxConcurrentJobs, options.Value.MaxConcurrentJobs);

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("The job worker starts: every {Interval}, up to {Max} jobs at once.", _options.PollingInterval, _options.MaxConcurrentJobs);
        var cleanedAt = clock.GetUtcNow();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken).ConfigureAwait(false);
                if (clock.GetUtcNow() - cleanedAt > TimeSpan.FromHours(1))
                {
                    await CleanAsync(stoppingToken).ConfigureAwait(false);
                    cleanedAt = clock.GetUtcNow();
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "The job worker failed a cycle; it goes on.");
            }
            try
            {
                await Task.Delay(_options.PollingInterval, clock, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
        logger.LogInformation("The job worker stops.");
    }

    /// <summary>
    /// One cycle: claims as many jobs as there are free slots, and runs each in its own scope without waiting for
    /// it. Public for a host that drives the worker itself, in tests for instance.
    /// </summary>
    public async Task RunOnceAsync(CancellationToken cancellationToken = default)
    {
        var free = _slots.CurrentCount;
        if (free == 0)
            return;

        IReadOnlyList<JobRecord> claimed;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IJobStore>();
            claimed = await store.ClaimAsync(free, _options.ClaimLease, Actor, cancellationToken).ConfigureAwait(false);
        }
        foreach (var job in claimed)
        {
            await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);
            _ = RunAsync(job, cancellationToken).ContinueWith(_ => _slots.Release(), TaskScheduler.Default);
        }
    }

    /// <summary>Runs every claimed job to its end before returning: the form of a test, or of a command-line tool.</summary>
    public async Task<int> RunUntilIdleAsync(CancellationToken cancellationToken = default)
    {
        var ran = 0;
        while (true)
        {
            IReadOnlyList<JobRecord> claimed;
            await using (var scope = scopes.CreateAsyncScope())
            {
                var store = scope.ServiceProvider.GetRequiredService<IJobStore>();
                claimed = await store.ClaimAsync(_options.MaxConcurrentJobs, _options.ClaimLease, Actor, cancellationToken).ConfigureAwait(false);
            }
            if (claimed.Count == 0)
                return ran;
            foreach (var job in claimed)
            {
                await RunAsync(job, cancellationToken).ConfigureAwait(false);
                ran++;
            }
        }
    }

    private async Task RunAsync(JobRecord job, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IJobStore>();
        var handler = scope.ServiceProvider.GetServices<IJobHandler>()
            .FirstOrDefault(candidate => string.Equals(candidate.JobType, job.JobType, StringComparison.Ordinal));
        if (handler is null)
        {
            // Another host may hold the handler: the claim expires, and that host claims the job.
            logger.LogWarning("No handler for the job type {JobType} on this host: the job {Job} waits for another.", job.JobType, job.JobUniqueId);
            return;
        }

        var actor = job.Actor.Through(Actor.Job(job.JobType));
        var context = new JobContext(job.JobUniqueId, job.JobType, job.Space, actor, job.Input ?? EmptyObject, new Progress(store, job.JobUniqueId, actor));
        try
        {
            await handler.ExecuteAsync(context, cancellationToken).ConfigureAwait(false);
            var result = context.Result is null ? (JsonElement?)null : JsonSerializer.SerializeToElement(context.Result, JobJson.Options);
            await store.CompleteAsync(job.JobUniqueId, JobStatus.Succeeded, result, actor, CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The host stops: the claim expires, and the job runs again on the next start.
            logger.LogWarning("The job {Job} is interrupted by the stop of the host; it runs again once its claim expires.", job.JobUniqueId);
        }
        catch (UserFacingException exception)
        {
            logger.LogWarning(exception, "The job {Job} ({JobType}) failed: {Code}.", job.JobUniqueId, job.JobType, exception.Error.Code);
            var failure = new JobFailure(exception.Error.Code, exception.Message, null);
            await store.CompleteAsync(job.JobUniqueId, JobStatus.Failed, failure.ToJson(), actor, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            var logId = Guid.NewGuid().ToString("N");
            logger.LogError(exception, "The job {Job} ({JobType}) failed with an exception not meant for the caller; log {LogId}.", job.JobUniqueId, job.JobType, logId);
            var failure = new JobFailure(JobErrors.Failed, null, logId);
            await store.CompleteAsync(job.JobUniqueId, JobStatus.Failed, failure.ToJson(), actor, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task CleanAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IJobStore>();
        var cleaned = await store.CleanExpiredAsync(Actor, cancellationToken).ConfigureAwait(false);
        if (cleaned > 0)
            logger.LogInformation("{Count} expired jobs cleaned.", cleaned);
    }

    private static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement.Clone();

    private sealed class Progress(IJobStore store, Guid jobUniqueId, Actor actor) : IJobProgress
    {
        public Task ReportAsync(int percent, string label, CancellationToken cancellationToken = default)
            => store.ReportProgressAsync(jobUniqueId, Math.Clamp(percent, 0, 100), Guard.NotNull(label), actor, cancellationToken);
    }
}
