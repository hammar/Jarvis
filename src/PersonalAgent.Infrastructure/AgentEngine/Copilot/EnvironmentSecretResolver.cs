using PersonalAgent.Application;

namespace PersonalAgent.Infrastructure.AgentEngine.Copilot;

/// <summary>Resolves trusted secret references from named environment variables without logging their values.</summary>
public sealed class EnvironmentSecretResolver : ISecretResolver
{
    /// <inheritdoc />
    public ValueTask<ReadOnlyMemory<char>> ResolveAsync(
        SecretReference reference,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reference);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(reference.Name)
            || reference.Name.Length > 256
            || reference.Name.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '_' or '-')))
        {
            throw new ArgumentException("Secret references must be valid configured environment-variable names.", nameof(reference));
        }

        var value = Environment.GetEnvironmentVariable(reference.Name);
        if (string.IsNullOrEmpty(value))
        {
            throw new InvalidOperationException("The configured provider secret reference is unavailable.");
        }

        return ValueTask.FromResult<ReadOnlyMemory<char>>(value.AsMemory());
    }
}
