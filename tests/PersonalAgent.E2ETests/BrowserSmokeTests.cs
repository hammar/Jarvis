using Microsoft.Playwright;
using Xunit;

namespace PersonalAgent.E2ETests;

[Collection(SimulatorCollection.Name)]
public sealed class BrowserSmokeTests(SimulatorHostFixture fixture)
{
    [Fact]
    [Trait("Category", "BrowserE2E")]
    public async Task PlaywrightLoadsTheAspireSimulatorPage()
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
            await page.GetByText("Running the E2E profile.").WaitForAsync();
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
