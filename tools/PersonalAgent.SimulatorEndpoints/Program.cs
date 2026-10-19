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
var delayNextStreamFrame = 0;
var failStreamResponses = 0;
var nextToolCalls = new ConcurrentQueue<SimulatorToolCall>();
var demoEnabled = builder.Configuration.GetValue<bool>("JARVIS_SIMULATOR_DEMO_ENABLED");
var demoToolCallCount = 0;
var delayedStreamStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var firstDelayedFrameWritten = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
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
        app.MapPost("/fixture/control/delay-after-first-frame", () =>
        {
            Interlocked.Exchange(ref delayNextStreamFrame, 1);
            return Results.NoContent();
        });
        app.MapGet("/fixture/control/wait-for-first-frame", async (HttpContext context) =>
        {
            await firstDelayedFrameWritten.Task.WaitAsync(TimeSpan.FromSeconds(15), context.RequestAborted);
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

            await WriteStreamingCompletionAsync(context.Response, context.RequestAborted);
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
                    message = new { role = "assistant", content = "Controlled fixture response" },
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

async Task WriteStreamingCompletionAsync(HttpResponse response, CancellationToken cancellationToken)
{
    var frames = new[]
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
                    delta = new { role = "assistant", content = "Controlled streaming " },
                    finish_reason = (string?)null
                }
            }
        }),
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
                    delta = new { content = "response" },
                    finish_reason = (string?)null
                }
            }
        }),
        JsonSerializer.Serialize(new
        {
            id = "chatcmpl-simulator",
            @object = "chat.completion.chunk",
            created = 0,
            model = "simulator-model",
            choices = new[] { new { index = 0, delta = new { }, finish_reason = "stop" } }
        }),
        "[DONE]"
    };

    foreach (var frame in frames)
    {
        await response.WriteAsync($"data: {frame}\n\n", cancellationToken);
        await response.Body.FlushAsync(cancellationToken);
        if (frame == frames[0] && Interlocked.Exchange(ref delayNextStreamFrame, 0) == 1)
        {
            firstDelayedFrameWritten.TrySetResult();
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
    var lastUserMessage = lastContent.GetString();
    if (string.IsNullOrWhiteSpace(lastUserMessage))
    {
        return null;
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

internal sealed class SimulatorClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

internal sealed record SimulatorToolCall(string Name, string Arguments);
