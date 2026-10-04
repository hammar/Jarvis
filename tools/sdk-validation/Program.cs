using System.ComponentModel;
using System.Net;
using System.Text.Json;
using GitHub.Copilot;
using Microsoft.Extensions.AI;
using PermissionDecision = GitHub.Copilot.Rpc.PermissionDecision;

return await SdkValidation.RunAsync(args);

internal static class SdkValidation
{
    private const string ToolName = "read_only_lookup";
    private const string HostileMarker = "JARVIS_HOSTILE_AMBIENT_SENTINEL";

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            if (args is ["--diagnose-egress-block"])
            {
                using var socket = new System.Net.Sockets.Socket(
                    System.Net.Sockets.AddressFamily.InterNetwork,
                    System.Net.Sockets.SocketType.Stream,
                    System.Net.Sockets.ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(IPAddress.Parse("192.0.2.1"), 443)
                        .WaitAsync(TimeSpan.FromSeconds(3));
                }
                catch (System.Net.Sockets.SocketException exception) when (
                    exception.SocketErrorCode == System.Net.Sockets.SocketError.NetworkUnreachable)
                {
                    Console.WriteLine("PASS deliberate external TCP connection was rejected as network unreachable.");
                    return 0;
                }
                throw new InvalidOperationException("Expected OS-level network-unreachable rejection was not observed.");
            }

            if (args is ["--diagnose-listener"])
            {
                await using var provider = await FakeOpenAiProvider.StartAsync();
                Console.WriteLine("PASS fixture listener started/disposed without constructing a Copilot client.");
                return 0;
            }

            if (args is ["--diagnose-runtime"])
            {
                using var fixture = new TemporaryDirectory();
                var workspace = Path.Combine(fixture.Path, "workspace");
                Directory.CreateDirectory(workspace);
                await using var client = CreateClient(Path.Combine(fixture.Path, "runtime"), workspace);
                await client.StartAsync().WaitAsync(TimeSpan.FromSeconds(15));
                await client.PingAsync().WaitAsync(TimeSpan.FromSeconds(5));
                await client.StopAsync().WaitAsync(TimeSpan.FromSeconds(15));
                Console.WriteLine("PASS isolated runtime startup/ping/shutdown without a provider or fixture listener.");
                return 0;
            }

            if (args is ["--contracts"])
            {
                await RunContractAsync();
                return 0;
            }

            if (args is ["--live-ollama"])
            {
                var model = Environment.GetEnvironmentVariable("JARVIS_OLLAMA_MODEL");
                if (string.IsNullOrWhiteSpace(model))
                {
                    throw new InvalidOperationException("Set JARVIS_OLLAMA_MODEL to an installed Ollama model name.");
                }
                var upstreamUrl = Environment.GetEnvironmentVariable("JARVIS_OLLAMA_BASE_URL") ?? "http://127.0.0.1:11434/v1";
                await using var proxy = await OllamaInferenceProxy.StartAsync(upstreamUrl);
                await RunLiveAsync(
                    new ProviderConfig { Type = "openai", BaseUrl = proxy.BaseUrl },
                    "local-ollama",
                    model);
                Require(proxy.RequestCount > 0 && proxy.Destinations.All(destination => Uri.TryCreate(destination, UriKind.Absolute, out var uri) && uri.IsLoopback),
                    "The local inference proxy did not capture loopback-only model requests.");
                Console.WriteLine($"PASS instrumented local inference proxy captured {proxy.RequestCount} loopback request(s); payloads and credentials were not logged.");
                return 0;
            }

            if (args is ["--live-cloud"])
            {
                var (provider, model) = CreateCloudProvider();
                await RunLiveAsync(provider, "cloud-byok", model);
                return 0;
            }

            Console.Error.WriteLine("Usage: dotnet run --project tools/sdk-validation -- --contracts|--live-ollama|--live-cloud|--diagnose-listener|--diagnose-runtime|--diagnose-egress-block");
            return 2;
        }
        catch (Exception exception)
        {
            var detail = args is ["--live-cloud"]
                ? "Cloud evaluation failed; provider details omitted to protect credentials."
                : exception.Message;
            Console.Error.WriteLine($"SDK validation failed: {exception.GetType().Name}: {detail}");
            return 1;
        }
    }

    private static async Task RunContractAsync()
    {
        CloudEvaluationSettings.ValidateParser();
        await ValidateRedirectRefusalAsync();
        using var fixture = new TemporaryDirectory();
        var workspace = Path.Combine(fixture.Path, "workspace");
        var runtimeHome = Path.Combine(fixture.Path, "copilot-home");
        var ambientHome = Path.Combine(fixture.Path, "ambient-home");
        CreateHostileAmbientConfiguration(workspace, ambientHome);

        await using var provider = await FakeOpenAiProvider.StartAsync();
        using var environment = new ScopedEnvironment(new Dictionary<string, string?>
        {
            ["HOME"] = ambientHome,
            ["USERPROFILE"] = ambientHome,
            ["COPILOT_HOME"] = Path.Combine(ambientHome, ".copilot"),
            ["COPILOT_GITHUB_TOKEN"] = "ghu_test_sentinel",
            ["GH_TOKEN"] = "ghu_test_sentinel",
            ["GITHUB_TOKEN"] = "ghu_test_sentinel",
        });

        await using var client = CreateClient(runtimeHome, workspace);
        await client.StartAsync();

        {
            var toolCalls = 0;
            var resultMarker = $"fixture-result:{Guid.NewGuid():N}";
            provider.ExpectedToolResult = resultMarker;
            await using var session = await client.CreateSessionAsync(CreateSessionConfig(
                new ProviderConfig { Type = "openai", BaseUrl = provider.BaseUrl },
                "fixture-model",
                ToolName,
                async ([Description("Lookup key")] string key) =>
                {
                    Interlocked.Increment(ref toolCalls);
                    return resultMarker;
                }));

            var response = await session.SendAndWaitAsync(new MessageOptions
            {
                Prompt = "Use the read-only lookup tool to retrieve the fixture value, then report it. JARVIS_CONVERSATION_A",
            }).WaitAsync(TimeSpan.FromSeconds(30));

            Require(toolCalls == 1, $"Expected one host-owned tool invocation; observed {toolCalls}.");
            Require(response?.Data.Content == resultMarker,
                "The final response did not include the custom tool result.");
            Require(provider.RequestCount == 2,
                $"Expected one tool-request turn and one final-response turn; observed {provider.RequestCount} provider requests.");
            Require(provider.Requests.All(request => !request.Body.Contains(HostileMarker, StringComparison.Ordinal)),
                "Hostile ambient instructions leaked into the provider request.");
            var exposedTools = string.Join("; ", provider.Requests.Select(request => string.Join(",", request.ToolNames)));
            Require(provider.Requests.All(request => request.ToolNames.SequenceEqual([ToolName])),
                $"The runtime exposed a tool outside the explicit custom-tool allowlist. Captured provider tool names: {exposedTools}");
            Require(provider.Requests.All(request => request.Authorization?.Contains("ghu_test_sentinel", StringComparison.Ordinal) != true),
                "A hostile ambient GitHub token was forwarded as provider authorization.");
            Require(Directory.Exists(Path.Combine(runtimeHome, "session-state")),
                "The runtime did not persist session data under the configured Copilot home.");
            Require(!Directory.Exists(Path.Combine(ambientHome, ".copilot", "session-state")),
                "The runtime wrote session data into the ambient Copilot home.");
            Console.WriteLine("PASS actual Copilot SDK runtime executed one custom read-only tool and returned its result.");
            Console.WriteLine("PASS only the explicitly allowlisted custom tool was exposed; no built-in, MCP, or subagent tool was advertised.");
            Console.WriteLine("PASS hostile workspace/user instructions and hostile GitHub-token sentinels were not observed in model requests.");
            Console.WriteLine($"PASS inference traffic was captured at the instrumented loopback provider ({provider.RequestCount} requests; authorization redacted).");
            Console.WriteLine($"PASS runtime session data used the configured isolated directory ({Path.GetFileName(runtimeHome)}).");
        }

        await using (var secondProvider = await FakeOpenAiProvider.StartAsync())
        {
            var toolCalls = 0;
            await using var secondSession = await client.CreateSessionAsync(CreateSessionConfig(
                new ProviderConfig { Type = "openai", BaseUrl = secondProvider.BaseUrl },
                "fixture-model",
                ToolName,
                async ([Description("Lookup key")] string key) =>
                {
                    Interlocked.Increment(ref toolCalls);
                    return $"fixture-result:{key}";
                }));
            await secondSession.SendAndWaitAsync(new MessageOptions
            {
                Prompt = "JARVIS_CONVERSATION_B",
            }).WaitAsync(TimeSpan.FromSeconds(30));
            Require(toolCalls == 1, "The second explicit session did not invoke its own registered tool.");
            Require(provider.Requests.All(request => !request.Body.Contains("JARVIS_CONVERSATION_B", StringComparison.Ordinal)) &&
                    secondProvider.Requests.All(request => !request.Body.Contains("JARVIS_CONVERSATION_A", StringComparison.Ordinal)),
                "A transcript marker crossed between isolated sessions.");
            Console.WriteLine("PASS explicit per-session providers kept captured transcript markers separate.");
        }

        await using (var streamingProvider = await FakeOpenAiProvider.StartAsync())
        await using (var streamingSession = await client.CreateSessionAsync(CreateTextOnlySessionConfig(
            new ProviderConfig { Type = "openai", BaseUrl = streamingProvider.BaseUrl },
            "fixture-model")))
        {
            var deltas = new System.Collections.Concurrent.ConcurrentQueue<string>();
            var idleCount = 0;
            using var subscription = streamingSession.On<SessionEvent>(evt =>
            {
                if (evt is AssistantMessageDeltaEvent delta)
                {
                    deltas.Enqueue(delta.Data.DeltaContent);
                }
                else if (evt is SessionIdleEvent)
                {
                    Interlocked.Increment(ref idleCount);
                }
            });
            var response = await streamingSession.SendAndWaitAsync(new MessageOptions
            {
                Prompt = "Return the fixture response.",
            }).WaitAsync(TimeSpan.FromSeconds(30));
            Require(deltas.Any(), "The streaming session emitted no assistant deltas.");
            Require(response?.Data.Content == "streamed fixture answer",
                "The streaming session did not return its final complete response.");
            Require(idleCount == 1, $"Expected one terminal idle event; observed {idleCount}.");
            Console.WriteLine("PASS streaming, final completion, and single idle completion event.");
        }

        await client.ForceStopAsync();
        await client.StartAsync();
        await client.PingAsync().WaitAsync(TimeSpan.FromSeconds(15));
        Console.WriteLine("PASS forced runtime termination followed by restart and readiness ping.");

        await ValidateToolCancellationAsync(fixture.Path);
        await ValidateHostileToolsAsync(client);
        await ValidatePermissionDenialAsync(client);
        await ValidateProviderFailureAsync(client);
        await ValidateHostBudgetsAsync(client);
        await ValidateActiveCrashAsync(fixture.Path);
    }

    private static async Task RunLiveAsync(ProviderConfig provider, string route, string model)
    {
        using var fixture = new TemporaryDirectory();
        var workspace = Path.Combine(fixture.Path, "workspace");
        var runtimeHome = Path.Combine(fixture.Path, "copilot-home");
        Directory.CreateDirectory(workspace);

        var toolCalls = 0;
        await using var client = CreateClient(runtimeHome, workspace, route == "cloud-byok");
        await client.StartAsync();
        await using var session = await client.CreateSessionAsync(CreateSessionConfig(
            provider,
            model,
            ToolName,
            async ([Description("Return a fixed validation value")] string key) =>
            {
                Interlocked.Increment(ref toolCalls);
                return $"live-fixture:{key}";
            }));

        var response = await session.SendAndWaitAsync(new MessageOptions
        {
            Prompt = "Call the read-only lookup tool exactly once with key 'm0', then return its value.",
        });
        Require(response?.Data.Content is { Length: > 0 }, "The explicitly configured provider returned no final assistant message.");
        Require(toolCalls == 1, $"Expected exactly one actual tool call from {route}; observed {toolCalls}.");
        Require(response!.Data.Content!.Contains("live-fixture:m0", StringComparison.Ordinal),
            $"The {route} final response did not contain the custom tool result.");
        Console.WriteLine($"PASS {route} returned a non-empty final response through the explicitly configured BYOK provider.");
        Console.WriteLine($"Response length: {response!.Data.Content!.Length} characters; content omitted.");
    }

    private static async Task ValidateHostileToolsAsync(CopilotClient client)
    {
        foreach (var requestedTool in new[] { "unregistered_lookup", "bash", "view", "web_fetch", "task", "install_extension" })
        {
            await using var provider = await FakeOpenAiProvider.StartAsync();
            provider.RequestedTool = requestedTool;
            provider.ExpectedToolResult = null;
            var calls = 0;
            await using var session = await client.CreateSessionAsync(CreateSessionConfig(
                new ProviderConfig { Type = "openai", BaseUrl = provider.BaseUrl }, "fixture-model", ToolName,
                async (string key) => { Interlocked.Increment(ref calls); return key; }));
            await session.SendAndWaitAsync(new MessageOptions { Prompt = "Exercise the hostile fixture request." },
                timeout: TimeSpan.FromSeconds(20));
            Require(calls == 0, "A hostile request reached the approved tool callback.");
            Require(provider.Requests.All(request => request.ToolNames.SequenceEqual([ToolName])),
                "A hostile request broadened the tool catalog.");
            Require(provider.Requests.Skip(1).Any(request =>
                request.Body.Contains($"Tool '{requestedTool}' does not exist.", StringComparison.Ordinal)),
                $"No explicit unavailable-tool result was observed for {requestedTool}.");
            Console.WriteLine($"PASS unsolicited '{requestedTool}' call returned an unavailable-tool result without invoking the approved callback.");
        }
    }

    private static async Task ValidateProviderFailureAsync(CopilotClient client)
    {
        await using var provider = await FakeOpenAiProvider.StartAsync();
        provider.FailInference = true;
        await using var session = await client.CreateSessionAsync(CreateTextOnlySessionConfig(
            new ProviderConfig { Type = "openai", BaseUrl = provider.BaseUrl }, "fixture-model"));
        var failed = false;
        try
        {
            await session.SendAndWaitAsync(new MessageOptions { Prompt = "The configured provider must fail." },
                timeout: TimeSpan.FromSeconds(20));
        }
        catch (Exception exception) when (exception is not TimeoutException &&
                                         exception.Message.Contains("Controlled provider refusal", StringComparison.Ordinal))
        {
            failed = true;
        }
        Require(failed && provider.RequestCount > 0, "Explicit provider refusal did not produce the expected failure.");
        Console.WriteLine("PASS explicit provider refusal propagated to the caller without a successful fallback response.");
    }

    private static async Task ValidatePermissionDenialAsync(CopilotClient client)
    {
        await using var provider = await FakeOpenAiProvider.StartAsync();
        var calls = 0;
        provider.ExpectedToolResult = null;
        var denials = 0;
        var config = CreateSessionConfig(new ProviderConfig { Type = "openai", BaseUrl = provider.BaseUrl },
            "fixture-model", ToolName, async (string key) => key);
        config.Tools =
        [
            CopilotTool.DefineTool(async (string key) =>
            {
                Interlocked.Increment(ref calls);
                return key;
            }, toolOptions: new CopilotToolOptions { SkipPermission = false },
                factoryOptions: new AIFunctionFactoryOptions { Name = ToolName }),
        ];
        config.OnPermissionRequest = (_, _) =>
        {
            Interlocked.Increment(ref denials);
            return Task.FromResult(PermissionDecision.Reject("Controlled host policy denial."));
        };
        await using var session = await client.CreateSessionAsync(config);
        await session.SendAndWaitAsync(new MessageOptions { Prompt = "Request the permission-gated lookup." },
            timeout: TimeSpan.FromSeconds(20));
        Require(denials == 1 && calls == 0, "Permission denial did not prevent host tool execution.");
        Console.WriteLine("PASS host permission callback rejected the registered tool before execution.");
    }

    private static async Task ValidateRedirectRefusalAsync()
    {
        await using var destination = await FakeOpenAiProvider.StartAsync();
        await using var upstream = await FakeOpenAiProvider.StartAsync();
        upstream.RedirectUrl = destination.BaseUrl + "/chat/completions";
        await using var proxy = await OllamaInferenceProxy.StartAsync(upstream.BaseUrl);
        using var caller = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        using var response = await caller.PostAsync(proxy.BaseUrl + "/chat/completions",
            new StringContent("{}")).WaitAsync(TimeSpan.FromSeconds(5));
        Require(response.StatusCode == HttpStatusCode.BadGateway &&
                destination.RequestCount == 0 && proxy.RequestCount == 1,
            "The inference proxy did not explicitly refuse the controlled redirect.");
        Console.WriteLine("PASS inference proxy refused HTTP 307 without forwarding a request to the redirect destination.");
    }

    private static async Task ValidateActiveCrashAsync(string temporaryRoot)
    {
        var home = Path.Combine(temporaryRoot, "crash-home");
        var workspace = Path.Combine(temporaryRoot, "crash-workspace");
        Directory.CreateDirectory(workspace);
        await using var provider = await FakeOpenAiProvider.StartAsync();
        await using var client = CreateClient(home, workspace);
        var existingProcesses = System.Diagnostics.Process.GetProcesses();
        var existingProcessIds = existingProcesses.Select(process => process.Id).ToHashSet();
        foreach (var process in existingProcesses)
        {
            process.Dispose();
        }
        await client.StartAsync();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var config = CreateSessionConfig(new ProviderConfig { Type = "openai", BaseUrl = provider.BaseUrl },
            "fixture-model", ToolName, async (string key) => key);
        config.Tools =
        [
            CopilotTool.DefineTool(async (string key, CancellationToken token) =>
            {
                started.TrySetResult();
                using var registration = token.Register(() => cancelled.TrySetResult());
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return key;
            }, toolOptions: new CopilotToolOptions { SkipPermission = true },
                factoryOptions: new AIFunctionFactoryOptions { Name = ToolName }),
        ];
        var session = await client.CreateSessionAsync(config);
        var turn = session.SendAndWaitAsync(new MessageOptions { Prompt = "Call the blocking lookup." },
            timeout: TimeSpan.FromSeconds(10));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var processListing = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ps")
        {
            Arguments = "-axo pid=,ppid=,comm=",
            RedirectStandardOutput = true,
            UseShellExecute = false,
        }) ?? throw new InvalidOperationException("Could not inspect owned runtime process.");
        var listing = await processListing.StandardOutput.ReadToEndAsync();
        await processListing.WaitForExitAsync();
        var owned = listing.Split('\n')
            .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length >= 3 && parts[1] == Environment.ProcessId.ToString() &&
                            !existingProcessIds.Contains(int.Parse(parts[0])) &&
                            (parts[2].Contains("copilot", StringComparison.Ordinal) ||
                             parts[2].EndsWith("runtime.node", StringComparison.Ordinal)))
            .ToArray();
        Require(owned.Length == 1, "Could not uniquely identify this harness's disposable runtime child.");
        using var runtime = System.Diagnostics.Process.GetProcessById(int.Parse(owned[0][0]));
        runtime.Kill();
        await runtime.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var interrupted = false;
        try
        {
            await turn;
        }
        catch (Exception exception) when (exception is not TimeoutException)
        {
            interrupted = true;
        }
        catch (TimeoutException)
        {
            Console.WriteLine("OBSERVATION runtime crash required the bounded send deadline to terminate the caller wait.");
        }
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await client.ForceStopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await client.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await client.PingAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Require(provider.RequestCount == 1, "A crashed turn sent another provider request.");
        Console.WriteLine($"PASS active runtime kill cancelled its host callback and allowed disposal/restart; immediate turn interruption: {interrupted}.");
    }

    private static async Task ValidateHostBudgetsAsync(CopilotClient client)
    {
        const int promptCharacterLimit = 64;
        static void ValidatePrompt(string prompt)
        {
            if (prompt.Length > promptCharacterLimit)
            {
                throw new ArgumentException("Prompt exceeds the host character budget.");
            }
        }
        ValidatePrompt(new string('a', promptCharacterLimit));
        var rejected = false;
        try
        {
            ValidatePrompt(new string('a', promptCharacterLimit + 1));
        }
        catch (ArgumentException)
        {
            rejected = true;
        }
        Require(rejected, "The host prompt boundary did not reject an oversized prompt.");

        await using var provider = await FakeOpenAiProvider.StartAsync();
        provider.RepeatTool = true;
        var toolAttempts = 0;
        var executions = 0;
        var budgetExceeded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var session = await client.CreateSessionAsync(CreateSessionConfig(
            new ProviderConfig { Type = "openai", BaseUrl = provider.BaseUrl }, "fixture-model", ToolName,
            async (string key) =>
            {
                if (Interlocked.Increment(ref toolAttempts) > 1)
                {
                    budgetExceeded.TrySetResult();
                    throw new InvalidOperationException("Host tool invocation budget exhausted.");
                }
                Interlocked.Increment(ref executions);
                return key;
            }));
        var idleCount = 0;
        using var subscription = session.On<SessionEvent>(evt =>
        {
            if (evt is SessionIdleEvent)
            {
                Interlocked.Increment(ref idleCount);
            }
        });
        var prompt = "Exercise the tool loop.";
        ValidatePrompt(prompt);
        var turn = session.SendAndWaitAsync(new MessageOptions { Prompt = prompt },
            timeout: TimeSpan.FromSeconds(15));
        await budgetExceeded.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await session.AbortAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await turn.WaitAsync(TimeSpan.FromSeconds(5));
        Require(executions == 1 && toolAttempts >= 2, "The host tool budget did not stop excess execution.");
        Require(idleCount == 1, "The budget-aborted turn did not emit exactly one idle event.");
        Console.WriteLine("PASS host character boundary (64 accepted, 65 rejected) and one-execution tool budget with explicit AbortAsync and one idle event; character limit is not a token limit.");
    }

    private static CopilotClient CreateClient(string runtimeHome, string workspace, bool suppressRuntimeLogs = false) =>
        new(new CopilotClientOptions
        {
            Mode = CopilotClientMode.Empty,
            BaseDirectory = runtimeHome,
            WorkingDirectory = workspace,
            UseLoggedInUser = false,
            LogLevel = suppressRuntimeLogs ? CopilotLogLevel.None : CopilotLogLevel.Error,
            Environment = CreateRuntimeEnvironment(runtimeHome),
        });

    private static IReadOnlyDictionary<string, string> CreateRuntimeEnvironment(string runtimeHome)
    {
        var temporaryDirectory = Path.Combine(runtimeHome, "tmp");
        Directory.CreateDirectory(runtimeHome);
        Directory.CreateDirectory(temporaryDirectory);

        return new Dictionary<string, string>
        {
            ["HOME"] = runtimeHome,
            ["USERPROFILE"] = runtimeHome,
            ["TMPDIR"] = temporaryDirectory,
            ["TMP"] = temporaryDirectory,
            ["TEMP"] = temporaryDirectory,
            ["XDG_CONFIG_HOME"] = Path.Combine(runtimeHome, "config"),
            ["XDG_CACHE_HOME"] = Path.Combine(runtimeHome, "cache"),
        };
    }

    private static SessionConfig CreateSessionConfig(
        ProviderConfig provider,
        string model,
        string toolName,
        Func<string, Task<string>> handler) =>
        new()
        {
            Model = model,
            Provider = provider,
            AvailableTools = [$"custom:{toolName}"],
            EnableSessionStore = false,
            InfiniteSessions = new InfiniteSessionConfig { Enabled = false },
            SystemMessage = new SystemMessageConfig
            {
                Mode = SystemMessageMode.Replace,
                Content = "Use only the explicitly registered read-only lookup tool when needed.",
            },
            OnPermissionRequest = (_, _) => Task.FromResult(PermissionDecision.Reject("Only registered read-only tools are permitted.")),
            Tools =
            [
                CopilotTool.DefineTool(
                    handler,
                    toolOptions: new CopilotToolOptions { SkipPermission = true },
                    factoryOptions: new AIFunctionFactoryOptions
                    {
                        Name = toolName,
                        Description = "Read a fixed validation fixture; it has no external side effects.",
                    }),
            ],
        };

    private static SessionConfig CreateTextOnlySessionConfig(ProviderConfig provider, string model) =>
        new()
        {
            Model = model,
            Provider = provider,
            AvailableTools = [],
            EnableSessionStore = false,
            InfiniteSessions = new InfiniteSessionConfig { Enabled = false },
            Streaming = true,
            SystemMessage = new SystemMessageConfig
            {
                Mode = SystemMessageMode.Replace,
                Content = "Return a brief plain-text answer.",
            },
        };

    private static async Task ValidateToolCancellationAsync(string temporaryRoot)
    {
        var runtimeHome = Path.Combine(temporaryRoot, "cancel-copilot-home");
        var workspace = Path.Combine(temporaryRoot, "cancel-workspace");
        Directory.CreateDirectory(workspace);
        await using var provider = await FakeOpenAiProvider.StartAsync();
        await using var client = CreateClient(runtimeHome, workspace);
        await client.StartAsync();

        var toolStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var terminal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var eventTypes = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var terminalEvents = 0;
        await using var session = await client.CreateSessionAsync(new SessionConfig
        {
            Model = "fixture-model",
            Provider = new ProviderConfig { Type = "openai", BaseUrl = provider.BaseUrl },
            AvailableTools = [$"custom:{ToolName}"],
            EnableSessionStore = false,
            InfiniteSessions = new InfiniteSessionConfig { Enabled = false },
            Tools =
            [
                CopilotTool.DefineTool(
                    async ([Description("Block until cancelled")] string key, CancellationToken cancellationToken) =>
                    {
                        toolStarted.TrySetResult();
                        using var registration = cancellationToken.Register(() => callbackCancelled.TrySetResult());
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                        return key;
                    },
                    toolOptions: new CopilotToolOptions { SkipPermission = true },
                    factoryOptions: new AIFunctionFactoryOptions
                    {
                        Name = ToolName,
                        Description = "A controlled tool that waits for cancellation.",
                    }),
            ],
        });
        using var subscription = session.On<SessionEvent>(evt =>
        {
            eventTypes.Enqueue(evt.Type);
            if (evt is SessionIdleEvent or SessionErrorEvent)
            {
                Interlocked.Increment(ref terminalEvents);
                terminal.TrySetResult();
            }
        });

        var turn = session.SendAndWaitAsync(
            new MessageOptions { Prompt = "Invoke the blocking read-only lookup." },
            timeout: TimeSpan.FromMilliseconds(500));
        await toolStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));
        try
        {
            await turn;
            throw new InvalidOperationException("The bounded turn unexpectedly completed while its tool was blocked.");
        }
        catch (TimeoutException)
        {
        }
        await session.AbortAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await callbackCancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await terminal.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Require(terminalEvents == 1,
            $"Expected exactly one terminal session event after cancellation; observed {terminalEvents}. Events: {string.Join(", ", eventTypes)}");
        Require(provider.RequestCount == 1, "The aborted turn unexpectedly made another provider request.");
        Console.WriteLine($"PASS bounded turn timeout followed by AbortAsync cancelled the running host tool without a follow-up provider request ({terminalEvents} terminal event(s)).");
    }

    private static (ProviderConfig Provider, string Model) CreateCloudProvider()
    {
        var settings = CloudEvaluationSettings.Load(Path.Combine(Directory.GetCurrentDirectory(), ".env.cloud-evaluation"));
        return (new ProviderConfig
        {
            Type = "openai",
            BaseUrl = settings.BaseUrl,
            WireApi = settings.WireApi,
            ApiKey = settings.ApiKey,
        }, settings.Model);
    }

    private static void CreateHostileAmbientConfiguration(string workspace, string ambientHome)
    {
        Directory.CreateDirectory(Path.Combine(workspace, ".github", "skills", "ambient"));
        Directory.CreateDirectory(Path.Combine(workspace, ".github", "agents"));
        Directory.CreateDirectory(Path.Combine(ambientHome, ".copilot"));
        File.WriteAllText(Path.Combine(workspace, "AGENTS.md"), HostileMarker);
        File.WriteAllText(Path.Combine(workspace, ".github", "copilot-instructions.md"), HostileMarker);
        File.WriteAllText(Path.Combine(workspace, ".github", "skills", "ambient", "SKILL.md"), HostileMarker);
        File.WriteAllText(Path.Combine(workspace, ".github", "agents", "ambient.agent.md"), HostileMarker);
        File.WriteAllText(Path.Combine(workspace, ".mcp.json"),
            """{"mcpServers":{"ambient":{"command":"jarvis-ambient-command-must-not-run"}}}""");
        File.WriteAllText(Path.Combine(ambientHome, ".copilot", "mcp-config.json"),
            """{"mcpServers":{"ambient-user":{"command":"jarvis-ambient-command-must-not-run"}}}""");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}

internal sealed class ScopedEnvironment : IDisposable
{
    private readonly IReadOnlyDictionary<string, string?> _previous;

    public ScopedEnvironment(IReadOnlyDictionary<string, string?> values)
    {
        _previous = values.Keys.ToDictionary(key => key, Environment.GetEnvironmentVariable);
        foreach (var (key, value) in values)
        {
            Environment.SetEnvironmentVariable(key, value);
        }
    }

    public void Dispose()
    {
        foreach (var (key, value) in _previous)
        {
            Environment.SetEnvironmentVariable(key, value);
        }
    }
}

internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory() => Path = Directory.CreateTempSubdirectory("jarvis-sdk-validation-").FullName;
    public string Path { get; }
    public void Dispose() => Directory.Delete(Path, recursive: true);
}
