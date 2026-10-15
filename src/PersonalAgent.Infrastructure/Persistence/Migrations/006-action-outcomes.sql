ALTER TABLE actions ADD COLUMN outcome_json TEXT
    CHECK (outcome_json IS NULL OR (
        json_valid(outcome_json)
        AND json_extract(outcome_json, '$.Status') IS status
        AND json_extract(outcome_json, '$.ActionId.Value') IS id
        AND status IN ('Succeeded', 'Failed', 'Unknown', 'Rejected', 'Expired')
    ));
