CREATE TABLE owner_account (
    owner_id TEXT NOT NULL PRIMARY KEY CHECK (owner_id = 'owner'),
    password_hash TEXT NOT NULL,
    created_at_utc TEXT NOT NULL
);

CREATE TABLE owner_settings (
    setting_key TEXT NOT NULL PRIMARY KEY CHECK (
        setting_key IN ('conversation_retention_days', 'audit_retention_days')
    ),
    setting_value INTEGER NOT NULL CHECK (setting_value BETWEEN 1 AND 3650)
);
