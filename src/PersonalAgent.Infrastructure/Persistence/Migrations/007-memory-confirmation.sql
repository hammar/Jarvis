ALTER TABLE memory_facts ADD COLUMN source_type TEXT NOT NULL DEFAULT 'Legacy';
ALTER TABLE memory_facts ADD COLUMN source_message_id TEXT REFERENCES messages(message_id) ON DELETE SET NULL;
ALTER TABLE memory_facts ADD COLUMN distinguishing_context TEXT;

DROP TRIGGER memory_facts_fts_insert;
DROP TRIGGER memory_facts_fts_delete;
DROP TRIGGER memory_facts_fts_update;
DROP TABLE memory_facts_fts;
CREATE VIRTUAL TABLE memory_facts_fts USING fts5(
    subject,
    fact_key,
    value,
    distinguishing_context,
    content='memory_facts',
    content_rowid='rowid'
);
CREATE TRIGGER memory_facts_fts_insert AFTER INSERT ON memory_facts BEGIN
    INSERT INTO memory_facts_fts(rowid, subject, fact_key, value, distinguishing_context)
    VALUES (new.rowid, new.subject, new.fact_key, new.value, new.distinguishing_context);
END;
CREATE TRIGGER memory_facts_fts_delete AFTER DELETE ON memory_facts BEGIN
    INSERT INTO memory_facts_fts(memory_facts_fts, rowid, subject, fact_key, value, distinguishing_context)
    VALUES ('delete', old.rowid, old.subject, old.fact_key, old.value, old.distinguishing_context);
END;
CREATE TRIGGER memory_facts_fts_update AFTER UPDATE OF subject, fact_key, value, distinguishing_context ON memory_facts BEGIN
    INSERT INTO memory_facts_fts(memory_facts_fts, rowid, subject, fact_key, value, distinguishing_context)
    VALUES ('delete', old.rowid, old.subject, old.fact_key, old.value, old.distinguishing_context);
    INSERT INTO memory_facts_fts(rowid, subject, fact_key, value, distinguishing_context)
    VALUES (new.rowid, new.subject, new.fact_key, new.value, new.distinguishing_context);
END;
INSERT INTO memory_facts_fts(memory_facts_fts) VALUES ('rebuild');

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
