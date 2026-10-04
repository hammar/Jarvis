using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace PersonalAgent.SdkContractTests;

internal sealed class ControlledOpenAiProvider : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentBag<Task> _requests = [];
    private readonly Task _acceptLoop;
    private int _requestCount;
    private int _toolRequestIssued;

    private ControlledOpenAiProvider(int port)
    {
        BaseUrl = $"http://127.0.0.1:{port}/v1";
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();
        _acceptLoop = AcceptAsync();
    }

    public string BaseUrl { get; }
    public int RequestCount => Volatile.Read(ref _requestCount);
    public int ToolCallsBeforeCompletion { get; set; } = 1;
    public bool StreamResponses { get; set; } = true;
    public bool FailInference { get; set; }
    public ConcurrentQueue<CapturedProviderRequest> CapturedRequests { get; } = new();

    public static Task<ControlledOpenAiProvider> StartAsync()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return Task.FromResult(new ControlledOpenAiProvider(port));
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        _listener.Close();
        await _acceptLoop.ConfigureAwait(false);
        await Task.WhenAll(_requests.ToArray()).ConfigureAwait(false);
        _shutdown.Dispose();
    }

    private async Task AcceptAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            try
            {
                var context = await _listener.GetContextAsync().WaitAsync(_shutdown.Token).ConfigureAwait(false);
                _requests.Add(RespondAsync(context));
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

    private async Task RespondAsync(HttpListenerContext context)
    {
        try
        {
            if (context.Request.Url?.AbsolutePath.EndsWith("/models", StringComparison.Ordinal) == true)
            {
                await WriteJsonAsync(
                    context.Response,
                    """{"object":"list","data":[{"id":"fixture-model","object":"model","created":0,"owned_by":"jarvis"}]}""")
                    .ConfigureAwait(false);
                return;
            }

            using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding);
            var body = await reader.ReadToEndAsync(_shutdown.Token).ConfigureAwait(false);
            var requestNumber = Interlocked.Increment(ref _requestCount);
            CapturedRequests.Enqueue(new CapturedProviderRequest(body, context.Request.Headers["Authorization"]));
            if (FailInference)
            {
                context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                await WriteJsonAsync(
                    context.Response,
                    """{"error":{"message":"Controlled provider refusal","type":"invalid_request_error"}}""")
                    .ConfigureAwait(false);
                return;
            }

            using var request = JsonDocument.Parse(body);
            var root = request.RootElement;
            var priorToolRequests = Volatile.Read(ref _toolRequestIssued);
            if (HasTools(root) && priorToolRequests < ToolCallsBeforeCompletion &&
                Interlocked.CompareExchange(ref _toolRequestIssued, priorToolRequests + 1, priorToolRequests) == priorToolRequests)
            {
                var toolName = root.GetProperty("tools")[0].GetProperty("function").GetProperty("name").GetString();
                await WriteJsonAsync(context.Response, JsonSerializer.Serialize(new
                {
                    id = $"chatcmpl-tool-{requestNumber}",
                    @object = "chat.completion",
                    created = 0,
                    model = "fixture-model",
                    choices = new[]
                    {
                        new
                        {
                            index = 0,
                            message = new
                            {
                                role = "assistant",
                                content = (string?)null,
                                tool_calls = new[]
                                {
                                    new
                                    {
                                        id = "call-controlled-1",
                                        type = "function",
                                        function = new
                                        {
                                            name = toolName,
                                            arguments = """{"key":"fixture-key"}""",
                                        },
                                    },
                                },
                            },
                            finish_reason = "tool_calls",
                        },
                    },
                })).ConfigureAwait(false);
                return;
            }

            if (StreamResponses)
            {
                await WriteStreamingResponseAsync(context.Response).ConfigureAwait(false);
            }
            else
            {
                await WriteJsonAsync(context.Response, JsonSerializer.Serialize(new
                {
                    id = $"chatcmpl-final-{requestNumber}",
                    @object = "chat.completion",
                    created = 0,
                    model = "fixture-model",
                    choices = new[]
                    {
                        new
                        {
                            index = 0,
                            message = new { role = "assistant", content = "non-streamed answer" },
                            finish_reason = "stop",
                        },
                    },
                })).ConfigureAwait(false);
            }
        }
        catch (Exception) when (_shutdown.IsCancellationRequested)
        {
        }
    }

    private static bool HasTools(JsonElement root) =>
        root.TryGetProperty("tools", out var tools) && tools.ValueKind == JsonValueKind.Array && tools.GetArrayLength() > 0;

    private static bool HasToolResult(JsonElement root) =>
        root.TryGetProperty("messages", out var messages) &&
        messages.EnumerateArray().Any(message =>
            message.TryGetProperty("role", out var role) && role.GetString() == "tool");

    private static async Task WriteStreamingResponseAsync(HttpListenerResponse response)
    {
        response.StatusCode = (int)HttpStatusCode.OK;
        response.ContentType = "text/event-stream";
        response.SendChunked = true;
        response.KeepAlive = false;
        foreach (var chunk in new[]
        {
            """data: {"id":"chatcmpl-stream","object":"chat.completion.chunk","created":0,"model":"fixture-model","choices":[{"index":0,"delta":{"role":"assistant","content":"streamed "},"finish_reason":null}]}""",
            """data: {"id":"chatcmpl-stream","object":"chat.completion.chunk","created":0,"model":"fixture-model","choices":[{"index":0,"delta":{"content":"answer"},"finish_reason":null}]}""",
            """data: {"id":"chatcmpl-stream","object":"chat.completion.chunk","created":0,"model":"fixture-model","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}""",
            "data: [DONE]",
        })
        {
            var bytes = Encoding.UTF8.GetBytes(chunk + "\n\n");
            try
            {
                await response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
                await response.OutputStream.FlushAsync().ConfigureAwait(false);
            }
            catch (IOException)
            {
                return;
            }
            catch (HttpListenerException)
            {
                return;
            }
        }

        response.Close();
    }

    private static async Task WriteJsonAsync(HttpListenerResponse response, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        response.StatusCode = (int)HttpStatusCode.OK;
        response.ContentType = "application/json";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        response.Close();
    }
}

internal sealed record CapturedProviderRequest(string Body, string? Authorization);
