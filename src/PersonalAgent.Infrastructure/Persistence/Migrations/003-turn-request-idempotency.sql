ALTER TABLE turns ADD COLUMN client_request_id TEXT;
ALTER TABLE turns ADD COLUMN request_fingerprint TEXT;

CREATE UNIQUE INDEX ux_turns_conversation_client_request
    ON turns(conversation_id, client_request_id)
    WHERE client_request_id IS NOT NULL;

CREATE UNIQUE INDEX ux_messages_turn_user
    ON messages(turn_id)
    WHERE turn_id IS NOT NULL AND role = 'user';

CREATE UNIQUE INDEX ux_messages_turn_assistant
    ON messages(turn_id)
    WHERE turn_id IS NOT NULL AND role = 'assistant';
