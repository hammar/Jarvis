using Microsoft.Playwright;
using System.Text.Json;
using Xunit;

namespace PersonalAgent.E2ETests;

[Collection(SimulatorCollection.Name)]
public sealed class BrowserSmokeTests(SimulatorHostFixture fixture)
{
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
            await page.Locator("#new-conversation").ClickAsync();
            await page.WaitForFunctionAsync("() => conversationId !== ''");
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
                    const originalFetch = window.fetch.bind(window);
                    window.fetch = async (input, init = {}) => {
                        const url = typeof input === "string" ? input : input.url;
                        const method = (init.method || (input instanceof Request ? input.method : "GET")).toUpperCase();
                        const response = await originalFetch(input, init);
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
            await page.GetByText("Controlled streaming response", new PageGetByTextOptions { Exact = false })
                .WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
            await page.Locator("#cancel-turn").WaitForAsync(
                new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 15_000 });
            await page.EvaluateAsync("window.__releaseStaleRead()");
            await page.EvaluateAsync("window.__staleOpenPromise");
            Assert.Equal(submittedConversationId, await page.EvaluateAsync<string>("conversationId"));
            var streamedReplies = page.Locator("#messages li")
                .Filter(new LocatorFilterOptions { HasText = "Controlled streaming response" });
            Assert.Equal(1, await streamedReplies.CountAsync());

            await page.Locator("#new-conversation").ClickAsync();
            await page.Locator("#conversation-list button").Nth(1)
                .WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
            Assert.Equal(2, await page.Locator("#conversation-list button").CountAsync());
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
            await cancelButton.ClickAsync();
            await cancelButton.WaitForAsync(
                new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 15_000 });
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Activity" }).ClickAsync();
            await page.GetByText("Cancelled", new PageGetByTextOptions { Exact = false })
                .WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });

            await page.Locator("#conversation-list button").Nth(1).ClickAsync();
            await page.GetByText("Controlled streaming response", new PageGetByTextOptions { Exact = false })
                .WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });
            await fixture.RestartAsync();
            await page.GotoAsync(fixture.WebClient.BaseAddress!.ToString());
            await page.WaitForFunctionAsync(
                "() => !document.getElementById('auth').hidden || !document.getElementById('chat').hidden");
            if (await page.Locator("#auth").IsVisibleAsync())
            {
                await page.GetByLabel("Passphrase").FillAsync(SimulatorHostFixture.OwnerPassphrase);
                await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Sign in" }).ClickAsync();
            }

            var savedConversation = page.Locator("#conversation-list")
                .GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "New conversation" })
                .Nth(1);
            await savedConversation.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
            await savedConversation.ClickAsync();
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
