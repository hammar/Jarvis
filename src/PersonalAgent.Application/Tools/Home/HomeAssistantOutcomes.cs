namespace PersonalAgent.Application;

/// <summary>Classifies a safe, non-payload-bearing Home Assistant read failure.</summary>
public enum HomeAssistantFailure
{
    /// <summary>The endpoint rejected authentication or authorization.</summary>
    Unauthorized,
    /// <summary>The approved entity does not exist at the endpoint.</summary>
    UnknownEntity,
    /// <summary>Transport failed or the endpoint returned a transient failure.</summary>
    Transport,
    /// <summary>The original request deadline expired.</summary>
    Timeout,
    /// <summary>The response was malformed, oversized or inconsistent with the requested entity.</summary>
    InvalidResponse,
    /// <summary>The entity/operation is not approved.</summary>
    NotAllowed
}

/// <summary>Surfaces a typed HA failure without retaining raw remote bodies, credentials or inner exceptions.</summary>
public sealed class HomeAssistantException : Exception
{
    /// <summary>Creates a safe failure with a fixed diagnostic message.</summary>
    /// <param name="failure">Typed failure classification.</param>
    public HomeAssistantException(HomeAssistantFailure failure)
        : base($"Home Assistant request failed: {failure}.")
    {
        Failure = failure;
    }

    /// <summary>Gets the non-secret failure classification.</summary>
    public HomeAssistantFailure Failure { get; }
}

/// <summary>Classifies submission independently of observing a physical state.</summary>
public enum HomeAssistantWriteStatus
{
    /// <summary>The service accepted the request; state remains to be verified.</summary>
    Accepted,
    /// <summary>The operation was definitively rejected without application.</summary>
    Rejected,
    /// <summary>The operation may have been submitted; observation only is safe.</summary>
    Unknown
}

/// <summary>Contains only submission status and a stable safe reason code.</summary>
/// <param name="Status">Submission status, not physical success.</param>
/// <param name="ReasonCode">Fixed reason without remote payload or secret data.</param>
public sealed record HomeAssistantWriteResult(HomeAssistantWriteStatus Status, string? ReasonCode);
