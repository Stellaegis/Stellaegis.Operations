using Stellaegis.Primitives;

namespace Stellaegis.Operations;

/// <summary>
/// The identity of the module: <c>operations</c>. Its routes belong to it, and the application declares it once,
/// which <c>AddStellaegisOperationsInMemory</c> and <c>AddStellaegisOperationsPostgreSQL</c> do.
/// </summary>
public sealed class OperationsModule : IStellaegisModule
{
    /// <inheritdoc/>
    public static string Name => "operations";
}

/// <summary>
/// The operations of the module's routes, declared on the module and granted by the application. The word
/// <c>operation</c> is the one of Stellaegis.Primitives — what a route does; what this module runs is a
/// <b>job</b>.
/// </summary>
public static class JobOperations
{
    /// <summary><c>jobs.launch</c>: launch a job in the space of the request.</summary>
    public static readonly Operation Launch = new("jobs", "launch", OperationKind.Write);

    /// <summary><c>jobs.read</c>: read a job of the space of the request.</summary>
    public static readonly Operation Read = new("jobs", "read", OperationKind.Read);

    /// <summary><c>jobs.list</c>: list the jobs of the space of the request.</summary>
    public static readonly Operation List = new("jobs", "list", OperationKind.Read);

    /// <summary><c>jobs.cancel</c>: cancel a pending job of the space of the request.</summary>
    public static readonly Operation Cancel = new("jobs", "cancel", OperationKind.Write);

    /// <summary><c>jobs.oversee</c>: read or list the jobs of any space, on the administration routes.</summary>
    public static readonly Operation Oversee = new("jobs", "oversee", OperationKind.Read);

    /// <summary><c>jobs.purge</c>: delete every job and schedule of a compartment, on the administration routes.</summary>
    public static readonly Operation Purge = new("jobs", "purge", OperationKind.Delete);

    /// <summary><c>schedules.read</c>: read or list the schedules, on the administration routes.</summary>
    public static readonly Operation ReadSchedules = new("schedules", "read", OperationKind.Read);

    /// <summary><c>schedules.write</c>: create or change a schedule, on the administration routes.</summary>
    public static readonly Operation WriteSchedules = new("schedules", "write", OperationKind.Write);

    /// <summary><c>schedules.delete</c>: remove a schedule, on the administration routes.</summary>
    public static readonly Operation DeleteSchedules = new("schedules", "delete", OperationKind.Delete);

    /// <summary>Every operation of the module, as its declaration lists them.</summary>
    public static IReadOnlyList<Operation> All { get; } =
        [Launch, Read, List, Cancel, Oversee, Purge, ReadSchedules, WriteSchedules, DeleteSchedules];
}
