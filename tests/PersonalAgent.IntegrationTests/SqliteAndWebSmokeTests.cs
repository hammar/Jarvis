using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PersonalAgent.Application;
using PersonalAgent.Application.Context;
using PersonalAgent.Application.Routing;
using PersonalAgent.Infrastructure.Persistence;
using PersonalAgent.TestSupport;
using Xunit;

namespace PersonalAgent.IntegrationTests;

public sealed class SqliteAndWebSmokeTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task SqlitePersistsDataInAnIsolatedTestDatabase()
    {
        using var data = IsolatedDirectory.Create();
        var databasePath = Path.Combine(data.Path, "test.db");
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath
        }.ToString();

        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE fixture (value TEXT NOT NULL); INSERT INTO fixture VALUES ('isolated');";
            await command.ExecuteNonQueryAsync();
        }

        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM fixture;";

            Assert.Equal("isolated", await command.ExecuteScalarAsync());
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RazorHostServesTheConfiguredProfileWithoutExternalServices()
    {
        using var data = IsolatedDirectory.Create();
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(web =>
        {
            web.UseSetting("JARVIS_PROFILE", "Local");
            web.UseSetting("JARVIS_DATA_DIR", data.Path);
        });
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/");
        var html = await response.Content.ReadAsStringAsync();

        response.EnsureSuccessStatusCode();
        Assert.Contains("Running the Local profile.", html, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RazorHostUsesLocalProfileWhenNoProfileIsConfigured()
    {
        using var data = IsolatedDirectory.Create();
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(web =>
            web.UseSetting("JARVIS_DATA_DIR", data.Path));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/");
        var html = await response.Content.ReadAsStringAsync();

        response.EnsureSuccessStatusCode();
        Assert.Contains("Running the Local profile.", html, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ProductionCompositionRegistersApplicationOwnedRoutingAndContextPolicies()
    {
        using var data = IsolatedDirectory.Create();
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(web =>
            web.UseSetting("JARVIS_DATA_DIR", data.Path));

        Assert.IsType<LocalOnlyModelRouter>(factory.Services.GetRequiredService<IModelRouter>());
        Assert.IsType<ConversationContextBuilder>(factory.Services.GetRequiredService<IContextBuilder>());
    }

    [Theory]
    [InlineData("Simulator")]
    [InlineData("E2E")]
    [Trait("Category", "Integration")]
    public void TestProfileRequiresAnExplicitDataDirectory(string profile)
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(web =>
        {
            web.UseSetting("JARVIS_PROFILE", profile);
            web.UseSetting("JARVIS_DATA_DIR", string.Empty);
        });

        var exception = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
        Assert.Contains("requires an explicit isolated JARVIS_DATA_DIR", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("simulator")]
    [InlineData("Unknown")]
    [Trait("Category", "Integration")]
    public void DirectHostRejectsUnsupportedProfilesBeforeOpeningStorage(string profile)
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(web =>
            web.UseSetting("JARVIS_PROFILE", profile));

        var exception = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
        Assert.Contains("JARVIS_PROFILE must be Simulator, Local, Hybrid, or E2E", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task DevelopmentHealthEndpointsAreNotMappedInProduction()
    {
        using var data = IsolatedDirectory.Create();
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(web =>
        {
            web.UseEnvironment("Production");
            web.UseSetting("JARVIS_DATA_DIR", data.Path);
        });
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health");

        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task DevelopmentHealthEndpointsReportReadinessAndLiveness()
    {
        using var data = IsolatedDirectory.Create();
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(web =>
        {
            web.UseEnvironment("Development");
            web.UseSetting("JARVIS_DATA_DIR", data.Path);
        });
        using var client = factory.CreateClient();

        using var readiness = await client.GetAsync("/health");
        using var liveness = await client.GetAsync("/alive");

        Assert.Equal(System.Net.HttpStatusCode.OK, readiness.StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.OK, liveness.StatusCode);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void OtlpExportIsRegisteredOnlyForAnExplicitEndpoint()
    {
        var builder = Host.CreateApplicationBuilder();
        var baselineCount = builder.Services.Count;
        builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"] = "http://127.0.0.1:4318";

        builder.ConfigureOpenTelemetry();

        Assert.True(builder.Services.Count > baselineCount);
    }
}
