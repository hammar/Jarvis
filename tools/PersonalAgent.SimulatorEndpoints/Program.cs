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
