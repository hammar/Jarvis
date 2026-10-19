using Microsoft.Playwright;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace PersonalAgent.E2ETests;

[Collection(SimulatorCollection.Name)]
public sealed class BrowserSmokeTests(SimulatorHostFixture fixture)
{
    [Fact]
    [Trait("Category", "BrowserE2E")]
    public async Task EnterSendsMessageAndShiftEnterAddsANewLine()
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

            var submittedText = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var submitCount = 0;
            await page.RouteAsync("**/api/conversations/*/turns", async route =>
            {
                Interlocked.Increment(ref submitCount);
                using var payload = JsonDocument.Parse(route.Request.PostData!);
                submittedText.TrySetResult(payload.RootElement.GetProperty("text").GetString()!);
                await route.FulfillAsync(new RouteFulfillOptions
                {
                    Status = 400,
                    ContentType = "application/problem+json",
                    Body = "{\"title\":\"Controlled turn rejection.\"}"
                });
            });

            var prompt = page.GetByLabel("Message");
            foreach (var chord in new[] { "Alt+Enter", "Control+Enter", "Meta+Enter" })
            {
                await prompt.FillAsync("Guarded draft");
                await prompt.PressAsync(chord);
                Assert.Equal("Guarded draft", await prompt.InputValueAsync());
                Assert.Equal(0, Volatile.Read(ref submitCount));
            }
            foreach (var options in new[] { new { isComposing = true, keyCode = 13 }, new { isComposing = false, keyCode = 229 } })
            {
                Assert.True(await prompt.EvaluateAsync<bool>(
                    """
                    (element, options) => element.dispatchEvent(new KeyboardEvent("keydown",
                        { key: "Enter", bubbles: true, cancelable: true, ...options }))
                    """, options));
                Assert.Equal("Guarded draft", await prompt.InputValueAsync());
                Assert.Equal(0, Volatile.Read(ref submitCount));
            }
            await prompt.FillAsync("First line");
            await prompt.PressAsync("Shift+Enter");
            await prompt.PressSequentiallyAsync("second line");
            Assert.Equal("First line\nsecond line", await prompt.InputValueAsync());
            Assert.Equal(0, Volatile.Read(ref submitCount));

            await prompt.PressAsync("Enter");
            Assert.Equal("First line\nsecond line", await submittedText.Task.WaitAsync(TimeSpan.FromSeconds(15)));
            await page.GetByRole(AriaRole.Alert).Filter(new LocatorFilterOptions { HasText = "Controlled turn rejection." })
                .WaitForAsync();
            Assert.Equal(1, Volatile.Read(ref submitCount));
            Assert.Equal("First line\nsecond line", await prompt.InputValueAsync());
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
    public async Task AcceptedSendClearsSubmittedTextWithoutDiscardingANewerDraft(bool editWhileSending)
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
            var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            string? acceptedRecovery = null;
            if (editWhileSending)
            {
                var conversation = await page.EvaluateAsync<string>("conversationId");
                await page.RouteAsync($"**/api/conversations/{conversation}", async route =>
                {
                    var response = await route.FetchAsync();
                    var history = JsonNode.Parse(await response.TextAsync())!;
                    history["messages"]!.AsArray().Add(new JsonObject
                    {
                        ["role"] = "user",
                        ["content"] = "A newer message from another tab"
                    });
                    await route.FulfillAsync(new RouteFulfillOptions { Response = response, Body = history.ToJsonString() });
                });
            }
            await page.RouteAsync("**/api/conversations/*/turns", async route =>
            {
                var response = await route.FetchAsync();
                Assert.Equal(202, response.Status);
                using var request = JsonDocument.Parse(route.Request.PostData!);
                using var result = JsonDocument.Parse(await response.TextAsync());
                acceptedRecovery = JsonSerializer.Serialize(new
                {
                    conversationId = route.Request.Url.Split('/')[^2],
                    text = request.RootElement.GetProperty("text").GetString(),
                    requestId = request.RootElement.GetProperty("requestId").GetString(),
                    turnId = result.RootElement.GetProperty("turnId").GetProperty("value").GetString()
                });
                reached.TrySetResult();
                await release.Task.WaitAsync(TimeSpan.FromSeconds(15));
                await route.FulfillAsync(new RouteFulfillOptions { Response = response });
            });
            var prompt = page.GetByLabel("Message");
            await page.EvaluateAsync(
                """
                () => {
                    window.responseAnnouncements = [];
                    new MutationObserver(() => {
                        const text = document.getElementById("response-announcement").textContent;
                        if (text) window.responseAnnouncements.push(text);
                    }).observe(document.getElementById("response-announcement"), { childList: true });
                }
                """);
            await prompt.FillAsync("A controlled message");
            await prompt.PressAsync("Enter");
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(15));
            try
            {
                Assert.Equal("A controlled message", await prompt.InputValueAsync());
                if (editWhileSending) await prompt.FillAsync("My next draft");
            }
            finally
            {
                release.TrySetResult();
            }
            await page.WaitForFunctionAsync("() => pendingTurnSubmission?.turnId || pendingTurnSubmission === null");
            Assert.Equal(editWhileSending ? "My next draft" : "", await prompt.InputValueAsync());
            await page.WaitForFunctionAsync("() => activeTurnId === '' && !turnSubmissionInFlight");
            Assert.Equal(editWhileSending ? "My next draft" : "", await prompt.InputValueAsync());
            Assert.Equal("polite", await page.Locator("#response-announcement").GetAttributeAsync("aria-live"));
            Assert.Equal("true", await page.Locator("#response-announcement").GetAttributeAsync("aria-atomic"));
            var completedReply = await page.Locator("#messages .assistant-message .message-content").Last.InnerTextAsync();
            Assert.Equal($"Jarvis: {completedReply}", await page.Locator("#response-announcement").InnerTextAsync());
            Assert.Single(await page.EvaluateAsync<string[]>("window.responseAnnouncements"));
            if (editWhileSending)
                Assert.Equal("A newer message from another tab", await page.Locator("#messages .message-content").Last.InnerTextAsync());
            Assert.Contains("Georgia", await page.Locator("#messages .assistant-message .message-content").Last
                .EvaluateAsync<string>("element => getComputedStyle(element).fontFamily"));
            await page.EvaluateAsync("saved => sessionStorage.setItem('jarvis.pending-turn.v1', saved)", acceptedRecovery);
            await page.ReloadAsync();
            await page.Locator("#chat").WaitForAsync();
            await page.WaitForFunctionAsync("() => activeTurnId === '' && pendingTurnSubmission === null");
            Assert.Equal("", await prompt.InputValueAsync());
        }
        finally
        {
            await isolated.DisposeAsync();
        }
    }

    [Fact]
    [Trait("Category", "BrowserE2E")]
    public async Task SoftEditorialChatReflowsWithoutOverflowAndKeepsLongMessagesReadable()
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
            const string longTitle = "A very long conversation title about a quiet morning, thoughtful routines, and the little things that make a day feel more manageable";
            await page.Locator("#rename-conversation").ClickAsync();
            await page.GetByLabel("Conversation title", new PageGetByLabelOptions { Exact = true }).FillAsync(longTitle);
            await page.Locator("#save-title").ClickAsync();
            await page.WaitForFunctionAsync("() => !conversationRenameInFlight && document.getElementById('rename-form').hidden");
            Assert.Equal(longTitle, await page.Locator("#conversation-title").InnerTextAsync());
            Assert.Equal(longTitle, await page.Locator("#conversation-list button[aria-current='page']").InnerTextAsync());
            Assert.Contains("UTC", await page.Locator("#conversation-created").InnerTextAsync());
            await page.EvaluateAsync(
                """
                () => {
                    renderMessages([
                        { role: "user", content: `${"A long message with spaces and punctuation— ".repeat(30)}🌱` },
                        { role: "assistant", content: `${"A careful answer that should wrap within the conversation panel. ".repeat(35)}終わり` }
                    ]);
                }
                """);
            await page.Locator("#rename-conversation").ClickAsync();

            foreach (var width in new[] { 1440, 768, 390, 320 })
            {
                await page.SetViewportSizeAsync(width, 900);
                var layout = await page.EvaluateAsync<JsonElement>(
                    """
                    () => ({
                        viewport: document.documentElement.clientWidth,
                        document: document.documentElement.scrollWidth,
                        message: document.querySelector("#messages li").getBoundingClientRect().width,
                        composer: document.querySelector(".composer").getBoundingClientRect().width,
                        sendHeight: document.getElementById("send-turn").getBoundingClientRect().height
                    })
                    """);
                var viewport = layout.GetProperty("viewport").GetInt32();
                var document = layout.GetProperty("document").GetInt32();
                var message = layout.GetProperty("message").GetDouble();
                var composer = layout.GetProperty("composer").GetDouble();
                var sendHeight = layout.GetProperty("sendHeight").GetDouble();
                Assert.True(document <= viewport,
                    $"The {width}px layout overflows horizontally ({document}px > {viewport}px).");
                Assert.True(message <= composer,
                    $"Long message width {message}px exceeds composer width {composer}px at {width}px.");
                Assert.True(sendHeight >= 44,
                    $"Send target is only {sendHeight}px high at {width}px.");
                Assert.True(await page.Locator("#conversation-list time").IsVisibleAsync());
                Assert.True(await page.EvaluateAsync<bool>(
                    """
                    () => {
                        const list = document.getElementById("conversation-list").getBoundingClientRect();
                        const timestamp = document.querySelector("#conversation-list time").getBoundingClientRect();
                        return timestamp.top >= list.top && timestamp.bottom <= list.bottom;
                    }
                    """), $"Creation timestamp is clipped by the conversation list at {width}px.");
                Assert.True(await page.Locator("#conversation-title-input").IsVisibleAsync());
            }

            await page.Locator("#cancel-title").ClickAsync();
            foreach (var (width, height) in new[] { (1440, 900), (768, 900), (390, 900), (320, 900),
                (1440, 700), (768, 700), (390, 700), (320, 700) })
            {
                await page.SetViewportSizeAsync(width, height);
                var scrolling = await page.EvaluateAsync<JsonElement>(
                    """
                    () => {
                        const messages = document.getElementById("messages");
                        const composer = document.querySelector(".composer").getBoundingClientRect();
                        messages.scrollTop = messages.scrollHeight;
                        return { height: messages.clientHeight, scrollHeight: messages.scrollHeight,
                            scrollTop: messages.scrollTop, composerTop: composer.top, composerBottom: composer.bottom,
                            viewport: window.innerHeight };
                    }
                    """);
                Assert.True(scrolling.GetProperty("height").GetInt32() > 0 &&
                    scrolling.GetProperty("scrollHeight").GetInt32() > scrolling.GetProperty("height").GetInt32() &&
                    scrolling.GetProperty("scrollTop").GetInt32() > 0 &&
                    scrolling.GetProperty("composerTop").GetDouble() >= 0 &&
                    scrolling.GetProperty("composerBottom").GetDouble() <= scrolling.GetProperty("viewport").GetInt32(),
                    $"Long history must scroll internally with the composer inside the viewport at {width}px: {scrolling}");
            }
            await page.Locator("#new-conversation").FocusAsync();
            await page.Keyboard.PressAsync("Tab");
            await page.Keyboard.PressAsync("Tab");
            var focus = await page.EvaluateAsync<JsonElement>(
                """
                () => {
                    const active = document.activeElement;
                    const style = getComputedStyle(active);
                    return { id: active.id, outlineStyle: style.outlineStyle, outlineWidth: style.outlineWidth };
                }
                """);
            Assert.Equal("settings-button", focus.GetProperty("id").GetString());
            Assert.NotEqual("none", focus.GetProperty("outlineStyle").GetString());
            Assert.True(double.Parse(focus.GetProperty("outlineWidth").GetString()!.TrimEnd('p', 'x'),
                System.Globalization.CultureInfo.InvariantCulture) >= 3);
        }
        finally
        {
            await isolated.DisposeAsync();
        }
    }

    [Fact]
    [Trait("Category", "BrowserE2E")]
    public async Task SoftEditorialSurfacesReflowInBothThemesWithZoomAndReducedMotion()
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
            await AssertReflowAsync("#auth", "#passphrase, #auth-submit");
            await page.GetByLabel("Passphrase").FillAsync(SimulatorHostFixture.OwnerPassphrase);
            await page.Locator("#auth-submit").ClickAsync();
            await page.WaitForFunctionAsync("() => !authenticationInFlight && !document.getElementById('chat').hidden");
            await page.Locator("#new-conversation").ClickAsync();
            await page.WaitForFunctionAsync("() => conversationId !== '' && !conversationCreationInFlight");
            await AssertReflowAsync("#conversation", "#prompt, #send-turn");
            await page.Locator("#settings-button").ClickAsync();
            await page.Locator("#settings").WaitForAsync();
            await AssertReflowAsync("#settings", "#conversation-days, #audit-days, #retention-form button");
            await page.Locator("#activity-button").ClickAsync();
            await page.Locator("#activity").WaitForAsync();
            await AssertReflowAsync("#activity", "#activity-button");

            async Task AssertReflowAsync(string surface, string controls)
            {
                foreach (var scheme in new[] { ColorScheme.Light, ColorScheme.Dark })
                {
                    await page.EmulateMediaAsync(new PageEmulateMediaOptions
                    {
                        ColorScheme = scheme,
                        ReducedMotion = ReducedMotion.Reduce
                    });
                    foreach (var (width, zoom) in new[] { (1440, 1), (768, 1), (390, 1), (320, 1), (1280, 2) })
                    {
                        await page.SetViewportSizeAsync(width, 900);
                        // CSS zoom exercises layout scaling; device scale alone would not test reflow.
                        await page.EvaluateAsync("zoom => document.documentElement.style.zoom = String(zoom)", zoom);
                        Assert.True(await page.Locator(surface).IsVisibleAsync(),
                            $"{surface} is hidden at {width}px, {zoom * 100}% CSS zoom, {scheme}.");
                        Assert.True(await page.EvaluateAsync<bool>(
                            """
                            () => document.documentElement.scrollWidth <= document.documentElement.clientWidth
                            """), $"{surface} overflows at {width}px, {zoom * 100}% CSS zoom, {scheme}.");
                        foreach (var control in await page.Locator(controls).AllAsync())
                        {
                            Assert.True(await control.IsVisibleAsync());
                            Assert.True(await control.EvaluateAsync<bool>(
                                """
                                element => {
                                    const bounds = element.getBoundingClientRect();
                                    return bounds.width > 0 && bounds.left >= 0 &&
                                        bounds.right <= window.innerWidth + 1;
                                }
                                """), $"{controls} is clipped at {width}px, {zoom * 100}% CSS zoom, {scheme}.");
                        }
                        Assert.Equal(scheme == ColorScheme.Dark ? "dark" : "light",
                            await page.EvaluateAsync<string>("() => getComputedStyle(document.documentElement).colorScheme"));
                        Assert.True(await page.Locator(controls).First.EvaluateAsync<bool>(
                            """
                            element => getComputedStyle(element).transitionDuration.split(",")
                                .every(duration => parseFloat(duration) <= .00001)
                            """));
                    }
                }
                await page.EvaluateAsync("() => document.documentElement.style.zoom = ''");
            }
        }
        finally
        {
            await isolated.DisposeAsync();
        }
    }

    [Fact]
    [Trait("Category", "BrowserE2E")]
    public async Task LateStreamRecoveryLookupCannotClearANewerConversation()
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
            var turn = Guid.NewGuid().ToString();
            var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var checks = 0;
            await page.RouteAsync($"**/api/conversations/{conversation}", async route =>
            {
                if (Interlocked.Increment(ref checks) != 1)
                {
                    await route.ContinueAsync();
                    return;
                }
                reached.TrySetResult();
                await release.Task.WaitAsync(TimeSpan.FromSeconds(15));
                await route.FulfillAsync(new RouteFulfillOptions { Status = 404, Body = "{}" });
            });
            await page.RouteAsync($"**/api/turns/{turn}/events",
                route => route.FulfillAsync(new RouteFulfillOptions { Status = 404 }));
            await page.EvaluateAsync(
                """
                input => {
                    pendingTurnSubmission = { conversationId: input.conversation, text: "Old request",
                        requestId: crypto.randomUUID(), turnId: input.turn };
                    sessionStorage.setItem("jarvis.pending-turn.v1", JSON.stringify(pendingTurnSubmission));
                    activeTurnId = input.turn;
                    activeTurnConversationId = input.conversation;
                    setTurnNavigationLocked(true);
                    connectEvents(input.turn, 0);
                    const original = eventSource.onerror;
                    eventSource.onerror = () => window.__streamCheck = original();
                }
                """, new { conversation, turn });
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(15));
            string selected;
            try
            {
                await page.EvaluateAsync(
                    "() => eventSource.dispatchEvent(new MessageEvent('TurnCompleted', { data: '{}', lastEventId: '1' }))");
                await page.WaitForFunctionAsync("() => activeTurnId === '' && pendingTurnSubmission === null");
                await page.Locator("#new-conversation").ClickAsync();
                await page.WaitForFunctionAsync("() => conversationId !== '' && !conversationCreationInFlight");
                selected = await page.EvaluateAsync<string>("conversationId");
            }
            finally
            {
                release.TrySetResult();
            }
            await page.EvaluateAsync("() => window.__streamCheck");
            Assert.Equal(selected, await page.EvaluateAsync<string>("conversationId"));
            Assert.True(await page.Locator("#conversation").IsVisibleAsync());
            Assert.True(await page.Locator("#send-turn").IsEnabledAsync());
            Assert.True(await page.GetByRole(AriaRole.Alert).IsHiddenAsync());
        }
        finally
        {
            await isolated.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(404, 404, false)]
    [InlineData(200, 404, false)]
    [InlineData(404, 404, true)]
    [InlineData(404, 500, false)]
    [InlineData(500, 200, false)]
    [Trait("Category", "BrowserE2E")]
    public async Task StreamFailureChecksMissingHistoryWithoutDiscardingTransientOrUnclearableRecovery(
        int streamStatus, int historyStatus, bool removalFails)
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
            var turn = Guid.NewGuid().ToString();
            await page.EvaluateAsync(
                """
                input => {
                    pendingTurnSubmission = { conversationId: input.conversation, text: "Recovered request",
                        requestId: crypto.randomUUID(), turnId: input.turn };
                    sessionStorage.setItem("jarvis.pending-turn.v1", JSON.stringify(pendingTurnSubmission));
                    activeTurnId = input.turn;
                    activeTurnConversationId = input.conversation;
                    setTurnNavigationLocked(true);
                    document.getElementById("send-turn").disabled = true;
                    const original = Storage.prototype.removeItem;
                    Storage.prototype.removeItem = function(key) {
                        if (input.removalFails && key === "jarvis.pending-turn.v1") throw new Error("Controlled removal failure.");
                        return original.call(this, key);
                    };
                    window.__restoreRemoval = () => Storage.prototype.removeItem = original;
                }
                """, new { conversation, turn, removalFails });
            await page.RouteAsync($"**/api/conversations/{conversation}", route => route.FulfillAsync(new RouteFulfillOptions
            {
                Status = historyStatus,
                ContentType = "application/json",
                Body = historyStatus == 200 ? "{}" : "{\"title\":\"Controlled recovery lookup.\"}"
            }));
            await page.RouteAsync($"**/api/turns/{turn}/events", route => route.FulfillAsync(new RouteFulfillOptions
            {
                Status = streamStatus,
                ContentType = "text/event-stream",
                Body = ""
            }));
            await page.EvaluateAsync("turn => connectEvents(turn, 0)", turn);
            var expected = historyStatus == 200 ? "Reload this page" : removalFails ? "could not be cleared" :
                historyStatus == 404 ? "no longer exists" : "Stream recovery could not be checked";
            await page.GetByRole(AriaRole.Alert).Filter(new LocatorFilterOptions { HasText = expected }).WaitForAsync();
            if (historyStatus == 500)
                Assert.Contains("Reload this page", await page.GetByRole(AriaRole.Alert).InnerTextAsync());
            if (removalFails || historyStatus is 500 or 200)
            {
                Assert.Equal(turn, await page.EvaluateAsync<string>("activeTurnId"));
                Assert.NotNull(await page.EvaluateAsync<string?>("sessionStorage.getItem('jarvis.pending-turn.v1')"));
                Assert.True(await page.Locator("#new-conversation").IsDisabledAsync());
                if (removalFails)
                {
                    await page.EvaluateAsync("async () => { window.__restoreRemoval(); await eventSource.onerror(); }");
                    await page.GetByRole(AriaRole.Alert).Filter(new LocatorFilterOptions { HasText = "no longer exists" }).WaitForAsync();
                }
                else
                {
                    await page.EvaluateAsync(
                        "() => eventSource.dispatchEvent(new MessageEvent('TextDelta', { data: '{\"text\":\"Resumed\"}', lastEventId: '1' }))");
                    Assert.True(await page.GetByRole(AriaRole.Alert).IsHiddenAsync());
                    await page.EvaluateAsync("() => eventSource.close()");
                }
            }
            if (historyStatus == 404)
            {
                Assert.Equal("", await page.EvaluateAsync<string>("activeTurnId"));
                Assert.Null(await page.EvaluateAsync<string?>("sessionStorage.getItem('jarvis.pending-turn.v1')"));
                Assert.True(await page.Locator("#new-conversation").IsEnabledAsync());
                Assert.True(await page.EvaluateAsync<bool>("eventSource === null"));
            }
        }
        finally
        {
            await isolated.DisposeAsync();
        }
    }

    [Fact]
    [Trait("Category", "BrowserE2E")]
    public async Task ProfileGuidanceMatchesSignInReloadAndExpiredSession()
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
            Assert.Equal("Sign in to your local assistant.", await page.Locator("#profile").InnerTextAsync());
            await page.GetByLabel("Passphrase").FillAsync(SimulatorHostFixture.OwnerPassphrase);
            await page.Locator("#auth-submit").ClickAsync();
            await page.WaitForFunctionAsync("() => !authenticationInFlight && !document.getElementById('chat').hidden");
            Assert.Equal("Signed in to your local assistant.", await page.Locator("#profile").InnerTextAsync());
            await page.RouteAsync("**/api/status", route => route.FulfillAsync(new RouteFulfillOptions
            {
                Status = 500,
                ContentType = "application/problem+json",
                Body = "{\"title\":\"Controlled status failure.\"}"
            }));
            await page.ReloadAsync();
            await page.GetByRole(AriaRole.Alert).Filter(new LocatorFilterOptions { HasText = "Controlled status failure." }).WaitForAsync();
            Assert.Equal("Checking your local assistant session.", await page.Locator("#profile").InnerTextAsync());
            Assert.True(await page.Locator("#chat").IsVisibleAsync());
            await page.UnrouteAsync("**/api/status");
            await page.ReloadAsync();
            await page.Locator("#chat").WaitForAsync();
            Assert.Equal("Signed in to your local assistant.", await page.Locator("#profile").InnerTextAsync());
            await page.RouteAsync("**/api/settings", route => route.FulfillAsync(new RouteFulfillOptions { Status = 401 }));
            await page.Locator("#settings-button").ClickAsync();
            await page.Locator("#auth").WaitForAsync();
            Assert.Equal("Sign in to your local assistant.", await page.Locator("#profile").InnerTextAsync());
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
    public async Task MissingRecoveryRemovalFailurePreservesStateAndStopsReconnect(bool accepted)
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
                input => {
                    const record = { conversationId: input.missing, text: "Old request", requestId: crypto.randomUUID() };
                    if (input.accepted) record.turnId = crypto.randomUUID();
                    sessionStorage.setItem("jarvis.pending-turn.v1", JSON.stringify(record));
                    sessionStorage.setItem("fixture.blockRemoval", "true");
                }
                """, new { missing, accepted });
            var record = await page.EvaluateAsync<string>("sessionStorage.getItem('jarvis.pending-turn.v1')");
            await page.AddInitScriptAsync(
                """
                const originalRemoveItem = Storage.prototype.removeItem;
                Storage.prototype.removeItem = function(key) {
                    if (this === sessionStorage && key === "jarvis.pending-turn.v1" &&
                        sessionStorage.getItem("fixture.blockRemoval") === "true") throw new Error("Controlled removal failure.");
                    return originalRemoveItem.call(this, key);
                };
                """);
            var eventRequests = 0;
            page.Request += (_, request) =>
            {
                if (request.Url.Contains("/events", StringComparison.Ordinal)) Interlocked.Increment(ref eventRequests);
            };
            await page.ReloadAsync();
            await page.GetByRole(AriaRole.Alert).Filter(new LocatorFilterOptions { HasText = "could not be cleared" }).WaitForAsync();
            await page.WaitForFunctionAsync("() => pendingTurnSubmission !== null && eventSource === null");
            Assert.Equal(record, await page.EvaluateAsync<string>("sessionStorage.getItem('jarvis.pending-turn.v1')"));
            Assert.Equal(missing, await page.EvaluateAsync<string>("pendingTurnSubmission.conversationId"));
            Assert.Equal(accepted, await page.Locator("#new-conversation").IsDisabledAsync());
            Assert.Equal(0, Volatile.Read(ref eventRequests));
            Assert.DoesNotContain("no longer exists", await page.GetByRole(AriaRole.Alert).InnerTextAsync());
            await page.ReloadAsync();
            await page.GetByRole(AriaRole.Alert).Filter(new LocatorFilterOptions { HasText = "could not be cleared" }).WaitForAsync();
            Assert.Equal(record, await page.EvaluateAsync<string>("sessionStorage.getItem('jarvis.pending-turn.v1')"));
            await page.EvaluateAsync("sessionStorage.removeItem('fixture.blockRemoval')");
            await page.ReloadAsync();
            await page.GetByRole(AriaRole.Alert).Filter(new LocatorFilterOptions { HasText = "no longer exists" }).WaitForAsync();
            Assert.Null(await page.EvaluateAsync<string?>("sessionStorage.getItem('jarvis.pending-turn.v1')"));
            Assert.Equal("", await page.EvaluateAsync<string>("activeTurnId"));
            Assert.True(await page.Locator("#new-conversation").IsEnabledAsync());
            Assert.Equal(0, Volatile.Read(ref eventRequests));
        }
        finally
        {
            await isolated.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(ColorScheme.Light)]
    [InlineData(ColorScheme.Dark)]
    [Trait("Category", "BrowserE2E")]
    public async Task ErrorTextMeetsContrastInBothColorSchemes(ColorScheme scheme)
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync(new BrowserNewPageOptions { ColorScheme = scheme });
        await page.GotoAsync(fixture.WebClient.BaseAddress!.ToString());
        await page.EvaluateAsync("() => showError('Contrast fixture')");
        var contrast = await page.EvaluateAsync<double>(
            """
            () => {
                const luminance = color => {
                    const channels = color.match(/\d+/g).slice(0, 3).map(Number).map(x => {
                        const v = x / 255;
                        return v <= 0.04045 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4;
                    });
                    return channels[0] * 0.2126 + channels[1] * 0.7152 + channels[2] * 0.0722;
                };
                const foreground = luminance(getComputedStyle(document.getElementById("error")).color);
                const background = luminance(getComputedStyle(document.documentElement).backgroundColor);
                return (Math.max(foreground, background) + 0.05) / (Math.min(foreground, background) + 0.05);
            }
            """);
        Assert.True(contrast >= 4.5, $"Error text contrast {contrast:F2}:1 is below 4.5:1 in {scheme}.");
    }

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
            Assert.Equal("Create the local owner account.", await page.Locator("#profile").InnerTextAsync());
            Assert.True(await page.Locator("#bootstrap-token").IsVisibleAsync());
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
    public async Task ConversationTitlesAndCreationTimesRemainDistinctAndManualNamesPersist()
    {
        var isolated = new SimulatorHostFixture();
        await isolated.InitializeAsync();
        try
        {
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
            var page = await browser.NewPageAsync(new BrowserNewPageOptions { TimezoneId = "America/New_York" });
            await page.GotoAsync(isolated.WebClient.BaseAddress!.ToString());
            await page.GetByLabel("Passphrase").FillAsync(SimulatorHostFixture.OwnerPassphrase);
            await page.Locator("#auth-submit").ClickAsync();
            await page.Locator("#new-conversation").WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });

            var beforeFold = await page.EvaluateAsync<string>("formatConversationTime('2026-11-01T05:30:00Z')");
            var afterFold = await page.EvaluateAsync<string>("formatConversationTime('2026-11-01T06:30:00Z')");
            Assert.Contains("UTC-04:00", beforeFold, StringComparison.Ordinal);
            Assert.Contains("UTC-05:00", afterFold, StringComparison.Ordinal);
            Assert.NotEqual(beforeFold, afterFold);

            await page.Locator("#new-conversation").ClickAsync();
            await page.WaitForFunctionAsync("() => conversationId !== '' && !conversationCreationInFlight");
            Assert.Equal("New conversation", await page.Locator("#conversation-title").InnerTextAsync());
            Assert.Contains("Created ", await page.Locator("#conversation-created").InnerTextAsync(), StringComparison.Ordinal);

            await page.Locator("#rename-conversation").ClickAsync();
            Assert.Equal("", await page.GetByLabel("Conversation title").InputValueAsync());
            await page.GetByLabel("Conversation title").FillAsync("New conversation");
            await page.Locator("#save-title").ClickAsync();
            await page.WaitForFunctionAsync("() => document.getElementById('rename-form').hidden && !document.getElementById('save-title').disabled");
            await page.Locator("#rename-conversation").ClickAsync();
            Assert.Equal("New conversation", await page.GetByLabel("Conversation title").InputValueAsync());
            const string manualTitle = "<img src=x> Manual before first message";
            await page.GetByLabel("Conversation title").FillAsync(manualTitle);
            await page.Locator("#save-title").ClickAsync();
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = manualTitle }).WaitForAsync();

            var manualConversationId = await page.EvaluateAsync<string>("conversationId");
            await page.Locator("#new-conversation").ClickAsync();
            await page.WaitForFunctionAsync("id => conversationId !== id && !conversationCreationInFlight", manualConversationId);
            var automaticConversationId = await page.EvaluateAsync<string>("conversationId");
            Assert.Equal(2, await page.Locator("#conversation-list time").CountAsync());
            foreach (var timestamp in await page.Locator("#conversation-list time").AllInnerTextsAsync())
            {
                Assert.Contains("(", timestamp, StringComparison.Ordinal);
            }
            var descriptions = await page.Locator("#conversation-list button")
                .EvaluateAllAsync<string[]>("buttons => buttons.map(button => button.getAttribute('aria-describedby'))");
            Assert.Equal(2, descriptions.Distinct().Count());
            foreach (var descriptionId in descriptions)
            {
                Assert.False(string.IsNullOrWhiteSpace(descriptionId));
                var description = page.Locator($"time[id='{descriptionId}']");
                Assert.Equal(1, await description.CountAsync());
                Assert.Contains("(", await description.InnerTextAsync(), StringComparison.Ordinal);
            }

            const string firstMessage = "  A distinct   first request asks about café plans.  ";
            using (var delayFrameControl = await isolated.ModelClient.PostAsync(
                "/fixture/control/delay-after-first-content-frame",
                content: null))
            {
                delayFrameControl.EnsureSuccessStatusCode();
            }

            await page.Locator("#prompt").FillAsync(firstMessage);
            await page.Locator("#send-turn").ClickAsync();
            await page.WaitForFunctionAsync("() => activeTurnId !== ''");
            Assert.Equal(
                "A distinct first request asks about café plans.",
                await page.Locator("#conversation-title").InnerTextAsync());
            using (var firstFrame = await isolated.ModelClient.GetAsync("/fixture/control/wait-for-first-content-frame"))
            {
                firstFrame.EnsureSuccessStatusCode();
            }

            Assert.NotEqual("", await page.EvaluateAsync<string>("activeTurnId"));
            await page.GetByRole(AriaRole.Button,
                new PageGetByRoleOptions { Name = "A distinct first request asks about café plans." }).WaitForAsync();
            await page.WaitForFunctionAsync("() => activeTurnId === ''");
            await page.Locator("#rename-conversation").ClickAsync();
            Assert.Equal(
                "A distinct first request asks about café plans.",
                await page.GetByLabel("Conversation title").InputValueAsync());
            await page.Locator("#cancel-title").ClickAsync();
            await page.Locator("#conversation-list").GetByRole(AriaRole.Button,
                new LocatorGetByRoleOptions { Name = manualTitle }).WaitForAsync();

            await page.EvaluateAsync(
                """
                async id => {
                    let releaseRead;
                    let signalReadStarted;
                    window.__titleReadStarted = new Promise(resolve => { signalReadStarted = resolve; });
                    window.__releaseTitleRead = () => releaseRead();
                    const originalFetch = window.fetch.bind(window);
                    window.fetch = async (input, init = {}) => {
                        const url = typeof input === "string" ? input : input.url;
                        const method = (init.method || (input instanceof Request ? input.method : "GET")).toUpperCase();
                        const response = await originalFetch(input, init);
                        if (method === "GET" && url === `/api/conversations/${encodeURIComponent(id)}`) {
                            signalReadStarted();
                            await new Promise(resolve => { releaseRead = resolve; });
                        }
                        return response;
                    };
                    window.__staleTitleOpen = openConversation(id);
                    await window.__titleReadStarted;
                }
                """,
                automaticConversationId);
            const string renamedAfterFirstMessage = "Renamed while an older read is pending";
            await page.Locator("#rename-conversation").ClickAsync();
            await page.GetByLabel("Conversation title").FillAsync(renamedAfterFirstMessage);
            await page.Locator("#save-title").ClickAsync();
            await page.Locator("#conversation-list").GetByRole(AriaRole.Button,
                new LocatorGetByRoleOptions { Name = renamedAfterFirstMessage }).WaitForAsync();
            Assert.Equal(renamedAfterFirstMessage, await page.Locator("#conversation-title").InnerTextAsync());
            await page.EvaluateAsync("window.__releaseTitleRead()");
            await page.EvaluateAsync("window.__staleTitleOpen");
            Assert.Equal(renamedAfterFirstMessage, await page.Locator("#conversation-title").InnerTextAsync());

            await page.Locator("#conversation-list").GetByRole(AriaRole.Button,
                new LocatorGetByRoleOptions { Name = manualTitle }).ClickAsync();
            await page.WaitForFunctionAsync("id => conversationId === id", manualConversationId);
            await page.Locator("#prompt").FillAsync("This first message must not replace the manual title.");
            await page.Locator("#send-turn").ClickAsync();
            await page.WaitForFunctionAsync("() => !turnSubmissionInFlight && activeTurnId === '' && pendingTurnSubmission === null");
            Assert.Equal(manualTitle, await page.Locator("#conversation-title").InnerTextAsync());

            await page.ReloadAsync();
            await page.Locator("#conversation-list").GetByRole(AriaRole.Button,
                new LocatorGetByRoleOptions { Name = manualTitle }).WaitForAsync();
            await page.Locator("#conversation-list").GetByRole(AriaRole.Button,
                new LocatorGetByRoleOptions { Name = renamedAfterFirstMessage }).WaitForAsync();
            Assert.Equal(2, await page.Locator("#conversation-list time").CountAsync());
            Assert.Equal(0, await page.Locator("#conversation-list img, #conversation-title img").CountAsync());
            Assert.NotEqual(manualConversationId, automaticConversationId);

            await page.Locator("#new-conversation").ClickAsync();
            await page.WaitForFunctionAsync("() => conversationId !== '' && !conversationCreationInFlight");
            await page.Locator("#prompt").FillAsync("New conversation");
            var acceptedLiteralTitle = await page.RunAndWaitForResponseAsync(
                () => page.Locator("#send-turn").ClickAsync(),
                response => response.Request.Method == "POST" && response.Url.EndsWith("/turns", StringComparison.Ordinal));
            Assert.Equal(202, acceptedLiteralTitle.Status);
            await page.WaitForFunctionAsync("() => !turnSubmissionInFlight && activeTurnId === '' && pendingTurnSubmission === null");
            await page.Locator("#rename-conversation").ClickAsync();
            Assert.Equal("New conversation", await page.GetByLabel("Conversation title").InputValueAsync());
            await page.GetByLabel("Conversation title").FillAsync("Unsaved title");
            await page.Locator("#cancel-title").ClickAsync();
            await page.Locator("#rename-conversation").ClickAsync();
            Assert.Equal("New conversation", await page.GetByLabel("Conversation title").InputValueAsync());
            var renamedConversationId = await page.EvaluateAsync<string>("conversationId");
            const string renamedWhileNavigating = "Renamed after navigation";
            var renameReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseRename = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await page.RouteAsync($"**/api/conversations/{renamedConversationId}", async route =>
            {
                if (route.Request.Method != "PATCH")
                {
                    await route.ContinueAsync();
                    return;
                }

                var response = await route.FetchAsync();
                renameReady.TrySetResult();
                await releaseRename.Task.WaitAsync(TimeSpan.FromSeconds(15));
                await route.FulfillAsync(new RouteFulfillOptions { Response = response });
            });
            await page.GetByLabel("Conversation title").FillAsync(renamedWhileNavigating);
            await page.Locator("#save-title").ClickAsync();
            await renameReady.Task.WaitAsync(TimeSpan.FromSeconds(15));
            try
            {
                await page.Locator("#conversation-list").GetByRole(AriaRole.Button,
                    new LocatorGetByRoleOptions { Name = manualTitle }).ClickAsync();
                await page.WaitForFunctionAsync("id => conversationId === id", manualConversationId);
            }
            finally
            {
                releaseRename.TrySetResult();
            }

            await page.Locator("#conversation-list").GetByRole(AriaRole.Button,
                new LocatorGetByRoleOptions { Name = renamedWhileNavigating }).WaitForAsync();
            Assert.Equal(manualTitle, await page.Locator("#conversation-title").InnerTextAsync());
            Assert.Equal(manualConversationId, await page.EvaluateAsync<string>("conversationId"));
            await page.UnrouteAsync($"**/api/conversations/{renamedConversationId}");

            var navigationReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseNavigation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            renameReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            releaseRename = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await page.RouteAsync($"**/api/conversations/{manualConversationId}", async route =>
            {
                var response = await route.FetchAsync();
                renameReady.TrySetResult();
                await releaseRename.Task.WaitAsync(TimeSpan.FromSeconds(15));
                await route.FulfillAsync(new RouteFulfillOptions { Response = response });
            });
            await page.RouteAsync($"**/api/conversations/{automaticConversationId}", async route =>
            {
                var response = await route.FetchAsync();
                navigationReady.TrySetResult();
                await releaseNavigation.Task.WaitAsync(TimeSpan.FromSeconds(15));
                await route.FulfillAsync(new RouteFulfillOptions { Response = response });
            });
            const string renamedBeforeNavigationCompletes = "Rename must not cancel navigation";
            await page.Locator("#rename-conversation").ClickAsync();
            await page.GetByLabel("Conversation title").FillAsync(renamedBeforeNavigationCompletes);
            await page.Locator("#save-title").ClickAsync();
            await renameReady.Task.WaitAsync(TimeSpan.FromSeconds(15));
            try
            {
                await page.Locator("#conversation-list").GetByRole(AriaRole.Button,
                    new LocatorGetByRoleOptions { Name = renamedAfterFirstMessage }).ClickAsync();
                await navigationReady.Task.WaitAsync(TimeSpan.FromSeconds(15));
                releaseRename.TrySetResult();
                await page.Locator("#conversation-list").GetByRole(AriaRole.Button,
                    new LocatorGetByRoleOptions { Name = renamedBeforeNavigationCompletes }).WaitForAsync();
            }
            finally
            {
                releaseRename.TrySetResult();
                releaseNavigation.TrySetResult();
            }

            await page.WaitForFunctionAsync("id => conversationId === id", automaticConversationId);
            Assert.Equal(renamedAfterFirstMessage, await page.Locator("#conversation-title").InnerTextAsync());
            await page.UnrouteAsync($"**/api/conversations/{manualConversationId}");
            await page.UnrouteAsync($"**/api/conversations/{automaticConversationId}");

            renameReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            releaseRename = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await page.RouteAsync($"**/api/conversations/{automaticConversationId}", async route =>
            {
                var response = await route.FetchAsync();
                renameReady.TrySetResult();
                await releaseRename.Task.WaitAsync(TimeSpan.FromSeconds(15));
                await route.FulfillAsync(new RouteFulfillOptions { Response = response });
            });
            var concurrentTurnRequests = 0;
            await page.RouteAsync("**/api/conversations/*/turns", async route =>
            {
                Interlocked.Increment(ref concurrentTurnRequests);
                await route.ContinueAsync();
            });
            const string serializedManualTitle = "Manual title before another turn";
            await page.Locator("#rename-conversation").ClickAsync();
            await page.GetByLabel("Conversation title").FillAsync(serializedManualTitle);
            await page.Locator("#save-title").ClickAsync();
            await renameReady.Task.WaitAsync(TimeSpan.FromSeconds(15));
            try
            {
                Assert.True(await page.Locator("#send-turn").IsDisabledAsync());
                await page.EvaluateAsync("document.getElementById('turn-form').requestSubmit()");
                Assert.Equal(0, Volatile.Read(ref concurrentTurnRequests));
                Assert.True(await page.EvaluateAsync<bool>("pendingTurnSubmission === null"));
            }
            finally
            {
                releaseRename.TrySetResult();
            }

            await page.WaitForFunctionAsync("() => !conversationRenameInFlight");
            Assert.Equal(serializedManualTitle, await page.Locator("#conversation-title").InnerTextAsync());
            Assert.False(await page.Locator("#send-turn").IsDisabledAsync());
            await page.UnrouteAsync("**/api/conversations/*/turns");
            await page.UnrouteAsync($"**/api/conversations/{automaticConversationId}");

            foreach (var releaseReadFirst in new[] { true, false })
            {
                renameReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                releaseRename = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                navigationReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                releaseNavigation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var oldTitle = await page.Locator("#conversation-title").InnerTextAsync();
                var awayBackTitle = $"Renamed across away/back navigation {releaseReadFirst}";
                await page.RouteAsync($"**/api/conversations/{automaticConversationId}", async route =>
                {
                    if (route.Request.Method == "PATCH")
                    {
                        renameReady.TrySetResult();
                        await releaseRename.Task.WaitAsync(TimeSpan.FromSeconds(15));
                        await route.FulfillAsync(new RouteFulfillOptions { Response = await route.FetchAsync() });
                    }
                    else
                    {
                        var response = await route.FetchAsync();
                        navigationReady.TrySetResult();
                        await releaseNavigation.Task.WaitAsync(TimeSpan.FromSeconds(15));
                        await route.FulfillAsync(new RouteFulfillOptions { Response = response });
                    }
                });
                await page.Locator("#rename-conversation").ClickAsync();
                await page.GetByLabel("Conversation title").FillAsync(awayBackTitle);
                await page.Locator("#save-title").ClickAsync();
                await renameReady.Task.WaitAsync(TimeSpan.FromSeconds(15));
                try
                {
                    await page.Locator("#conversation-list").GetByRole(AriaRole.Button,
                        new LocatorGetByRoleOptions { Name = renamedBeforeNavigationCompletes }).ClickAsync();
                    await page.WaitForFunctionAsync("id => conversationId === id", manualConversationId);
                    await page.Locator("#conversation-list").GetByRole(AriaRole.Button,
                        new LocatorGetByRoleOptions { Name = oldTitle }).ClickAsync();
                    await navigationReady.Task.WaitAsync(TimeSpan.FromSeconds(15));
                    if (releaseReadFirst)
                    {
                        releaseNavigation.TrySetResult();
                        await page.WaitForFunctionAsync("id => conversationId === id", automaticConversationId);
                        Assert.Equal(oldTitle, await page.Locator("#conversation-title").InnerTextAsync());
                    }

                    releaseRename.TrySetResult();
                    await page.WaitForFunctionAsync("() => !conversationRenameInFlight");
                }
                finally
                {
                    releaseRename.TrySetResult();
                    releaseNavigation.TrySetResult();
                }

                await page.WaitForFunctionAsync("id => conversationId === id", automaticConversationId);
                Assert.Equal(awayBackTitle, await page.Locator("#conversation-title").InnerTextAsync());
                await page.Locator("#conversation-list").GetByRole(AriaRole.Button,
                    new LocatorGetByRoleOptions { Name = awayBackTitle }).WaitForAsync();
                await page.Locator("#rename-conversation").ClickAsync();
                Assert.Equal(awayBackTitle, await page.GetByLabel("Conversation title").InputValueAsync());
                await page.Locator("#cancel-title").ClickAsync();
                await page.UnrouteAsync($"**/api/conversations/{automaticConversationId}");
            }

            renameReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            releaseRename = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await page.RouteAsync($"**/api/conversations/{automaticConversationId}", async route =>
            {
                renameReady.TrySetResult();
                await releaseRename.Task.WaitAsync(TimeSpan.FromSeconds(15));
                await route.FulfillAsync(new RouteFulfillOptions
                {
                    Status = 400,
                    ContentType = "application/problem+json",
                    Body = "{\"title\":\"Obsolete rename failure.\"}"
                });
            });
            await page.Locator("#rename-conversation").ClickAsync();
            await page.GetByLabel("Conversation title").FillAsync("Rejected name");
            await page.Locator("#save-title").ClickAsync();
            await renameReady.Task.WaitAsync(TimeSpan.FromSeconds(15));
            try
            {
                await page.Locator("#conversation-list").GetByRole(AriaRole.Button,
                    new LocatorGetByRoleOptions { Name = renamedBeforeNavigationCompletes }).ClickAsync();
                await page.WaitForFunctionAsync("id => conversationId === id", manualConversationId);
                await page.EvaluateAsync("showError('Current conversation feedback.')");
            }
            finally
            {
                releaseRename.TrySetResult();
            }

            await page.WaitForFunctionAsync("() => !conversationRenameInFlight");
            Assert.Equal("Current conversation feedback.", await page.GetByRole(AriaRole.Alert).InnerTextAsync());
            Assert.False(await page.Locator("#send-turn").IsDisabledAsync());
            await page.UnrouteAsync($"**/api/conversations/{automaticConversationId}");

            await page.RouteAsync("**/api/conversations", route => route.FulfillAsync(new RouteFulfillOptions
            {
                Status = 500,
                ContentType = "application/problem+json",
                Body = "{\"title\":\"Conversation list refresh failed.\"}"
            }));
            await page.Locator("#rename-conversation").ClickAsync();
            await page.GetByLabel("Conversation title").FillAsync("Saved despite list refresh failure");
            await page.Locator("#save-title").ClickAsync();
            await page.WaitForFunctionAsync("() => !conversationRenameInFlight");
            Assert.Equal("Saved despite list refresh failure", await page.Locator("#conversation-title").InnerTextAsync());
            Assert.Equal("Conversation list refresh failed.", await page.GetByRole(AriaRole.Alert).InnerTextAsync());
            await page.UnrouteAsync("**/api/conversations");
        }
        finally
        {
            await isolated.DisposeAsync();
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
                "/fixture/control/delay-after-first-content-frame",
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
                "/fixture/control/wait-for-first-content-frame"))
            {
                firstFrame.EnsureSuccessStatusCode();
            }

            var partialReply = page.Locator("#messages li")
                .Filter(new LocatorFilterOptions { HasText = "Controlled streaming " });
            await partialReply.WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });
            Assert.DoesNotContain("response", await partialReply.InnerTextAsync(), StringComparison.Ordinal);
            Assert.Equal("", await page.Locator("#response-announcement").InnerTextAsync());
            Assert.Contains("Georgia", await partialReply.Locator(".message-content")
                .EvaluateAsync<string>("element => getComputedStyle(element).fontFamily"));
            await page.EvaluateAsync("window.__holdRefresh = true");
            await page.Locator("#messages").GetByText("Controlled streaming response", new LocatorGetByTextOptions { Exact = false })
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
            Assert.True(await page.Locator("#approvals-button").IsDisabledAsync());
            Assert.True(await page.Locator("#memory-button").IsDisabledAsync());
            Assert.True(await page.Locator("#household-button").IsDisabledAsync());
            Assert.True(await cancelButton.IsVisibleAsync());
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
                Assert.True(await page.Locator("#approvals-button").IsDisabledAsync());
                Assert.True(await page.Locator("#memory-button").IsDisabledAsync());
                Assert.True(await page.Locator("#household-button").IsDisabledAsync());
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
            await page.Locator("#messages").GetByText("Controlled streaming response", new LocatorGetByTextOptions { Exact = false })
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
            await page.Locator("#messages").GetByText("Controlled streaming response", new LocatorGetByTextOptions { Exact = false })
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
    [Trait("Category", "BrowserE2E")]
    public async Task HouseholdMemoryApprovalAndActivitySurfacesReflowAndKeepKeyboardFocusVisible()
    {
        var isolated = new SimulatorHostFixture();
        await isolated.InitializeAsync();
        try
        {
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(
                new BrowserTypeLaunchOptions { Headless = true });
            var page = await browser.NewPageAsync(new BrowserNewPageOptions
            {
                ViewportSize = new ViewportSize { Width = 390, Height = 844 }
            });
            await page.GotoAsync(isolated.WebClient.BaseAddress!.ToString());
            await page.GetByLabel("Passphrase").FillAsync(SimulatorHostFixture.OwnerPassphrase);
            await page.Locator("#auth-submit").ClickAsync();
            await page.WaitForFunctionAsync("() => !authenticationInFlight && !document.getElementById('chat').hidden");
            await page.RouteAsync("**/api/status", route => route.FulfillAsync(new RouteFulfillOptions
            {
                Status = 503,
                ContentType = "application/problem+json",
                Body = """{"title":"Controlled status failure."}"""
            }));
            await page.ReloadAsync();
            await page.GetByRole(AriaRole.Alert)
                .Filter(new LocatorFilterOptions { HasText = "Controlled status failure." }).WaitForAsync();
            Assert.True(await page.Locator("#chat").IsVisibleAsync());

            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Approvals" }).ClickAsync();
            await page.Locator("#approvals-status").GetByText("0 pending approvals").WaitForAsync();
            Assert.Contains("No recent approval", await page.Locator("#approval-list").InnerTextAsync(), StringComparison.Ordinal);

            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Memory" }).ClickAsync();
            await page.Locator("#memory-status").GetByText("0 active facts").WaitForAsync();
            Assert.Contains("No active remembered facts", await page.Locator("#memory-list").InnerTextAsync(), StringComparison.Ordinal);
            Assert.True(await page.GetByLabel("Change type").IsVisibleAsync());

            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Household" }).ClickAsync();
            await page.GetByText("connection probe: not-run", new PageGetByTextOptions { Exact = false }).WaitForAsync();
            Assert.Contains("light.simulator_lamp", await page.Locator("#household-list").InnerTextAsync(), StringComparison.Ordinal);
            Assert.DoesNotContain("synthetic-home-token", await page.Locator("#household").InnerTextAsync(), StringComparison.Ordinal);

            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Activity" }).ClickAsync();
            await page.Locator("#activity-status").GetByText("Recent local action outcomes").WaitForAsync();
            Assert.True(await page.Locator("#activity").IsVisibleAsync());
            Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= window.innerWidth"));

            await page.Locator("#settings-button").FocusAsync();
            await page.Keyboard.PressAsync("Tab");
            Assert.Equal("activity-button", await page.EvaluateAsync<string>("document.activeElement.id"));
            Assert.NotEqual("none", await page.EvaluateAsync<string>(
                "getComputedStyle(document.activeElement).outlineStyle"));

            var surfaces = new[]
            {
                ("#approvals-button", "#approvals-status", "0 pending approvals", "approvals.png"),
                ("#memory-button", "#memory-status", "0 active facts", "memory.png"),
                ("#household-button", "#household-status", "connection probe: not-run", "household.png"),
                ("#activity-button", "#activity-status", "Recent local action outcomes", "activity.png")
            };
            var workspaceRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
            var screenshotDirectory = Path.GetFullPath(Path.Combine(
                workspaceRoot,
                Environment.GetEnvironmentVariable("JARVIS_M204_SCREENSHOTS") ?? Path.Combine("artifacts", "m204")));
            foreach (var width in new[] { 1440, 768, 390, 320 })
            {
                await page.SetViewportSizeAsync(width, 844);
                foreach (var (button, status, expected, screenshotName) in surfaces)
                {
                    await page.Locator(button).ClickAsync();
                    await page.Locator(status).GetByText(expected, new LocatorGetByTextOptions
                    {
                        Exact = false
                    }).WaitForAsync();
                    if (width == 1440 || (width == 390 && button == "#household-button"))
                    {
                        if (width == 390)
                        {
                            await page.EvaluateAsync(
                                "const actions = document.querySelector('.sidebar-actions'); actions.scrollLeft = actions.scrollWidth - actions.clientWidth");
                            await page.Mouse.MoveAsync(380, 240);
                        }
                        Directory.CreateDirectory(screenshotDirectory);
                        var fileName = width == 390 ? "household-mobile-390.png" : screenshotName;
                        await page.ScreenshotAsync(new PageScreenshotOptions
                        {
                            Path = Path.Combine(screenshotDirectory, fileName),
                            FullPage = true
                        });
                    }
                    Assert.True(await page.EvaluateAsync<bool>(
                        "document.documentElement.scrollWidth <= window.innerWidth"),
                        $"Workspace surface {button} overflows at {width}px.");
                }
            }

            var longSubject = new string('s', 180);
            var longValue = new string('v', 420);
            var longEntityId = $"sensor.{new string('e', 180)}";
            var longFriendlyName = new string('n', 180);
            await page.RouteAsync("**/api/memory**", route => route.FulfillAsync(new RouteFulfillOptions
            {
                Status = 200,
                ContentType = "application/json",
                Body = $$"""[{"id":{"value":"11111111-1111-1111-1111-111111111111"},"subject":"{{longSubject}}","key":"{{longSubject}}","value":"{{longValue}}","version":1,"sourceType":"Legacy","sourceAvailable":false,"updatedAtUtc":"2025-01-01T00:00:00Z"}]"""
            }));
            await page.RouteAsync("**/api/household", route => route.FulfillAsync(new RouteFulfillOptions
            {
                Status = 200,
                ContentType = "application/json",
                Body = $$"""{"configured":true,"connectionStatus":"configured","connectionProbe":"not-run","entities":[{"entityId":"{{longEntityId}}","friendlyName":"{{longFriendlyName}}","area":null,"unit":"°C","operations":[]}]}"""
            }));
            await page.SetViewportSizeAsync(320, 844);
            await page.Locator("#memory-button").ClickAsync();
            await page.Locator("#memory-status").GetByText("1 active fact shown").WaitForAsync();
            Assert.Contains(longValue, await page.Locator("#memory-list").InnerTextAsync(), StringComparison.Ordinal);
            Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= window.innerWidth"));

            await page.Locator("#household-button").ClickAsync();
            await page.Locator("#household-status").GetByText("connection probe: not-run").WaitForAsync();
            Assert.Contains(longEntityId, await page.Locator("#household-list").InnerTextAsync(), StringComparison.Ordinal);
            Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= window.innerWidth"));
            await page.UnrouteAsync("**/api/status");
        }
        finally
        {
            await isolated.DisposeAsync();
        }
    }

    [Fact]
    [Trait("Category", "BrowserE2E")]
    public async Task ApprovedUnclaimedActionOffersAnExplicitResumeControl()
    {
        var isolated = new SimulatorHostFixture();
        await isolated.InitializeAsync();
        try
        {
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(
                new BrowserTypeLaunchOptions { Headless = true });
            var page = await browser.NewPageAsync();
            await page.GotoAsync(isolated.WebClient.BaseAddress!.ToString());
            await page.GetByLabel("Passphrase").FillAsync(SimulatorHostFixture.OwnerPassphrase);
            await page.Locator("#auth-submit").ClickAsync();
            await page.WaitForFunctionAsync("() => !authenticationInFlight && !document.getElementById('chat').hidden");

            var approvalId = Guid.NewGuid();
            var actionId = approvalId;
            var actionStatus = "AwaitingApproval";
            string? decisionBody = null;
            await page.RouteAsync("**/api/approvals", route =>
            {
                var responseBody = JsonSerializer.Serialize(new[]
                {
                    new
                    {
                        approval = new
                        {
                            id = new { value = approvalId },
                            actionId = new { value = actionId },
                            expiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(3),
                            expectedVersion = 2,
                            status = "Approved"
                        },
                        actionId = new { value = actionId },
                        toolName = "home.set_light",
                        actionType = "home.set_light",
                        operation = "turn_on",
                        entityId = "light.porch",
                        actionStatus,
                        argumentsJson = """{"Entity":"light.porch","On":true}"""
                    }
                });
                return route.FulfillAsync(new RouteFulfillOptions { Status = 200, Body = responseBody });
            });
            await page.RouteAsync("**/api/approvals/*/decision", async route =>
            {
                decisionBody = route.Request.PostData;
                actionStatus = "Succeeded";
                await route.FulfillAsync(new RouteFulfillOptions
                {
                    Status = 200,
                    Body = """{"status":"Succeeded"}"""
                });
            });

            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Approvals" }).ClickAsync();
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Resume approved action" })
                .WaitForAsync();
            Assert.Equal(0, await page.GetByRole(
                AriaRole.Button, new PageGetByRoleOptions { Name = "Approve exact action" }).CountAsync());
            await page.GetByRole(
                AriaRole.Button, new PageGetByRoleOptions { Name = "Resume approved action" }).ClickAsync();
            await page.GetByText("0 approved actions resumable").WaitForAsync();

            Assert.NotNull(decisionBody);
            using var submitted = JsonDocument.Parse(decisionBody);
            Assert.True(submitted.RootElement.GetProperty("approved").GetBoolean());
            Assert.Equal(2, submitted.RootElement.GetProperty("expectedVersion").GetInt64());
        }
        finally
        {
            await isolated.DisposeAsync();
        }
    }

    [Fact]
    [Trait("Category", "BrowserE2E")]
    public async Task ExpiredApprovedActionOffersNoExecutionFinalization()
    {
        var isolated = new SimulatorHostFixture();
        await isolated.InitializeAsync();
        try
        {
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(
                new BrowserTypeLaunchOptions { Headless = true });
            var page = await browser.NewPageAsync();
            await page.GotoAsync(isolated.WebClient.BaseAddress!.ToString());
            await page.GetByLabel("Passphrase").FillAsync(SimulatorHostFixture.OwnerPassphrase);
            await page.Locator("#auth-submit").ClickAsync();
            await page.WaitForFunctionAsync("() => !authenticationInFlight && !document.getElementById('chat').hidden");

            var approvalId = Guid.NewGuid();
            var actionStatus = "AwaitingApproval";
            string? expiryBody = null;
            await page.RouteAsync("**/api/approvals", route =>
            {
                var responseBody = JsonSerializer.Serialize(new[]
                {
                    new
                    {
                        approval = new
                        {
                            id = new { value = approvalId },
                            actionId = new { value = approvalId },
                            expiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
                            expectedVersion = 2,
                            status = "Approved"
                        },
                        actionId = new { value = approvalId },
                        toolName = "home.set_light",
                        actionType = "home.set_light",
                        operation = "turn_on",
                        entityId = "light.porch",
                        actionStatus,
                        argumentsJson = """{"Entity":"light.porch","On":true}"""
                    }
                });
                return route.FulfillAsync(new RouteFulfillOptions { Status = 200, Body = responseBody });
            });
            await page.RouteAsync("**/api/approvals/*/expire", async route =>
            {
                expiryBody = route.Request.PostData;
                actionStatus = "Expired";
                await route.FulfillAsync(new RouteFulfillOptions
                {
                    Status = 200,
                    Body = """{"status":"Expired","reasonCode":"approval_expired_before_execution"}"""
                });
            });

            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Approvals" }).ClickAsync();
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Finalize expired action" })
                .WaitForAsync();
            Assert.Equal(0, await page.GetByRole(
                AriaRole.Button, new PageGetByRoleOptions { Name = "Resume approved action" }).CountAsync());
            await page.GetByRole(
                AriaRole.Button, new PageGetByRoleOptions { Name = "Finalize expired action" }).ClickAsync();
            await page.GetByText("0 approved actions resumable").WaitForAsync();

            Assert.NotNull(expiryBody);
            using var submitted = JsonDocument.Parse(expiryBody);
            Assert.Equal(2, submitted.RootElement.GetProperty("expectedVersion").GetInt64());
        }
        finally
        {
            await isolated.DisposeAsync();
        }
    }

    [Fact]
    [Trait("Category", "BrowserE2E")]
    public async Task MemoryProposalRequiresExactOwnerApprovalBeforeItAppearsAsAFact()
    {
        var isolated = new SimulatorHostFixture();
        await isolated.InitializeAsync();
        try
        {
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(
                new BrowserTypeLaunchOptions { Headless = true });
            var page = await browser.NewPageAsync();
            await page.GotoAsync(isolated.WebClient.BaseAddress!.ToString());
            await page.GetByLabel("Passphrase").FillAsync(SimulatorHostFixture.OwnerPassphrase);
            await page.Locator("#auth-submit").ClickAsync();
            await page.WaitForFunctionAsync("() => !authenticationInFlight && !document.getElementById('chat').hidden");
            await page.Locator("#new-conversation").ClickAsync();
            await page.WaitForFunctionAsync("() => conversationId !== '' && !conversationCreationInFlight");

            using var scriptedTool = await isolated.ModelClient.PostAsJsonAsync(
                "/fixture/control/next-tool-call",
                new
                {
                    name = "memory_propose_change",
                    arguments = """{"Operation":"create","Subject":"synthetic room","Key":"lamp","Value":"blue","ExpectedVersion":0,"Conflicts":[]}"""
                });
            Assert.Equal(System.Net.HttpStatusCode.NoContent, scriptedTool.StatusCode);
            await page.GetByLabel("Message").FillAsync("Remember that the synthetic room lamp is blue.");
            await page.Locator("#send-turn").ClickAsync();
            await page.WaitForFunctionAsync("() => activeTurnId === '' && !turnSubmissionInFlight");

            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Approvals" }).ClickAsync();
            await page.Locator("#approval-list").GetByText(
                "Tool: memory.propose_change",
                new LocatorGetByTextOptions { Exact = false }).WaitForAsync();
            var preview = await page.Locator("#approval-list").InnerTextAsync();
            Assert.Contains("Pending", preview, StringComparison.Ordinal);
            Assert.Contains("synthetic room", preview, StringComparison.Ordinal);
            Assert.True(await page.Locator("#approval-list .approval-arguments").IsVisibleAsync());
            Assert.DoesNotContain("OwnerId", preview, StringComparison.Ordinal);
            var decisionResponseTask = page.WaitForResponseAsync(response =>
                response.Url.Contains("/api/approvals/", StringComparison.Ordinal)
                && response.Request.Method == "POST");
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Approve exact action" }).ClickAsync();
            var decisionResponse = await decisionResponseTask;
            var decisionBody = await decisionResponse.TextAsync();
            var decisionRequest = decisionResponse.Request.PostData;
            Assert.True(decisionResponse.Status == 200, $"{decisionBody} Request: {decisionRequest}");
            await page.WaitForFunctionAsync(
                """
                () => {
                    const status = document.getElementById("approvals-status").textContent;
                    return !status.includes("Submitting approval") && !status.includes("Loading owner approvals");
                }
                """);
            var decisionStatus = await page.Locator("#approvals-status").InnerTextAsync();
            Assert.Contains("0 pending approvals", decisionStatus, StringComparison.Ordinal);

            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Memory" }).ClickAsync();
            await page.Locator("#memory-list").GetByText("synthetic room · lamp").WaitForAsync();
            Assert.Contains("blue", await page.Locator("#memory-list").InnerTextAsync(), StringComparison.Ordinal);
        }
        finally
        {
            await isolated.DisposeAsync();
        }
    }

    [Fact]
    [Trait("Category", "BrowserE2E")]
    public async Task MemoryUiCreatesAndApprovesTypedProposalWithoutInference()
    {
        var isolated = new SimulatorHostFixture();
        await isolated.InitializeAsync();
        try
        {
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(
                new BrowserTypeLaunchOptions { Headless = true });
            var page = await browser.NewPageAsync();
            await page.GotoAsync(isolated.WebClient.BaseAddress!.ToString());
            await page.WaitForFunctionAsync(
                "() => !document.getElementById('auth').hidden || !document.getElementById('chat').hidden");
            if (!await page.Locator("#auth").IsHiddenAsync())
            {
                await page.GetByLabel("Passphrase").FillAsync(SimulatorHostFixture.OwnerPassphrase);
                await page.Locator("#auth-submit").ClickAsync();
                await page.WaitForFunctionAsync("() => !authenticationInFlight && !document.getElementById('chat').hidden");
            }

            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Memory" }).ClickAsync();
            await page.GetByLabel("Subject").FillAsync("typed room");
            await page.GetByLabel("Fact key").FillAsync("curtain");
            await page.GetByLabel("Exact fact value").FillAsync("linen");
            var proposalResponseTask = page.WaitForResponseAsync(response =>
                response.Url.EndsWith("/api/memory/proposals", StringComparison.Ordinal)
                && response.Request.Method == "POST");
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Prepare exact proposal" }).ClickAsync();
            var proposalResponse = await proposalResponseTask;
            Assert.Equal(200, proposalResponse.Status);
            await page.Locator("#memory-status").GetByText("inference was not required").WaitForAsync();
            Assert.Empty(await page.Locator("#messages .user-message").AllTextContentsAsync());

            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Approvals" }).ClickAsync();
            await page.Locator("#approval-list").GetByText(
                "Tool: memory.propose_change",
                new LocatorGetByTextOptions { Exact = false }).WaitForAsync();
            var approvalPreview = await page.Locator("#approval-list").InnerTextAsync();
            Assert.Contains("typed room", approvalPreview, StringComparison.Ordinal);
            Assert.Contains("linen", approvalPreview, StringComparison.Ordinal);
            Assert.True(await page.Locator("#approval-list .approval-arguments").IsVisibleAsync());
            var decisionResponseTask = page.WaitForResponseAsync(response =>
                response.Url.Contains("/api/approvals/", StringComparison.Ordinal)
                && response.Request.Method == "POST");
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Approve exact action" }).ClickAsync();
            Assert.Equal(200, (await decisionResponseTask).Status);

            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Memory", Exact = true }).ClickAsync();
            await page.Locator("#memory-list").GetByText("typed room · curtain").WaitForAsync();
            Assert.Contains("linen", await page.Locator("#memory-list").InnerTextAsync(), StringComparison.Ordinal);

            await page.GetByLabel("Subject").FillAsync("typed room");
            await page.GetByLabel("Fact key").FillAsync("curtain");
            await page.GetByLabel("Exact fact value").FillAsync("velvet");
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Prepare exact proposal" }).ClickAsync();
            await page.Locator("#memory-conflict-review").WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible
            });
            Assert.Contains("linen", await page.Locator("#memory-conflict-list").InnerTextAsync(), StringComparison.Ordinal);
            await page.GetByLabel("Conflict decision").SelectOptionAsync("retain_both");
            await page.GetByLabel("Distinguishing context (required to keep both)").FillAsync("guest room preference");
            var conflictProposalTask = page.WaitForResponseAsync(response =>
                response.Url.EndsWith("/api/memory/proposals", StringComparison.Ordinal)
                && response.Request.Method == "POST");
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Prepare exact proposal" }).ClickAsync();
            Assert.Equal(200, (await conflictProposalTask).Status);
            await page.Locator("#memory-status").GetByText("inference was not required").WaitForAsync();

            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Approvals" }).ClickAsync();
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Approve exact action" }).ClickAsync();
            await page.Locator("#approvals-status").GetByText("0 pending approvals").WaitForAsync();
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Memory", Exact = true }).ClickAsync();
            await page.Locator("#memory-list").GetByText("velvet").WaitForAsync();
            Assert.Contains("guest room preference", await page.Locator("#memory-list").InnerTextAsync(), StringComparison.Ordinal);
        }
        finally
        {
            await isolated.DisposeAsync();
        }
    }

    [Fact]
    [Trait("Category", "BrowserE2E")]
    public async Task DelayedMemoryProposalCannotResetFormOrSelectionAfterConversationSwitch()
    {
        var isolated = new SimulatorHostFixture();
        await isolated.InitializeAsync();
        var proposalReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseProposal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string? firstProposalRequestJson = null;
        var proposalRequestCount = 0;
        try
        {
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(
                new BrowserTypeLaunchOptions { Headless = true });
            var page = await browser.NewPageAsync();
            await page.GotoAsync(isolated.WebClient.BaseAddress!.ToString());
            await page.WaitForFunctionAsync(
                "() => !document.getElementById('auth').hidden || !document.getElementById('chat').hidden");
            if (!await page.Locator("#auth").IsHiddenAsync())
            {
                await page.GetByLabel("Passphrase").FillAsync(SimulatorHostFixture.OwnerPassphrase);
                await page.Locator("#auth-submit").ClickAsync();
                await page.WaitForFunctionAsync(
                    "() => !authenticationInFlight && !document.getElementById('chat').hidden");
            }

            async Task<Guid> CreateConversationAsync(string title) =>
                await page.EvaluateAsync<Guid>(
                    """
                    async title => {
                        const created = await api("/api/conversations", {
                            method: "POST",
                            headers: { "Content-Type": "application/json" },
                            body: JSON.stringify({ title })
                        });
                        await loadConversations();
                        return created.id.value || created.id;
                    }
                    """,
                    title);

            var proposalConversationId = await CreateConversationAsync("Proposal source");
            var selectedConversationId = await CreateConversationAsync("Current selection");
            await page.Locator(
                $"#conversation-list button[data-conversation-id='{proposalConversationId:D}']").ClickAsync();
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Memory" }).ClickAsync();
            await page.GetByLabel("Subject").FillAsync("stale response room");
            await page.GetByLabel("Fact key").FillAsync("curtain");
            await page.GetByLabel("Exact fact value").FillAsync("preserve this draft");
            await page.RouteAsync("**/api/memory/proposals", async route =>
            {
                if (++proposalRequestCount == 1)
                {
                    firstProposalRequestJson = route.Request.PostData;
                    proposalReached.TrySetResult();
                    await releaseProposal.Task.WaitAsync(TimeSpan.FromSeconds(15));
                }
                await route.ContinueAsync();
            });
            var responseTask = page.WaitForResponseAsync(response =>
                response.Url.EndsWith("/api/memory/proposals", StringComparison.Ordinal)
                && response.Request.Method == "POST");
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Prepare exact proposal" }).ClickAsync();
            await proposalReached.Task.WaitAsync(TimeSpan.FromSeconds(15));

            await page.Locator(
                $"#conversation-list button[data-conversation-id='{selectedConversationId:D}']").ClickAsync();
            await page.WaitForFunctionAsync(
                $"() => conversationId === '{selectedConversationId:D}'");
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Memory", Exact = true }).ClickAsync();
            releaseProposal.SetResult();
            Assert.Equal(200, (await responseTask).Status);

            Assert.Equal(selectedConversationId.ToString("D"),
                await page.EvaluateAsync<string>("conversationId"));
            Assert.Equal("preserve this draft", await page.GetByLabel("Exact fact value").InputValueAsync());
            Assert.Equal(selectedConversationId.ToString("D"),
                await page.Locator("#conversation-list button[aria-current='page']")
                    .GetAttributeAsync("data-conversation-id"));

            using var firstProposalRequest = JsonDocument.Parse(firstProposalRequestJson!);
            var firstConversation = firstProposalRequest.RootElement.GetProperty("conversationId").GetString();
            var firstRequestId = firstProposalRequest.RootElement.GetProperty("requestId").GetGuid();
            Assert.Equal(proposalConversationId.ToString("D"), firstConversation);

            var retryResponseTask = page.WaitForResponseAsync(response =>
                response.Url.EndsWith("/api/memory/proposals", StringComparison.Ordinal)
                && response.Request.Method == "POST");
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Prepare exact proposal" }).ClickAsync();
            var retryResponse = await retryResponseTask;
            Assert.Equal(200, retryResponse.Status);
            using var retryProposalRequest = JsonDocument.Parse(retryResponse.Request.PostData!);
            Assert.Equal(firstConversation,
                retryProposalRequest.RootElement.GetProperty("conversationId").GetString());
            Assert.Equal(firstRequestId,
                retryProposalRequest.RootElement.GetProperty("requestId").GetGuid());
            Assert.Equal(selectedConversationId.ToString("D"),
                await page.EvaluateAsync<string>("conversationId"));
            Assert.Equal(string.Empty, await page.GetByLabel("Exact fact value").InputValueAsync());
            Assert.Equal(2, proposalRequestCount);
        }
        finally
        {
            releaseProposal.TrySetResult();
            await isolated.DisposeAsync();
        }
    }

    [Fact]
    [Trait("Category", "BrowserE2E")]
    public async Task DelayedMemoryProposalResponsePreservesAnEditedDraft()
    {
        var isolated = new SimulatorHostFixture();
        await isolated.InitializeAsync();
        var proposalReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseProposal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string? proposalRequestJson = null;
        try
        {
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(
                new BrowserTypeLaunchOptions { Headless = true });
            var page = await browser.NewPageAsync();
            await page.GotoAsync(isolated.WebClient.BaseAddress!.ToString());
            await page.WaitForFunctionAsync(
                "() => !document.getElementById('auth').hidden || !document.getElementById('chat').hidden");
            if (!await page.Locator("#auth").IsHiddenAsync())
            {
                await page.GetByLabel("Passphrase").FillAsync(SimulatorHostFixture.OwnerPassphrase);
                await page.Locator("#auth-submit").ClickAsync();
                await page.WaitForFunctionAsync(
                    "() => !authenticationInFlight && !document.getElementById('chat').hidden");
            }

            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Memory" }).ClickAsync();
            await page.GetByLabel("Subject").FillAsync("draft room");
            await page.GetByLabel("Fact key").FillAsync("curtain");
            await page.GetByLabel("Exact fact value").FillAsync("submitted value");
            await page.RouteAsync("**/api/memory/proposals", async route =>
            {
                proposalRequestJson = route.Request.PostData;
                proposalReached.TrySetResult();
                await releaseProposal.Task.WaitAsync(TimeSpan.FromSeconds(15));
                await route.ContinueAsync();
            });

            var responseTask = page.WaitForResponseAsync(response =>
                response.Url.EndsWith("/api/memory/proposals", StringComparison.Ordinal)
                && response.Request.Method == "POST");
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Prepare exact proposal" }).ClickAsync();
            await proposalReached.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await page.GetByLabel("Exact fact value").FillAsync("newer unsent value");
            releaseProposal.SetResult();
            Assert.Equal(200, (await responseTask).Status);

            using var sentProposal = JsonDocument.Parse(proposalRequestJson!);
            Assert.Equal("submitted value", sentProposal.RootElement.GetProperty("value").GetString());
            Assert.Equal("newer unsent value", await page.GetByLabel("Exact fact value").InputValueAsync());
            Assert.Contains(
                "your newer editor changes were preserved",
                await page.Locator("#memory-status").InnerTextAsync(),
                StringComparison.OrdinalIgnoreCase);

            var retryResponseTask = page.WaitForResponseAsync(response =>
                response.Url.EndsWith("/api/memory/proposals", StringComparison.Ordinal)
                && response.Request.Method == "POST");
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Prepare exact proposal" }).ClickAsync();
            var retryResponse = await retryResponseTask;
            Assert.Equal(200, retryResponse.Status);
            using var retryProposal = JsonDocument.Parse(retryResponse.Request.PostData!);
            Assert.Equal("newer unsent value", retryProposal.RootElement.GetProperty("value").GetString());
            Assert.NotEqual(
                sentProposal.RootElement.GetProperty("requestId").GetGuid(),
                retryProposal.RootElement.GetProperty("requestId").GetGuid());
        }
        finally
        {
            releaseProposal.TrySetResult();
            await isolated.DisposeAsync();
        }
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
