CREATE TABLE memory_facts (
    id TEXT NOT NULL PRIMARY KEY,
    owner_id TEXT NOT NULL DEFAULT 'owner',
    subject TEXT NOT NULL,
    fact_key TEXT NOT NULL,
    value TEXT NOT NULL,
    source_id TEXT NOT NULL,
    privacy_class TEXT NOT NULL,
    validity_status TEXT NOT NULL DEFAULT 'Active' CHECK (validity_status IN ('Active', 'Superseded')),
    supersedes_id TEXT REFERENCES memory_facts(id) ON DELETE SET NULL,
    created_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL,
    version INTEGER NOT NULL CHECK (version > 0)
);

CREATE INDEX ix_memory_facts_active_updated
    ON memory_facts(validity_status, updated_at_utc);

CREATE VIRTUAL TABLE memory_facts_fts USING fts5(
    subject,
    fact_key,
    value,
    content='memory_facts',
    content_rowid='rowid'
);

CREATE TRIGGER memory_facts_fts_insert AFTER INSERT ON memory_facts BEGIN
    INSERT INTO memory_facts_fts(rowid, subject, fact_key, value)
    VALUES (new.rowid, new.subject, new.fact_key, new.value);
END;

CREATE TRIGGER memory_facts_fts_delete AFTER DELETE ON memory_facts BEGIN
    INSERT INTO memory_facts_fts(memory_facts_fts, rowid, subject, fact_key, value)
    VALUES ('delete', old.rowid, old.subject, old.fact_key, old.value);
END;

CREATE TRIGGER memory_facts_fts_update AFTER UPDATE OF subject, fact_key, value ON memory_facts BEGIN
    INSERT INTO memory_facts_fts(memory_facts_fts, rowid, subject, fact_key, value)
    VALUES ('delete', old.rowid, old.subject, old.fact_key, old.value);
    INSERT INTO memory_facts_fts(rowid, subject, fact_key, value)
    VALUES (new.rowid, new.subject, new.fact_key, new.value);
END;

CREATE TABLE memory_proposals (
    id TEXT NOT NULL PRIMARY KEY,
    owner_id TEXT NOT NULL,
    source_id TEXT,
    subject TEXT NOT NULL,
    fact_key TEXT NOT NULL,
    proposed_value TEXT NOT NULL,
    status TEXT NOT NULL CHECK (status IN ('Pending', 'Accepted', 'Rejected', 'Superseded')),
    created_at_utc TEXT NOT NULL,
    resolved_at_utc TEXT,
    version INTEGER NOT NULL DEFAULT 1 CHECK (version > 0)
);

CREATE TABLE actions (
    id TEXT NOT NULL PRIMARY KEY,
    turn_id TEXT REFERENCES turns(id) ON DELETE SET NULL,
    action_type TEXT NOT NULL,
    canonical_arguments TEXT NOT NULL,
    request_hash TEXT NOT NULL,
    status TEXT NOT NULL CHECK (
        status IN ('Prepared', 'AwaitingApproval', 'Executing', 'Succeeded', 'Failed', 'Unknown', 'Rejected', 'Expired')
    ),
    created_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL,
    version INTEGER NOT NULL CHECK (version > 0)
);

CREATE INDEX ix_actions_status_updated ON actions(status, updated_at_utc);

CREATE TABLE approval_requests (
    id TEXT NOT NULL PRIMARY KEY,
    action_id TEXT NOT NULL REFERENCES actions(id) ON DELETE RESTRICT,
    owner_id TEXT NOT NULL,
    status TEXT NOT NULL CHECK (status IN ('Pending', 'Approved', 'Rejected', 'Expired')),
    created_at_utc TEXT NOT NULL,
    expires_at_utc TEXT NOT NULL,
    resolved_at_utc TEXT,
    version INTEGER NOT NULL CHECK (version > 0),
    CHECK ((status = 'Pending' AND resolved_at_utc IS NULL) OR
           (status <> 'Pending' AND resolved_at_utc IS NOT NULL))
);

CREATE INDEX ix_approval_requests_owner_status_expiry
    ON approval_requests(owner_id, status, expires_at_utc);

CREATE TABLE jobs (
    id TEXT NOT NULL PRIMARY KEY,
    owner_id TEXT NOT NULL,
    kind TEXT NOT NULL,
    payload_version INTEGER NOT NULL CHECK (payload_version > 0),
    payload TEXT NOT NULL,
    time_zone_id TEXT NOT NULL,
    due_at_utc TEXT NOT NULL,
    recurrence_json TEXT,
    enabled INTEGER NOT NULL CHECK (enabled IN (0, 1)),
    misfire_policy TEXT NOT NULL,
    lease_owner TEXT,
    lease_expires_at_utc TEXT,
    attempt_count INTEGER NOT NULL DEFAULT 0 CHECK (attempt_count >= 0),
    outcome TEXT,
    version INTEGER NOT NULL DEFAULT 1 CHECK (version > 0),
    CHECK ((lease_owner IS NULL AND lease_expires_at_utc IS NULL) OR
           (lease_owner IS NOT NULL AND lease_expires_at_utc IS NOT NULL))
);

CREATE INDEX ix_jobs_due_lease ON jobs(enabled, due_at_utc, lease_expires_at_utc);

CREATE TABLE job_runs (
    id TEXT NOT NULL PRIMARY KEY,
    job_id TEXT NOT NULL REFERENCES jobs(id) ON DELETE CASCADE,
    scheduled_occurrence_utc TEXT NOT NULL,
    status TEXT NOT NULL,
    started_at_utc TEXT,
    completed_at_utc TEXT,
    notification_id TEXT,
    error_code TEXT,
    UNIQUE (job_id, scheduled_occurrence_utc)
);

CREATE TABLE notifications (
    id TEXT NOT NULL PRIMARY KEY,
    job_run_id TEXT REFERENCES job_runs(id) ON DELETE SET NULL,
    kind TEXT NOT NULL,
    content_json TEXT NOT NULL,
    created_at_utc TEXT NOT NULL,
    read_at_utc TEXT
);

CREATE TABLE cloud_consents (
    id TEXT NOT NULL PRIMARY KEY,
    owner_id TEXT NOT NULL,
    packet_id TEXT NOT NULL,
    packet_hash TEXT NOT NULL,
    policy_version TEXT NOT NULL,
    provider TEXT NOT NULL,
    purpose TEXT NOT NULL,
    created_at_utc TEXT NOT NULL,
    expires_at_utc TEXT NOT NULL,
    revoked_at_utc TEXT,
    version INTEGER NOT NULL DEFAULT 1 CHECK (version > 0)
);

CREATE TABLE audit_events (
    id TEXT NOT NULL PRIMARY KEY,
    event_type TEXT NOT NULL,
    subject_id TEXT,
    payload_json TEXT NOT NULL,
    occurred_at_utc TEXT NOT NULL
);

CREATE INDEX ix_audit_events_occurred
    ON audit_events(occurred_at_utc, id);
