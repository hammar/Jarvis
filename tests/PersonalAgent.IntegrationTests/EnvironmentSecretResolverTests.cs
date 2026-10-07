using PersonalAgent.Application;
using PersonalAgent.Infrastructure.AgentEngine.Copilot;
using Xunit;

namespace PersonalAgent.IntegrationTests;

public sealed class EnvironmentSecretResolverTests
{
    [Theory]
    [InlineData("controlled-fixture-value")]
    [InlineData("")]
    [InlineData(null)]
    [Trait("Category", "Integration")]
    public async Task NamedLookupReturnsFixtureOrExplicitlyRejectsUnavailableValue(string? value)
    {
        var name = $"JARVIS_FIXTURE_{Guid.NewGuid():N}";
        var previous = Environment.GetEnvironmentVariable(name);
        try
        {
            Environment.SetEnvironmentVariable(name, value);
            var resolver = new EnvironmentSecretResolver();
            if (string.IsNullOrEmpty(value))
            {
                var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                    await resolver.ResolveAsync(new SecretReference(name), CancellationToken.None));
                Assert.Equal("The configured provider secret reference is unavailable.", error.Message);
                Assert.DoesNotContain(name, error.Message, StringComparison.Ordinal);
            }
            else
            {
                Assert.Equal(value,
                    (await resolver.ResolveAsync(new SecretReference(name), CancellationToken.None)).ToString());
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, previous);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("not=a-name")]
    [InlineData("non-ascii-\u00e9")]
    [Trait("Category", "Integration")]
    public async Task InvalidNamesAreRejectedBeforeLookup(string name)
    {
        var error = await Assert.ThrowsAsync<ArgumentException>(async () =>
            await new EnvironmentSecretResolver().ResolveAsync(new SecretReference(name), CancellationToken.None));
        Assert.Equal("reference", error.ParamName);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task NullOrOversizedReferenceAndCancellationAreExplicit()
    {
        var resolver = new EnvironmentSecretResolver();
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await resolver.ResolveAsync(null!, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await resolver.ResolveAsync(new SecretReference(new string('a', 257)), CancellationToken.None));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await resolver.ResolveAsync(new SecretReference($"JARVIS_FIXTURE_{Guid.NewGuid():N}"), cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
    }
}
