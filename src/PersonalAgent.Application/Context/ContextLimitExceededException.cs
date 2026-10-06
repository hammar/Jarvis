namespace PersonalAgent.Application.Context;

/// <summary>Reports that mandatory host-selected prompt content exceeds the fixed M1 context budget.</summary>
public sealed class ContextLimitExceededException : InvalidOperationException
{
    /// <summary>Creates a context-budget failure without including prompt or private content.</summary>
    /// <param name="reasonCode">Stable reason describing which envelope limit was exceeded.</param>
    public ContextLimitExceededException(string reasonCode)
        : base($"The required local context exceeds its configured limit ({reasonCode}).")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reasonCode);
        ReasonCode = reasonCode;
    }

    /// <summary>Gets the stable, non-sensitive reason code.</summary>
    public string ReasonCode { get; }
}
