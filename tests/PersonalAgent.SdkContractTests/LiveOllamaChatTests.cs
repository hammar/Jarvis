using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using PersonalAgent.Application;
using PersonalAgent.Application.Context;
using PersonalAgent.Domain;
using PersonalAgent.Infrastructure.AgentEngine.Copilot;
using PersonalAgent.Infrastructure.Persistence;
using PersonalAgent.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace PersonalAgent.SdkContractTests;

[Collection("Copilot runtime process isolation")]
public sealed class LiveOllamaChatTests(ITestOutputHelper output)
{
    private const string Instructions = LocalChatPrompt.Instructions;

    [LiveOllamaTheory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [Trait("Category", "LiveOllama")]
    public async Task ActualEngineAnswersSuccessiveRequestsLikeDirectOllama(int scenario)
    {
        var model = Environment.GetEnvironmentVariable("JARVIS_OLLAMA_MODEL");
        Assert.False(string.IsNullOrWhiteSpace(model), "Set JARVIS_OLLAMA_MODEL to opt into this live, synthetic-only test.");
        var upstream = Environment.GetEnvironmentVariable("JARVIS_OLLAMA_BASE_URL") ?? "http://127.0.0.1:11434/v1";
        using var directory = IsolatedDirectory.Create();
        var database = new SqliteDatabase(Path.Combine(directory.Path, "jarvis.db"));
        await database.InitializeAsync();
        var clock = new SystemClock();
        var store = new SqliteConversationStore(database, clock);
        var captured = new ConcurrentQueue<string>();
        await using var proxy = await OllamaInferenceProxy.StartAsync(upstream);
        proxy.ObserveSyntheticRequest = captured.Enqueue;
        var engine = new CopilotAgentEngine(store, new RejectTools(), clock,
            new CopilotAgentEngineOptions(Path.Combine(directory.Path, "runtime"),
                new CopilotProviderOptions(model!, new Uri(proxy.BaseUrl)), null));
        var builder = new ConversationContextBuilder(store);
        var conversation = ConversationId.New();
        await store.CreateConversationAsync(new ConversationRecord(conversation, "synthetic-owner", "Synthetic arithmetic",
            clock.UtcNow, clock.UtcNow), CancellationToken.None);
        var failures = new List<string>();
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false });
        var directMessages = new List<object> { new { role = "system", content = Instructions } };
        var questions = scenario switch
        {
            0 => new[] { ("What is 7 times 9? Reply with just the number.", "63"), ("What is 5 times 3? Reply with just the number.", "15"), ("What is 2 times 4? Reply with just the number.", "8") },
            1 => new[] { ("7*9. Reply with just the number.", "63"), ("5*3. Reply with just the number.", "15"), ("2*4. Reply with just the number.", "8") },
            _ => new[] { ("For this conversation, my test word is otter. Reply with just OK.", "OK"), ("What test word did I give you? Reply with just the word.", "otter") }
        };
        foreach (var (task, expected) in questions)
        {
            var submitted = await store.SubmitTurnAsync(new ConversationTurnSubmission(TurnId.New(), conversation,
                Guid.NewGuid().ToString("N"), new string('A', 64), Guid.NewGuid(), task, clock.UtcNow), CancellationToken.None);
            var context = await builder.BuildAsync(new ContextBuildRequest(conversation, ProviderKind.Local, task,
                Instructions, [], submitted.UserMessageId), CancellationToken.None);
            var events = new List<AgentEvent>();
            var engineTurn = Stopwatch.StartNew();
            await foreach (var item in engine.RunTurnAsync(new AgentTurnRequest(submitted.Turn.Id, conversation, ProviderKind.Local,
                Instructions, context, [], TimeSpan.FromSeconds(120), 0), CancellationToken.None))
            {
                events.Add(item);
            }
            engineTurn.Stop();
            Assert.Single(events.OfType<TurnCompleted>());
            var answer = (await store.ReadRecentAsync(conversation, 1, CancellationToken.None)).Single().Content.Trim();
            output.WriteLine($"{model}: {task}; engine={answer}; expected={expected}");
            if (answer != expected)
            {
                failures.Add($"{task}: expected {expected}, received {answer}");
            }
            var raw = captured.Last();
            output.WriteLine($"SDK turn elapsed: {engineTurn.ElapsedMilliseconds} ms; provider request size: {raw.Length} characters.");
            output.WriteLine($"Synthetic SDK request: {raw}");
            var replay = JsonNode.Parse(raw)!.AsObject();
            replay["stream"] = false;
            replay.Remove("stream_options");
            using var response = await http.PostAsJsonAsync(upstream.TrimEnd('/') + "/chat/completions", replay);
            response.EnsureSuccessStatusCode();
            using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var replayAnswer = result.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()!.Trim();
            output.WriteLine("Exact SDK request replay: " + replayAnswer);
            Assert.Equal(expected, replayAnswer);

            directMessages.Add(new { role = "user", content = task });
            using var directResponse = await http.PostAsJsonAsync(upstream.TrimEnd('/') + "/chat/completions",
                new { model, messages = directMessages, temperature = 0, top_p = 0.95, stream = false });
            directResponse.EnsureSuccessStatusCode();
            using var directResult = JsonDocument.Parse(await directResponse.Content.ReadAsStringAsync());
            var directAnswer = directResult.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()!.Trim();
            output.WriteLine("Role-separated direct Ollama: " + directAnswer);
            Assert.Equal(expected, directAnswer);
            directMessages.Add(new { role = "assistant", content = directAnswer });
        }
        Assert.True(failures.Count == 0, string.Join("; ", failures));
    }

    private sealed class RejectTools : IToolDispatcher
    {
        public void ReleaseTurn(TurnId turnId)
        {
        }
        public void ReleaseApproval(ActionId actionId)
        {
        }

        public ValueTask<ToolDispatchResult> DispatchAsync(ToolDispatchRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The synthetic text-only diagnostic must not dispatch tools.");
    }

    private sealed class LiveOllamaTheoryAttribute : TheoryAttribute
    {
        public LiveOllamaTheoryAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("JARVIS_OLLAMA_MODEL")))
            {
                Skip = "Opt-in live evaluation requires JARVIS_OLLAMA_MODEL.";
            }
        }
    }
}
