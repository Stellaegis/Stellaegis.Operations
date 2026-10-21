using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stellaegis.Primitives;

namespace Stellaegis.Operations;

/// <summary>
/// Turns the due occurrences of the schedules into pending jobs. It never runs a job itself: it owns the calendar,
/// the job worker owns the run. Several hosts share the store safely, an occurrence being claimed atomically.
/// </summary>
/// <remarks>
/// The evaluation resumes where the last one stopped, which the store keeps per schedule: what came due while
/// the host was down is launched at the next start, up to <see cref="OperationsOptions.MaxOccurrencesPerEvaluation"/>
/// per evaluation. The schedule worker acts as <c>job:scheduler</c>, and so do the jobs it launches.
/// </remarks>
public sealed class ScheduleWorker(
    IServiceScopeFactory scopes,
    IOptions<OperationsOptions> options,
    TimeProvider clock,
    ILogger<ScheduleWorker> logger) : BackgroundService
{
    /// <summary>The actor the schedule worker launches jobs under.</summary>
    public static Actor Actor { get; } = Actor.Job("scheduler");

    private readonly OperationsOptions _options = options.Value;

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("The schedule worker starts: every {Interval}.", _options.SchedulePollingInterval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await EvaluateOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "The schedule worker failed an evaluation; it goes on.");
            }
            try
            {
                await Task.Delay(_options.SchedulePollingInterval, clock, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
        logger.LogInformation("The schedule worker stops.");
    }

    /// <summary>
    /// One evaluation of every enabled schedule, as of now: launches the occurrences due since the last
    /// evaluation. Answers how many jobs were launched. Public for a host that drives the worker itself.
    /// </summary>
    public async Task<int> EvaluateOnceAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var schedules = scope.ServiceProvider.GetRequiredService<IScheduleStore>();
        var jobs = scope.ServiceProvider.GetRequiredService<IJobStore>();
        var now = clock.GetUtcNow();
        var launched = 0;

        foreach (var schedule in await schedules.ListEnabledAsync(Actor, cancellationToken).ConfigureAwait(false))
        {
            IReadOnlyList<DateTimeOffset> due;
            try
            {
                due = CronSchedule.OccurrencesBetween(schedule, schedule.LastEvaluatedAt ?? now, now, _options.MaxOccurrencesPerEvaluation);
            }
            catch (InvalidScheduleException exception)
            {
                logger.LogWarning("The schedule {Schedule} ({Name}) does not hold and is skipped: {Error}", schedule.ScheduleUniqueId, schedule.Name, exception.Message);
                continue;
            }

            foreach (var occurrence in due)
            {
                if (!await schedules.TryClaimOccurrenceAsync(schedule.ScheduleUniqueId, occurrence, Actor, cancellationToken).ConfigureAwait(false))
                    continue;
                if (!await AdmitAsync(schedule, jobs, cancellationToken).ConfigureAwait(false))
                    continue;
                await jobs.CreateAsync(new JobRequest
                {
                    JobType = schedule.JobType,
                    Space = schedule.Space,
                    Actor = Actor,
                    Input = schedule.Input,
                    Visibility = JobVisibility.Space,
                    ScheduleUniqueId = schedule.ScheduleUniqueId,
                    ScheduledFor = occurrence,
                    IdempotencyKey = $"schedule:{schedule.ScheduleUniqueId:N}:{occurrence.UtcDateTime:O}",
                }, now + _options.JobRetention, cancellationToken).ConfigureAwait(false);
                launched++;
                logger.LogInformation("The schedule {Schedule} ({Name}) launched a job of type {JobType} for its occurrence {Occurrence:o}.",
                    schedule.ScheduleUniqueId, schedule.Name, schedule.JobType, occurrence);
            }

            // Evaluated up to the last occurrence launched, or up to now when nothing was due: a cycle that could
            // not launch every due occurrence resumes at the last one it launched.
            var through = due.Count == _options.MaxOccurrencesPerEvaluation ? due[^1] : now;
            await schedules.MarkEvaluatedAsync(schedule.ScheduleUniqueId, through, Actor, cancellationToken).ConfigureAwait(false);
        }
        return launched;
    }

    private async Task<bool> AdmitAsync(Schedule schedule, IJobStore jobs, CancellationToken cancellationToken)
    {
        if (schedule.ConcurrencyPolicy == ConcurrencyPolicy.Allow)
            return true;
        var inFlight = await jobs.ListAsync(new JobQuery { ScheduleUniqueId = schedule.ScheduleUniqueId, MaxResults = 1000 }, Actor, cancellationToken).ConfigureAwait(false);
        var pending = inFlight.Where(job => job.Status == JobStatus.Pending).ToList();
        var running = inFlight.Count(job => job.Status == JobStatus.Running);
        if (pending.Count == 0 && running == 0)
            return true;
        if (schedule.ConcurrencyPolicy == ConcurrencyPolicy.Forbid)
        {
            logger.LogInformation("The schedule {Schedule} ({Name}) skips an occurrence: a job of a previous one is still in flight.", schedule.ScheduleUniqueId, schedule.Name);
            return false;
        }
        foreach (var job in pending)
            await jobs.CancelAsync(job.Space, job.JobUniqueId, Actor, cancellationToken).ConfigureAwait(false);
        return true;
    }
}
