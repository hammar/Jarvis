using System.Text.Json;

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
        completion = "controlled fixture response"
    }));
}
else
{
    app.MapGet("/fixture/entities", () => Results.Json(new[]
    {
        new { entityId = "light.simulator_lamp", state = "off", available = true }
    }));
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
