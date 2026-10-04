using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

internal sealed class OllamaInferenceProxy : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly HttpClient _upstreamClient = new(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false });
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentBag<Task> _requests = [];
    private readonly Uri _upstream;
    private readonly Task _acceptLoop;
    private int _requestCount;

    private OllamaInferenceProxy(int port, Uri upstream)
    {
        BaseUrl = $"http://127.0.0.1:{port}/v1";
        _upstream = upstream;
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();
        _acceptLoop = AcceptAsync();
    }

    public string BaseUrl { get; }
    public int RequestCount => Volatile.Read(ref _requestCount);
    public ConcurrentQueue<string> Destinations { get; } = new();

    public static Task<OllamaInferenceProxy> StartAsync(string upstreamBaseUrl)
    {
        if (!Uri.TryCreate(upstreamBaseUrl, UriKind.Absolute, out var upstream) ||
            upstream.Scheme != Uri.UriSchemeHttp ||
            !upstream.IsLoopback ||
            !string.IsNullOrEmpty(upstream.UserInfo) ||
            !string.IsNullOrEmpty(upstream.Query))
        {
            throw new InvalidOperationException("The Ollama instrumentation proxy accepts only an HTTP loopback endpoint.");
        }

        using var portProbe = new TcpListener(IPAddress.Loopback, 0);
        portProbe.Start();
        var port = ((IPEndPoint)portProbe.LocalEndpoint).Port;
        portProbe.Stop();
        return Task.FromResult(new OllamaInferenceProxy(port, upstream));
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        _listener.Close();
        await _acceptLoop;
        await Task.WhenAll(_requests.ToArray());
        _upstreamClient.Dispose();
        _shutdown.Dispose();
    }

    private async Task AcceptAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            try
            {
                var context = await _listener.GetContextAsync().WaitAsync(_shutdown.Token);
                _requests.Add(ForwardAsync(context));
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
                break;
            }
            catch (HttpListenerException) when (_shutdown.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task ForwardAsync(HttpListenerContext context)
    {
        var incoming = context.Request.Url ?? throw new InvalidOperationException("The inference proxy received a request without a URL.");
        var upstreamOrigin = _upstream.GetLeftPart(UriPartial.Authority);
        var prefix = _upstream.AbsolutePath.TrimEnd('/');
        var path = incoming.AbsolutePath;
        if (!path.StartsWith("/v1/", StringComparison.Ordinal) && path != "/v1")
        {
            throw new InvalidOperationException($"Unexpected inference path: {path}");
        }

        var suffix = path == "/v1" ? string.Empty : path["/v1".Length..];
        var destination = new Uri($"{upstreamOrigin}{prefix}{suffix}{incoming.Query}");
        if (!destination.IsLoopback)
        {
            throw new InvalidOperationException("The inference proxy refused a non-loopback destination.");
        }

        Destinations.Enqueue($"{destination.GetLeftPart(UriPartial.Authority)}{destination.AbsolutePath}");
        Interlocked.Increment(ref _requestCount);

        using var request = new HttpRequestMessage(new HttpMethod(context.Request.HttpMethod), destination)
        {
            Content = context.Request.HasEntityBody ? new StreamContent(context.Request.InputStream) : null,
        };
        if (request.Content is not null && context.Request.ContentType is { Length: > 0 } contentType)
        {
            request.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        }

        using var response = await _upstreamClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            _shutdown.Token);
        if ((int)response.StatusCode is >= 300 and < 400)
        {
            context.Response.StatusCode = 502;
            context.Response.ContentType = "application/json";
            var error = System.Text.Encoding.UTF8.GetBytes("""{"error":{"message":"Inference proxy refused an upstream redirect."}}""");
            context.Response.ContentLength64 = error.Length;
            await context.Response.OutputStream.WriteAsync(error, _shutdown.Token);
            context.Response.Close();
            return;
        }
        context.Response.StatusCode = (int)response.StatusCode;
        if (response.Content.Headers.ContentType is { } responseContentType)
        {
            context.Response.ContentType = responseContentType.ToString();
        }
        if (response.Content.Headers.ContentLength is { } contentLength)
        {
            context.Response.ContentLength64 = contentLength;
        }
        else
        {
            context.Response.SendChunked = true;
        }

        await response.Content.CopyToAsync(context.Response.OutputStream, _shutdown.Token);
        context.Response.Close();
    }
}
