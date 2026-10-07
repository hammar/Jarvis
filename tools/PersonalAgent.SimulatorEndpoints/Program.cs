using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var kind = builder.Configuration["JARVIS_SIMULATOR_KIND"];
if (kind is not ("model" or "home-assistant"))
{
    throw new InvalidOperationException("JARVIS_SIMULATOR_KIND must be model or home-assistant.");
}

app.MapGet("/health/live", () => Results.Ok());
app.MapGet("/health/ready", () => Results.Ok(new { kind }));

if (kind == "model")
{
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
            context.Response.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache";
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

static async Task WriteStreamingCompletionAsync(HttpResponse response, CancellationToken cancellationToken)
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
                    delta = new { role = "assistant", content = "Controlled fixture response" },
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
    }
}
