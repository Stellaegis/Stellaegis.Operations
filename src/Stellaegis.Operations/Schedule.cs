using System.Text.Json;
using Cronos;
using Stellaegis.Primitives;

namespace Stellaegis.Operations;

/// <summary>
/// A recurring launch: a cron expression, evaluated in a time zone, which launches a job of a type in a space at
/// each occurrence. The schedule owns the calendar; the job owns the run.
/// </summary>
public sealed record Schedule
{
    /// <summary>The public identifier of the schedule.</summary>
    public required Guid ScheduleUniqueId { get; init; }

    /// <summary>A name for the administration, 1 to 200 characters.</summary>
    public required string Name { get; init; }

    /// <summary>The cron expression, five fields, or six with seconds first.</summary>
    public required string CronExpression { get; init; }

    /// <summary>The time zone the expression is evaluated in, an IANA or Windows identifier; <c>UTC</c> by default.</summary>
    public string TimeZoneId { get; init; } = "UTC";

    /// <summary>The type of job launched at each occurrence.</summary>
    public required string JobType { get; init; }

    /// <summary>Where the jobs act: a compartment, or the shared space for a job of the assembly.</summary>
    public required Space Space { get; init; }

    /// <summary>The input handed to each job.</summary>
    public JsonElement? Input { get; init; }

    /// <summary>What to do when an occurrence is due while a job of a previous one is still in flight.</summary>
    public ConcurrencyPolicy ConcurrencyPolicy { get; init; } = ConcurrencyPolicy.Forbid;

    /// <summary>Whether the worker evaluates it.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Who created or last changed it.</summary>
    public required Actor CreatedBy { get; init; }

    /// <summary>When it was created.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Up to when its occurrences were evaluated; a restart resumes there, and fires what came due meanwhile.</summary>
    public DateTimeOffset? LastEvaluatedAt { get; init; }

    /// <summary>The first setting that keeps the schedule from working, or <see langword="null"/>.</summary>
    public string? FindError()
    {
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 200)
            return "Name is required, 1 to 200 characters.";
        if (!NameRules.IsIdentifier(JobType))
            return $"'{JobType}' is not a valid job type: {NameRules.IdentifierRule}.";
        if (CronSchedule.TryParse(CronExpression, out _) is false)
            return $"'{CronExpression}' is not a cron expression of five fields, or six with seconds.";
        if (CronSchedule.TryFindTimeZone(TimeZoneId, out _) is false)
            return $"'{TimeZoneId}' is not a known time zone.";
        return null;
    }
}

/// <summary>The cron arithmetic of the module, over Cronos: nobody writes a cron parser by hand.</summary>
public static class CronSchedule
{
    /// <summary>Parses a cron expression of five fields, or six with seconds first.</summary>
    public static bool TryParse(string? expression, out CronExpression? parsed)
    {
        parsed = null;
        if (string.IsNullOrWhiteSpace(expression))
            return false;
        var format = expression.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).Length == 6 ? CronFormat.IncludeSeconds : CronFormat.Standard;
        try
        {
            parsed = CronExpression.Parse(expression, format);
            return true;
        }
        catch (CronFormatException)
        {
            return false;
        }
    }

    /// <summary>Finds a time zone by its IANA or Windows identifier.</summary>
    public static bool TryFindTimeZone(string? id, out TimeZoneInfo? timeZone)
    {
        timeZone = null;
        if (string.IsNullOrWhiteSpace(id))
            return false;
        return TimeZoneInfo.TryFindSystemTimeZoneById(id, out timeZone);
    }

    /// <summary>
    /// The occurrences of <paramref name="schedule"/> strictly after <paramref name="from"/> and up to
    /// <paramref name="until"/> included, at most <paramref name="max"/>, in order.
    /// </summary>
    /// <exception cref="InvalidScheduleException">The schedule does not hold.</exception>
    public static IReadOnlyList<DateTimeOffset> OccurrencesBetween(Schedule schedule, DateTimeOffset from, DateTimeOffset until, int max)
    {
        Guard.NotNull(schedule);
        if (schedule.FindError() is { } error)
            throw new InvalidScheduleException(error);
        TryParse(schedule.CronExpression, out var cron);
        TryFindTimeZone(schedule.TimeZoneId, out var timeZone);

        var occurrences = new List<DateTimeOffset>();
        var next = cron!.GetNextOccurrence(from, timeZone!, inclusive: false);
        while (next is { } occurrence && occurrence <= until && occurrences.Count < max)
        {
            occurrences.Add(occurrence);
            next = cron.GetNextOccurrence(occurrence, timeZone!, inclusive: false);
        }
        return occurrences;
    }
}
