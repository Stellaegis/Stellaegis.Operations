-- Stellaegis.Operations — PostgreSQL, schema ops.
--
-- Applied by DatabaseLifecycle.InstallAsync under the administration role, which creates the schema and the
-- migrations table before it; the application role is granted the data rights after it.
--
-- No row-level security on these tables: the queue is served by the module itself, which filters every read
-- and write by the space of the call, and the workers claim across spaces under a named job actor — the same
-- exception the outbox of Stellaegis.Messaging holds. A job of the assembly acting on spaces sits in the shared
-- space (compartment null), its targets being part of its input.

create table ops.jobs (
    job_unique_id      uuid        not null,
    job_type           text        not null,
    status             text        not null,
    visibility         text        not null,
    compartment        text        null,
    created_by         text        not null,
    via                text        null,
    input              jsonb       null,
    result             jsonb       null,
    progress           smallint    null,
    progress_label     text        not null default '',
    created_at         timestamptz not null default now(),
    started_at         timestamptz null,
    completed_at       timestamptz null,
    expires_at         timestamptz not null,
    claimed_until      timestamptz null,
    claimed_by         text        null,
    idempotency_key    text        not null default '',
    schedule_unique_id uuid        null,
    scheduled_for      timestamptz null,
    constraint pk_ops_jobs primary key (job_unique_id),
    constraint ck_ops_jobs_type check (job_type ~ '^[a-z](_?[a-z0-9])*$' and length(job_type) <= 63),
    constraint ck_ops_jobs_status check (status in ('pending', 'running', 'succeeded', 'failed', 'cancelled')),
    constraint ck_ops_jobs_visibility check (visibility in ('initiator_only', 'space')),
    constraint ck_ops_jobs_progress check (progress is null or progress between 0 and 100)
);

-- The claim: the pending jobs and the running ones whose lease expired, oldest first.
create index ix_ops_jobs_claim on ops.jobs (created_at) where status in ('pending', 'running');

-- The lists of a space, the most recent first; the shared space is compartment null.
create index ix_ops_jobs_space on ops.jobs (compartment, created_at desc);

-- A replayed launch creates nothing twice within its space.
create unique index ux_ops_jobs_idempotency on ops.jobs (coalesce(compartment, '_shared'), idempotency_key) where idempotency_key <> '';

-- The jobs of a schedule, for its concurrency policy.
create index ix_ops_jobs_schedule on ops.jobs (schedule_unique_id) where schedule_unique_id is not null;

-- The cleaning of the completed jobs past their expiry.
create index ix_ops_jobs_expiry on ops.jobs (expires_at) where status in ('succeeded', 'failed', 'cancelled');

create table ops.schedules (
    schedule_unique_id uuid        not null,
    name               text        not null,
    cron_expression    text        not null,
    time_zone_id       text        not null default 'UTC',
    job_type           text        not null,
    compartment        text        null,
    input              jsonb       null,
    concurrency_policy text        not null default 'forbid',
    enabled            boolean     not null default true,
    created_by         text        not null,
    created_at         timestamptz not null default now(),
    last_evaluated_at  timestamptz null,
    constraint pk_ops_schedules primary key (schedule_unique_id),
    constraint ck_ops_schedules_name check (length(name) between 1 and 200),
    constraint ck_ops_schedules_type check (job_type ~ '^[a-z](_?[a-z0-9])*$' and length(job_type) <= 63),
    constraint ck_ops_schedules_policy check (concurrency_policy in ('forbid', 'allow', 'replace'))
);

-- The occurrences claimed: one row per occurrence, whichever worker claimed it first.
create table ops.schedule_runs (
    schedule_unique_id uuid        not null,
    scheduled_for      timestamptz not null,
    claimed_at         timestamptz not null default now(),
    claimed_by         text        not null,
    constraint pk_ops_schedule_runs primary key (schedule_unique_id, scheduled_for),
    constraint fk_ops_schedule_runs_schedule foreign key (schedule_unique_id) references ops.schedules (schedule_unique_id) on delete cascade
);
