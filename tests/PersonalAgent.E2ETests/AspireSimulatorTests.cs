using System.Net;
using System.Text.Json;
using Xunit;

namespace PersonalAgent.E2ETests;

[Collection(SimulatorCollection.Name)]
public sealed class AspireSimulatorTests(SimulatorHostFixture fixture)
{
    [Fact]
    [Trait("Category", "AspireE2E")]
    public async Task SimulatorStartsWebAndDiscoversDeterministicManagedEndpoints()
    {
        using var webResponse = await fixture.WebClient.GetAsync("/health/ready");
        using var modelResponse = await fixture.ModelClient.GetAsync("/fixture/model");
        using var homeResponse = await fixture.HomeAssistantClient.GetAsync("/fixture/entities");
        var page = await fixture.WebClient.GetStringAsync("/");
        var model = await modelResponse.Content.ReadAsStringAsync();
        var home = await homeResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, webResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, modelResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, homeResponse.StatusCode);
        Assert.Contains("Running the E2E profile.", page, StringComparison.Ordinal);
        Assert.Equal("controlled fixture response", JsonDocument.Parse(model).RootElement.GetProperty("completion").GetString());
        Assert.Equal(
            "light.simulator_lamp",
            JsonDocument.Parse(home).RootElement[0].GetProperty("entityId").GetString());
    }
}
