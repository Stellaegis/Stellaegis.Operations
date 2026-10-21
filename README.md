# Stellaegis.Operations

Stellaegis.Operations is a .NET library that runs **jobs** outside requests: a provisioning that takes a minute, an
import that takes an hour, a nightly cleaning. A caller launches a job and gets its identifier back at once; a worker
claims it, runs the handler of its type, and records its outcome. A schedule launches a job of a type on a cron
expression. Jobs are partitioned by **compartment**, the party a job acts for, so one deployment serves many customers
without one of them ever seeing another's jobs; a job of the assembly that acts on compartments sits in the
**shared space**, its targets being part of its input.

Three packages are published:

| Package | Content |
| --- | --- |
| `Stellaegis.Operations` | `IJobService`, `IJobHandler`, the job and schedule model, the two workers, in-memory stores for development |
| `Stellaegis.Operations.PostgreSQL` | the stores on PostgreSQL, in the `ops` schema, with an atomic claim |
| `Stellaegis.Operations.AspNetCore` | the HTTP endpoints, mapped on the route groups your host provides |

The library targets .NET 10 and is released under the MIT license. It is extracted from the Stellaegis framework,
where it was the operations module and the scheduling module, merged here into one.

## What the library does, and what it leaves to you

| The library keeps | Your application keeps |
| --- | --- |
| the queue: each job, its space, who asked for it, its state, its progress, its outcome | the handlers: what a job of each type does |
| the claim: one worker runs a job, bounded concurrency, a lease that survives a crash | who may launch, read, cancel or oversee, through the policies of your host |
| the calendar: schedules, their occurrences, launched once whatever the number of hosts | who may see a job among the users of its space, through an authorization handler reading its visibility |
| the cleaning of completed jobs past their retention, the purge of a compartment | the input and the outcome of each job, opaque JSON to the library |

A job is identified by a GUID. Its type is an identifier such as `tenant_provision`, naming the handler that runs it.
Its input and its outcome are JSON the library never reads. A failure records a code: the code and the message of a
`UserFacingException` thrown by the handler, which are meant for the caller; for any other exception, only
`Job.Failed` and a log identifier, the message staying in the log.

## Compartments, the shared space, and the actor

Every job acts in a **space** — a `Compartment` or `Space.Shared`, the types of
[Stellaegis.Primitives](https://github.com/Stellaegis/Stellaegis.Primitives) — and for an **actor**, the one who
asked for it. A job of another space behaves exactly as an absent one. The handler receives both in its context and
passes them to everything it calls: nothing is ambient, and a job of the assembly that acts on several compartments
takes them from its input.

The handler runs on behalf of the actor who asked for the job, through the worker: `jane` through `job:tenant_provision`.
What the handler writes is attributed to the right person, and the mention says how. The scheduler launches its jobs
as `job:scheduler`; the worker claims and cleans as `job:operations`.

## Getting started in process

```sh
dotnet add package Stellaegis.Operations.PostgreSQL
```

Write a handler, declare your application's frame, and register the module with its store:

```csharp
public sealed class TenantProvisionJob(IProvisioner provisioner) : IJobHandler
{
    public string JobType => "tenant_provision";

    public async Task ExecuteAsync(JobContext job, CancellationToken ct)
    {
        await job.Progress.ReportAsync(10, "Creating the database", ct);
        await provisioner.ProvisionAsync(job.Space, job.Actor, ct);
        job.Result = new { database = "primary_acme" };
    }
}

builder.Services.AddStellaegisFrame(AppFrame.Value);   // ApplicationFrame.ManyCompartments().WithSharedSpace(), for instance
builder.Services.AddStellaegisOperationsPostgreSQL(builder.Configuration, handlers => handlers.AddFromAssemblyOf<Program>());
```

The registration declares the module to the frame — compartmented, serving the shared space, with its operations —,
binds its options, and registers the two workers as hosted services. A handler is resolved in a scope of its own for
each job it runs, so it takes scoped services as any request handler does.

The options live under `Stellaegis:Operations`, and the store's under `Stellaegis:Operations:PostgreSQL`; both are
validated at startup:

```json
{
  "Stellaegis": {
    "Operations": {
      "PollingInterval": "00:00:05",
      "MaxConcurrentJobs": 4,
      "ClaimLease": "00:10:00",
      "JobRetention": "7.00:00:00",
      "SchedulePollingInterval": "00:00:30",
      "PostgreSQL": { "ConnectionString": "Host=db;Database=primary;Username=myapp_app;Password=…" }
    }
  }
}
```

**Installing the database.** The PostgreSQL store declares its database, `OperationsDatabase.Value`: the `ops` schema
and its scripts, embedded in the package. An installer applies it under a role that may create schemas, naming the
role the application connects under, and the application verifies it at startup under its own role:

```csharp
await DatabaseLifecycle.InstallAsync([OperationsDatabase.Value], AppFrame.Value,
    (_, ct) => adminSource.OpenConnectionAsync(ct).AsTask(), applicationRole: "myapp_app", cancellationToken: ct);

builder.Services.AddStellaegisDatabaseVerification();            // a generic host
await app.Services.VerifyStellaegisDatabasesAsync();             // an ASP.NET Core host, before app.Run()
```

**Launching and following a job.**

```csharp
var job = await jobs.LaunchAsync(new JobRequest
{
    JobType = "tenant_provision",
    Space = Compartment.Parse("acme"),
    Actor = actor,
    Input = JsonSerializer.SerializeToElement(new { plan = "pro" }),
    Visibility = JobVisibility.Space,
    IdempotencyKey = "provision:acme",
}, ct);

var state = await jobs.GetAsync(job.Space, job.JobUniqueId, actor, ct);   // Pending, Running, Succeeded, Failed, Cancelled
```

A launch replayed with the same idempotency key, in the same space, answers the job of the first launch and creates
nothing. A pending job is cancelled with `CancelAsync`; a running one runs to its end. The visibility is a fact about
the job the library records and never reads: your authorization handler does.

## Schedules

A schedule launches a job of a type in a space on a cron expression — five fields, or six with seconds first —,
evaluated in a time zone. Its concurrency policy says what happens when an occurrence is due while a job of a previous
one is still pending or running: `Forbid` skips it, `Allow` launches it anyway, `Replace` cancels the pending jobs
first. The evaluation resumes where it stopped, which the store keeps: what came due while every host was down is
launched at the next start, a few occurrences per evaluation. Several hosts share the store safely, each occurrence
being claimed once.

```csharp
await schedules.UpsertAsync(new Schedule
{
    ScheduleUniqueId = Guid.NewGuid(),
    Name = "Nightly cleaning",
    CronExpression = "0 2 * * *",
    TimeZoneId = "Europe/Paris",
    JobType = "cleaning",
    Space = Space.Shared,
    ConcurrencyPolicy = ConcurrencyPolicy.Forbid,
    CreatedBy = actor,
    CreatedAt = DateTimeOffset.UtcNow,
}, actor, ct);
```

## Exposing the jobs over HTTP

```sh
dotnet add package Stellaegis.Operations.AspNetCore
```

```csharp
builder.Services.AddStellaegisRequestContext(c => c.CompartmentFromRoute("compartment"));
builder.Services.AddAuthorization(options => { /* one policy per operation */ });
builder.Services.AddSingleton<IAuthorizationHandler, JobVisibilityRule>();

app.MapGroup("/api/t/{compartment}").MapOperationsEndpoints().RequireAuthorizationPerOperation(op => op.ToString());
app.MapGroup("/api/shared").MapOperationsEndpoints().InSharedSpace().RequireAuthorization("assembly");
app.MapGroup("/api/admin").MapOperationsAdminEndpoints().RequireAuthorization("operator");
```

`MapOperationsEndpoints` maps the routes of a space under `/jobs`: `POST` launches (202, with the `Location` of the
job), `GET` lists, `GET /{id}` reads, `POST /{id}/cancel` cancels. Each route is labelled with an operation —
`jobs.launch`, `jobs.list`, `jobs.read`, `jobs.cancel` — that your host attaches its policy to; every job loaded is
asked to your authorization handler, an `AuthorizationHandler<OperationRequirement, JobRecord>` reading its
`Visibility`, and a refusal answers 404. `MapOperationsAdminEndpoints` acts on spaces: `GET /jobs` lists the jobs of
every space (`?compartment=`, `_shared` for the shared space), `DELETE /compartments/{compartment}/jobs` purges one,
and `/schedules` is a CRUD — `jobs.oversee`, `jobs.purge`, `schedules.read`, `schedules.write`, `schedules.delete`.
The routes carry no name, so a host maps the module once per group it serves.

Errors are answered as problem details with the code of the error: `JobType.Unknown`, `Job.NotPending` (409),
`Schedule.Invalid`.

## Running the tests

```sh
dotnet test
```

The in-memory tests run everywhere. The PostgreSQL tests need a server and the administration connection string in
`STELLAEGIS_TEST_POSTGRESQL`, such as `Host=127.0.0.1;Username=postgres;Database=postgres`: each test class creates a
database of its own, installs the module in it, and drops it. Without the variable, they are skipped.
