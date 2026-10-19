using System.Net.Http.Json;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Xunit;

namespace PersonalAgent.E2ETests;

[Collection(SimulatorCollection.Name)]
public sealed class AspireSimulatorTests(SimulatorHostFixture fixture)
{
    [Fact]
    [Trait("Category", "AspireE2E")]
    public async Task ReadinessReportsDatabaseUnavailableAfterStartup()
    {
        var databasePath = Path.Combine(fixture.DataDirectoryPath, "jarvis.db");
        var movedFiles = new List<(string Original, string Backup)>();
        try
        {
            foreach (var path in new[] { databasePath, databasePath + "-wal", databasePath + "-shm" })
            {
                if (File.Exists(path))
                {
                    var backup = path + ".readiness-probe";
                    File.Move(path, backup);
                    movedFiles.Add((path, backup));
                }
            }

            using var response = await fixture.WebClient.GetAsync("/health/ready");
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal("Unhealthy", body.RootElement.GetProperty("status").GetString());
            Assert.Equal(
                "not_ready",
                body.RootElement.GetProperty("checks")
                    .GetProperty("local-turn-runtime")
                    .GetProperty("data")
                    .GetProperty("database_and_migrations")
                    .GetString());
            Assert.False(File.Exists(databasePath));
        }
        finally
        {
            foreach (var path in new[] { databasePath, databasePath + "-wal", databasePath + "-shm" })
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }

            foreach (var (original, backup) in movedFiles)
            {
                File.Move(backup, original);
            }
        }
    }

    [Fact]
    [Trait("Category", "AspireE2E")]
    public async Task SimulatorStartsWebAndDiscoversDeterministicManagedEndpoints()
    {
        using var webResponse = await fixture.WebClient.GetAsync("/health/ready");
        using var modelResponse = await fixture.ModelClient.GetAsync("/fixture/model");
        using var modelsResponse = await fixture.ModelClient.GetAsync("/v1/models");
        using var homeResponse = await fixture.HomeAssistantClient.GetAsync("/fixture/entities");
        var page = await fixture.WebClient.GetStringAsync("/");
        var model = await modelResponse.Content.ReadAsStringAsync();
        using var ready = JsonDocument.Parse(await webResponse.Content.ReadAsStringAsync());
        using var models = JsonDocument.Parse(await modelsResponse.Content.ReadAsStringAsync());
        var home = await homeResponse.Content.ReadAsStringAsync();
        using var completionResponse = await fixture.ModelClient.PostAsJsonAsync(
            "/v1/chat/completions",
            new { model = "simulator-model", messages = new[] { new { role = "user", content = "test" } } });
        using var completion = JsonDocument.Parse(await completionResponse.Content.ReadAsStringAsync());
        using var streamingResponse = await fixture.ModelClient.PostAsJsonAsync(
            "/v1/chat/completions",
            new
            {
                model = "simulator-model",
                stream = true,
                messages = new[] { new { role = "user", content = "test" } }
            });
        var streaming = await streamingResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, webResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, modelResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, modelsResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, homeResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, completionResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, streamingResponse.StatusCode);
        Assert.Equal(
            "configured",
            ready.RootElement.GetProperty("checks")
                .GetProperty("local-turn-runtime")
                .GetProperty("data")
                .GetProperty("local_provider_configuration")
                .GetString());
        Assert.Equal("simulator-model", models.RootElement.GetProperty("data")[0].GetProperty("id").GetString());
        Assert.Equal(
            "Controlled fixture response",
            completion.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString());
        Assert.Contains("Controlled streaming ", streaming, StringComparison.Ordinal);
        Assert.Contains("\"content\":\"response\"", streaming, StringComparison.Ordinal);
        Assert.EndsWith("data: [DONE]\n\n", streaming, StringComparison.Ordinal);
        Assert.Contains("Running the E2E profile.", page, StringComparison.Ordinal);
        Assert.Equal("controlled fixture response", JsonDocument.Parse(model).RootElement.GetProperty("completion").GetString());
        Assert.Equal(
            "light.simulator_lamp",
            JsonDocument.Parse(home).RootElement[0].GetProperty("entityId").GetString());
    }

    [Fact]
    [Trait("Category", "AspireE2E")]
    public async Task SimulatorDemoMapsDocumentedCommandsToBoundedRegisteredTools()
    {
        var cases = new[]
        {
            ("list household entities", "home.list_entities", "{}"),
            ("read temperature", "home.get_state", """{"entity":"sensor.temperature"}"""),
            ("turn on simulator lamp", "home.set_light", """{"entity":"simulator lamp","on":true}"""),
            ("search memory: curtain", "memory.search", """{"Query":"curtain","Limit":10}"""),
            ("remember: synthetic room | curtain | linen", "memory.propose_change",
                """{"Operation":"create","Subject":"synthetic room","Key":"curtain","Value":"linen"}""")
        };
        foreach (var (command, expectedTool, expectedArguments) in cases)
        {
            using var response = await fixture.ModelClient.PostAsJsonAsync(
                "/v1/chat/completions",
                new
                {
                    model = "simulator-model",
                    messages = new[] { new { role = "user", content = command } }
                });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var call = payload.RootElement.GetProperty("choices")[0]
                .GetProperty("message").GetProperty("tool_calls")[0].GetProperty("function");
            Assert.Equal(expectedTool, call.GetProperty("name").GetString());
            Assert.Equal(
                JsonDocument.Parse(expectedArguments).RootElement.GetRawText(),
                JsonDocument.Parse(call.GetProperty("arguments").GetString()!).RootElement.GetRawText());
        }
    }

    [Fact]
    [Trait("Category", "AspireE2E")]
    public async Task OwnerUiApisExposeOnlyBoundedMemoryApprovalsAndAllowlistedHouseholdStatus()
    {
        using var approvalsResponse = await fixture.WebClient.GetAsync("/api/approvals");
        using var memoryResponse = await fixture.WebClient.GetAsync("/api/memory?limit=10");
        using var householdResponse = await fixture.WebClient.GetAsync("/api/household");
        using var actionsResponse = await fixture.WebClient.GetAsync("/api/actions");
        using var auditResponse = await fixture.WebClient.GetAsync("/api/audit");
        using var approvals = JsonDocument.Parse(await approvalsResponse.Content.ReadAsStringAsync());
        using var memory = JsonDocument.Parse(await memoryResponse.Content.ReadAsStringAsync());
        using var household = JsonDocument.Parse(await householdResponse.Content.ReadAsStringAsync());
        using var actions = JsonDocument.Parse(await actionsResponse.Content.ReadAsStringAsync());
        using var audit = JsonDocument.Parse(await auditResponse.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, approvalsResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, memoryResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, householdResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, actionsResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, auditResponse.StatusCode);
        Assert.Empty(approvals.RootElement.EnumerateArray());
        Assert.Empty(memory.RootElement.EnumerateArray());
        Assert.Empty(actions.RootElement.EnumerateArray());
        Assert.True(audit.RootElement.GetArrayLength() <= 200);
        Assert.True(household.RootElement.GetProperty("configured").GetBoolean());
        Assert.Equal("not-run", household.RootElement.GetProperty("connectionProbe").GetString());
        var entities = household.RootElement.GetProperty("entities").EnumerateArray().ToArray();
        Assert.Contains(entities, entity =>
            entity.GetProperty("entityId").GetString() == "light.simulator_lamp");
        Assert.DoesNotContain(entities, entity =>
            entity.GetProperty("entityId").GetString() == "lock.forbidden");

        using var invalidLimit = await fixture.WebClient.GetAsync("/api/memory?limit=101");
        Assert.Equal(HttpStatusCode.BadRequest, invalidLimit.StatusCode);

        using var invalidDecision = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/approvals/{Guid.NewGuid():D}/decision")
        {
            Content = JsonContent.Create(new { expectedVersion = 1, approved = true })
        };
        invalidDecision.Headers.Add("X-CSRF-TOKEN", "invalid-request-token");
        using var rejectedDecision = await fixture.WebClient.SendAsync(invalidDecision);
        Assert.Equal(HttpStatusCode.BadRequest, rejectedDecision.StatusCode);
    }

    [Fact]
    [Trait("Category", "AspireE2E")]
    public async Task WebChatDispatchesAllowedSimulatedLightThroughProductionComposition()
    {
        var isolated = new SimulatorHostFixture();
        await isolated.InitializeAsync();
        try
        {
            using var initialCounts = JsonDocument.Parse(
                await isolated.HomeAssistantClient.GetStringAsync("/fixture/control/counts"));
            var writesBefore = initialCounts.RootElement.GetProperty("writes").GetInt32();

            using var created = await isolated.WebClient.PostAsJsonAsync("/api/conversations", new { title = "" });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            using var createdBody = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
            var conversationId = createdBody.RootElement.GetProperty("id").GetProperty("value").GetGuid();
            using var toolCall = await isolated.ModelClient.PostAsJsonAsync(
                "/fixture/control/next-tool-call",
                new { name = "home.set_light", arguments = """{"entity":"light.simulator_lamp","on":true}""" });
            Assert.Equal(HttpStatusCode.NoContent, toolCall.StatusCode);
            using var submitted = await isolated.WebClient.PostAsJsonAsync(
                $"/api/conversations/{conversationId:D}/turns",
                new { requestId = Guid.NewGuid().ToString("D"), text = "Turn on simulator lamp" });
            Assert.Equal(HttpStatusCode.Accepted, submitted.StatusCode);
            using var submittedBody = JsonDocument.Parse(await submitted.Content.ReadAsStringAsync());
            var turnId = submittedBody.RootElement.GetProperty("turnId").GetProperty("value").GetGuid();

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            using var eventsResponse = await isolated.WebClient.GetAsync(
                $"/api/turns/{turnId:D}/events", HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            eventsResponse.EnsureSuccessStatusCode();
            var events = await eventsResponse.Content.ReadAsStringAsync(timeout.Token);
            Assert.True(events.Contains("event: TurnCompleted", StringComparison.Ordinal), events);
            Assert.Contains("event: ToolProposed", events, StringComparison.Ordinal);
            Assert.Contains("event: ToolCompleted", events, StringComparison.Ordinal);

            using var actionsResponse = await isolated.WebClient.GetAsync("/api/actions");
            actionsResponse.EnsureSuccessStatusCode();
            using var actions = JsonDocument.Parse(await actionsResponse.Content.ReadAsStringAsync());
            var action = Assert.Single(actions.RootElement.EnumerateArray());
            Assert.Equal("Verified", action.GetProperty("status").GetString());
            Assert.DoesNotContain("synthetic-home-token", action.GetRawText(), StringComparison.Ordinal);
            using var counts = JsonDocument.Parse(
                await isolated.HomeAssistantClient.GetStringAsync("/fixture/control/counts"));
            Assert.Equal(writesBefore + 1, counts.RootElement.GetProperty("writes").GetInt32());
        }
        finally
        {
            await isolated.DisposeAsync();
        }
    }

    [Fact]
    [Trait("Category", "AspireE2E")]
    public async Task WebChatRunsDocumentedSimulatorLightCommandWithoutFixtureInjection()
    {
        var isolated = new SimulatorHostFixture();
        await isolated.InitializeAsync();
        try
        {
            using var before = JsonDocument.Parse(
                await isolated.HomeAssistantClient.GetStringAsync("/fixture/control/counts"));
            var writesBefore = before.RootElement.GetProperty("writes").GetInt32();
            using var created = await isolated.WebClient.PostAsJsonAsync("/api/conversations", new { title = "" });
            created.EnsureSuccessStatusCode();
            using var createdBody = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
            var conversationId = createdBody.RootElement.GetProperty("id").GetProperty("value").GetGuid();
            using var submitted = await isolated.WebClient.PostAsJsonAsync(
                $"/api/conversations/{conversationId:D}/turns",
                new { requestId = Guid.NewGuid().ToString("D"), text = "turn on simulator lamp" });
            Assert.Equal(HttpStatusCode.Accepted, submitted.StatusCode);
            using var submittedBody = JsonDocument.Parse(await submitted.Content.ReadAsStringAsync());
            var turnId = submittedBody.RootElement.GetProperty("turnId").GetProperty("value").GetGuid();

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            using var eventsResponse = await isolated.WebClient.GetAsync(
                $"/api/turns/{turnId:D}/events", HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            eventsResponse.EnsureSuccessStatusCode();
            var events = await eventsResponse.Content.ReadAsStringAsync(timeout.Token);
            using var model = JsonDocument.Parse(await isolated.ModelClient.GetStringAsync("/fixture/model"));
            using var activity = JsonDocument.Parse(await isolated.WebClient.GetStringAsync("/api/activity"));
            Assert.True(events.Contains("event: TurnCompleted", StringComparison.Ordinal),
                $"Events: {events} Model: {model.RootElement.GetRawText()} Activity: {activity.RootElement.GetRawText()}");
            Assert.Contains("event: ToolCompleted", events, StringComparison.Ordinal);

            using var actionsResponse = await isolated.WebClient.GetAsync("/api/actions");
            actionsResponse.EnsureSuccessStatusCode();
            using var actions = JsonDocument.Parse(await actionsResponse.Content.ReadAsStringAsync());
            Assert.Equal("Verified", Assert.Single(actions.RootElement.EnumerateArray())
                .GetProperty("status").GetString());
            using var after = JsonDocument.Parse(
                await isolated.HomeAssistantClient.GetStringAsync("/fixture/control/counts"));
            Assert.Equal(writesBefore + 1, after.RootElement.GetProperty("writes").GetInt32());
        }
        finally
        {
            await isolated.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(65_536, false, HttpStatusCode.Unauthorized)]
    [InlineData(65_537, false, HttpStatusCode.RequestEntityTooLarge)]
    [InlineData(64_000, true, HttpStatusCode.Unauthorized)]
    [InlineData(65_537, true, HttpStatusCode.RequestEntityTooLarge)]
    [Trait("Category", "AspireE2E")]
    public async Task RequestSizeLimitAcceptsValidJsonAndRejectsOversizedBodies(
        int bodyLength, bool chunked, HttpStatusCode expectedStatus)
    {
        const string json = "{\"passphrase\":\"incorrect-owner-passphrase\"}";
        var body = System.Text.Encoding.UTF8.GetBytes(json.PadRight(bodyLength));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = chunked ? new UnknownLengthContent(body) : new ByteArrayContent(body)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.TransferEncodingChunked = chunked;

        using var response = await fixture.WebClient.SendAsync(request);

        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.NotEmpty(response.Headers.GetValues("X-Correlation-ID"));
    }

    private sealed class UnknownLengthContent : HttpContent
    {
        private readonly byte[] bytes;

        public UnknownLengthContent(byte[] bytes)
        {
            this.bytes = bytes;
            Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(bytes, 0, bytes.Length);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
