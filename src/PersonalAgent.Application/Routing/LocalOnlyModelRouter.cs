namespace PersonalAgent.Application.Routing;

/// <summary>Applies M1's deterministic local-only route policy.</summary>
public sealed class LocalOnlyModelRouter : IModelRouter
{
    /// <summary>Gets the stable policy version emitted by this router.</summary>
    public const string PolicyVersion = "m1-local-only-v1";

    /// <inheritdoc />
    public ValueTask<RouteDecision> RouteAsync(RoutingRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (!Enum.IsDefined(request.RequestedMode))
        {
            return Unsupported("invalid_route_mode", "The requested route mode is not supported.");
        }

        if (request.RequestedMode != RouteMode.LocalOnly)
        {
            return request.HasLocalOnlyContent
                ? Unsupported("local_only_content_blocks_cloud", "LocalOnly content cannot be sent to a cloud provider.")
                : Unsupported("cloud_routing_unavailable_in_m1", "Cloud routing is not available in this release.");
        }

        if (string.IsNullOrWhiteSpace(request.TaskText))
        {
            return ValueTask.FromResult(RouteDecision.Clarify(
                "task_text_required",
                PolicyVersion,
                "Please enter a request before starting a turn."));
        }

        if (request.TaskText.Length > LocalContextLimits.MaximumTaskTextCharacters)
        {
            return Unsupported("task_text_too_long", "The request exceeds the supported local text limit.");
        }

        return request.TaskKind switch
        {
            RoutingTaskKind.TextConversation => Local("local_text_conversation"),
            RoutingTaskKind.RecognizedLocalWorkflow => Local("recognized_local_workflow"),
            RoutingTaskKind.RequiresCloud => Unsupported(
                "cloud_escalation_blocked_local_only",
                "This request requires cloud inference, which is disabled by the LocalOnly route."),
            RoutingTaskKind.Ambiguous => ValueTask.FromResult(RouteDecision.Clarify(
                "task_category_ambiguous",
                PolicyVersion,
                "Please clarify what supported local task you want completed.")),
            RoutingTaskKind.Unsupported => Unsupported(
                "task_category_unsupported",
                "This task is not supported by the local capabilities in this release."),
            _ => Unsupported("invalid_task_category", "The task category is not supported.")
        };
    }

    private static ValueTask<RouteDecision> Local(string reasonCode) =>
        ValueTask.FromResult(RouteDecision.Local(reasonCode, PolicyVersion));

    private static ValueTask<RouteDecision> Unsupported(string reasonCode, string userMessage) =>
        ValueTask.FromResult(RouteDecision.Unsupported(reasonCode, PolicyVersion, userMessage));
}
