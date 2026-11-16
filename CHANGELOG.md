# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project
adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [5.0.3] — 2026-11-16

On `Stellaegis.Primitives` 6.0.0, whose `Stellaegis.Primitives.Data` no longer references Dapper: an application
that takes the PostgreSQL adapter of the jobs no longer receives Dapper through it. What the packages do is
unchanged.

### Changed

- **`Stellaegis.Primitives` 6.0.0 and Microsoft.Extensions 10.0.15.** Nothing changes in this module's code. The
  adapter stopped using Dapper in 5.0.2; until this version it still came with `Stellaegis.Primitives.Data` 5.x,
  which the adapter references.

### Updating from 5.0.2

Take the version: a host has nothing to change in what it asks of this module. The version brings Primitives 6.0.0
to the host, and two things follow from that, both described in the CHANGELOG of Primitives, § *Updating from 5.6.0*:

1. **The other packages of the family the host references take a version that runs without what
   `Stellaegis.Primitives.Data` 6.0.0 removed.** That section lists the least version of each. An older PostgreSQL
   or SQL Server adapter restores on Primitives 6 and fails at its first query.
2. **Code of the host that used Dapper without referencing it now references it itself**: Dapper came through
   the adapter, and no longer does.

## [5.0.2] — 2026-11-16

The stores talk to PostgreSQL through Npgsql, and the adapter no longer references Dapper. What the packages do is
unchanged, and so is their SQL.

### Changed

- **`Stellaegis.Operations.PostgreSQL` no longer references Dapper.** Its repositories run `NpgsqlCommand`, each
  parameter under its declared type, each row read by the names of its columns. Dapper still comes with
  `Stellaegis.Primitives.Data` 5.x, which the adapter references.

### Updating from 5.0.1

Take the version: a host has nothing to change.

## [5.0.1] — 2026-11-09

On `Stellaegis.Primitives` 5.6.0 and Microsoft.Extensions 10.0.14, so that a host taking this module takes the
version of Primitives that closes what a badly assembled host let through. Nothing changes in the module's code.

### Changed

- **`Stellaegis.Primitives` 5.6.0 and Microsoft.Extensions 10.0.14.**

### Updating from 5.0.0

Take the version. A host meets what the startup of Primitives 5.6.0 now refuses, listed with its way out in the
CHANGELOG of Primitives, § *Updating from 5.5.0* — for the jobs, mostly this: on PostgreSQL, the application connects
with its own role, never a superuser, a `BYPASSRLS` role nor the owner of the schema of the jobs. A job still runs
on behalf of its actor through `job:…`: only a token may not name one.

## [5.0.0] — 2026-10-21

The first release, extracted from the Stellaegis framework, where the operations module and the scheduling module
were two skeletons: they are one module here, on Stellaegis.Primitives 5.0.0, at the version of the 5.0 line of the
family.

### What the extraction changes

- The vocabulary is **jobs**: `Job`, `JobRecord`, `IJobStore`, `IJobHandler`, `JobStatus`, `JobVisibility`. The word
  *operation* is the one of Stellaegis.Primitives, what a route does; the routes of this module declare nine of them.
- A job carries a `Space` — a compartment, or the shared space for a job of the assembly — and an `Actor`, the one
  who asked for it. The handler receives both in a `JobContext`; nothing is ambient, and the scope initializers of
  the framework are gone. The handler runs on behalf of the requester through a named job actor,
  `jane` through `job:tenant_provision`.
- The worker claims atomically, under a lease: one statement with `for update skip locked` on PostgreSQL, a lock in
  memory. A job whose claim expired is claimed again, which is how it survives the crash of the host running it.
  `MaxConcurrentJobs` bounds what one host runs at once.
- A failure records the code and the message of a `UserFacingException`, and for any other exception only
  `Job.Failed` and a log identifier: the message stays in the log.
- A launch carries an idempotency key, unique within its space: a replayed launch creates nothing twice.
- The schedules are in the core, with Cronos: a concurrency policy per schedule, an evaluation mark persisted per
  schedule for the catch-up after a stop, an occurrence claimed once across hosts. The scheduler launches as
  `job:scheduler`; the configurable `EnqueuedBy` of the framework is gone.
- The database is declared by `OperationsDatabase.Value`, the `ops` schema and its embedded `0001_initial.sql`,
  installed by `DatabaseLifecycle.InstallAsync` and verified at startup. The tables are `ops.jobs`, `ops.schedules`
  and `ops.schedule_runs`; the `sched` schema and the audit log of the framework are gone. No row-level security on
  the queue: the module serves it itself, filtering by space, the workers acting across spaces under their actor.
- The routes follow Primitives 5.0.0: `BelongsTo<OperationsModule>()`, `RequireRequestContext()`, one `Operation`
  per route, unnamed routes, the `Location` built from the request; the administration acts on spaces. The actor
  comes from the request context, never from a GUID claim.
- The options are `Stellaegis:Operations` and `Stellaegis:Operations:PostgreSQL`, validated at startup.
