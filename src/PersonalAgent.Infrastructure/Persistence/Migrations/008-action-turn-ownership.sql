ALTER TABLE actions ADD COLUMN action_owner_id TEXT;
ALTER TABLE actions ADD COLUMN action_turn_id TEXT;

UPDATE actions
SET action_owner_id = CASE
        WHEN json_valid(canonical_arguments)
            AND json_type(canonical_arguments, '$.OwnerId') = 'text'
            AND length(json_extract(canonical_arguments, '$.OwnerId')) BETWEEN 1 AND 128
            AND length(trim(json_extract(canonical_arguments, '$.OwnerId'))) > 0
            AND json_type(canonical_arguments, '$.TurnId') = 'text'
            AND length(json_extract(canonical_arguments, '$.TurnId')) = 36
            AND substr(json_extract(canonical_arguments, '$.TurnId'), 9, 1) = '-'
            AND substr(json_extract(canonical_arguments, '$.TurnId'), 14, 1) = '-'
            AND substr(json_extract(canonical_arguments, '$.TurnId'), 19, 1) = '-'
            AND substr(json_extract(canonical_arguments, '$.TurnId'), 24, 1) = '-'
            AND length(replace(json_extract(canonical_arguments, '$.TurnId'), '-', '')) = 32
            AND json_extract(canonical_arguments, '$.TurnId') NOT GLOB '*[^0-9A-Fa-f-]*'
        THEN json_extract(canonical_arguments, '$.OwnerId') END,
    action_turn_id = CASE
        WHEN json_valid(canonical_arguments)
            AND json_type(canonical_arguments, '$.OwnerId') = 'text'
            AND length(json_extract(canonical_arguments, '$.OwnerId')) BETWEEN 1 AND 128
            AND length(trim(json_extract(canonical_arguments, '$.OwnerId'))) > 0
            AND json_type(canonical_arguments, '$.TurnId') = 'text'
            AND length(json_extract(canonical_arguments, '$.TurnId')) = 36
            AND substr(json_extract(canonical_arguments, '$.TurnId'), 9, 1) = '-'
            AND substr(json_extract(canonical_arguments, '$.TurnId'), 14, 1) = '-'
            AND substr(json_extract(canonical_arguments, '$.TurnId'), 19, 1) = '-'
            AND substr(json_extract(canonical_arguments, '$.TurnId'), 24, 1) = '-'
            AND length(replace(json_extract(canonical_arguments, '$.TurnId'), '-', '')) = 32
            AND json_extract(canonical_arguments, '$.TurnId') NOT GLOB '*[^0-9A-Fa-f-]*'
        THEN lower(json_extract(canonical_arguments, '$.TurnId')) END;

CREATE INDEX ix_actions_owner_turn_created
    ON actions(action_owner_id, action_turn_id, created_at_utc);

CREATE INDEX ix_actions_unattributed_unresolved
    ON actions(status)
    WHERE memory_invalidated = 0
      AND status IN ('Unknown', 'Executing')
      AND (action_owner_id IS NULL OR action_turn_id IS NULL);
