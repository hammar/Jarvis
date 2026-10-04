using System.Reflection;
using Xunit;

namespace PersonalAgent.SdkContractTests;

public sealed class SdkPinTests
{
    [Fact]
    [Trait("Category", "SdkContract")]
    public void PinnedCopilotSdkAssemblyIsPresentForTheInfrastructureAdapter()
    {
        var sdk = Assembly.Load("GitHub.Copilot.SDK");
        var informationalVersion = sdk
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        Assert.NotNull(informationalVersion);
        Assert.StartsWith("1.0.16", informationalVersion, StringComparison.Ordinal);
    }
}
