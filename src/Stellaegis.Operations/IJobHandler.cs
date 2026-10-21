using System.Text.Json;
using Stellaegis.Primitives;

namespace Stellaegis.Operations;

/// <summary>
/// What runs a job of one type. The application registers its handlers with the module; the worker resolves the
/// one whose <see cref="JobType"/> matches, in a scope of its own, and hands it the context of the job: where it
/// acts, on whose behalf, with what input. Nothing ambient: a handler that needs the space or the actor takes
/// them from the context and passes them on.
/// <code>
/// public sealed class TenantProvisionJob(IProvisioner provisioner) : IJobHandler
/// {
///     public string JobType => "tenant_provision";
///
///     public async Task ExecuteAsync(JobContext job, CancellationToken ct)
///     {
///         await job.Progress.ReportAsync(10, "Creating the database", ct);
///         await provisioner.ProvisionAsync(job.Space, job.Actor, ct);
///         job.Result = new { database = "primary_acme" };
///     }
/// }
/// </code>
/// </summary>
/// <remarks>
/// A handler that returns succeeds, with <see cref="JobContext.Result"/> as the outcome. One that throws fails:
/// a <see cref="UserFacingException"/> is part of the contract and its code and message are recorded; any other
/// exception is a failure whose message stays in the log, the outcome carrying only the code
/// <see cref="JobErrors.Failed"/> and an identifier to find it by.
/// </remarks>
public interface IJobHandler
{
    /// <summary>The type of job the handler runs, an identifier such as <c>tenant_provision</c>.</summary>
    string JobType { get; }

    /// <summary>Runs the job.</summary>
    Task ExecuteAsync(JobContext job, CancellationToken cancellationToken);
}

/// <summary>What a handler reports while it runs.</summary>
public interface IJobProgress
{
    /// <summary>Reports a progress, 0 to 100, with a label.</summary>
    Task ReportAsync(int percent, string label, CancellationToken cancellationToken = default);
}

/// <summary>
/// The context of a running job: its identity, where it acts, on whose behalf, and its input. The actor is the
/// one who asked for the job, acting through the worker — <c>jane</c> through <c>job:tenant_provision</c> —, so
/// that what the handler writes is attributed to the right person, and the mention says how.
/// </summary>
public sealed class JobContext
{
    internal JobContext(Guid jobUniqueId, string jobType, Space space, Actor actor, JsonElement input, IJobProgress progress)
    {
        JobUniqueId = jobUniqueId;
        JobType = jobType;
        Space = space;
        Actor = actor;
        Input = input;
        Progress = progress;
    }

    /// <summary>The identifier of the job.</summary>
    public Guid JobUniqueId { get; }

    /// <summary>The type of the job.</summary>
    public string JobType { get; }

    /// <summary>Where the job acts: a compartment, or the shared space for a job of the assembly.</summary>
    public Space Space { get; }

    /// <summary>Who asked for the job, acting through the worker: pass it to every call that takes an actor.</summary>
    public Actor Actor { get; }

    /// <summary>The input of the job, an empty object when none was given.</summary>
    public JsonElement Input { get; }

    /// <summary>Where the handler reports its progress.</summary>
    public IJobProgress Progress { get; }

    /// <summary>What the handler produced, recorded as the outcome when it returns. Serialized as JSON.</summary>
    public object? Result { get; set; }
}
