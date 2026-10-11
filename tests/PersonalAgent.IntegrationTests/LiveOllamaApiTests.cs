using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using PersonalAgent.TestSupport;
using PersonalAgent.Web;
using Xunit;

namespace PersonalAgent.IntegrationTests;

[Collection("Copilot runtime process isolation")]
public sealed class LiveOllamaApiTests
{
    [LiveOllamaFact]
    [Trait("Category", "LiveOllama")]
    public async Task AuthenticatedLocalHttpAndSseUseTheCorrectPromptAndPersistSuccessiveAnswers()
    {
        var model = Environment.GetEnvironmentVariable("JARVIS_OLLAMA_MODEL");
        Assert.False(string.IsNullOrWhiteSpace(model), "Set JARVIS_OLLAMA_MODEL to opt into synthetic live HTTP checks.");
        using var data = IsolatedDirectory.Create();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(web =>
        {
            web.UseSetting("JARVIS_PROFILE", "Local");
            web.UseSetting("JARVIS_DATA_DIR", data.Path);
            web.UseSetting("JARVIS_OLLAMA_BASE_URL", "http://127.0.0.1:11434");
            web.UseSetting("JARVIS_OLLAMA_MODEL", model);
        });
        using var client = factory.CreateClient();
        await SetCsrfAsync();
        using var bootstrap = await client.PostAsJsonAsync("/api/auth/bootstrap", new
        {
            bootstrapToken = factory.Services.GetRequiredService<OwnerAuthenticationService>().BootstrapToken,
            passphrase = "synthetic-live-test-passphrase",
            rememberMe = false
        }, deadline.Token);
        bootstrap.EnsureSuccessStatusCode();
        await SetCsrfAsync();
        using var created = await client.PostAsJsonAsync("/api/conversations", new { title = "Synthetic live arithmetic" }, deadline.Token);
        created.EnsureSuccessStatusCode();
        using var conversation = JsonDocument.Parse(await created.Content.ReadAsStringAsync(deadline.Token));
        var id = conversation.RootElement.GetProperty("id").GetProperty("value").GetGuid();
        var expectedAnswers = new List<string>();
        foreach (var (question, expected) in new[] { ("7 times 9", "63"), ("5 times 3", "15"), ("2 times 4", "8") })
        {
            using var submitted = await client.PostAsJsonAsync($"/api/conversations/{id:D}/turns",
                new { requestId = Guid.NewGuid().ToString("N"), text = $"What is {question}? Reply with just the number." }, deadline.Token);
            submitted.EnsureSuccessStatusCode();
            using var turn = JsonDocument.Parse(await submitted.Content.ReadAsStringAsync(deadline.Token));
            var turnId = turn.RootElement.GetProperty("turnId").GetProperty("value").GetGuid();
            using var stream = await client.GetAsync($"/api/turns/{turnId:D}/events", deadline.Token);
            stream.EnsureSuccessStatusCode();
            var events = await stream.Content.ReadAsStringAsync(deadline.Token);
            Assert.Contains("event: TurnCompleted", events, StringComparison.Ordinal);
            Assert.DoesNotContain("event: TurnFailed", events, StringComparison.Ordinal);
            using var saved = await client.GetAsync($"/api/conversations/{id:D}", deadline.Token);
            saved.EnsureSuccessStatusCode();
            using var history = JsonDocument.Parse(await saved.Content.ReadAsStringAsync(deadline.Token));
            expectedAnswers.Add(expected);
            Assert.Equal(expectedAnswers, history.RootElement.GetProperty("messages").EnumerateArray()
                .Where(message => message.GetProperty("role").GetString() == "assistant")
                .Select(message => message.GetProperty("content").GetString()!.Trim()));
        }

        async Task SetCsrfAsync()
        {
            using var response = await client.GetAsync("/api/auth/csrf", deadline.Token);
            response.EnsureSuccessStatusCode();
            using var token = JsonDocument.Parse(await response.Content.ReadAsStringAsync(deadline.Token));
            client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
            client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", token.RootElement.GetProperty("token").GetString());
        }

    }

    private sealed class LiveOllamaFactAttribute : FactAttribute
    {
        public LiveOllamaFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("JARVIS_OLLAMA_MODEL")))
            {
                Skip = "Opt-in live evaluation requires JARVIS_OLLAMA_MODEL.";
            }
        }
    }
}
