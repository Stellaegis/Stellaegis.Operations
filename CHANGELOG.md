# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project
adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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
