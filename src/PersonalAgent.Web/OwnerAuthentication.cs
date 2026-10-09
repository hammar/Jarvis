using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using PersonalAgent.Application;

namespace PersonalAgent.Web;

/// <summary>Provides single-owner bootstrap and passphrase authentication without retaining plaintext credentials.</summary>
public sealed class OwnerAuthenticationService
{
    private const string OwnerId = "owner";
    private readonly IOwnerAccountStore accounts;
    private readonly IPasswordHasher<OwnerIdentity> passwordHasher;
    private readonly IClock clock;
    private readonly string bootstrapToken;
    private int failedBootstrapAttempts;

    /// <summary>Creates a process-local one-time setup token; the token is consumed by durable owner creation.</summary>
    /// <param name="accounts">Application-owned owner account persistence.</param>
    /// <param name="passwordHasher">Adaptive password hashing implementation.</param>
    /// <param name="clock">UTC clock for account creation timestamps.</param>
    /// <param name="bootstrapToken">Optional trusted token for the isolated E2E profile; production generates one.</param>
    public OwnerAuthenticationService(
        IOwnerAccountStore accounts,
        IPasswordHasher<OwnerIdentity> passwordHasher,
        IClock clock,
        string? bootstrapToken = null)
    {
        this.accounts = accounts;
        this.passwordHasher = passwordHasher;
        this.clock = clock;
        this.bootstrapToken = string.IsNullOrWhiteSpace(bootstrapToken)
            ? Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            : bootstrapToken;
    }

    /// <summary>Gets the one-time token for local console display while bootstrap is pending.</summary>
    public string BootstrapToken => bootstrapToken;

    /// <summary>Gets whether the owner account has been created.</summary>
    public ValueTask<bool> IsOwnerConfiguredAsync(CancellationToken cancellationToken) =>
        accounts.IsConfiguredAsync(cancellationToken);

    /// <summary>Verifies and atomically consumes the setup token to create the owner account.</summary>
    /// <param name="providedToken">One-time local bootstrap token.</param>
    /// <param name="passphrase">New owner passphrase; at least 12 characters.</param>
    /// <param name="cancellationToken">Token that cancels persistence.</param>
    /// <returns>True only when the setup token was valid and the account was created.</returns>
    public async ValueTask<bool> BootstrapAsync(
        string providedToken,
        string passphrase,
        CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref failedBootstrapAttempts, 0, 0) >= 5
            || !FixedTimeEquals(bootstrapToken, providedToken)
            || !ValidPassphrase(passphrase)
            || await accounts.IsConfiguredAsync(cancellationToken))
        {
            Interlocked.Increment(ref failedBootstrapAttempts);
            return false;
        }

        var owner = new OwnerIdentity(OwnerId);
        var hash = passwordHasher.HashPassword(owner, passphrase);
        var created = await accounts.TryCreateOwnerAsync(hash, clock.UtcNow, cancellationToken);
        if (!created)
        {
            return false;
        }

        return true;
    }

    /// <summary>Validates a passphrase against the stored adaptive hash.</summary>
    /// <param name="passphrase">Candidate passphrase.</param>
    /// <param name="cancellationToken">Token that cancels the account read.</param>
    /// <returns>True only when an owner account exists and the password hash verifies.</returns>
    public async ValueTask<bool> VerifyAsync(string passphrase, CancellationToken cancellationToken)
    {
        var hash = await accounts.GetPasswordHashAsync(cancellationToken);
        if (hash is null || string.IsNullOrEmpty(passphrase) || passphrase.Length > 1024)
        {
            return false;
        }

        return passwordHasher.VerifyHashedPassword(
            new OwnerIdentity(OwnerId),
            hash,
            passphrase) is PasswordVerificationResult.Success or PasswordVerificationResult.SuccessRehashNeeded;
    }

    /// <summary>Signs the owner into a protected, bounded-duration browser session.</summary>
    /// <param name="context">Authenticated HTTP request context.</param>
    /// <param name="persistent">Whether the browser should retain the session cookie.</param>
    /// <param name="cancellationToken">Token that cancels cookie issuance.</param>
    public Task SignInAsync(HttpContext context, bool persistent, CancellationToken cancellationToken)
    {
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, OwnerId)],
            CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);
        return context.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            principal,
            new AuthenticationProperties
            {
                IsPersistent = persistent,
                AllowRefresh = true,
                ExpiresUtc = clock.UtcNow.AddHours(12)
            });
    }

    private static bool ValidPassphrase(string passphrase) =>
        !string.IsNullOrWhiteSpace(passphrase)
        && passphrase.Length is >= 12 and <= 1024;

    private static bool FixedTimeEquals(string expected, string provided)
    {
        if (string.IsNullOrEmpty(provided) || provided.Length > 256)
        {
            return false;
        }

        var expectedBytes = System.Text.Encoding.UTF8.GetBytes(expected);
        var providedBytes = System.Text.Encoding.UTF8.GetBytes(provided);
        return expectedBytes.Length == providedBytes.Length
            && CryptographicOperations.FixedTimeEquals(expectedBytes, providedBytes);
    }
}

/// <summary>Identifies the fixed local owner while ASP.NET Identity hashes/verifies the owner's passphrase.</summary>
/// <param name="Id">Stable local owner identity, never supplied by a request.</param>
public sealed record OwnerIdentity(string Id);

/// <summary>Provides cookie-safe owner identity and shared antiforgery validation for API endpoints.</summary>
public static class OwnerHttpSecurity
{
    /// <summary>Gets the authenticated local owner identifier, or null when the request is unauthenticated.</summary>
    /// <param name="principal">Request principal.</param>
    /// <returns>The fixed owner ID when authenticated.</returns>
    public static string? GetOwnerId(ClaimsPrincipal principal) =>
        principal.Identity?.IsAuthenticated == true
            && principal.FindFirstValue(ClaimTypes.NameIdentifier) == "owner"
                ? "owner"
                : null;

    /// <summary>Validates the cookie-backed request's antiforgery token from the configured header.</summary>
    /// <param name="antiforgery">ASP.NET antiforgery service.</param>
    /// <param name="context">Request to validate.</param>
    /// <param name="cancellationToken">Token that cancels validation.</param>
    /// <returns>Whether the request has a valid cookie/header token pair.</returns>
    public static async Task<bool> ValidateCsrfAsync(
        IAntiforgery antiforgery,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(context);
            cancellationToken.ThrowIfCancellationRequested();
            return true;
        }
        catch (AntiforgeryValidationException)
        {
            return false;
        }
    }
}
