using System.Globalization;
using PersonalAgent.Application;
using PersonalAgent.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddRazorPages();
builder.Services.AddHealthChecks();

var dataDirectory = SqliteDataDirectory.Resolve(
    builder.Configuration["JARVIS_DATA_DIR"],
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

var database = new SqliteDatabase(Path.Combine(dataDirectory, "jarvis.db"));
await database.InitializeAsync();
var clock = new SystemClock();
var retentionOptions = new SqliteRetentionOptions(
    ReadRetentionDays(builder.Configuration, "JARVIS_CONVERSATION_RETENTION_DAYS", 90),
    ReadRetentionDays(builder.Configuration, "JARVIS_AUDIT_RETENTION_DAYS", 30));
retentionOptions.Validate();
await new SqliteRetentionService(database, clock, retentionOptions).CleanupExpiredAsync(CancellationToken.None);

builder.Services.AddSingleton(database);
builder.Services.AddSingleton<IClock>(clock);
builder.Services.AddSingleton(retentionOptions);
builder.Services.AddSingleton<IConversationStore, SqliteConversationStore>();
builder.Services.AddSingleton<IMemoryStore, SqliteMemoryStore>();
builder.Services.AddSingleton<IJobStore, SqliteJobStore>();
builder.Services.AddSingleton<IActionJournalStore, SqliteActionJournalStore>();
builder.Services.AddSingleton<IApprovalStore, SqliteApprovalStore>();
builder.Services.AddSingleton<IAuditStore, SqliteAuditStore>();
builder.Services.AddSingleton<SqliteBackupRestoreService>();

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

static int ReadRetentionDays(Microsoft.Extensions.Configuration.IConfiguration configuration, string key, int defaultValue)
{
    var configuredValue = configuration[key];
    if (configuredValue is null)
    {
        return defaultValue;
    }

    if (int.TryParse(configuredValue, NumberStyles.None, CultureInfo.InvariantCulture, out var days))
    {
        return days;
    }

    throw new InvalidOperationException($"{key} must be an integer number of days.");
}

/// <summary>Exposes the generated entry point to in-process ASP.NET Core integration tests.</summary>
public partial class Program
{
}
