namespace PersonalAgent.Infrastructure.Persistence.Migrations;

internal sealed record SchemaMigration(int Version, string Name, string Sql);

internal static class SchemaMigrations
{
    internal static readonly SchemaMigration[] All =
    [
        new(1, "initial durable state", InitialSchema),
        new(2, "memory full-text search", MemorySearch),
        new(3, "approval action owner binding", ApprovalOwnerBinding)
    ];

    private const string InitialSchema = """
        CREATE TABLE Conversations (
            Id TEXT PRIMARY KEY NOT NULL,
            CreatedAtUtc TEXT NOT NULL
        );
        CREATE TABLE Messages (
            MessageId TEXT PRIMARY KEY NOT NULL,
            ConversationId TEXT NOT NULL REFERENCES Conversations(Id) ON DELETE CASCADE,
            Role TEXT NOT NULL CHECK (Role IN ('system', 'user', 'assistant', 'tool')),
            Content TEXT NOT NULL,
            CreatedAtUtc TEXT NOT NULL
        );
        CREATE INDEX IX_Messages_Conversation_Time ON Messages(ConversationId, CreatedAtUtc DESC);
        CREATE TABLE Turns (
            TurnId TEXT PRIMARY KEY NOT NULL,
            ConversationId TEXT NOT NULL REFERENCES Conversations(Id) ON DELETE CASCADE,
            Status TEXT NOT NULL,
            CreatedAtUtc TEXT NOT NULL,
            Version INTEGER NOT NULL DEFAULT 1 CHECK (Version > 0)
        );
        CREATE TABLE TurnEvents (
            EventId TEXT PRIMARY KEY NOT NULL,
            TurnId TEXT NOT NULL REFERENCES Turns(TurnId) ON DELETE CASCADE,
            Sequence INTEGER NOT NULL CHECK (Sequence > 0),
            EventType TEXT NOT NULL,
            Payload TEXT NOT NULL,
            OccurredAtUtc TEXT NOT NULL,
            UNIQUE (TurnId, Sequence)
        );
        CREATE TABLE MemoryFacts (
            Id TEXT PRIMARY KEY NOT NULL,
            OwnerId TEXT NOT NULL DEFAULT '',
            Subject TEXT NOT NULL,
            FactKey TEXT NOT NULL,
            Value TEXT NOT NULL,
            SourceId TEXT NOT NULL,
            PrivacyClass TEXT NOT NULL,
            CreatedAtUtc TEXT NOT NULL,
            UpdatedAtUtc TEXT NOT NULL,
            Version INTEGER NOT NULL CHECK (Version > 0),
            ValidityStatus TEXT NOT NULL DEFAULT 'Active',
            SupersedesId TEXT
        );
        CREATE INDEX IX_MemoryFacts_Active ON MemoryFacts(ValidityStatus, UpdatedAtUtc DESC);
        CREATE TABLE MemoryProposals (
            Id TEXT PRIMARY KEY NOT NULL,
            OwnerId TEXT NOT NULL,
            ProposalJson TEXT NOT NULL,
            Status TEXT NOT NULL,
            CreatedAtUtc TEXT NOT NULL,
            Version INTEGER NOT NULL CHECK (Version > 0)
        );
        CREATE TABLE Actions (
            Id TEXT PRIMARY KEY NOT NULL,
            OwnerId TEXT NOT NULL,
            ActionType TEXT NOT NULL,
            CanonicalArguments TEXT NOT NULL,
            RequestHash TEXT NOT NULL,
            Status TEXT NOT NULL CHECK (Status IN ('Prepared', 'AwaitingApproval', 'Executing', 'Succeeded', 'Failed', 'Unknown', 'Rejected', 'Expired')),
            CreatedAtUtc TEXT NOT NULL,
            UpdatedAtUtc TEXT NOT NULL,
            Version INTEGER NOT NULL CHECK (Version > 0)
        );
        CREATE INDEX IX_Actions_Status ON Actions(Status, UpdatedAtUtc);
        CREATE TABLE ApprovalRequests (
            Id TEXT PRIMARY KEY NOT NULL,
            ActionId TEXT NOT NULL REFERENCES Actions(Id),
            OwnerId TEXT NOT NULL,
            ExpiresAtUtc TEXT NOT NULL,
            Status TEXT NOT NULL,
            CreatedAtUtc TEXT NOT NULL,
            Version INTEGER NOT NULL CHECK (Version > 0)
        );
        CREATE INDEX IX_ApprovalRequests_Owner_Status ON ApprovalRequests(OwnerId, Status, ExpiresAtUtc);
        CREATE TABLE Jobs (
            Id TEXT PRIMARY KEY NOT NULL,
            OwnerId TEXT NOT NULL,
            Kind TEXT NOT NULL,
            PayloadVersion INTEGER NOT NULL CHECK (PayloadVersion > 0),
            Payload TEXT NOT NULL,
            TimeZoneId TEXT NOT NULL,
            DueAtUtc TEXT NOT NULL,
            Recurrence TEXT,
            Enabled INTEGER NOT NULL CHECK (Enabled IN (0, 1)),
            MisfirePolicy TEXT NOT NULL,
            LeaseOwner TEXT,
            LeaseExpiresAtUtc TEXT,
            AttemptCount INTEGER NOT NULL DEFAULT 0 CHECK (AttemptCount >= 0),
            Outcome TEXT,
            ClientRequestId TEXT,
            Version INTEGER NOT NULL DEFAULT 1 CHECK (Version > 0),
            UNIQUE (OwnerId, ClientRequestId)
        );
        CREATE INDEX IX_Jobs_Due_Lease ON Jobs(Enabled, DueAtUtc, LeaseExpiresAtUtc);
        CREATE TABLE JobRuns (
            Id TEXT PRIMARY KEY NOT NULL,
            JobId TEXT NOT NULL REFERENCES Jobs(Id),
            ScheduledOccurrenceUtc TEXT NOT NULL,
            Status TEXT NOT NULL,
            StartedAtUtc TEXT,
            CompletedAtUtc TEXT,
            NotificationId TEXT,
            Error TEXT,
            UNIQUE (JobId, ScheduledOccurrenceUtc)
        );
        CREATE TABLE Notifications (
            Id TEXT PRIMARY KEY NOT NULL,
            OwnerId TEXT NOT NULL,
            JobId TEXT REFERENCES Jobs(Id),
            Message TEXT NOT NULL,
            CreatedAtUtc TEXT NOT NULL,
            ReadAtUtc TEXT
        );
        CREATE TABLE CloudConsents (
            Id TEXT PRIMARY KEY NOT NULL,
            OwnerId TEXT NOT NULL,
            Provider TEXT NOT NULL,
            PacketHash TEXT NOT NULL,
            PolicyVersion TEXT NOT NULL,
            ExpiresAtUtc TEXT NOT NULL,
            CreatedAtUtc TEXT NOT NULL,
            RevokedAtUtc TEXT,
            Version INTEGER NOT NULL CHECK (Version > 0)
        );
        CREATE TABLE AuditEvents (
            Id TEXT PRIMARY KEY NOT NULL,
            OwnerId TEXT NOT NULL,
            EventType TEXT NOT NULL,
            SubjectId TEXT,
            DetailsJson TEXT NOT NULL,
            OccurredAtUtc TEXT NOT NULL
        );
        CREATE INDEX IX_AuditEvents_Occurred ON AuditEvents(OccurredAtUtc);
        """;

    private const string MemorySearch = """
        CREATE VIRTUAL TABLE MemoryFactsSearch USING fts5(
            Subject, FactKey, Value, content='MemoryFacts', content_rowid='rowid'
        );
        CREATE TRIGGER TR_MemoryFactsSearch_Insert AFTER INSERT ON MemoryFacts BEGIN
            INSERT INTO MemoryFactsSearch(rowid, Subject, FactKey, Value)
            VALUES (new.rowid, new.Subject, new.FactKey, new.Value);
        END;
        CREATE TRIGGER TR_MemoryFactsSearch_Delete AFTER DELETE ON MemoryFacts BEGIN
            INSERT INTO MemoryFactsSearch(MemoryFactsSearch, rowid, Subject, FactKey, Value)
            VALUES ('delete', old.rowid, old.Subject, old.FactKey, old.Value);
        END;
        CREATE TRIGGER TR_MemoryFactsSearch_Update AFTER UPDATE ON MemoryFacts BEGIN
            INSERT INTO MemoryFactsSearch(MemoryFactsSearch, rowid, Subject, FactKey, Value)
            VALUES ('delete', old.rowid, old.Subject, old.FactKey, old.Value);
            INSERT INTO MemoryFactsSearch(rowid, Subject, FactKey, Value)
            VALUES (new.rowid, new.Subject, new.FactKey, new.Value);
        END;
        INSERT INTO MemoryFactsSearch(MemoryFactsSearch) VALUES ('rebuild');
        """;

    private const string ApprovalOwnerBinding = """
        CREATE UNIQUE INDEX UX_Actions_Id_OwnerId ON Actions(Id, OwnerId);
        CREATE TRIGGER TR_ApprovalRequests_Owner_Insert
        BEFORE INSERT ON ApprovalRequests
        WHEN NOT EXISTS (
            SELECT 1 FROM Actions WHERE Id = NEW.ActionId AND OwnerId = NEW.OwnerId
        )
        BEGIN
            SELECT RAISE(ABORT, 'approval owner must match action owner');
        END;
        CREATE TRIGGER TR_ApprovalRequests_Owner_Update
        BEFORE UPDATE OF ActionId, OwnerId ON ApprovalRequests
        WHEN NOT EXISTS (
            SELECT 1 FROM Actions WHERE Id = NEW.ActionId AND OwnerId = NEW.OwnerId
        )
        BEGIN
            SELECT RAISE(ABORT, 'approval owner must match action owner');
        END;
        """;
}
