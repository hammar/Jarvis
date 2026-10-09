using Microsoft.Playwright;
using System.Text.Json;
using Xunit;

namespace PersonalAgent.E2ETests;

[Collection(SimulatorCollection.Name)]
public sealed class BrowserSmokeTests(SimulatorHostFixture fixture)
{
    [Fact]
    [Trait("Category", "BrowserE2E")]
    public async Task InvalidRecoveryValuesAreClearedBeforeLockingOrRequestingEvents()
    {
        var isolated = new SimulatorHostFixture();
        await isolated.InitializeAsync();
        try
        {
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
            var page = await browser.NewPageAsync();
            await page.GotoAsync(isolated.WebClient.BaseAddress!.ToString());
            await page.GetByLabel("Passphrase").FillAsync(SimulatorHostFixture.OwnerPassphrase);
            await page.Locator("#auth-submit").ClickAsync();
            await page.WaitForFunctionAsync("() => !authenticationInFlight && !document.getElementById('chat').hidden");
            await page.Locator("#new-conversation").ClickAsync();
            await page.WaitForFunctionAsync("() => conversationId !== '' && !conversationCreationInFlight");
            var conversation = await page.EvaluateAsync<string>("conversationId");
            var eventRequests = 0;
            page.Request += (_, request) =>
            {
                if (request.Url.Contains("/events", StringComparison.Ordinal))
                    Interlocked.Increment(ref eventRequests);
            };
            foreach (var (field, value) in new[]
            {
                ("conversationId", ""),
                ("conversationId", Guid.Empty.ToString()),
                ("turnId", "not-a-guid"),
                ("turnId", ""),
                ("turnId", Guid.Empty.ToString()),
                ("requestId", "not-a-guid"),
                ("requestId", ""),
                ("text", " "),
                ("text", new string('x', 8001))
            })
            {
                await page.EvaluateAsync(
                    """
                    input => {
                        const record = { conversationId: input.conversation, text: "Old request",
                            requestId: crypto.randomUUID(), turnId: crypto.randomUUID() };
                        record[input.field] = input.value;
                        sessionStorage.setItem("jarvis.pending-turn.v1", JSON.stringify(record));
                    }
                    """, new { conversation, field, value });
                await page.ReloadAsync();
                await page.GetByRole(AriaRole.Alert).Filter(new LocatorFilterOptions { HasText = "was cleared" }).WaitForAsync();
                await page.Locator("#chat").WaitForAsync();
                Assert.Null(await page.EvaluateAsync<string?>("sessionStorage.getItem('jarvis.pending-turn.v1')"));
                Assert.Equal("", await page.EvaluateAsync<string>("activeTurnId"));
                Assert.True(await page.Locator("#new-conversation").IsEnabledAsync());
                Assert.Equal(0, Volatile.Read(ref eventRequests));
            }
        }
        finally
        {
            await isolated.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(404)]
    [InlineData(500)]
    [InlineData(0)]
    [Trait("Category", "BrowserE2E")]
    public async Task RecentConversationErrorsAreVisibleOnlyForTheCurrentSelection(int status)
    {
        var isolated = new SimulatorHostFixture();
        await isolated.InitializeAsync();
        try
        {
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
            var page = await browser.NewPageAsync();
            var unhandled = new List<string>();
            page.PageError += (_, error) => unhandled.Add(error);
            await page.AddInitScriptAsync(
                """
                const originalAddEventListener = EventTarget.prototype.addEventListener;
                EventTarget.prototype.addEventListener = function(type, listener, options) {
                    if (type === "click" && this instanceof Element && this.closest("#conversation-list")) {
                        const original = listener;
                        listener = function(event) {
                            window.__selection = original.call(this, event);
                            return window.__selection;
                        };
                    }
                    return originalAddEventListener.call(this, type, listener, options);
                };
                """);
            await page.GotoAsync(isolated.WebClient.BaseAddress!.ToString());
            await page.GetByLabel("Passphrase").FillAsync(SimulatorHostFixture.OwnerPassphrase);
            await page.Locator("#auth-submit").ClickAsync();
            await page.WaitForFunctionAsync("() => !authenticationInFlight && !document.getElementById('chat').hidden");
            await page.Locator("#new-conversation").ClickAsync();
            await page.WaitForFunctionAsync("() => conversationId !== '' && !conversationCreationInFlight");
            var id = await page.EvaluateAsync<string>("conversationId");
            var pattern = $"**/api/conversations/{id}";
            var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var requests = 0;
            await page.RouteAsync(pattern, async route =>
            {
                if (Interlocked.Increment(ref requests) == 2)
                {
                    reached.TrySetResult();
                    await release.Task.WaitAsync(TimeSpan.FromSeconds(15));
                }
                if (status == 0) await route.AbortAsync("failed");
                else await route.FulfillAsync(new RouteFulfillOptions
                {
                    Status = status,
                    ContentType = "application/problem+json",
                    Body = "{\"title\":\"Controlled conversation failure.\"}"
                });
            });
            await page.Locator("#conversation-list button").ClickAsync();
            await page.GetByRole(AriaRole.Alert).WaitForAsync();
            Assert.Contains(status == 0 ? "fetch" : "Controlled conversation failure.",
                await page.GetByRole(AriaRole.Alert).InnerTextAsync(), StringComparison.OrdinalIgnoreCase);
            await page.EvaluateAsync("() => clearError()");
            await page.Locator("#conversation-list button").ClickAsync();
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(15));
            try
            {
                await page.Locator("#settings-button").ClickAsync();
                await page.Locator("#settings").WaitForAsync();
            }
            finally
            {
                release.TrySetResult();
            }
            await page.UnrouteAllAsync(new PageUnrouteAllOptions { Behavior = UnrouteBehavior.Wait });
            await page.EvaluateAsync("() => window.__selection");
            Assert.True(await page.GetByRole(AriaRole.Alert).IsHiddenAsync());
            Assert.True(await page.Locator("#settings").IsVisibleAsync());
            Assert.Empty(unhandled);
        }
        finally
        {
            await isolated.DisposeAsync();
        }
    }

    [Theory]
    [InlineData("{", false)]
    [InlineData("{\"conversationId\":7}", false)]
    [InlineData("null", true)]
    [Trait("Category", "BrowserE2E")]
    public async Task InvalidRecoveryIsClearedUnlessStorageRemovalFails(string record, bool removalFails)
    {
        var isolated = new SimulatorHostFixture();
        await isolated.InitializeAsync();
        try
        {
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
            var page = await browser.NewPageAsync();
            await page.GotoAsync(isolated.WebClient.BaseAddress!.ToString());
            await page.Locator("#auth").WaitForAsync();
            await page.EvaluateAsync("record => sessionStorage.setItem('jarvis.pending-turn.v1', record)", record);
            await page.AddInitScriptAsync(
                """
                const originalRemoveItem = Storage.prototype.removeItem;
                Storage.prototype.removeItem = function(key) {
                    if (this === sessionStorage && key === "jarvis.pending-turn.v1" &&
                        sessionStorage.getItem("fixture.blockRemoval") === "true") {
                        throw new Error("Controlled removal failure.");
                    }
                    return originalRemoveItem.call(this, key);
                };
                """);
            if (removalFails)
            {
                await page.EvaluateAsync("sessionStorage.setItem('fixture.blockRemoval', 'true')");
            }
            await page.ReloadAsync();
            if (removalFails)
            {
                await page.GetByRole(AriaRole.Alert).Filter(new LocatorFilterOptions { HasText = "could not be cleared" }).WaitForAsync();
                Assert.True(await page.Locator("#auth").IsHiddenAsync());
                Assert.True(await page.Locator("#chat").IsHiddenAsync());
                Assert.Equal(record, await page.EvaluateAsync<string>("sessionStorage.getItem('jarvis.pending-turn.v1')"));
                await page.EvaluateAsync("sessionStorage.removeItem('fixture.blockRemoval')");
                await page.ReloadAsync();
            }
            await page.Locator("#auth").WaitForAsync();
            Assert.Null(await page.EvaluateAsync<string?>("sessionStorage.getItem('jarvis.pending-turn.v1')"));
            await page.GetByLabel("Passphrase").FillAsync(SimulatorHostFixture.OwnerPassphrase);
            await page.Locator("#auth-submit").ClickAsync();
            await page.WaitForFunctionAsync("() => !authenticationInFlight && !document.getElementById('chat').hidden");
            await page.EvaluateAsync("record => sessionStorage.setItem('jarvis.pending-turn.v1', record)", record);
            await page.ReloadAsync();
            await page.GetByRole(AriaRole.Alert).Filter(new LocatorFilterOptions { HasText = "was cleared" }).WaitForAsync();
            Assert.Null(await page.EvaluateAsync<string?>("sessionStorage.getItem('jarvis.pending-turn.v1')"));
            Assert.True(await page.Locator("#chat").IsVisibleAsync());
            Assert.True(await page.Locator("#new-conversation").IsEnabledAsync());
            await page.Locator("#new-conversation").ClickAsync();
            await page.WaitForFunctionAsync("() => conversationId !== '' && !conversationCreationInFlight");
            Assert.True(await page.Locator("#send-turn").IsEnabledAsync());
            await page.ReloadAsync();
            await page.Locator("#chat").WaitForAsync();
            Assert.True(await page.GetByRole(AriaRole.Alert).IsHiddenAsync());
        }
        finally
        {
            await isolated.DisposeAsync();
        }
    }

    [Fact]
    [Trait("Category", "BrowserE2E")]
    public async Task LateMissingRecoveryDoesNotHideANewerConversation()
    {
        var isolated = new SimulatorHostFixture();
        await isolated.InitializeAsync();
        try
        {
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
            var page = await browser.NewPageAsync();
            await page.GotoAsync(isolated.WebClient.BaseAddress!.ToString());
            await page.GetByLabel("Passphrase").FillAsync(SimulatorHostFixture.OwnerPassphrase);
            await page.Locator("#auth-submit").ClickAsync();
            await page.WaitForFunctionAsync("() => !authenticationInFlight && !document.getElementById('chat').hidden");
            var missing = Guid.NewGuid().ToString();
            await page.EvaluateAsync(
                """
            id => {
                pendingTurnSubmission = { conversationId: id, text: "Old request", requestId: crypto.randomUUID() };
                sessionStorage.setItem("jarvis.pending-turn.v1", JSON.stringify(pendingTurnSubmission));
            }
            """, missing);
            var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await page.RouteAsync($"**/api/conversations/{missing}", async route =>
            {
                reached.TrySetResult();
                await release.Task.WaitAsync(TimeSpan.FromSeconds(15));
                await route.FulfillAsync(new RouteFulfillOptions { Status = 404, Body = "{}" });
            });
            await page.EvaluateAsync("() => { window.__recovery = showChat(); }");
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(15));
            string selected;
            try
            {
                await page.Locator("#new-conversation").ClickAsync();
                await page.WaitForFunctionAsync("() => conversationId !== '' && !conversationCreationInFlight");
                selected = await page.EvaluateAsync<string>("conversationId");
            }
            finally
            {
                release.TrySetResult();
            }
            await page.EvaluateAsync("() => window.__recovery");
            Assert.Equal(selected, await page.EvaluateAsync<string>("conversationId"));
            Assert.True(await page.Locator("#conversation").IsVisibleAsync());
            Assert.True(await page.Locator("#send-turn").IsEnabledAsync());
            Assert.Null(await page.EvaluateAsync<string?>("sessionStorage.getItem('jarvis.pending-turn.v1')"));
        }
        finally
        {
            await isolated.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "BrowserE2E")]
    public async Task MissingRecoveredConversationUnlocksChatAndConflictAllowsANewRequest(bool accepted)
    {
        var isolated = new SimulatorHostFixture();
        await isolated.InitializeAsync();
        try
        {
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
            var page = await browser.NewPageAsync();
            await page.GotoAsync(isolated.WebClient.BaseAddress!.ToString());
            await page.GetByLabel("Passphrase").FillAsync(SimulatorHostFixture.OwnerPassphrase);
            await page.Locator("#auth-submit").ClickAsync();
            await page.WaitForFunctionAsync("() => !authenticationInFlight && !document.getElementById('chat').hidden");
            var missingConversation = Guid.NewGuid().ToString();
            var missingTurn = Guid.NewGuid().ToString();
            var eventRequests = 0;
            page.Request += (_, request) =>
            {
                if (request.Url.Contains($"/api/turns/{missingTurn}/events", StringComparison.Ordinal))
                    Interlocked.Increment(ref eventRequests);
            };
            await page.EvaluateAsync(
                """
            record => sessionStorage.setItem("jarvis.pending-turn.v1", JSON.stringify(record))
            """,
                new
                {
                    conversationId = missingConversation,
                    text = "Old request",
                    requestId = Guid.NewGuid().ToString(),
                    turnId = accepted ? missingTurn : null
                });
            if (!accepted)
            {
                await page.EvaluateAsync(
                    """
                () => {
                    const record = JSON.parse(sessionStorage.getItem("jarvis.pending-turn.v1"));
                    delete record.turnId;
                    sessionStorage.setItem("jarvis.pending-turn.v1", JSON.stringify(record));
                }
                """);
            }
            await page.ReloadAsync();
            await page.GetByRole(AriaRole.Alert).Filter(new LocatorFilterOptions { HasText = "no longer exists" }).WaitForAsync();
            Assert.Null(await page.EvaluateAsync<string?>("sessionStorage.getItem('jarvis.pending-turn.v1')"));
            Assert.Equal("", await page.EvaluateAsync<string>("activeTurnId"));
            Assert.True(await page.Locator("#cancel-turn").IsHiddenAsync());
            Assert.True(await page.Locator("#new-conversation").IsEnabledAsync());
            Assert.Equal(0, Volatile.Read(ref eventRequests));
            await page.Locator("#new-conversation").ClickAsync();
            await page.WaitForFunctionAsync("() => conversationId !== '' && !conversationCreationInFlight");
            var requestIds = new List<string>();
            await page.RouteAsync("**/api/conversations/*/turns", async route =>
            {
                using var body = JsonDocument.Parse(route.Request.PostData!);
                requestIds.Add(body.RootElement.GetProperty("requestId").GetString()!);
                await route.FulfillAsync(new RouteFulfillOptions
                {
                    Status = requestIds.Count == 1 ? 409 : 400,
                    ContentType = "application/problem+json",
                    Body = "{\"title\":\"Controlled definitive rejection.\"}"
                });
            });
            await page.GetByLabel("Message").FillAsync("Conflicting request");
            await page.Locator("#send-turn").ClickAsync();
            await page.WaitForFunctionAsync("() => !turnSubmissionInFlight && pendingTurnSubmission === null");
            await page.GetByLabel("Message").FillAsync("A different request");
            await page.Locator("#send-turn").ClickAsync();
            await page.WaitForFunctionAsync("() => !turnSubmissionInFlight && pendingTurnSubmission === null");
            Assert.Equal(2, requestIds.Count);
            Assert.NotEqual(requestIds[0], requestIds[1]);
        }
        finally
        {
            await isolated.DisposeAsync();
        }
    }

    [Fact]
    [Trait("Category", "BrowserE2E")]
    public async Task FirstRunReportsAuthStatusFailureAndSerializesBootstrapThenReauthenticatesWithoutReload()
    {
        var firstRun = new SimulatorHostFixture { AuthenticateOnStart = false };
        await firstRun.InitializeAsync();
        try
        {
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
            var page = await browser.NewPageAsync();
            await page.RouteAsync("**/api/auth/status", route => route.FulfillAsync(new RouteFulfillOptions
            {
                Status = 500,
                ContentType = "application/problem+json",
                Body = "{\"title\":\"Controlled owner status failure.\"}"
            }));
            await page.GotoAsync(firstRun.WebClient.BaseAddress!.ToString());
            await page.GetByRole(AriaRole.Alert).Filter(new LocatorFilterOptions { HasText = "Controlled owner status failure." })
                .WaitForAsync();
            Assert.True(await page.Locator("#auth").IsHiddenAsync());
            Assert.True(await page.Locator("#chat").IsHiddenAsync());
            await page.UnrouteAsync("**/api/auth/status");
            await page.ReloadAsync();
            await page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Set up owner account" }).WaitForAsync();
            await page.Locator("#bootstrap-token").FillAsync(SimulatorHostFixture.BootstrapToken);
            await page.GetByLabel("Passphrase").FillAsync(SimulatorHostFixture.OwnerPassphrase);
            var requestReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseResponse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var bootstrapRequests = 0;
            await page.RouteAsync("**/api/auth/bootstrap", async route =>
            {
                Interlocked.Increment(ref bootstrapRequests);
                var response = await route.FetchAsync();
                requestReady.TrySetResult();
                await releaseResponse.Task.WaitAsync(TimeSpan.FromSeconds(15));
                await route.FulfillAsync(new RouteFulfillOptions { Response = response });
            });
            await page.Locator("#auth-submit").ClickAsync();
            await requestReady.Task.WaitAsync(TimeSpan.FromSeconds(15));
            try
            {
                Assert.True(await page.Locator("#auth-submit").IsDisabledAsync());
                await page.Locator("#auth-form").DispatchEventAsync("submit");
                Assert.Equal(1, Volatile.Read(ref bootstrapRequests));
            }
            finally
            {
                releaseResponse.TrySetResult();
            }
            await page.WaitForFunctionAsync("() => !authenticationInFlight && !document.getElementById('chat').hidden");
            Assert.Equal("", await page.Locator("#passphrase").InputValueAsync());
            Assert.Equal("", await page.Locator("#bootstrap-token").InputValueAsync());
            Assert.True(await page.Locator("#bootstrap-token").IsHiddenAsync());
            Assert.True(await page.Locator("#bootstrap-token-label").IsHiddenAsync());
            Assert.True(await page.GetByRole(AriaRole.Alert).IsHiddenAsync());
            var pageMarker = Guid.NewGuid().ToString("D");
            await page.EvaluateAsync("marker => { window.__reauthenticationMarker = marker; }", pageMarker);
            await page.EvaluateAsync(
                """
                async () => {
                    await api("/api/auth/logout", { method: "POST" });
                    try { await refreshStatus(); } catch (error) { showError(error.message); }
                }
                """);
            await page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Owner sign-in" }).WaitForAsync();
            Assert.True(await page.Locator("#bootstrap-token").IsHiddenAsync());
            Assert.True(await page.Locator("#bootstrap-token-label").IsHiddenAsync());
            await page.GetByLabel("Passphrase").FillAsync("incorrect-owner-passphrase");
            await page.Locator("#auth-submit").ClickAsync();
            await page.GetByRole(AriaRole.Alert).Filter(new LocatorFilterOptions { HasText = "Sign-in failed." }).WaitForAsync();
            await page.WaitForFunctionAsync("() => !authenticationInFlight");
            await page.GetByLabel("Passphrase").FillAsync(SimulatorHostFixture.OwnerPassphrase);
            await page.Locator("#auth-submit").ClickAsync();
            await page.WaitForFunctionAsync("() => !authenticationInFlight && !document.getElementById('chat').hidden");
            Assert.Equal("", await page.Locator("#passphrase").InputValueAsync());
            Assert.True(await page.GetByRole(AriaRole.Alert).IsHiddenAsync());
            Assert.Equal(pageMarker, await page.EvaluateAsync<string>("window.__reauthenticationMarker"));
            Assert.Equal(1, Volatile.Read(ref bootstrapRequests));
        }
        finally
        {
            await firstRun.DisposeAsync();
        }
    }

    [Fact]
    [Trait("Category", "BrowserE2E")]
    public async Task PlaywrightSignsInAndStreamsASimulatorChat()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true
        });
        var page = await browser.NewPageAsync();
        await page.Context.Tracing.StartAsync(new TracingStartOptions
        {
            Screenshots = true,
            Snapshots = true
        });

        try
        {
            await page.GotoAsync(fixture.WebClient.BaseAddress!.ToString());
            await page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Jarvis" }).WaitForAsync();
            await page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Owner sign-in" }).WaitForAsync();
            await page.GetByLabel("Passphrase").FillAsync(SimulatorHostFixture.OwnerPassphrase);
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Sign in" }).ClickAsync();
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "New conversation" }).WaitForAsync();
            Assert.Contains("simulator-model", await page.Locator("#route-status").InnerTextAsync(), StringComparison.Ordinal);
            await page.RouteAsync("**/api/status", route => route.FulfillAsync(new RouteFulfillOptions
            {
                Status = 500,
                ContentType = "application/problem+json",
                Body = "{\"title\":\"Controlled status failure.\"}"
            }));
            await page.ReloadAsync();
            await page.GetByRole(AriaRole.Alert).Filter(new LocatorFilterOptions { HasText = "Controlled status failure." })
                .WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
            Assert.True(await page.Locator("#auth").IsHiddenAsync());
            await page.UnrouteAsync("**/api/status");
            await page.ReloadAsync();
            await page.Locator("#new-conversation").WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
            await page.EvaluateAsync(
                """
                async () => {
                    await api("/api/auth/logout", { method: "POST" });
                    try { await refreshStatus(); } catch (error) { showError(error.message); }
                }
                """);
            await page.GetByLabel("Passphrase").FillAsync(SimulatorHostFixture.OwnerPassphrase);
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Sign in" }).ClickAsync();
            await page.Locator("#new-conversation").WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
            var createResponseReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseCreateResponse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var createRequests = 0;
            await page.RouteAsync("**/api/conversations", async route =>
            {
                if (route.Request.Method != "POST")
                {
                    await route.ContinueAsync();
                    return;
                }
                Interlocked.Increment(ref createRequests);
                var response = await route.FetchAsync();
                createResponseReady.TrySetResult();
                await releaseCreateResponse.Task.WaitAsync(TimeSpan.FromSeconds(15));
                await route.FulfillAsync(new RouteFulfillOptions { Response = response });
            });
            await page.Locator("#new-conversation").ClickAsync();
            await createResponseReady.Task.WaitAsync(TimeSpan.FromSeconds(15));
            try
            {
                Assert.True(await page.Locator("#new-conversation").IsDisabledAsync());
                await page.EvaluateAsync(
                    "document.getElementById('new-conversation').dispatchEvent(new Event('click'))");
                Assert.Equal(1, Volatile.Read(ref createRequests));
            }
            finally
            {
                releaseCreateResponse.TrySetResult();
            }
            await page.WaitForFunctionAsync("() => conversationId !== ''");
            await page.WaitForFunctionAsync("() => !document.getElementById('new-conversation').disabled");
            await page.UnrouteAsync("**/api/conversations");
            var firstConversationId = await page.EvaluateAsync<string>("conversationId");
            await page.Locator("#new-conversation").ClickAsync();
            await page.WaitForFunctionAsync("previous => conversationId !== previous", firstConversationId);
            var submittedConversationId = await page.EvaluateAsync<string>("conversationId");
            await page.EvaluateAsync(
                """
                async id => {
                    let releaseRead;
                    let signalReadStarted;
                    window.__staleReadStarted = new Promise(resolve => { signalReadStarted = resolve; });
                    window.__releaseStaleRead = () => releaseRead();
                    let releaseRefresh;
                    let signalRefreshStarted;
                    window.__refreshStarted = new Promise(resolve => { signalRefreshStarted = resolve; });
                    window.__releaseRefresh = () => releaseRefresh();
                    window.__holdRefresh = false;
                    const originalFetch = window.fetch.bind(window);
                    window.fetch = async (input, init = {}) => {
                        const url = typeof input === "string" ? input : input.url;
                        const method = (init.method || (input instanceof Request ? input.method : "GET")).toUpperCase();
                        const response = await originalFetch(input, init);
                        if (window.__holdRefresh && method === "GET" && url === "/api/conversations") {
                            window.__holdRefresh = false;
                            signalRefreshStarted();
                            await new Promise(resolve => { releaseRefresh = resolve; });
                        }
                        if (method === "GET" && url.includes(id)) {
                            signalReadStarted();
                            await new Promise(resolve => { releaseRead = resolve; });
                        }
                        return response;
                    };
                    window.__staleOpenPromise = openConversation(id);
                    await window.__staleReadStarted;
                }
                """,
                firstConversationId);
            var submitResponseReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseSubmitResponse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var submitRequests = 0;
            await page.RouteAsync("**/api/conversations/*/turns", async route =>
            {
                if (Interlocked.Increment(ref submitRequests) > 1)
                {
                    await route.ContinueAsync();
                    return;
                }

                var response = await route.FetchAsync();
                submitResponseReady.TrySetResult();
                await releaseSubmitResponse.Task.WaitAsync(TimeSpan.FromSeconds(15));
                await route.FulfillAsync(new RouteFulfillOptions { Response = response });
            });
            using (var delayFrameControl = await fixture.ModelClient.PostAsync(
                "/fixture/control/delay-after-first-frame",
                content: null))
            {
                delayFrameControl.EnsureSuccessStatusCode();
            }

            await page.GetByLabel("Message").FillAsync("Hello from the browser demo");
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Send" }).ClickAsync();
            await submitResponseReady.Task.WaitAsync(TimeSpan.FromSeconds(15));
            try
            {
                Assert.True(await page.Locator("#new-conversation").IsDisabledAsync());
                Assert.True(await page.Locator("#settings-button").IsDisabledAsync());
                Assert.True(await page.Locator("#activity-button").IsDisabledAsync());
                Assert.True(await page.Locator("#logout").IsDisabledAsync());
                Assert.True(await page.Locator("#conversation-list button").First.IsDisabledAsync());
                Assert.True(await page.Locator("#send-turn").IsDisabledAsync());
                await page.EvaluateAsync("openConversation('not-the-submitted-conversation')");
                Assert.Equal(
                    "Finish or cancel the active turn before switching conversations.",
                    await page.GetByRole(AriaRole.Alert).InnerTextAsync());
                await page.EvaluateAsync("document.getElementById('turn-form').requestSubmit()");
                Assert.Equal(1, Volatile.Read(ref submitRequests));
            }
            finally
            {
                releaseSubmitResponse.TrySetResult();
            }

            await page.UnrouteAsync("**/api/conversations/*/turns");
            using (var firstFrame = await fixture.ModelClient.GetAsync(
                "/fixture/control/wait-for-first-frame"))
            {
                firstFrame.EnsureSuccessStatusCode();
            }

            var partialReply = page.Locator("#messages li")
                .Filter(new LocatorFilterOptions { HasText = "Controlled streaming " });
            await partialReply.WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });
            Assert.DoesNotContain("response", await partialReply.InnerTextAsync(), StringComparison.Ordinal);
            await page.EvaluateAsync("window.__holdRefresh = true");
            await page.GetByText("Controlled streaming response", new PageGetByTextOptions { Exact = false })
                .WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
            await page.Locator("#cancel-turn").WaitForAsync(
                new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 15_000 });
            await page.EvaluateAsync("window.__refreshStarted");
            Assert.True(await page.Locator("#new-conversation").IsDisabledAsync());
            Assert.True(await page.Locator("#send-turn").IsDisabledAsync());
            await page.EvaluateAsync("window.__releaseRefresh()");
            await page.WaitForFunctionAsync("() => !document.getElementById('new-conversation').disabled");
            await page.EvaluateAsync("window.__releaseStaleRead()");
            await page.EvaluateAsync("window.__staleOpenPromise");
            Assert.Equal(submittedConversationId, await page.EvaluateAsync<string>("conversationId"));
            var streamedReplies = page.Locator("#messages li")
                .Filter(new LocatorFilterOptions { HasText = "Controlled streaming response" });
            Assert.Equal(1, await streamedReplies.CountAsync());

            await page.Locator("#new-conversation").ClickAsync();
            await page.WaitForFunctionAsync("previous => conversationId !== previous", submittedConversationId);
            await page.WaitForFunctionAsync("() => document.querySelectorAll('#conversation-list button').length === 3");
            Assert.Equal(3, await page.Locator("#conversation-list button").CountAsync());
            using (var failureControl = await fixture.ModelClient.PostAsync(
                "/fixture/control/fail-next-stream",
                content: null))
            {
                failureControl.EnsureSuccessStatusCode();
            }

            await page.GetByLabel("Message").FillAsync("Show a controlled provider failure");
            var failureRequestIds = new List<string>();
            await page.RouteAsync("**/api/conversations/*/turns", async route =>
            {
                using var payload = JsonDocument.Parse(route.Request.PostData!);
                failureRequestIds.Add(payload.RootElement.GetProperty("requestId").GetString()!);
                if (failureRequestIds.Count == 1)
                {
                    _ = await route.FetchAsync();
                    await route.AbortAsync();
                    return;
                }

                await route.ContinueAsync();
            });
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Send" }).ClickAsync();
            var failureAlert = page.GetByRole(AriaRole.Alert);
            await failureAlert.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
            await page.ReloadAsync();
            await page.Locator("#send-turn").WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
            Assert.Equal("Show a controlled provider failure", await page.GetByLabel("Message").InputValueAsync());
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Send" }).ClickAsync();
            await page.GetByRole(AriaRole.Alert)
                .Filter(new LocatorFilterOptions
                {
                    HasText = "The assistant could not complete this request. Your conversation is saved; you can try again."
                })
                .WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
            Assert.Equal(2, failureRequestIds.Count);
            Assert.Equal(failureRequestIds[0], failureRequestIds[1]);
            await page.UnrouteAsync("**/api/conversations/*/turns");
            using (var clearFailureControl = await fixture.ModelClient.PostAsync(
                "/fixture/control/clear-stream-failure",
                content: null))
            {
                clearFailureControl.EnsureSuccessStatusCode();
            }

            using var delayControl = await fixture.ModelClient.PostAsync(
                "/fixture/control/delay-next-stream",
                content: null);
            delayControl.EnsureSuccessStatusCode();
            await page.GetByLabel("Message").FillAsync("Cancel this controlled response");
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Send" }).ClickAsync();
            var cancelButton = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Cancel turn" });
            await cancelButton.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
            Assert.True(await page.Locator("#new-conversation").IsDisabledAsync());
            Assert.True(await page.Locator("#conversation-list button").Nth(1).IsDisabledAsync());
            using var delayedStream = await fixture.ModelClient.GetAsync(
                "/fixture/control/wait-for-delayed-stream");
            delayedStream.EnsureSuccessStatusCode();
            var acceptedTurnId = await page.EvaluateAsync<string>("activeTurnId");
            var statusRequestReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseStatusRequest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await page.RouteAsync("**/api/status", async route =>
            {
                statusRequestReady.TrySetResult();
                await releaseStatusRequest.Task.WaitAsync(TimeSpan.FromSeconds(15));
                await route.FulfillAsync(new RouteFulfillOptions
                {
                    Status = 500,
                    ContentType = "application/problem+json",
                    Body = "{\"title\":\"Controlled recovery status failure.\"}"
                });
            });
            await page.ReloadAsync();
            await statusRequestReady.Task.WaitAsync(TimeSpan.FromSeconds(15));
            try
            {
                Assert.True(await page.Locator("#new-conversation").IsDisabledAsync());
                Assert.True(await page.Locator("#send-turn").IsDisabledAsync());
                Assert.Equal(acceptedTurnId, await page.EvaluateAsync<string>("activeTurnId"));
            }
            finally
            {
                releaseStatusRequest.TrySetResult();
            }
            await page.WaitForFunctionAsync("() => conversationId === activeTurnConversationId");
            await page.GetByRole(AriaRole.Alert).Filter(new LocatorFilterOptions { HasText = "Controlled recovery status failure." })
                .WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
            Assert.True(await page.Locator("#conversation").IsVisibleAsync());
            await page.WaitForFunctionAsync("() => eventSource !== null");
            await page.UnrouteAsync("**/api/status");
            await cancelButton.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
            Assert.Equal(acceptedTurnId, await page.EvaluateAsync<string>("activeTurnId"));
            Assert.True(await page.Locator("#new-conversation").IsDisabledAsync());
            Assert.True(await page.Locator("#send-turn").IsDisabledAsync());
            await cancelButton.ClickAsync();
            await cancelButton.WaitForAsync(
                new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 15_000 });
            await page.WaitForFunctionAsync("() => !document.getElementById('activity-button').disabled");
            Assert.True(await page.GetByRole(AriaRole.Alert).IsHiddenAsync());
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Activity" }).ClickAsync();
            await page.GetByText("Cancelled", new PageGetByTextOptions { Exact = false })
                .WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });

            await page.Locator("#conversation-list button").Nth(1).ClickAsync();
            await page.GetByText("Controlled streaming response", new PageGetByTextOptions { Exact = false })
                .WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });
            Assert.True(await page.Locator("#activity").IsHiddenAsync());
            Assert.True(await page.Locator("#settings").IsHiddenAsync());
            var persistedConversationId = await page.EvaluateAsync<string>("conversationId");
            await page.Locator("#settings-button").ClickAsync();
            await page.Locator("#settings").WaitForAsync();
            Assert.Equal("status", await page.Locator("#settings-status").GetAttributeAsync("role"));
            await page.Locator("#retention-form").GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Save settings" }).ClickAsync();
            await page.GetByRole(AriaRole.Status).Filter(new LocatorFilterOptions { HasText = "Saved:" }).WaitForAsync();
            await page.Locator("#new-conversation").ClickAsync();
            await page.WaitForFunctionAsync("() => !conversationCreationInFlight && !document.getElementById('conversation').hidden");
            Assert.True(await page.Locator("#settings").IsHiddenAsync());
            Assert.True(await page.Locator("#activity").IsHiddenAsync());

            var slowSettingsReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseSettings = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await page.RouteAsync("**/api/settings", async route =>
            {
                var response = await route.FetchAsync();
                slowSettingsReady.TrySetResult();
                await releaseSettings.Task.WaitAsync(TimeSpan.FromSeconds(15));
                await route.FulfillAsync(new RouteFulfillOptions { Response = response });
            });
            await page.EvaluateAsync("() => { window.__pendingSettingsNavigation = openSettings(); }");
            await slowSettingsReady.Task.WaitAsync(TimeSpan.FromSeconds(15));
            try
            {
                await page.Locator("#activity-button").ClickAsync();
                await page.Locator("#activity").WaitForAsync();
            }
            finally
            {
                releaseSettings.TrySetResult();
            }
            await page.EvaluateAsync("() => window.__pendingSettingsNavigation");
            await page.UnrouteAsync("**/api/settings");
            Assert.True(await page.Locator("#settings").IsHiddenAsync());
            Assert.True(await page.Locator("#activity").IsVisibleAsync());
            var slowActivityReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseActivity = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await page.RouteAsync("**/api/activity", async route =>
            {
                var response = await route.FetchAsync();
                slowActivityReady.TrySetResult();
                await releaseActivity.Task.WaitAsync(TimeSpan.FromSeconds(15));
                await route.FulfillAsync(new RouteFulfillOptions { Response = response });
            });
            await page.EvaluateAsync("() => { window.__pendingActivityNavigation = openActivity(); }");
            await slowActivityReady.Task.WaitAsync(TimeSpan.FromSeconds(15));
            try
            {
                await page.EvaluateAsync("id => openConversation(id)", persistedConversationId);
                await page.Locator("#conversation").WaitForAsync();
            }
            finally
            {
                releaseActivity.TrySetResult();
            }
            await page.EvaluateAsync("() => window.__pendingActivityNavigation");
            await page.UnrouteAsync("**/api/activity");
            Assert.True(await page.Locator("#conversation").IsVisibleAsync());
            Assert.True(await page.Locator("#settings").IsHiddenAsync());
            Assert.True(await page.Locator("#activity").IsHiddenAsync());
            await page.Locator("#settings-button").ClickAsync();
            await page.Locator("#settings").WaitForAsync();
            var saveReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await page.RouteAsync("**/api/settings/retention", async route =>
            {
                saveReady.TrySetResult();
                await releaseSave.Task.WaitAsync(TimeSpan.FromSeconds(15));
                await route.FulfillAsync(new RouteFulfillOptions
                {
                    Status = 500,
                    ContentType = "application/problem+json",
                    Body = "{\"title\":\"Controlled stale save failure.\"}"
                });
            });
            await page.EvaluateAsync("() => { window.__pendingRetentionSave = saveRetention({ preventDefault() {} }); }");
            await saveReady.Task.WaitAsync(TimeSpan.FromSeconds(15));
            try
            {
                await page.Locator("#activity-button").ClickAsync();
                await page.Locator("#activity").WaitForAsync();
            }
            finally
            {
                releaseSave.TrySetResult();
            }
            await page.EvaluateAsync("() => window.__pendingRetentionSave");
            await page.UnrouteAsync("**/api/settings/retention");
            Assert.True(await page.GetByRole(AriaRole.Alert).IsHiddenAsync());
            Assert.True(await page.Locator("#activity").IsVisibleAsync());
            Assert.True(await page.Locator("#settings").IsHiddenAsync());
            await fixture.RestartAsync();
            await page.GotoAsync(fixture.WebClient.BaseAddress!.ToString());
            await page.WaitForFunctionAsync(
                "() => !document.getElementById('auth').hidden || !document.getElementById('chat').hidden");

            var savedConversation = page.Locator("#conversation-list")
                .GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "New conversation" })
                .Nth(1);
            await savedConversation.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
            Assert.True(await page.Locator("#auth").IsHiddenAsync());
            await page.EvaluateAsync("id => openConversation(id)", persistedConversationId);
            await page.GetByText("Controlled streaming response", new PageGetByTextOptions { Exact = false })
                .WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });
            Assert.Equal(1, await page.Locator("#messages li")
                .Filter(new LocatorFilterOptions { HasText = "Controlled streaming response" }).CountAsync());
        }
        catch
        {
            var artifactDirectory = Environment.GetEnvironmentVariable("JARVIS_PLAYWRIGHT_ARTIFACTS")
                ?? Path.Combine("artifacts", "playwright");
            Directory.CreateDirectory(artifactDirectory);
            await page.ScreenshotAsync(new PageScreenshotOptions
            {
                Path = Path.Combine(artifactDirectory, "browser-smoke-failure.png"),
                FullPage = true
            });
            await page.Context.Tracing.StopAsync(new TracingStopOptions
            {
                Path = Path.Combine(artifactDirectory, "browser-smoke-failure.zip")
            });
            throw;
        }

        await page.Context.Tracing.StopAsync();
    }

    [Fact]
    [Trait("Category", "GateProbe")]
    public async Task DeliberatelyIncorrectBrowserAssertionFails()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true
        });
        var page = await browser.NewPageAsync();
        await page.GotoAsync(fixture.WebClient.BaseAddress!.ToString());

        Assert.Equal("This title must never match.", await page.TitleAsync());
    }
}
