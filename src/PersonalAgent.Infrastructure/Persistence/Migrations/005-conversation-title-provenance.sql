ALTER TABLE conversations
    ADD COLUMN title_is_manual INTEGER NOT NULL DEFAULT 0 CHECK (title_is_manual IN (0, 1));
