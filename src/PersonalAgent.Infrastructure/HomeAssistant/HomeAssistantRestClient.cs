using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using PersonalAgent.Application;
using PersonalAgent.Application.Tools.Home;

namespace PersonalAgent.Infrastructure.HomeAssistant;

/// <summary>Implements exact approved HA REST operations, secret-safe errors and one bounded safe-read retry.</summary>
/// <remarks>The supplied HTTP pipeline must disable redirects, retries and hedging. Service acceptance is not verification.</remarks>
public sealed class HomeAssistantRestClient : IHomeAssistantClient
{
    private const int MaximumResponseBytes = 65_536;
    private readonly HttpClient http;
    private readonly Uri baseUri;
    private readonly SecretReference secret;
    private readonly ISecretResolver secrets;
    private readonly HomeEntityCatalog catalog;
    private readonly IClock clock;

    /// <summary>Creates the adapter using trusted endpoint, secret reference and catalog.</summary>
    /// <param name="http">Host-owned client with no redirect/retry/hedging pipeline.</param>
    /// <param name="baseUri">Trusted HTTP(S) root without userinfo, query or fragment.</param>
    /// <param name="secret">Named protected reference, never a resolved token.</param>
    /// <param name="secrets">Server-side resolver.</param>
    /// <param name="catalog">Exact host-owned permission/bounds catalog.</param>
    /// <param name="clock">UTC observation-age clock.</param>
    public HomeAssistantRestClient(
        HttpClient http, Uri baseUri, SecretReference secret, ISecretResolver secrets,
        HomeEntityCatalog catalog, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(baseUri);
        ArgumentNullException.ThrowIfNull(secret);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(clock);
        _ = HomeAssistantConfiguration.PolicyVersion(catalog, baseUri, secret);
        this.http = http;
        this.baseUri = HomeAssistantConfiguration.NormalizeEndpoint(baseUri);
        this.secret = secret;
        this.secrets = secrets;
        this.catalog = catalog;
        this.clock = clock;
    }

    /// <inheritdoc />
    public async ValueTask<HomeAssistantEntity> GetStateAsync(string entityId, CancellationToken cancellationToken)
    {
        var definition = catalog.Require(entityId);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(catalog.Options.RequestTimeoutSeconds));
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    using var request = await RequestAsync(HttpMethod.Get, "api/states/" + entityId, deadline.Token);
                    using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                    if (Transient(response.StatusCode) && attempt == 0)
                    {
                        deadline.Token.ThrowIfCancellationRequested();
                        continue;
                    }
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new HomeAssistantException(ReadFailure(response.StatusCode));
                    }
                    return await ReadStateAsync(response, definition, request.Headers.Authorization!.Parameter!, deadline.Token);
                }
                catch (HttpRequestException error)
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    if (attempt != 0 || !TransientTransport(error))
                    {
                        throw new HomeAssistantException(HomeAssistantFailure.Transport);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new HomeAssistantException(HomeAssistantFailure.Timeout);
        }
    }

    /// <inheritdoc />
    public ValueTask<HomeAssistantWriteResult> SetLightAsync(
        string entityId, bool on, int? brightness, CancellationToken cancellationToken)
    {
        if (!catalog.AllowsLight(entityId, on, brightness))
        {
            return ValueTask.FromResult(new HomeAssistantWriteResult(HomeAssistantWriteStatus.Rejected, "home_operation_not_allowed"));
        }
        return WriteAsync("api/services/light/" + (on ? "turn_on" : "turn_off"),
            brightness is null ? new { entity_id = entityId } : (object)new { entity_id = entityId, brightness }, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<HomeAssistantWriteResult> SetTemperatureAsync(
        string entityId, decimal temperature, CancellationToken cancellationToken)
    {
        if (!catalog.AllowsTemperature(entityId, temperature))
        {
            return ValueTask.FromResult(new HomeAssistantWriteResult(HomeAssistantWriteStatus.Rejected, "home_operation_not_allowed"));
        }
        return WriteAsync("api/services/climate/set_temperature", new { entity_id = entityId, temperature }, cancellationToken);
    }

    private async ValueTask<HomeAssistantWriteResult> WriteAsync(string path, object body, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(catalog.Options.RequestTimeoutSeconds));
        HttpRequestMessage? request = null;
        try
        {
            // Failures before SendAsync are definitively not submitted, not physical uncertainty.
            try
            {
                request = await RequestAsync(HttpMethod.Post, path, deadline.Token);
                request.Content = JsonContent.Create(body);
                deadline.Token.ThrowIfCancellationRequested();
            }
            catch (HomeAssistantException error)
            {
                return new(HomeAssistantWriteStatus.Rejected, "home_not_submitted_" + error.Failure.ToString().ToLowerInvariant());
            }
            catch (OperationCanceledException)
            {
                return new(HomeAssistantWriteStatus.Rejected,
                    token.IsCancellationRequested ? "home_not_submitted_cancelled" : "home_not_submitted_timeout");
            }
            try
            {
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                if (response.IsSuccessStatusCode)
                {
                    return new(HomeAssistantWriteStatus.Accepted, null);
                }
                // Service handlers can fail after side effects; an HTTP error is not rollback evidence.
                return new(HomeAssistantWriteStatus.Unknown, response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "home_unauthorized",
                    HttpStatusCode.NotFound => "home_service_not_found",
                    _ => "home_delivery_uncertain"
                });
            }
            catch (HttpRequestException)
            {
                return new(HomeAssistantWriteStatus.Unknown, "home_delivery_uncertain");
            }
            catch (OperationCanceledException)
            {
                return new(HomeAssistantWriteStatus.Unknown, "home_delivery_uncertain");
            }
        }
        finally
        {
            request?.Dispose();
        }
    }

    private async ValueTask<HttpRequestMessage> RequestAsync(HttpMethod method, string path, CancellationToken token)
    {
        ReadOnlyMemory<char> value;
        try
        {
            value = await secrets.ResolveAsync(secret, token);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            throw new HomeAssistantException(HomeAssistantFailure.Unauthorized);
        }
        if (value.IsEmpty || value.Span.ContainsAnyInRange('\0', ' ') || value.Span.ContainsAnyInRange('\x7f', char.MaxValue))
        {
            throw new HomeAssistantException(HomeAssistantFailure.Unauthorized);
        }
        var request = new HttpRequestMessage(method, new Uri(baseUri, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", value.ToString());
        return request;
    }

    private async ValueTask<HomeAssistantEntity> ReadStateAsync(
        HttpResponseMessage response, HomeEntityDefinition definition, string tokenValue, CancellationToken token)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(token);
            using var buffer = new MemoryStream();
            var bytes = new byte[4096];
            int count;
            while ((count = await stream.ReadAsync(bytes, token)) != 0)
            {
                if (buffer.Length + count > MaximumResponseBytes)
                {
                    throw new HomeAssistantException(HomeAssistantFailure.InvalidResponse);
                }
                buffer.Write(bytes, 0, count);
            }
            using var document = JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            var id = root.GetProperty("entity_id").GetString();
            var state = root.GetProperty("state").GetString();
            var source = root.GetProperty("last_updated").GetDateTimeOffset().ToUniversalTime();
            if (id != definition.EntityId || state is null || state.Length > 256 || state.Any(char.IsControl)
                || state.Contains(tokenValue, StringComparison.Ordinal)
                || source > clock.UtcNow)
            {
                throw new HomeAssistantException(HomeAssistantFailure.InvalidResponse);
            }
            var attributes = root.GetProperty("attributes");
            int? brightness = attributes.TryGetProperty("brightness", out var b) && b.ValueKind != JsonValueKind.Null
                ? b.GetInt32() : null;
            decimal? temperature = attributes.TryGetProperty("temperature", out var t) && t.ValueKind != JsonValueKind.Null
                ? t.GetDecimal() : null;
            if (brightness is < 0 or > 255)
            {
                throw new HomeAssistantException(HomeAssistantFailure.InvalidResponse);
            }
            return new(id, state, source, state is not ("unavailable" or "unknown"))
            {
                Brightness = brightness,
                TargetTemperature = temperature,
                Stale = clock.UtcNow - source > TimeSpan.FromSeconds(definition.MaximumAgeSeconds)
            };
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException
            or FormatException or OverflowException)
        {
            throw new HomeAssistantException(HomeAssistantFailure.InvalidResponse);
        }
        catch (IOException error)
        {
            throw new HttpRequestException(
                error is HttpIOException transport ? transport.HttpRequestError : HttpRequestError.Unknown,
                "Home Assistant response transport failed.", error);
        }
    }

    private static bool Transient(HttpStatusCode status) =>
        status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
            or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;

    private static bool TransientTransport(HttpRequestException error) =>
        error.HttpRequestError is HttpRequestError.ConnectionError or HttpRequestError.ResponseEnded
        // Some platforms wrap a dropped connection as Unknown with a typed socket cause.
        || error.HttpRequestError == HttpRequestError.Unknown
        && error.InnerException is IOException
        {
            InnerException: SocketException
            {
                SocketErrorCode: SocketError.ConnectionReset or SocketError.ConnectionAborted
                    or SocketError.NetworkReset or SocketError.TimedOut
            }
        };

    private static HomeAssistantFailure ReadFailure(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => HomeAssistantFailure.Unauthorized,
        HttpStatusCode.NotFound => HomeAssistantFailure.UnknownEntity,
        _ => HomeAssistantFailure.Transport
    };
}
