using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using PersonalAgent.Application;
using PersonalAgent.TestSupport.HomeAssistantFake;

namespace PersonalAgent.SimulatorEndpoints;

/// <summary>Hosts an isolated HA-shaped endpoint, with fixture controls only on explicitly controlled test hosts.</summary>
public sealed class HomeAssistantSimulator(IClock clock)
{
    private readonly FakeHomeAssistantClient fake = new(clock);
    private readonly ConcurrentQueue<int> readFailures = new();
    private int readCount;
    private int writeCount;
    private int writeStatus = 200;

    /// <summary>Gets total GET attempts for request-count assertions.</summary>
    public int ReadCount => Volatile.Read(ref readCount);

    /// <summary>Gets total submitted POST attempts.</summary>
    public int WriteCount => Volatile.Read(ref writeCount);

    /// <summary>Gets or sets a controlled callback at the read boundary.</summary>
    public Func<CancellationToken, Task>? BeforeRead { get; set; }

    /// <summary>Gets or sets a controlled callback after fixture write submission.</summary>
    public Func<CancellationToken, Task>? AfterWrite { get; set; }

    /// <summary>Gets or sets the body for controlled failed reads; never a real credential.</summary>
    public string ErrorBody { get; set; } = "Controlled Home Assistant failure.";

    /// <summary>Gets or sets a replacement successful body for malformed-response tests.</summary>
    public string? ReadBody { get; set; }

    /// <summary>Gets or sets a synthetic redirect location for a failed-read response.</summary>
    public string? RedirectLocation { get; set; }

    /// <summary>Gets or sets how many read responses to abort for real transport-failure tests.</summary>
    public int DropReadResponses { get; set; }

    /// <summary>Gets or sets whether a submitted write loses its response.</summary>
    public bool DropWriteResponses { get; set; }

    /// <summary>Changes only controlled fixture state, without another service request.</summary>
    /// <param name="state">Synthetic state with explicit source time.</param>
    public void SetState(HomeAssistantEntity state) => fake.SetState(state);

    /// <summary>Configures one-shot read failures and write behavior for this instance.</summary>
    /// <param name="failures">HTTP failure codes for successive reads.</param>
    /// <param name="applyWrites">Whether to apply accepted writes.</param>
    /// <param name="writeResponseStatus">Response code after possible application.</param>
    public void Configure(IEnumerable<int> failures, bool applyWrites = true, int writeResponseStatus = 200)
    {
        readFailures.Clear();
        foreach (var status in failures)
        {
            readFailures.Enqueue(status);
        }
        fake.ApplyWrites = applyWrites;
        writeStatus = writeResponseStatus;
    }

    /// <summary>Maps only HA state reads and fixed service calls; no state-update endpoint is provided.</summary>
    /// <param name="app">Controlled standalone simulator host.</param>
    /// <param name="controls">Whether explicit E2E fixture-control routes are available.</param>
    public void Map(WebApplication app, bool controls)
    {
        app.MapGet("/fixture/entities", () => Results.Json(new[]
        {
            new { entityId = "light.simulator_lamp", state = "off", available = true }
        }));
        app.MapGet("/api/states/{entity}", async (string entity, HttpContext context) =>
        {
            Interlocked.Increment(ref readCount);
            if (BeforeRead is not null)
            {
                await BeforeRead(context.RequestAborted);
            }
            if (context.Request.Headers.Authorization != "Bearer synthetic-home-token")
            {
                return Results.Unauthorized();
            }
            if (DropReadResponses > 0)
            {
                DropReadResponses--;
                context.Abort();
                return Results.Empty;
            }
            if (readFailures.TryDequeue(out var status))
            {
                if (RedirectLocation is not null)
                {
                    context.Response.Headers.Location = RedirectLocation;
                }
                return Results.Text(ErrorBody, statusCode: status);
            }
            if (ReadBody is not null)
            {
                return Results.Text(ReadBody, "application/json");
            }
            try
            {
                return Results.Json(WireState(await fake.GetStateAsync(entity, context.RequestAborted)));
            }
            catch (HomeAssistantException)
            {
                return Results.NotFound();
            }
        });
        app.MapPost("/api/services/{domain}/{service}", async (string domain, string service, HttpContext context) =>
        {
            Interlocked.Increment(ref writeCount);
            if (context.Request.Headers.Authorization != "Bearer synthetic-home-token")
            {
                return Results.Unauthorized();
            }
            using var body = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
            var id = body.RootElement.GetProperty("entity_id").GetString()!;
            if (domain == "light" && service is "turn_on" or "turn_off" && id.StartsWith("light.", StringComparison.Ordinal))
            {
                await fake.SetLightAsync(id, service == "turn_on",
                    body.RootElement.TryGetProperty("brightness", out var brightness) ? brightness.GetInt32() : null,
                    context.RequestAborted);
            }
            else if (domain == "climate" && service == "set_temperature" && id.StartsWith("climate.", StringComparison.Ordinal))
            {
                await fake.SetTemperatureAsync(id, body.RootElement.GetProperty("temperature").GetDecimal(), context.RequestAborted);
            }
            else
            {
                return Results.BadRequest();
            }
            if (AfterWrite is not null)
            {
                await AfterWrite(context.RequestAborted);
            }
            if (DropWriteResponses)
            {
                context.Abort();
                return Results.Empty;
            }
            return writeStatus == 200 ? Results.Json(Array.Empty<object>()) : Results.Text(ErrorBody, statusCode: writeStatus);
        });
        if (controls)
        {
            app.MapGet("/fixture/control/counts", () => Results.Json(new { reads = ReadCount, writes = WriteCount }));
            app.MapPost("/fixture/control/configure", (FixtureControl control) =>
            {
                Configure(control.ReadFailures, control.ApplyWrites, control.WriteStatus);
                return Results.NoContent();
            });
        }
    }

    private static object WireState(HomeAssistantEntity state) => new
    {
        entity_id = state.EntityId,
        state = state.State,
        last_updated = state.ObservedAtUtc,
        last_changed = state.ObservedAtUtc,
        attributes = new { brightness = state.Brightness, temperature = state.TargetTemperature }
    };

    private sealed record FixtureControl(int[] ReadFailures, bool ApplyWrites = true, int WriteStatus = 200);
}
