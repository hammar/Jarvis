ALTER TABLE actions ADD COLUMN action_owner_id TEXT;
ALTER TABLE actions ADD COLUMN action_turn_id TEXT;

UPDATE actions
SET action_owner_id = CASE
        WHEN json_valid(canonical_arguments) THEN
            CASE WHEN json_type(canonical_arguments, '$.OwnerId') = 'text'
                THEN json_extract(canonical_arguments, '$.OwnerId') END
    END,
    action_turn_id = CASE
        WHEN json_valid(canonical_arguments) THEN
            CASE WHEN json_type(canonical_arguments, '$.TurnId') = 'text'
                THEN json_extract(canonical_arguments, '$.TurnId') END
    END;

CREATE INDEX ix_actions_owner_turn_created
    ON actions(action_owner_id, action_turn_id, created_at_utc);

CREATE INDEX ix_actions_unattributed_unresolved
    ON actions(status)
    WHERE memory_invalidated = 0
      AND status IN ('Unknown', 'Executing')
      AND (action_owner_id IS NULL OR action_turn_id IS NULL);
