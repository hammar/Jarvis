using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using PersonalAgent.Application;
using PersonalAgent.Domain;
using PersonalAgent.TestSupport;
using PersonalAgent.Web;
using Xunit;

namespace PersonalAgent.IntegrationTests;

[Collection("Copilot runtime process isolation")]
public sealed class WebProblemDetailsTests
{
    [Theory]
    [InlineData("/api/conversations")]
    [InlineData("/health/ready")]
    [InlineData("/api/turns/00000000-0000-0000-0000-000000000001/events")]
    [Trait("Category", "Integration")]
    public async Task CookieChallengeReturnsCorrelatedUnauthorizedProblem(string path)
    {
        using var data = IsolatedDirectory.Create();
        await using var factory = CreateFactory(data.Path);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Correlation-ID", "untrusted-client-correlation");
        client.DefaultRequestHeaders.Accept.ParseAdd("text/event-stream");
        using var response = await client.GetAsync(path);
        await AssertProblemAsync(response, HttpStatusCode.Unauthorized, "Unauthorized");
        Assert.Null(response.Headers.Location);
        Assert.NotEqual("untrusted-client-correlation", Assert.Single(response.Headers.GetValues("X-Correlation-ID")));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task AuthenticatedCookieForbidReturnsCorrelatedForbiddenProblem()
    {
        using var data = IsolatedDirectory.Create();
        await using var factory = CreateFactory(data.Path).WithWebHostBuilder(web =>
            web.ConfigureTestServices(services => services.Configure<AuthorizationOptions>(options =>
                options.DefaultPolicy = new AuthorizationPolicyBuilder()
                    .RequireAuthenticatedUser()
                    .RequireClaim("controlled-test-permission")
                    .Build())));
        using var client = factory.CreateClient();
        await BootstrapAsync(factory, client);
        using var response = await client.GetAsync("/api/conversations");
        await AssertProblemAsync(response, HttpStatusCode.Forbidden, "Forbidden");
        Assert.Null(response.Headers.Location);
    }

    [Theory]
    [InlineData("lookup", false)]
    [InlineData("submission", false)]
    [InlineData("cancel", false)]
    [InlineData("events", false)]
    [InlineData("lookup", true)]
    [InlineData("submission", true)]
    [InlineData("cancel", true)]
    [InlineData("events", true)]
    [Trait("Category", "Integration")]
    public async Task MissingAndOtherOwnerObjectsReturnIndistinguishableCorrelatedProblems(string operation, bool otherOwner)
    {
        using var data = IsolatedDirectory.Create();
        await using var factory = CreateFactory(data.Path);
        using var client = factory.CreateClient();
        await BootstrapAsync(factory, client);
        var conversationId = ConversationId.New();
        var turnId = TurnId.New();
        if (otherOwner)
        {
            var store = factory.Services.GetRequiredService<IConversationStore>();
            var now = factory.Services.GetRequiredService<IClock>().UtcNow;
            await store.CreateConversationAsync(
                new ConversationRecord(conversationId, "another-owner", "private-other-owner-title", now, now),
                CancellationToken.None);
            await store.CreateTurnAsync(turnId, conversationId, TurnStatus.Completed, now, CancellationToken.None);
        }

        using var request = operation switch
        {
            "lookup" => new HttpRequestMessage(HttpMethod.Get, $"/api/conversations/{conversationId.Value:D}"),
            "submission" => new HttpRequestMessage(HttpMethod.Post, $"/api/conversations/{conversationId.Value:D}/turns")
            {
                Content = JsonContent.Create(new { requestId = "missing-object", text = "private-request-text" })
            },
            "cancel" => new HttpRequestMessage(HttpMethod.Post, $"/api/turns/{turnId.Value:D}/cancel"),
            _ => new HttpRequestMessage(HttpMethod.Get, $"/api/turns/{turnId.Value:D}/events")
        };
        if (operation == "events")
        {
            request.Headers.Accept.ParseAdd("text/event-stream");
        }
        using var response = await client.SendAsync(request);
        await AssertProblemAsync(response, HttpStatusCode.NotFound, "Not Found");
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("-1")]
    [InlineData("9223372036854775808")]
    [Trait("Category", "Integration")]
    public async Task MalformedSseCursorReturnsProblemBeforeStartingTheStream(string cursor)
    {
        using var data = IsolatedDirectory.Create();
        await using var factory = CreateFactory(data.Path);
        using var client = factory.CreateClient();
        await BootstrapAsync(factory, client);
        var store = factory.Services.GetRequiredService<IConversationStore>();
        var now = factory.Services.GetRequiredService<IClock>().UtcNow;
        var conversationId = ConversationId.New();
        var turnId = TurnId.New();
        await store.CreateConversationAsync(
            new ConversationRecord(conversationId, "owner", "Cursor test", now, now), CancellationToken.None);
        await store.CreateTurnAsync(turnId, conversationId, TurnStatus.Completed, now, CancellationToken.None);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/turns/{turnId.Value:D}/events");
        request.Headers.Accept.ParseAdd("text/event-stream");
        request.Headers.Add("Last-Event-ID", cursor);
        using var response = await client.SendAsync(request);
        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "Bad Request");
        Assert.False(response.Headers.Contains("X-Accel-Buffering"));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ConflictAndExplicitValidationKeepSafeTitlesWhileSuccessAndSseAreNotRewritten()
    {
        using var data = IsolatedDirectory.Create();
        await using var factory = CreateFactory(data.Path);
        using var client = factory.CreateClient();
        await BootstrapAsync(factory, client);
        using var created = await client.PostAsJsonAsync("/api/conversations", new { title = "Problem test" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("application/json", created.Content.Headers.ContentType?.MediaType);
        using var conversation = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var id = conversation.RootElement.GetProperty("id").GetProperty("value").GetGuid();
        var path = $"/api/conversations/{id:D}/turns";
        using var accepted = await client.PostAsJsonAsync(path, new { requestId = "conflict-test", text = "Original text" });
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        using var turn = JsonDocument.Parse(await accepted.Content.ReadAsStringAsync());
        var turnId = turn.RootElement.GetProperty("turnId").GetProperty("value").GetGuid();
        client.DefaultRequestHeaders.Accept.ParseAdd("text/html");
        using var conflict = await client.PostAsJsonAsync(path, new { requestId = "conflict-test", text = "private-request-text" });
        await AssertProblemAsync(conflict, HttpStatusCode.Conflict, "Request ID was already used for different content.");

        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        using var csrf = await client.PostAsJsonAsync(path, new { requestId = "no-csrf", text = "private-request-text" });
        await AssertProblemAsync(csrf, HttpStatusCode.BadRequest, "Invalid request token.");

        using var stream = await client.GetAsync($"/api/turns/{turnId:D}/events");
        Assert.Equal(HttpStatusCode.OK, stream.StatusCode);
        Assert.Equal("text/event-stream", stream.Content.Headers.ContentType?.MediaType);
        var events = await stream.Content.ReadAsStringAsync();
        Assert.Contains("event: TurnFailed", events, StringComparison.Ordinal);
        Assert.DoesNotContain("application/problem+json", events, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Production", "Bad Request")]
    [InlineData("Development", "The request could not be read.")]
    [Trait("Category", "Integration")]
    public async Task FrameworkBodyBindingFailureReturnsSafeCorrelatedProblem(string environment, string title)
    {
        using var data = IsolatedDirectory.Create();
        await using var factory = CreateFactory(data.Path).WithWebHostBuilder(web => web.UseEnvironment(environment));
        using var client = factory.CreateClient();
        await BootstrapAsync(factory, client);
        client.DefaultRequestHeaders.Accept.ParseAdd("text/html");
        using var response = await client.PostAsync("/api/conversations",
            new StringContent("{", Encoding.UTF8, "application/json"));
        await AssertProblemAsync(response, HttpStatusCode.BadRequest, title);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task OversizedRequestKeepsExplicitCorrelatedProblemBeforeEndpointExecution()
    {
        using var data = IsolatedDirectory.Create();
        await using var factory = CreateFactory(data.Path);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Accept.ParseAdd("text/html");
        using var response = await client.PostAsync("/api/conversations",
            new StringContent(new string(' ', 65_537), Encoding.UTF8, "application/json"));
        await AssertProblemAsync(response, HttpStatusCode.RequestEntityTooLarge, "Request exceeds the supported size.");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PasswordRejectionAndRateLimitReturnCorrelatedProblems()
    {
        using var data = IsolatedDirectory.Create();
        await using var factory = CreateFactory(data.Path);
        using var client = factory.CreateClient();
        await RefreshCsrfAsync(client);
        for (var attempt = 0; attempt < 21; attempt++)
        {
            using var response = await client.PostAsJsonAsync("/api/auth/login", new { passphrase = "private-request-text" });
            await AssertProblemAsync(response,
                attempt < 20 ? HttpStatusCode.Unauthorized : HttpStatusCode.TooManyRequests,
                attempt < 20 ? "Sign-in failed." : "Too Many Requests");
        }
    }

    private static WebApplicationFactory<Program> CreateFactory(string dataDirectory) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(web =>
        {
            web.UseSetting("JARVIS_PROFILE", "Local");
            web.UseSetting("JARVIS_DATA_DIR", dataDirectory);
        });

    private static async Task BootstrapAsync(WebApplicationFactory<Program> factory, HttpClient client)
    {
        await RefreshCsrfAsync(client);
        using var response = await client.PostAsJsonAsync("/api/auth/bootstrap", new
        {
            bootstrapToken = factory.Services.GetRequiredService<OwnerAuthenticationService>().BootstrapToken,
            passphrase = "a-long-owner-passphrase",
            rememberMe = false
        });
        response.EnsureSuccessStatusCode();
        await RefreshCsrfAsync(client);
    }

    private static async Task RefreshCsrfAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/api/auth/csrf");
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", body.RootElement.GetProperty("token").GetString());
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string title)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var content = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("private-request-text", content, StringComparison.Ordinal);
        Assert.DoesNotContain("private-other-owner-title", content, StringComparison.Ordinal);
        using var problem = JsonDocument.Parse(content);
        Assert.Equal((int)status, problem.RootElement.GetProperty("status").GetInt32());
        Assert.Equal(title, problem.RootElement.GetProperty("title").GetString());
        Assert.False(problem.RootElement.TryGetProperty("detail", out _));
        var correlation = Assert.Single(response.Headers.GetValues("X-Correlation-ID"));
        Assert.False(string.IsNullOrWhiteSpace(correlation));
        Assert.Equal(correlation, problem.RootElement.GetProperty("correlationId").GetString());
    }
}
