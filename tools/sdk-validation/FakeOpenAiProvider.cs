using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

internal sealed class FakeOpenAiProvider : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentBag<Task> _requests = [];
    private readonly Task _acceptLoop;
    private int _requestCount;

    private FakeOpenAiProvider(int port)
    {
        BaseUrl = $"http://127.0.0.1:{port}/v1";
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();
        _acceptLoop = AcceptAsync();
    }

    public string BaseUrl { get; }
    public int RequestCount => Volatile.Read(ref _requestCount);
    public ConcurrentQueue<CapturedRequest> Requests { get; } = new();
    public string RequestedTool { get; set; } = "read_only_lookup";
    public bool RepeatTool { get; set; }
    public bool FailInference { get; set; }
    public bool StallInference { get; set; }
    public string? StreamingContent { get; set; }
    public int StreamingDeltaCount { get; set; }
    public string? RedirectUrl { get; set; }
    public string? ExpectedToolResult { get; set; } = "fixture-result:fixture-key";
    public TaskCompletionSource InferenceStalled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource StreamingResponseWritten { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static Task<FakeOpenAiProvider> StartAsync()
    {
        using var portProbe = new TcpListener(IPAddress.Loopback, 0);
        portProbe.Start();
        var port = ((IPEndPoint)portProbe.LocalEndpoint).Port;
        portProbe.Stop();
        return Task.FromResult(new FakeOpenAiProvider(port));
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        _listener.Close();
        await _acceptLoop;
        await Task.WhenAll(_requests.ToArray());
        _shutdown.Dispose();
    }

    private async Task AcceptAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            try
            {
                var context = await _listener.GetContextAsync().WaitAsync(_shutdown.Token);
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
        if (RedirectUrl is not null)
        {
            context.Response.StatusCode = 307;
            context.Response.RedirectLocation = RedirectUrl;
            context.Response.Close();
            return;
        }
        using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding);
        var body = await reader.ReadToEndAsync(_shutdown.Token);
        if (context.Request.Url?.AbsolutePath.EndsWith("/models", StringComparison.Ordinal) == true)
        {
            await WriteJsonAsync(context.Response,
                """{"object":"list","data":[{"id":"fixture-model","object":"model","created":0,"owned_by":"jarvis"}]}""");
            return;
        }

        var index = Interlocked.Increment(ref _requestCount);
        var toolNames = Array.Empty<string>();
        if (body.Length > 0)
        {
            using var json = JsonDocument.Parse(body);
            if (json.RootElement.TryGetProperty("tools", out var tools))
            {
                toolNames = tools.EnumerateArray()
                    .Select(tool => tool.TryGetProperty("function", out var function) &&
                                    function.TryGetProperty("name", out var name)
                        ? name.GetString() ?? string.Empty
                        : string.Empty)
                    .ToArray();
            }
        }

        Requests.Enqueue(new CapturedRequest(
            context.Request.Url?.AbsolutePath ?? string.Empty,
            body,
            toolNames,
            context.Request.Headers["Authorization"]));

        if (FailInference)
        {
            context.Response.StatusCode = 400;
            var error = Encoding.UTF8.GetBytes("""{"error":{"message":"Controlled provider refusal","type":"invalid_request_error"}}""");
            context.Response.ContentLength64 = error.Length;
            await context.Response.OutputStream.WriteAsync(error);
            context.Response.Close();
            return;
        }

        if (StallInference)
        {
            InferenceStalled.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, _shutdown.Token);
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
            }

            context.Response.Close();
            return;
        }

        if ((index == 1 || RepeatTool) && toolNames.Length > 0)
        {
            await WriteJsonAsync(context.Response,
                JsonSerializer.Serialize(new
                {
                    id = $"chatcmpl-jarvis-{index}",
                    @object = "chat.completion",
                    created = 0,
                    model = "fixture-model",
                    choices = new[] { new { index = 0, message = new { role = "assistant", content = (string?)null,
                        tool_calls = new[] { new { id = $"call-jarvis-{index}", type = "function",
                            function = new { name = RequestedTool, arguments = "{\"key\":\"fixture-key\"}" } } } },
                        finish_reason = "tool_calls" } },
                }));
            return;
        }

        using var requestJson = JsonDocument.Parse(body);
        if (requestJson.RootElement.TryGetProperty("stream", out var stream) && stream.GetBoolean())
        {
            await WriteStreamingResponseAsync(context.Response, StreamingContent, StreamingDeltaCount);
            return;
        }

        var answer = "Controlled denied-tool fixture completed.";
        if (ExpectedToolResult is not null && toolNames.Length > 0)
        {
            var returnedResults = requestJson.RootElement.GetProperty("messages").EnumerateArray()
                .Where(message => message.GetProperty("role").GetString() == "tool" &&
                    message.GetProperty("tool_call_id").GetString() == "call-jarvis-1")
                .ToArray();
            if (returnedResults.Length != 1 ||
                returnedResults[0].GetProperty("content").GetString() != ExpectedToolResult)
            {
                context.Response.Abort();
                throw new InvalidOperationException("The runtime did not forward the expected correlated tool result.");
            }
            answer = returnedResults[0].GetProperty("content").GetString()!;
        }
        await WriteJsonAsync(context.Response, JsonSerializer.Serialize(new
        {
            id = "chatcmpl-jarvis-final",
            @object = "chat.completion",
            created = 0,
            model = "fixture-model",
            choices = new[] { new { index = 0, message = new { role = "assistant", content = answer }, finish_reason = "stop" } },
        }));
    }

    private async Task WriteStreamingResponseAsync(
        HttpListenerResponse response,
        string? contentOverride,
        int deltaCount)
    {
        response.StatusCode = (int)HttpStatusCode.OK;
        response.ContentType = "text/event-stream";
        response.SendChunked = true;
        response.KeepAlive = false;
        var chunks = deltaCount > 0
            ? Enumerable.Range(0, deltaCount)
                .Select(_ => $"data: {JsonSerializer.Serialize(new
                {
                    id = "chatcmpl-stream",
                    @object = "chat.completion.chunk",
                    created = 0,
                    model = "fixture-model",
                    choices = new[] { new { index = 0, delta = new { content = "x" }, finish_reason = (string?)null } }
                })}")
                .Append("""data: {"id":"chatcmpl-stream","object":"chat.completion.chunk","created":0,"model":"fixture-model","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}""")
                .Append("data: [DONE]")
                .ToArray()
            : contentOverride is null
            ? new[]
                {
                    """data: {"id":"chatcmpl-stream","object":"chat.completion.chunk","created":0,"model":"fixture-model","choices":[{"index":0,"delta":{"role":"assistant","content":"streamed "},"finish_reason":null}]}""",
                    """data: {"id":"chatcmpl-stream","object":"chat.completion.chunk","created":0,"model":"fixture-model","choices":[{"index":0,"delta":{"content":"fixture answer"},"finish_reason":null}]}""",
                    """data: {"id":"chatcmpl-stream","object":"chat.completion.chunk","created":0,"model":"fixture-model","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}""",
                    "data: [DONE]",
                }
            : new[]
            {
                $"data: {JsonSerializer.Serialize(new
                {
                    id = "chatcmpl-stream",
                    @object = "chat.completion.chunk",
                    created = 0,
                    model = "fixture-model",
                    choices = new[] { new { index = 0, delta = new { role = "assistant", content = contentOverride }, finish_reason = (string?)null } }
                })}",
                """data: {"id":"chatcmpl-stream","object":"chat.completion.chunk","created":0,"model":"fixture-model","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}""",
                "data: [DONE]"
            };
        foreach (var chunk in chunks)
        {
            var bytes = Encoding.UTF8.GetBytes(chunk + "\n\n");
            await response.OutputStream.WriteAsync(bytes);
            await response.OutputStream.FlushAsync();
        }
        response.Close();
        StreamingResponseWritten.TrySetResult();
    }

    private static async Task WriteJsonAsync(HttpListenerResponse response, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        response.StatusCode = (int)HttpStatusCode.OK;
        response.ContentType = "application/json";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
        response.Close();
    }
}

internal sealed record CapturedRequest(
    string Path,
    string Body,
    IReadOnlyList<string> ToolNames,
    string? Authorization);
