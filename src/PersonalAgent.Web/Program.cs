var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddRazorPages();
builder.Services.AddHealthChecks();

var app = builder.Build();

app.UseStaticFiles();
app.UseRouting();
app.MapRazorPages();
app.MapHealthChecks("/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = _ => false
});
app.MapHealthChecks("/health/ready");
app.MapDefaultEndpoints();

app.Run();

/// <summary>Exposes the generated entry point to in-process ASP.NET Core integration tests.</summary>
public partial class Program
{
}
