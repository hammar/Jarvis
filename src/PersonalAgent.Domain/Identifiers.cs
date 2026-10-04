namespace PersonalAgent.Domain;

/// <summary>Identifies a durable application conversation independently of a Copilot session.</summary>
/// <param name="Value">The nonempty identifier value.</param>
public readonly record struct ConversationId(Guid Value)
{
    /// <summary>Creates a new conversation identifier.</summary>
    public static ConversationId New() => new(Guid.NewGuid());
}

/// <summary>Identifies one application-owned turn.</summary>
/// <param name="Value">The nonempty identifier value.</param>
public readonly record struct TurnId(Guid Value)
{
    /// <summary>Creates a new turn identifier.</summary>
    public static TurnId New() => new(Guid.NewGuid());
}

/// <summary>Identifies one journaled action attempt.</summary>
/// <param name="Value">The nonempty identifier value.</param>
public readonly record struct ActionId(Guid Value)
{
    /// <summary>Creates a new action identifier.</summary>
    public static ActionId New() => new(Guid.NewGuid());
}

/// <summary>Identifies an exact owner approval request.</summary>
/// <param name="Value">The nonempty identifier value.</param>
public readonly record struct ApprovalId(Guid Value)
{
    /// <summary>Creates a new approval identifier.</summary>
    public static ApprovalId New() => new(Guid.NewGuid());
}

/// <summary>Identifies a durable memory fact.</summary>
/// <param name="Value">The nonempty identifier value.</param>
public readonly record struct MemoryFactId(Guid Value)
{
    /// <summary>Creates a new memory fact identifier.</summary>
    public static MemoryFactId New() => new(Guid.NewGuid());
}

/// <summary>Identifies a persisted scheduled job.</summary>
/// <param name="Value">The nonempty identifier value.</param>
public readonly record struct JobId(Guid Value)
{
    /// <summary>Creates a new job identifier.</summary>
    public static JobId New() => new(Guid.NewGuid());
}

/// <summary>Describes the application-owned state of an agent turn.</summary>
public enum TurnStatus
{
    /// <summary>The turn has been accepted but routing has not started.</summary>
    Received,
    /// <summary>The host is selecting a route.</summary>
    Routing,
    /// <summary>The owner must review selected context.</summary>
    ContextReview,
    /// <summary>The agent engine is running the turn.</summary>
    Running,
    /// <summary>The turn is waiting for an owner approval.</summary>
    WaitingForApproval,
    /// <summary>The turn completed successfully.</summary>
    Completed,
    /// <summary>The turn failed with an explicit failure outcome.</summary>
    Failed,
    /// <summary>The owner or host cancelled the turn.</summary>
    Cancelled,
    /// <summary>The turn stopped without a confirmed terminal engine event.</summary>
    Interrupted
}
