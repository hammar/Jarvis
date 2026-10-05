CREATE TABLE conversations (
    id TEXT NOT NULL PRIMARY KEY,
    owner_id TEXT NOT NULL,
    title TEXT NOT NULL DEFAULT '',
    created_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL,
    version INTEGER NOT NULL DEFAULT 1 CHECK (version > 0)
);

CREATE TABLE turns (
    id TEXT NOT NULL PRIMARY KEY,
    conversation_id TEXT NOT NULL REFERENCES conversations(id) ON DELETE CASCADE,
    status TEXT NOT NULL,
    created_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL,
    version INTEGER NOT NULL DEFAULT 1 CHECK (version > 0)
);

CREATE INDEX ix_turns_conversation_created
    ON turns(conversation_id, created_at_utc);

CREATE TABLE messages (
    message_id TEXT NOT NULL PRIMARY KEY,
    conversation_id TEXT NOT NULL REFERENCES conversations(id) ON DELETE CASCADE,
    turn_id TEXT REFERENCES turns(id) ON DELETE SET NULL,
    sequence INTEGER NOT NULL CHECK (sequence > 0),
    role TEXT NOT NULL CHECK (role IN ('user', 'assistant', 'tool', 'system')),
    content TEXT NOT NULL,
    created_at_utc TEXT NOT NULL,
    UNIQUE (conversation_id, sequence)
);

CREATE INDEX ix_messages_conversation_created
    ON messages(conversation_id, created_at_utc);

CREATE TABLE turn_events (
    event_id TEXT NOT NULL PRIMARY KEY,
    turn_id TEXT NOT NULL REFERENCES turns(id) ON DELETE CASCADE,
    sequence INTEGER NOT NULL CHECK (sequence > 0),
    event_type TEXT NOT NULL,
    payload_json TEXT NOT NULL,
    occurred_at_utc TEXT NOT NULL,
    UNIQUE (turn_id, sequence)
);
