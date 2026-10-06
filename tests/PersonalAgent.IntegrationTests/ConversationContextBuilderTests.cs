using PersonalAgent.Application;
using PersonalAgent.Application.Context;
using PersonalAgent.Domain;
using PersonalAgent.Infrastructure.Persistence;
using PersonalAgent.TestSupport;
using Xunit;

namespace PersonalAgent.IntegrationTests;

public sealed class ConversationContextBuilderTests
{
    private static readonly DateTimeOffset CreatedAtUtc = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ContextBuilderUsesIsolatedSqliteHistoryAndPreservesOrderProvenanceAndPrivacyLabels()
    {
        using var file = IsolatedDatabaseFile.Create();
        var database = new SqliteDatabase(file.Path);
        await database.InitializeAsync();
        var store = new SqliteConversationStore(database, new SystemClock());
        var conversationId = ConversationId.New();
        var messages = Enumerable.Range(0, 100)
            .Select(index => new ConversationMessage(
                Guid.NewGuid(),
                conversationId,
                index % 2 == 0 ? "user" : "assistant",
                $"persisted-{index}",
                CreatedAtUtc.AddSeconds(index)))
            .ToArray();
        foreach (var message in messages)
        {
            await store.AppendMessageAsync(message, CancellationToken.None);
        }

        var request = new ContextBuildRequest(
            conversationId,
            ProviderKind.Local,
            "current request",
            "Host-only instructions.",
            [new AgentToolDefinition("local_lookup", """{"type":"object"}""", true)]);
        var packet = await new ConversationContextBuilder(store).BuildAsync(request, CancellationToken.None);
        var estimate = packet.Estimate;
        var estimatedCharacters = estimate.SystemInstructionsCharacters
            + estimate.CurrentTaskCharacters
            + estimate.ToolCatalogCharacters
            + estimate.ConversationHistoryCharacters
            + estimate.SerializationOverheadCharacters;

        Assert.Equal(LocalContextLimits.MaximumHistoryMessages + 1, packet.Items.Count);
        Assert.Equal(messages[88].Content, packet.Items[0].Text);
        Assert.Equal(messages[^1].Content, packet.Items[^2].Text);
        Assert.Equal("current request", packet.Items[^1].Text);
        Assert.Equal($"conversation-message:{messages[88].MessageId:D}", packet.Items[0].SourceId);
        Assert.Equal("user", packet.Items[0].Role);
        Assert.All(packet.Items, item => Assert.Equal("LocalOnly", item.PrivacyClass));
        Assert.Equal(1, estimate.MinimumOmittedHistoryMessages);
        Assert.True(estimate.HasMoreHistory);
        Assert.Equal(request.HostInstructions.Length, estimate.SystemInstructionsCharacters);
        Assert.Equal(request.TaskText.Length, estimate.CurrentTaskCharacters);
        Assert.Equal(messages.Skip(88).Sum(message => message.Content.Length), estimate.ConversationHistoryCharacters);
        Assert.True(estimate.ToolCatalogCharacters > request.Tools[0].InputSchema.Length);
        Assert.True(estimate.SerializationOverheadCharacters > 0);
        Assert.Equal((estimatedCharacters + 3) / 4, estimate.EstimatedInputTokens);
        Assert.Equal((estimate.EstimatedInputTokens * 20 + 99) / 100, estimate.ReservedMarginTokens);
        Assert.Equal(estimate.EstimatedInputTokens + estimate.ReservedMarginTokens, estimate.EstimatedInputTokensWithMargin);
        Assert.False(estimate.IsExact);
    }
}
