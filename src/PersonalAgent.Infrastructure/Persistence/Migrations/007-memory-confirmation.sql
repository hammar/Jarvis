ALTER TABLE memory_facts ADD COLUMN source_type TEXT NOT NULL DEFAULT 'Legacy';
ALTER TABLE memory_facts ADD COLUMN source_message_id TEXT REFERENCES messages(message_id) ON DELETE SET NULL;
ALTER TABLE memory_facts ADD COLUMN distinguishing_context TEXT;

CREATE TABLE memory_commits (
    action_id TEXT NOT NULL PRIMARY KEY REFERENCES actions(id) ON DELETE RESTRICT,
    owner_id TEXT NOT NULL,
    fact_id TEXT NOT NULL,
    fact_version INTEGER NOT NULL,
    deleted INTEGER NOT NULL CHECK (deleted IN (0, 1)),
    committed_at_utc TEXT NOT NULL
);

CREATE TABLE memory_tombstones (
    fact_id TEXT NOT NULL PRIMARY KEY,
    owner_id TEXT NOT NULL,
    version INTEGER NOT NULL,
    deleted_at_utc TEXT NOT NULL
);

ALTER TABLE actions ADD COLUMN memory_invalidated INTEGER NOT NULL DEFAULT 0;
ALTER TABLE actions ADD COLUMN memory_owner_id TEXT;
CREATE TABLE memory_action_dependencies (
    action_id TEXT NOT NULL REFERENCES actions(id) ON DELETE CASCADE,
    fact_id TEXT NOT NULL,
    fact_version INTEGER NOT NULL,
    PRIMARY KEY (action_id, fact_id)
);
CREATE INDEX ix_memory_action_dependencies_fact ON memory_action_dependencies(fact_id);
