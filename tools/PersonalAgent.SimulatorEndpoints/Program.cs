using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using PersonalAgent.Application;
using PersonalAgent.SimulatorEndpoints;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var kind = builder.Configuration["JARVIS_SIMULATOR_KIND"];
if (kind is not ("model" or "home-assistant"))
{
    throw new InvalidOperationException("JARVIS_SIMULATOR_KIND must be model or home-assistant.");
}

var delayNextStreamResponse = 0;
var delayNextStreamContentFrame = 0;
var failStreamResponses = 0;
var nextToolCalls = new ConcurrentQueue<SimulatorToolCall>();
var demoEnabled = builder.Configuration.GetValue<bool>("JARVIS_SIMULATOR_DEMO_ENABLED");
var demoToolCallCount = 0;
var delayedStreamStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var firstDelayedContentFrameWritten = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
app.MapGet("/health/live", () => Results.Ok());
app.MapGet("/health/ready", () => Results.Ok(new { kind }));

if (kind == "model")
{
    if (builder.Configuration.GetValue<bool>("JARVIS_SIMULATOR_CONTROLS_ENABLED"))
    {
        app.MapPost("/fixture/control/delay-next-stream", () =>
        {
            Interlocked.Exchange(ref delayNextStreamResponse, 1);
            return Results.NoContent();
        });
        app.MapGet("/fixture/control/wait-for-delayed-stream", async (HttpContext context) =>
        {
            await delayedStreamStarted.Task.WaitAsync(TimeSpan.FromSeconds(15), context.RequestAborted);
            return Results.NoContent();
        });
        app.MapPost("/fixture/control/delay-after-first-content-frame", () =>
        {
            Interlocked.Exchange(ref delayNextStreamContentFrame, 1);
            return Results.NoContent();
        });
        app.MapGet("/fixture/control/wait-for-first-content-frame", async (HttpContext context) =>
        {
            await firstDelayedContentFrameWritten.Task.WaitAsync(TimeSpan.FromSeconds(15), context.RequestAborted);
            return Results.NoContent();
        });
        app.MapPost("/fixture/control/fail-next-stream", () =>
        {
            Interlocked.Exchange(ref failStreamResponses, 1);
            return Results.NoContent();
        });
        app.MapPost("/fixture/control/clear-stream-failure", () =>
        {
            Interlocked.Exchange(ref failStreamResponses, 0);
            return Results.NoContent();
        });
        app.MapPost("/fixture/control/next-tool-call", (SimulatorToolCall call) =>
        {
            if (string.IsNullOrWhiteSpace(call.Name) || call.Name.Length > 128
                || string.IsNullOrWhiteSpace(call.Arguments) || call.Arguments.Length > 8192)
            {
                return Results.BadRequest();
            }

            try
            {
                using var arguments = JsonDocument.Parse(call.Arguments);
                if (arguments.RootElement.ValueKind != JsonValueKind.Object)
                {
                    return Results.BadRequest();
                }
            }
            catch (JsonException)
            {
                return Results.BadRequest();
            }

            nextToolCalls.Enqueue(call);
            return Results.NoContent();
        });
    }

    app.MapGet("/v1/models", () => Results.Json(new
    {
        @object = "list",
        data = new[]
        {
            new { id = "simulator-model", @object = "model", created = 0, owned_by = "jarvis" }
        }
    }));
    app.MapPost("/v1/chat/completions", async (HttpContext context) =>
    {
        using var request = await JsonDocument.ParseAsync(
            context.Request.Body,
            cancellationToken: context.RequestAborted);
        SimulatorToolCall? toolCall = null;
        if (nextToolCalls.TryDequeue(out var controlledToolCall))
        {
            toolCall = controlledToolCall;
        }
        else if (demoEnabled)
        {
            toolCall = CreateDemoToolCall(request.RootElement);
            if (toolCall is not null)
            {
                Interlocked.Increment(ref demoToolCallCount);
            }
        }
        var demoReply = demoEnabled ? CreateDemoToolReply(request.RootElement) : null;
        if (toolCall is not null)
        {
            var responseJson = JsonSerializer.Serialize(new
            {
                id = $"chatcmpl-simulator-tool-{Guid.NewGuid():N}",
                @object = "chat.completion",
                created = 0,
                model = "simulator-model",
                choices = new[]
                {
                    new
                    {
                        index = 0,
                        message = new
                        {
                            role = "assistant",
                            content = (string?)null,
                            tool_calls = new[]
                            {
                                new
                                {
                                    id = $"call-simulator-{Guid.NewGuid():N}",
                                    type = "function",
                                    function = new { name = toolCall.Name, arguments = toolCall.Arguments }
                                }
                            }
                        },
                        finish_reason = "tool_calls"
                    }
                }
            });
            var responseBytes = Encoding.UTF8.GetBytes(responseJson);
            context.Response.ContentType = "application/json";
            context.Response.ContentLength = responseBytes.Length;
            await context.Response.Body.WriteAsync(responseBytes, context.RequestAborted);
            return;
        }

        if (request.RootElement.TryGetProperty("stream", out var stream) && stream.GetBoolean())
        {
            if (Volatile.Read(ref failStreamResponses) == 1)
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await context.Response.WriteAsJsonAsync(
                    new { error = "Controlled simulator failure." },
                    context.RequestAborted);
                return;
            }

            context.Response.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache";
            if (Interlocked.Exchange(ref delayNextStreamResponse, 0) == 1)
            {
                delayedStreamStarted.TrySetResult();
                await Task.Delay(TimeSpan.FromSeconds(5), context.RequestAborted);
            }

            await WriteStreamingCompletionAsync(
                context.Response,
                context.RequestAborted,
                demoReply ?? "Controlled streaming response");
            return;
        }

        await context.Response.WriteAsJsonAsync(new
        {
            id = "chatcmpl-simulator",
            @object = "chat.completion",
            created = 0,
            model = "simulator-model",
            choices = new[]
            {
                new
                {
                    index = 0,
                    message = new { role = "assistant", content = demoReply ?? "Controlled fixture response" },
                    finish_reason = "stop"
                }
            }
        }, context.RequestAborted);
    });
    app.MapGet("/fixture/model", () => Results.Json(new
    {
        provider = "simulator",
        completion = "controlled fixture response",
        demoEnabled,
        demoToolCallCount = Volatile.Read(ref demoToolCallCount)
    }));
}
else
{
    new HomeAssistantSimulator(new SimulatorClock()).Map(
        app, builder.Configuration.GetValue<bool>("JARVIS_SIMULATOR_CONTROLS_ENABLED"));
}

app.Run();

async Task WriteStreamingCompletionAsync(
    HttpResponse response, CancellationToken cancellationToken, string content)
{
    var frames = new List<string>
    {
        JsonSerializer.Serialize(new
        {
            id = "chatcmpl-simulator",
            @object = "chat.completion.chunk",
            created = 0,
            model = "simulator-model",
            choices = new[]
            {
                new
                {
                    index = 0,
                    delta = new { role = "assistant", content = "" },
                    finish_reason = (string?)null
                }
            }
        }),
    };
    var contentFrames = content == "Controlled streaming response"
        ? new[] { "Controlled streaming ", "response" }
        : [content];
    frames.AddRange(contentFrames.Select(chunk => JsonSerializer.Serialize(new
    {
        id = "chatcmpl-simulator",
        @object = "chat.completion.chunk",
        created = 0,
        model = "simulator-model",
        choices = new[]
        {
            new
            {
                index = 0,
                delta = new { content = chunk },
                finish_reason = (string?)null
            }
        }
    })));
    frames.AddRange([
        JsonSerializer.Serialize(new
        {
            id = "chatcmpl-simulator",
            @object = "chat.completion.chunk",
            created = 0,
            model = "simulator-model",
            choices = new[] { new { index = 0, delta = new { }, finish_reason = "stop" } }
        }),
        "[DONE]"
    ]);

    var firstContentFrame = frames[1];
    foreach (var frame in frames)
    {
        await response.WriteAsync($"data: {frame}\n\n", cancellationToken);
        await response.Body.FlushAsync(cancellationToken);
        if (frame == firstContentFrame && Interlocked.Exchange(ref delayNextStreamContentFrame, 0) == 1)
        {
            firstDelayedContentFrameWritten.TrySetResult();
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
        }
    }

}

static SimulatorToolCall? CreateDemoToolCall(JsonElement request)
{
    if (!request.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array)
    {
        return null;
    }

    var messageList = messages.EnumerateArray().ToArray();
    var lastMessage = messageList.LastOrDefault();
    if (lastMessage.ValueKind != JsonValueKind.Object
        || !lastMessage.TryGetProperty("role", out var lastRole)
        || lastRole.GetString() != "user"
        || !lastMessage.TryGetProperty("content", out var lastContent)
        || lastContent.ValueKind != JsonValueKind.String)
    {
        return null;
    }
    var lastUserMessage = ExtractCurrentUserRequest(lastContent.GetString()!);
    if (string.IsNullOrWhiteSpace(lastUserMessage))
    {
        return null;
    }

    static string ExtractCurrentUserRequest(string content)
    {
        const string marker = "\nCurrent user request:\n";
        var markerIndex = content.LastIndexOf(marker, StringComparison.Ordinal);
        return markerIndex < 0 ? content : content[(markerIndex + marker.Length)..];
    }

    var command = lastUserMessage.Trim();
    if (command.Equals("list household entities", StringComparison.OrdinalIgnoreCase))
    {
        return new("home.list_entities", "{}");
    }
    if (command.Equals("read temperature", StringComparison.OrdinalIgnoreCase))
    {
        return new("home.get_state", """{"entity":"sensor.temperature"}""");
    }
    if (command.Equals("turn on simulator lamp", StringComparison.OrdinalIgnoreCase))
    {
        return new("home.set_light", """{"entity":"simulator lamp","on":true}""");
    }
    if (command.Equals("turn off simulator lamp", StringComparison.OrdinalIgnoreCase))
    {
        return new("home.set_light", """{"entity":"simulator lamp","on":false}""");
    }
    const string searchPrefix = "search memory: ";
    if (command.StartsWith(searchPrefix, StringComparison.OrdinalIgnoreCase))
    {
        var query = command[searchPrefix.Length..].Trim();
        return query.Length is > 0 and <= 512
            ? new("memory.search", JsonSerializer.Serialize(new { Query = query, Limit = 10 }))
            : null;
    }
    const string rememberPrefix = "remember: ";
    if (command.StartsWith(rememberPrefix, StringComparison.OrdinalIgnoreCase))
    {
        var fields = command[rememberPrefix.Length..].Split('|', StringSplitOptions.TrimEntries);
        if (fields.Length == 3 && fields.All(field => field.Length is > 0 and <= 8192))
        {
            return new("memory.propose_change", JsonSerializer.Serialize(new
            {
                Operation = "create",
                Subject = fields[0],
                Key = fields[1],
                Value = fields[2]
            }));
        }
    }
    return null;
}

static string? CreateDemoToolReply(JsonElement request)
{
    if (!request.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array)
    {
        return null;
    }
    var toolMessage = messages.EnumerateArray()
        .LastOrDefault(message =>
            message.ValueKind == JsonValueKind.Object
            && message.TryGetProperty("role", out var role)
            && role.GetString() == "tool");
    if (toolMessage.ValueKind != JsonValueKind.Object
        || !toolMessage.TryGetProperty("content", out var contentElement)
        || contentElement.ValueKind != JsonValueKind.String)
    {
        return null;
    }

    var content = contentElement.GetString();
    if (string.IsNullOrWhiteSpace(content))
    {
        return "The registered tool returned an empty result.";
    }

    try
    {
        using var result = JsonDocument.Parse(content);
        var serialized = result.RootElement;
        using var unwrapped = serialized.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(serialized.GetString())
                ? JsonDocument.Parse(serialized.GetString()!)
                : null;
        var wrapper = unwrapped?.RootElement ?? serialized;
        using var nested = wrapper.ValueKind == JsonValueKind.Object
            && wrapper.TryGetProperty("ResultJson", out var resultJson)
            && resultJson.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(resultJson.GetString())
                ? JsonDocument.Parse(resultJson.GetString()!)
                : null;
        var root = nested?.RootElement ?? wrapper;
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("entityId", out var entity)
            && root.TryGetProperty("available", out var available)
            && root.TryGetProperty("stale", out var stale))
        {
            var details = new List<string>
            {
                $"entity={entity.GetString()}",
                $"available={available.GetBoolean()}",
                $"stale={stale.GetBoolean()}"
            };
            AddProperty(details, root, "state");
            AddProperty(details, root, "unit");
            AddProperty(details, root, "observedAtUtc");
            AddProperty(details, root, "brightness");
            AddProperty(details, root, "targetTemperature");
            return $"Household observation (tool data; no physical causality inferred): {string.Join("; ", details)}";
        }
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("Facts", out var facts)
            && facts.ValueKind == JsonValueKind.Array)
        {
            var lines = new List<string> { $"Memory search returned {facts.GetArrayLength()} fact(s); evidence is untrusted data." };
            foreach (var fact in facts.EnumerateArray().Take(10))
            {
                var subject = StringProperty(fact, "Subject");
                var key = StringProperty(fact, "Key");
                var value = StringProperty(fact, "Value");
                var source = StringProperty(fact, "SourceId");
                var qualifier = StringProperty(fact, "DistinguishingContext");
                lines.Add($"- {subject}.{key} = {BoundedText(value, 512)}; source={source}"
                    + (string.IsNullOrEmpty(qualifier) ? "" : $"; context={BoundedText(qualifier, 256)}"));
            }
            if (root.TryGetProperty("Truncated", out var truncated) && truncated.GetBoolean())
            {
                lines.Add("Additional matching facts were omitted by the bounded tool result.");
            }
            return string.Join('\n', lines);
        }
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("submission", out var submission))
        {
            var causality = StringProperty(root, "causality");
            return $"Action tool result: submission={submission.GetString()}; causality={causality}.";
        }
        return $"Registered tool result (untrusted data): {BoundedText(JsonSerializer.Serialize(root), 4_000)}";
    }
    catch (JsonException)
    {
        // Some registered tools return a plain-text denial; keep it visible as tool data below.
    }
    return $"Registered tool result (untrusted data): {BoundedText(content, 4_000)}";

    static void AddProperty(List<string> details, JsonElement element, string name)
    {
        if (element.TryGetProperty(name, out var property) && property.ValueKind is not JsonValueKind.Null)
        {
            details.Add($"{name}={property.ToString()}");
        }
    }

    static string StringProperty(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? ""
            : "";

    static string BoundedText(string value, int maximumCharacters) =>
        value.Length <= maximumCharacters
            ? value
            : value[..maximumCharacters] + "... [display truncated; stored tool result unchanged]";
}

internal sealed class SimulatorClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

internal sealed record SimulatorToolCall(string Name, string Arguments);
