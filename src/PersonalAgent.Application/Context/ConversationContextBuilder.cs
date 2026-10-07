using System.Text.Json;

namespace PersonalAgent.Application.Context;

/// <summary>Builds bounded, provenance-labelled local context from durable conversation history.</summary>
public sealed class ConversationContextBuilder : IContextBuilder
{
    /// <summary>Gets the policy version assigned to M1 local context packets.</summary>
    public const string PolicyVersion = "m1-local-context-v1";

    private const string LocalOnlyPrivacyClass = "LocalOnly";
    private const string EstimationMethod =
        "UTF-16 characters divided by four, plus a 20% reserve; not a provider-tokenizer count.";
    private readonly IConversationStore conversations;

    /// <summary>Creates a context builder over the authoritative application conversation store.</summary>
    /// <param name="conversations">Durable source of conversation messages.</param>
    public ConversationContextBuilder(IConversationStore conversations)
    {
        this.conversations = conversations ?? throw new ArgumentNullException(nameof(conversations));
    }

    /// <inheritdoc />
    public async ValueTask<ContextPacket> BuildAsync(
        ContextBuildRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateRequest(request);

        var tools = request.Tools.ToArray();
        var toolCatalogCharacters = JsonSerializer.Serialize(tools).Length;
        var packetId = Guid.NewGuid().ToString("N");
        var taskItem = new ContextItem("request:current", request.TaskText, LocalOnlyPrivacyClass, "user");
        var selectedNewestFirst = new List<ContextItem>();
        var mandatoryEstimate = Estimate(
            packetId,
            request.HostInstructions,
            taskItem,
            selectedNewestFirst,
            0,
            toolCatalogCharacters,
            minimumOmittedHistoryMessages: 0);
        EnsureWithinBudget(mandatoryEstimate, "mandatory_prompt_exceeds_limit");

        var historyReadLimit = LocalContextLimits.MaximumHistoryMessages + 1;
        var recent = await conversations.ReadRecentAsync(
            request.ConversationId,
            historyReadLimit,
            cancellationToken);
        if (recent is null)
        {
            throw new InvalidOperationException("The conversation store returned no history collection.");
        }

        var hasMoreHistory = recent.Count > LocalContextLimits.MaximumHistoryMessages;
        var minimumOmittedHistoryMessages = Math.Max(0, recent.Count - LocalContextLimits.MaximumHistoryMessages);
        var start = Math.Max(0, recent.Count - LocalContextLimits.MaximumHistoryMessages);
        for (var index = recent.Count - 1; index >= start; index--)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var message = recent[index];
            if (request.CurrentTaskMessageId == message.MessageId)
            {
                continue;
            }

            if (!CanInclude(message, request.ConversationId))
            {
                minimumOmittedHistoryMessages++;
                continue;
            }

            var item = new ContextItem(
                $"conversation-message:{message.MessageId:D}",
                message.Content,
                LocalOnlyPrivacyClass,
                message.Role);
            var candidateNewestFirst = selectedNewestFirst.Append(item).ToArray();
            var candidateHistoryCharacters = candidateNewestFirst.Sum(candidate => candidate.Text.Length);
            var candidateEstimate = Estimate(
                packetId,
                request.HostInstructions,
                taskItem,
                candidateNewestFirst,
                candidateHistoryCharacters,
                toolCatalogCharacters,
                minimumOmittedHistoryMessages,
                hasMoreHistory);
            if (candidateEstimate.EstimatedInputTokensWithMargin > LocalContextLimits.MaximumEstimatedInputTokens)
            {
                minimumOmittedHistoryMessages++;
                continue;
            }

            selectedNewestFirst.Add(item);
        }

        var orderedItems = selectedNewestFirst
            .AsEnumerable()
            .Reverse()
            .Append(taskItem)
            .ToArray();
        var historyCharacters = selectedNewestFirst.Sum(item => item.Text.Length);
        var estimate = Estimate(
            packetId,
            request.HostInstructions,
            taskItem,
            selectedNewestFirst,
            historyCharacters,
            toolCatalogCharacters,
            minimumOmittedHistoryMessages,
            hasMoreHistory);
        EnsureWithinBudget(estimate, "prompt_exceeds_limit");
        return new ContextPacket(packetId, PolicyVersion, orderedItems, estimate);
    }

    private static void ValidateRequest(ContextBuildRequest request)
    {
        if (request.Provider != ProviderKind.Local)
        {
            throw new InvalidOperationException("M1 context construction accepts only the local provider.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(request.TaskText);
        if (request.TaskText.Length > LocalContextLimits.MaximumTaskTextCharacters)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "The current task exceeds the fixed local text limit.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(request.HostInstructions);
        if (request.HostInstructions.Length > LocalContextLimits.MaximumInstructionCharacters)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "Host instructions exceed the fixed local text limit.");
        }

        ArgumentNullException.ThrowIfNull(request.Tools);
        if (request.Tools.Count > LocalContextLimits.MaximumToolCount)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "The tool catalog exceeds the fixed local limit.");
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tool in request.Tools)
        {
            if (tool is null)
            {
                throw new ArgumentException("The tool catalog cannot contain a null entry.", nameof(request));
            }

            ArgumentException.ThrowIfNullOrWhiteSpace(tool.Name);
            if (tool.Name.Length > LocalContextLimits.MaximumToolNameCharacters)
            {
                throw new ArgumentOutOfRangeException(nameof(request), "A tool name exceeds the fixed local limit.");
            }

            if (!names.Add(tool.Name))
            {
                throw new ArgumentException("Tool names must be unique within the catalog.", nameof(request));
            }

            ArgumentException.ThrowIfNullOrWhiteSpace(tool.InputSchema);
            if (tool.InputSchema.Length > LocalContextLimits.MaximumToolSchemaCharacters)
            {
                throw new ArgumentOutOfRangeException(nameof(request), "A tool schema exceeds the fixed local limit.");
            }
        }
    }

    private static bool CanInclude(ConversationMessage message, PersonalAgent.Domain.ConversationId conversationId) =>
        message is not null
        && message.ConversationId == conversationId
        && message.MessageId != Guid.Empty
        && message.Role is "user" or "assistant"
        && !string.IsNullOrWhiteSpace(message.Content)
        && message.Content.Length <= LocalContextLimits.MaximumHistoryMessageCharacters;

    private static ContextEstimate Estimate(
        string packetId,
        string instructions,
        ContextItem taskItem,
        IReadOnlyList<ContextItem> historyNewestFirst,
        int historyCharacters,
        int toolCatalogCharacters,
        int minimumOmittedHistoryMessages,
        bool hasMoreHistory = false)
    {
        var items = historyNewestFirst
            .AsEnumerable()
            .Reverse()
            .Append(taskItem)
            .ToArray();
        var policyVersion = PolicyVersion;
        var serializedPacket = JsonSerializer.Serialize(new { packetId, policyVersion, items });
        var serializationOverhead = serializedPacket.Length - taskItem.Text.Length - historyCharacters;
        if (serializationOverhead < 0)
        {
            throw new InvalidOperationException("Serialized context accounting produced a negative framing size.");
        }

        var totalCharacters = instructions.Length
            + taskItem.Text.Length
            + toolCatalogCharacters
            + historyCharacters
            + serializationOverhead;
        var estimatedTokens = DivideRoundUp(totalCharacters, LocalContextLimits.EstimatedCharactersPerToken);
        var reservedMargin = DivideRoundUp(
            estimatedTokens * LocalContextLimits.ReservedTokenMarginPercent,
            100);
        return new ContextEstimate(
            instructions.Length,
            taskItem.Text.Length,
            toolCatalogCharacters,
            historyCharacters,
            serializationOverhead,
            estimatedTokens,
            reservedMargin,
            estimatedTokens + reservedMargin,
            LocalContextLimits.MaximumEstimatedInputTokens,
            minimumOmittedHistoryMessages,
            hasMoreHistory,
            IsExact: false,
            EstimationMethod);
    }

    private static int DivideRoundUp(int value, int divisor) => (value + divisor - 1) / divisor;

    private static void EnsureWithinBudget(ContextEstimate estimate, string reasonCode)
    {
        if (estimate.EstimatedInputTokensWithMargin > LocalContextLimits.MaximumEstimatedInputTokens)
        {
            throw new ContextLimitExceededException(reasonCode);
        }
    }
}
