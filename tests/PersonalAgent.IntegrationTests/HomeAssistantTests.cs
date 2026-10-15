using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PersonalAgent.Application;
using PersonalAgent.Application.Approvals;
using PersonalAgent.Application.Tools;
using PersonalAgent.Application.Tools.Home;
using PersonalAgent.Domain;
using PersonalAgent.Infrastructure.HomeAssistant;
using PersonalAgent.Infrastructure.Persistence;
using PersonalAgent.SimulatorEndpoints;
using PersonalAgent.TestSupport;
using Xunit;

namespace PersonalAgent.IntegrationTests;

public sealed class HomeAssistantTests
{
    [Theory]
    [InlineData(503, 1, 2, true)]
    [InlineData(408, 1, 2, true)]
    [InlineData(429, 1, 2, true)]
    [InlineData(502, 2, 2, false)]
    [InlineData(504, 2, 2, false)]
    [InlineData(401, 1, 1, false)]
    [InlineData(403, 1, 1, false)]
    [InlineData(404, 1, 1, false)]
    [InlineData(400, 1, 1, false)]
    [InlineData(500, 1, 1, false)]
    [Trait("Category", "Integration")]
    public async Task SafeReadsRetryOnlyOnceForExplicitTransientFailures(int status, int failures, int requests, bool success)
    {
        await using var fixture = await Fixture.StartAsync();
        fixture.Endpoint.Configure(Enumerable.Repeat(status, failures));
        if (success)
        {
            var result = await fixture.Client.GetStateAsync("sensor.temperature", default);
            Assert.Equal("21.5", result.State);
            Assert.Equal(fixture.Clock.UtcNow, result.ObservedAtUtc);
        }
        else
        {
            var error = await Assert.ThrowsAsync<HomeAssistantException>(
                () => fixture.Client.GetStateAsync("sensor.temperature", default).AsTask());
            Assert.Equal(status switch
            {
                401 or 403 => HomeAssistantFailure.Unauthorized,
                404 => HomeAssistantFailure.UnknownEntity,
                _ => HomeAssistantFailure.Transport
            }, error.Failure);
            Assert.DoesNotContain("synthetic-home-token", error.ToString(), StringComparison.Ordinal);
        }
        Assert.Equal(requests, fixture.Endpoint.ReadCount);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ReadsDistinguishStalenessAvailabilityAndSourceTimeWithoutInventingValues()
    {
        await using var fixture = await Fixture.StartAsync();
        var stale = await fixture.Client.GetStateAsync("sensor.stale", default);
        var unavailable = await fixture.Client.GetStateAsync("sensor.unavailable", default);
        Assert.True(stale.Stale);
        Assert.True(stale.Available);
        Assert.Equal(fixture.Clock.UtcNow.AddHours(-1), stale.ObservedAtUtc);
        Assert.False(unavailable.Available);
        Assert.False(unavailable.Stale);
        await Assert.ThrowsAsync<HomeAssistantException>(() => fixture.Client.GetStateAsync("lock.forbidden", default).AsTask());
        Assert.Equal(2, fixture.Endpoint.ReadCount);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("not JSON")]
    [InlineData("""{"entity_id":"light.other","state":"on","last_updated":"2026-01-01T00:00:00Z","attributes":{}}""")]
    [InlineData("""{"entity_id":"sensor.temperature","state":null,"last_updated":"2026-01-01T00:00:00Z","attributes":{}}""")]
    [InlineData("""{"entity_id":"sensor.temperature","state":"synthetic-home-token","last_updated":"2026-01-01T00:00:00Z","attributes":{}}""")]
    [InlineData("""{"entity_id":"sensor.temperature","state":"21","last_updated":"2099-01-01T00:00:00Z","attributes":{}}""")]
    [InlineData("""{"entity_id":"sensor.temperature","state":"21","last_updated":"2026-01-01T00:00:00Z","attributes":{"brightness":256}}""")]
    [InlineData("""{"entity_id":"sensor.temperature","state":"21","last_updated":"2026-01-01T00:00:00Z","attributes":{"temperature":"bad"}}""")]
    [Trait("Category", "Integration")]
    public async Task MalformedUntrustedBodiesFailOnceAndNeverLeakRemotePayload(string body)
    {
        await using var fixture = await Fixture.StartAsync();
        fixture.Endpoint.ReadBody = body;
        var failure = await Assert.ThrowsAsync<HomeAssistantException>(() => fixture.Client.GetStateAsync("sensor.temperature", default).AsTask());
        Assert.Equal(HomeAssistantFailure.InvalidResponse, failure.Failure);
        Assert.DoesNotContain("synthetic-home-token", failure.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, fixture.Endpoint.ReadCount);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task OversizedResponsesAreRejectedBeforeDeserialization()
    {
        await using var fixture = await Fixture.StartAsync();
        fixture.Endpoint.ReadBody = new string('x', 65_537);
        Assert.Equal(HomeAssistantFailure.InvalidResponse,
            (await Assert.ThrowsAsync<HomeAssistantException>(() => fixture.Client.GetStateAsync("sensor.temperature", default).AsTask())).Failure);
        Assert.Equal(1, fixture.Endpoint.ReadCount);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task CallerCancellationAndOriginalDeadlinePreventSecondAttempt()
    {
        await using var fixture = await Fixture.StartAsync(new(RequestTimeoutSeconds: 1));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Endpoint.BeforeRead = async token =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        };
        using var cancellation = new CancellationTokenSource();
        var read = fixture.Client.GetStateAsync("sensor.temperature", cancellation.Token).AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        Assert.Equal(1, fixture.Endpoint.ReadCount);
        var timedOut = await Assert.ThrowsAsync<HomeAssistantException>(() => fixture.Client.GetStateAsync("sensor.temperature", default).AsTask());
        Assert.Equal(HomeAssistantFailure.Timeout, timedOut.Failure);
        Assert.Equal(2, fixture.Endpoint.ReadCount);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task FourToolsUseRealHttpAndSqliteWithExactApprovalAndReplay()
    {
        await using var fixture = await Fixture.StartAsync(new(true, true));
        var list = await fixture.Dispatch("home.list_entities", "{}");
        Assert.Equal("Succeeded", list.Status);
        Assert.DoesNotContain("lock.forbidden", list.ResultJson!, StringComparison.Ordinal);
        Assert.DoesNotContain("attributes", list.ResultJson!, StringComparison.Ordinal);
        var read = await fixture.Dispatch("home.get_state", """{"entity":"temperature"}""");
        Assert.Contains("\"unit\":\"C\"", read.ResultJson!, StringComparison.Ordinal);
        Assert.Contains("observedAtUtc", read.ResultJson!, StringComparison.Ordinal);
        Assert.Equal("Succeeded", (await fixture.Dispatch("home.set_light",
            """{"entity":"lamp","on":true,"brightness":42}""", "turn on simulator lamp at brightness 42")).Status);
        var request = fixture.Request("home.set_temperature", """{"entity":"thermostat","temperature":22}""");
        var pending = await fixture.Dispatcher.DispatchAsync(request, default);
        Assert.Equal("AwaitingApproval", pending.Status);
        Assert.Equal(1, fixture.Endpoint.WriteCount);
        var approval = await fixture.Approvals.GetAsync(pending.ApprovalId!.Value, default);
        await fixture.Approvals.ResolveAsync(approval!.Id, "owner", approval.ExpectedVersion, true, default);
        var executed = await fixture.Dispatcher.DispatchAsync(request, default);
        Assert.Equal("Succeeded", executed.Status);
        Assert.Equal(executed, await fixture.Dispatcher.DispatchAsync(request, default));
        Assert.Equal(executed, await fixture.CreateDispatcher().DispatchAsync(request, default));
        Assert.Equal(2, fixture.Endpoint.WriteCount);
        Assert.Equal(22, (await fixture.Client.GetStateAsync("climate.simulator", default)).TargetTemperature);
        var rejectRequest = fixture.Request("home.set_light", """{"entity":"lamp","on":false}""", "please turn it off");
        var reject = await fixture.Dispatcher.DispatchAsync(rejectRequest, default);
        var rejectApproval = await fixture.Approvals.GetAsync(reject.ApprovalId!.Value, default);
        await fixture.Approvals.ResolveAsync(rejectApproval!.Id, "owner", rejectApproval.ExpectedVersion, false, default);
        Assert.Equal("Rejected", (await fixture.Dispatcher.DispatchAsync(rejectRequest, default)).Status);
        Assert.Equal(2, fixture.Endpoint.WriteCount);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task AmbiguousSecurityUnknownAndOutOfBoundsProposalsMakeNoRequests()
    {
        await using var fixture = await Fixture.StartAsync(new(true, true));
        Assert.Equal("ClarificationRequired", (await fixture.Dispatch("home.set_light", """{"entity":"porch","on":true}""", "turn on porch")).Status);
        foreach (var target in new[] { "lock.forbidden", "alarm_control_panel.house", "light.*", "light.unknown", "switch.appliance", "cover.garage" })
        {
            Assert.Equal("Rejected", (await fixture.Dispatch("home.get_state", JsonSerializer.Serialize(new { entity = target }))).Status);
            Assert.Equal("Rejected", (await fixture.Dispatch("home.set_light", JsonSerializer.Serialize(new { entity = target, on = true }))).Status);
        }
        Assert.Equal("Rejected", (await fixture.Dispatch("home.set_light", """{"entity":"lamp","on":false,"brightness":30}""")).Status);
        Assert.Equal("Rejected", (await fixture.Dispatch("home.set_temperature", """{"entity":"thermostat","temperature":100}""")).Status);
        Assert.Equal(HomeAssistantWriteStatus.Rejected, (await fixture.Client.SetLightAsync("lock.forbidden", true, null, default)).Status);
        Assert.Equal(HomeAssistantWriteStatus.Rejected, (await fixture.Client.SetTemperatureAsync("climate.simulator", 100, default)).Status);
        Assert.Equal(0, fixture.Endpoint.ReadCount);
        Assert.Equal(0, fixture.Endpoint.WriteCount);
    }

    [Theory]
    [InlineData(true, "Succeeded")]
    [InlineData(false, "Unknown")]
    [Trait("Category", "Integration")]
    public async Task UncertainWriteReconcilesOnlyByObservationAndNeverResends(bool apply, string expected)
    {
        await using var fixture = await Fixture.StartAsync(new(AutomaticLightsEnabled: true, VerificationSeconds: 1, PollMilliseconds: 10));
        fixture.Endpoint.Configure([], apply, 503);
        var request = fixture.Request("home.set_light", """{"entity":"lamp","on":true}""", "turn on lamp");
        var result = await fixture.Dispatcher.DispatchAsync(request, default);
        Assert.Equal(expected, result.Status);
        Assert.Equal(1, fixture.Endpoint.WriteCount);
        Assert.Contains("\"submission\":\"Unknown\"", result.ResultJson!, StringComparison.Ordinal);
        Assert.Equal(result, await fixture.CreateDispatcher().DispatchAsync(request, default));
        if (!apply)
        {
            var next = await fixture.Dispatcher.DispatchAsync(request with { CallId = "next" }, default);
            Assert.Equal("equivalent_write_outcome_unknown", next.ReasonCode);
        }
        Assert.Equal(1, fixture.Endpoint.WriteCount);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task CancellationAfterSubmissionNeverClaimsRollbackOrSendsAgain()
    {
        await using var fixture = await Fixture.StartAsync(new(AutomaticLightsEnabled: true));
        var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Endpoint.AfterWrite = async token =>
        {
            sent.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        };
        using var cancellation = new CancellationTokenSource();
        var request = fixture.Request("home.set_light", """{"entity":"lamp","on":true}""", "turn on lamp");
        var dispatch = fixture.Dispatcher.DispatchAsync(request, cancellation.Token).AsTask();
        await sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        var result = await dispatch;
        Assert.Equal("Unknown", result.Status);
        Assert.Equal("on", (await fixture.Client.GetStateAsync("light.simulator_lamp", default)).State);
        Assert.Equal(result, await fixture.Dispatcher.DispatchAsync(request, default));
        Assert.Equal(1, fixture.Endpoint.WriteCount);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PollingObservesDelayedApplicationButAcceptedUnchangedStateRemainsUnknown()
    {
        await using var fixture = await Fixture.StartAsync(new(AutomaticLightsEnabled: true, PollMilliseconds: 10));
        fixture.Endpoint.Configure([], false);
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Endpoint.BeforeRead = async token =>
        {
            if (fixture.Endpoint.ReadCount == 2)
            {
                fixture.Endpoint.SetState(new("light.simulator_lamp", "on", fixture.Clock.UtcNow, true));
                observed.TrySetResult();
            }
            await Task.CompletedTask;
        };
        var result = await fixture.Dispatch("home.set_light", """{"entity":"lamp","on":true}""", "turn on lamp");
        Assert.Equal("Succeeded", result.Status);
        await observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, fixture.Endpoint.ReadCount);
        Assert.Equal(1, fixture.Endpoint.WriteCount);
        fixture.Endpoint.BeforeRead = null;
        fixture.Endpoint.Configure([], false);
        var unconfirmed = await fixture.Dispatch("home.set_light", """{"entity":"lamp","on":false}""", "turn off lamp");
        Assert.Equal("Unknown", unconfirmed.Status);
        Assert.Contains(unconfirmed.ReasonCode, new[] { "home_state_unconfirmed", "verification_interrupted" });
        Assert.Contains("\"submission\":\"Accepted\"", unconfirmed.ResultJson!, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task SecretsNeverAppearInErrorsToolResultsJournalOrCapturedLogs()
    {
        await using var fixture = await Fixture.StartAsync(new(AutomaticLightsEnabled: true));
        fixture.Endpoint.ErrorBody = "Authorization Bearer synthetic-home-token";
        fixture.Endpoint.Configure([401], false, 401);
        var read = await fixture.Dispatch("home.get_state", """{"entity":"temperature"}""");
        var write = await fixture.Dispatch("home.set_light", """{"entity":"lamp","on":true}""", "turn on lamp");
        foreach (var result in new[] { read, write })
        {
            Assert.Equal("Failed", result.Status);
            Assert.DoesNotContain("synthetic-home-token", JsonSerializer.Serialize(result), StringComparison.Ordinal);
            var stored = await fixture.Journal.GetAsync(result.ActionId!.Value, default);
            Assert.DoesNotContain("synthetic-home-token", JsonSerializer.Serialize(stored), StringComparison.Ordinal);
        }
        Assert.DoesNotContain("synthetic-home-token", string.Join("\n", fixture.Logs.Messages), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void TrustedConfigurationFailsSafelyAndNeverEnablesThermostatByDefault()
    {
        Assert.DoesNotContain(HomeAssistantConfiguration.SimulatorCatalog().Permissions(), p => p.ToolName == "home.set_temperature");
        var options = new HomeToolOptions();
        var catalog = HomeAssistantConfiguration.ParseCatalog(
            """[{"EntityId":"sensor.safe","FriendlyName":"safe","Area":"Hall","Unit":"C","Aliases":[]}]""", options);
        Assert.Single(catalog.Entities);
        foreach (var json in new[] { "", "null", "{}", "not JSON", """[{"EntityId":"lock.front","FriendlyName":"lock","Area":"Hall","Unit":null,"Aliases":[]}]""", new string('a', 65_537) })
        {
            Assert.Throws<ArgumentException>(() => HomeAssistantConfiguration.ParseCatalog(json, options));
        }
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [Trait("Category", "Integration")]
    public async Task RealDroppedReadResponsesHaveAtMostOneTransportRetry(int drops, bool success)
    {
        await using var fixture = await Fixture.StartAsync();
        fixture.Endpoint.DropReadResponses = drops;
        if (success)
        {
            Assert.Equal("21.5", (await fixture.Client.GetStateAsync("sensor.temperature", default)).State);
        }
        else
        {
            Assert.Equal(HomeAssistantFailure.Transport,
                (await Assert.ThrowsAsync<HomeAssistantException>(() => fixture.Client.GetStateAsync("sensor.temperature", default).AsTask())).Failure);
        }
        Assert.Equal(2, fixture.Endpoint.ReadCount);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task LostWriteResponseAndRestartRecoveryObserveWithoutReplaying()
    {
        await using var fixture = await Fixture.StartAsync(new(AutomaticLightsEnabled: true));
        fixture.Endpoint.DropWriteResponses = true;
        var request = fixture.Request("home.set_light", """{"entity":"lamp","on":true}""", "turn on lamp");
        var result = await fixture.Dispatcher.DispatchAsync(request, default);
        Assert.Equal("Succeeded", result.Status);
        Assert.Contains("\"submission\":\"Unknown\"", result.ResultJson!, StringComparison.Ordinal);
        Assert.Equal(1, fixture.Endpoint.WriteCount);
        var entry = await fixture.Journal.GetAsync(result.ActionId!.Value, default);
        // Recreate the durable crash checkpoint: delivery happened, but no terminal outcome survived.
        await fixture.Journal.UpdateStatusAsync(entry!.Id, "Executing", entry.Version, fixture.Clock.UtcNow, default);
        var recovered = await fixture.CreateDispatcher().DispatchAsync(request, default);
        Assert.Equal("Succeeded", recovered.Status);
        Assert.Equal(1, fixture.Endpoint.WriteCount);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task StaleMatchingSnapshotCannotVerifyPhysicalSuccess()
    {
        await using var fixture = await Fixture.StartAsync(new(AutomaticLightsEnabled: true, VerificationSeconds: 1, PollMilliseconds: 10));
        fixture.Endpoint.Configure([], false);
        fixture.Endpoint.SetState(new("light.simulator_lamp", "on", fixture.Clock.UtcNow.AddHours(-1), true));
        var result = await fixture.Dispatch("home.set_light", """{"entity":"lamp","on":true}""", "turn on lamp");
        Assert.Equal("Unknown", result.Status);
        Assert.Equal(1, fixture.Endpoint.WriteCount);
    }
    [Fact]
    [Trait("Category", "Integration")]
    public async Task ProductionWebCompositionUsesLiveCatalogDefaultsAndNeverFollowsRedirects()
    {
        await using var fixture = await Fixture.StartAsync();
        using var data = IsolatedDirectory.Create();
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(web =>
        {
            web.UseSetting("JARVIS_PROFILE", "Local");
            web.UseSetting("JARVIS_DATA_DIR", data.Path);
            web.UseSetting("JARVIS_HOME_ASSISTANT_BASE_URL", fixture.Http.BaseAddress!.ToString());
            web.UseSetting("JARVIS_HOME_ASSISTANT_SECRET_REFERENCE", "synthetic-reference");
            web.UseSetting("JARVIS_HOME_ENTITY_CATALOG", JsonSerializer.Serialize(
                HomeAssistantConfiguration.SimulatorCatalog().Entities));
            web.ConfigureTestServices(services => services.AddSingleton<ISecretResolver>(new Secret()));
        });
        using var webClient = factory.CreateClient();
        var catalog = factory.Services.GetRequiredService<HomeEntityCatalog>();
        Assert.False(catalog.Options.AutomaticLightsEnabled);
        Assert.False(catalog.Options.ThermostatEnabled);
        var dispatcher = factory.Services.GetRequiredService<IToolDispatcher>();
        var read = await dispatcher.DispatchAsync(fixture.Request("home.get_state", """{"entity":"temperature"}"""), default);
        Assert.Equal("Succeeded", read.Status);
        var pending = await dispatcher.DispatchAsync(fixture.Request("home.set_light",
            """{"entity":"lamp","on":true}""", "turn on lamp"), default);
        Assert.Equal("AwaitingApproval", pending.Status);
        Assert.Equal(0, fixture.Endpoint.WriteCount);
        fixture.Endpoint.Configure([302]);
        fixture.Endpoint.RedirectLocation = "/api/states/sensor.temperature";
        var client = factory.Services.GetRequiredService<IHomeAssistantClient>();
        Assert.Equal(HomeAssistantFailure.Transport,
            (await Assert.ThrowsAsync<HomeAssistantException>(() => client.GetStateAsync("sensor.temperature", default).AsTask())).Failure);
        Assert.Equal(2, fixture.Endpoint.ReadCount);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PartialLiveConfigurationFailsBeforeAnyHomeRequest()
    {
        using var data = IsolatedDirectory.Create();
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(web =>
        {
            web.UseSetting("JARVIS_PROFILE", "Local");
            web.UseSetting("JARVIS_DATA_DIR", data.Path);
            web.UseSetting("JARVIS_HOME_ASSISTANT_SECRET_REFERENCE", "synthetic-reference");
        });
        var error = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
        Assert.Contains("partial configuration", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Integration")]
    public async Task CancellationOrDeadlineDuringRetryAdmissionCannotSendASecondHttpRequest(bool deadline)
    {
        await using var fixture = await Fixture.StartAsync(new(RequestTimeoutSeconds: 1));
        fixture.Endpoint.Configure([503]);
        var retryAdmission = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var secret = new ControlledSecret(async token =>
        {
            if (Interlocked.Increment(ref calls) == 2)
            {
                retryAdmission.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            return "synthetic-home-token".AsMemory();
        });
        var client = new HomeAssistantRestClient(fixture.Http, fixture.Http.BaseAddress!, new("reference"), secret,
            HomeAssistantConfiguration.SimulatorCatalog(new(RequestTimeoutSeconds: 1)), fixture.Clock);
        using var cancellation = new CancellationTokenSource();
        var pending = client.GetStateAsync("sensor.temperature", cancellation.Token).AsTask();
        await retryAdmission.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (deadline)
        {
            Assert.Equal(HomeAssistantFailure.Timeout, (await Assert.ThrowsAsync<HomeAssistantException>(() => pending)).Failure);
        }
        else
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }
        Assert.Equal(1, fixture.Endpoint.ReadCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("bad\r\nheader")]
    [InlineData("space token")]
    [Trait("Category", "Integration")]
    public async Task InvalidSecretValuesFailWithoutSendingOrExposingThem(string value)
    {
        await using var fixture = await Fixture.StartAsync();
        var secret = new ControlledSecret(_ => Task.FromResult(value.AsMemory()));
        var client = new HomeAssistantRestClient(fixture.Http, fixture.Http.BaseAddress!, new("reference"), secret,
            HomeAssistantConfiguration.SimulatorCatalog(), fixture.Clock);
        var error = await Assert.ThrowsAsync<HomeAssistantException>(() => client.GetStateAsync("sensor.temperature", default).AsTask());
        Assert.Equal(HomeAssistantFailure.Unauthorized, error.Failure);
        Assert.Equal(0, fixture.Endpoint.ReadCount);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task SecretResolutionFailureHasOnlyASafeTypedReason()
    {
        await using var fixture = await Fixture.StartAsync();
        var client = new HomeAssistantRestClient(fixture.Http, fixture.Http.BaseAddress!, new("reference"),
            new ControlledSecret(_ => throw new InvalidOperationException("synthetic-home-token")),
            HomeAssistantConfiguration.SimulatorCatalog(), fixture.Clock);
        var error = await Assert.ThrowsAsync<HomeAssistantException>(() => client.GetStateAsync("sensor.temperature", default).AsTask());
        Assert.Equal(HomeAssistantFailure.Unauthorized, error.Failure);
        Assert.DoesNotContain("synthetic-home-token", error.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, fixture.Endpoint.ReadCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Integration")]
    public async Task ChangingInstallationOrSecretReferenceInvalidatesApprovedActionWithoutSending(bool changeReference)
    {
        var options = new HomeToolOptions(ThermostatEnabled: true);
        await using var first = await Fixture.StartAsync(options);
        await using var second = await Fixture.StartAsync(options);
        var request = first.Request("home.set_temperature", """{"entity":"thermostat","temperature":22}""");
        var pending = await first.Dispatcher.DispatchAsync(request, default);
        var approval = await first.Approvals.GetAsync(pending.ApprovalId!.Value, default);
        await first.Approvals.ResolveAsync(approval!.Id, "owner", approval.ExpectedVersion, true, default);
        var endpoint = changeReference ? first.Http.BaseAddress! : second.Http.BaseAddress!;
        var reference = new SecretReference(changeReference ? "other-reference" : "synthetic-reference");
        var catalog = HomeAssistantConfiguration.SimulatorCatalog(options);
        var changedClient = new HomeAssistantRestClient(first.Http, endpoint, reference, new Secret(), catalog, first.Clock);
        var restarted = first.CreateDispatcher(changedClient, HomeAssistantConfiguration.PolicyVersion(catalog, endpoint, reference));
        var rejected = await restarted.DispatchAsync(request, default);
        Assert.Equal("Rejected", rejected.Status);
        Assert.Equal(0, first.Endpoint.WriteCount);
        Assert.Equal(0, second.Endpoint.WriteCount);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("invalid")]
    [InlineData("cancelled")]
    [InlineData("timeout")]
    [Trait("Category", "Integration")]
    public async Task KnownPreSubmissionFailuresAreDefinitiveAndDoNotLockOutTheRemainingWrite(string failure)
    {
        var options = new HomeToolOptions(AutomaticLightsEnabled: true, RequestTimeoutSeconds: 1);
        await using var fixture = await Fixture.StartAsync(options);
        using var cancellation = new CancellationTokenSource();
        var fail = true;
        var resolving = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resolver = new ControlledSecret(async token =>
        {
            if (fail)
            {
                if (failure == "missing")
                {
                    throw new InvalidOperationException("synthetic-home-token");
                }
                if (failure == "invalid")
                {
                    return "bad\r\nheader".AsMemory();
                }
                resolving.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            return "synthetic-home-token".AsMemory();
        });
        var catalog = HomeAssistantConfiguration.SimulatorCatalog(options);
        var client = new HomeAssistantRestClient(fixture.Http, fixture.Http.BaseAddress!, new("synthetic-reference"),
            resolver, catalog, fixture.Clock);
        var dispatcher = fixture.CreateDispatcher(client);
        var request = fixture.Request("home.set_light", """{"entity":"lamp","on":true}""", "turn on lamp");
        var dispatch = dispatcher.DispatchAsync(request, cancellation.Token).AsTask();
        if (failure == "cancelled")
        {
            await resolving.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
        }
        var result = await dispatch;
        Assert.Equal("Failed", result.Status);
        Assert.Equal("home_not_submitted_" + (failure is "missing" or "invalid" ? "unauthorized" : failure), result.ReasonCode);
        Assert.Equal(0, fixture.Endpoint.WriteCount);
        Assert.Equal(result, await dispatcher.DispatchAsync(request, default));
        fail = false;
        var corrected = await dispatcher.DispatchAsync(request with { CallId = "corrected" }, default);
        Assert.Equal("Succeeded", corrected.Status);
        Assert.Equal(1, fixture.Endpoint.WriteCount);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void ConnectionPolicyIdentityNormalizesRootsRejectsUnsafeUrisAndNeverContainsCredentials()
    {
        var catalog = HomeAssistantConfiguration.SimulatorCatalog();
        var reference = new SecretReference("named-reference");
        var version = HomeAssistantConfiguration.PolicyVersion(catalog, new("http://LOCALHOST:80/ha"), reference);
        Assert.Equal(version, HomeAssistantConfiguration.PolicyVersion(catalog, new("http://localhost/ha/"), reference));
        Assert.NotEqual(version, HomeAssistantConfiguration.PolicyVersion(catalog, new("http://localhost/other/"), reference));
        Assert.NotEqual(version, HomeAssistantConfiguration.PolicyVersion(catalog, new("http://localhost/ha/"), new("different-reference")));
        Assert.DoesNotContain(reference.Name, version, StringComparison.Ordinal);
        foreach (var endpoint in new[] { "ftp://localhost/", "http://user:password@localhost/", "http://localhost/?token=value", "http://localhost/#fragment", "relative" })
        {
            Assert.Throws<ArgumentException>(() => HomeAssistantConfiguration.NormalizeEndpoint(new(endpoint, UriKind.RelativeOrAbsolute)));
        }
        Assert.Throws<ArgumentNullException>(() => HomeAssistantConfiguration.NormalizeEndpoint(null!));
        Assert.Throws<ArgumentNullException>(() => HomeAssistantConfiguration.PolicyVersion(null!, new("http://localhost"), reference));
        Assert.Throws<ArgumentNullException>(() => HomeAssistantConfiguration.PolicyVersion(catalog, new("http://localhost"), null!));
        foreach (var name in new[] { "", "bad/name", new string('x', 257) })
        {
            Assert.Throws<ArgumentException>(() => HomeAssistantConfiguration.PolicyVersion(catalog, new("http://localhost"), new(name)));
        }
    }

    [Theory]
    [InlineData(400, "home_service_rejected")]
    [InlineData(401, "home_unauthorized")]
    [InlineData(403, "home_unauthorized")]
    [InlineData(404, "home_unknownentity")]
    [InlineData(405, "home_service_rejected")]
    [InlineData(422, "home_service_rejected")]
    [Trait("Category", "Integration")]
    public async Task DefinitiveServiceRejectionsAreDistinctAndNeverRetried(int status, string reason)
    {
        await using var fixture = await Fixture.StartAsync();
        fixture.Endpoint.Configure([], false, status);
        var result = await fixture.Client.SetLightAsync("light.simulator_lamp", true, null, default);
        Assert.Equal(HomeAssistantWriteStatus.Rejected, result.Status);
        Assert.Equal(reason, result.ReasonCode);
        Assert.Equal(1, fixture.Endpoint.WriteCount);
        Assert.Equal(0, fixture.Endpoint.ReadCount);
    }

    private sealed class ControlledSecret(Func<CancellationToken, Task<ReadOnlyMemory<char>>> resolve) : ISecretResolver
    {
        public async ValueTask<ReadOnlyMemory<char>> ResolveAsync(SecretReference reference, CancellationToken token) =>
            await resolve(token);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly WebApplication app;
        private readonly IsolatedDatabaseFile file;
        private readonly HomeEntityCatalog catalog;
        public FixedClock Clock { get; } = new();
        public HomeAssistantSimulator Endpoint { get; }
        public HttpClient Http { get; }
        public HomeAssistantRestClient Client { get; }
        public SqliteActionJournalStore Journal { get; }
        public ApprovalService Approvals { get; }
        public ToolDispatcher Dispatcher { get; }
        public Logs Logs { get; }

        private Fixture(WebApplication app, IsolatedDatabaseFile file, SqliteDatabase database,
            FixedClock clock, HomeAssistantSimulator endpoint, HomeEntityCatalog catalog, Logs logs)
        {
            this.app = app;
            this.file = file;
            this.catalog = catalog;
            Clock = clock;
            Endpoint = endpoint;
            Logs = logs;
            Http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            {
                BaseAddress = new Uri(app.Urls.Single()),
                Timeout = Timeout.InfiniteTimeSpan
            };
            Client = new(Http, Http.BaseAddress, new("synthetic-reference"), new Secret(), catalog, clock);
            Journal = new(database);
            Approvals = new(new SqliteApprovalStore(database), Journal, clock);
            Dispatcher = CreateDispatcher();
        }

        public static async Task<Fixture> StartAsync(HomeToolOptions? options = null)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var logs = new Logs();
            builder.Logging.ClearProviders().AddProvider(logs);
            var app = builder.Build();
            var clock = new FixedClock();
            var endpoint = new HomeAssistantSimulator(clock);
            endpoint.Map(app, false);
            await app.StartAsync();
            var file = IsolatedDatabaseFile.Create();
            var database = new SqliteDatabase(file.Path);
            await database.InitializeAsync();
            return new(app, file, database, clock, endpoint, HomeAssistantConfiguration.SimulatorCatalog(options), logs);
        }

        public ToolDispatcher CreateDispatcher(HomeAssistantRestClient? client = null, string? policyVersion = null) => new(
            new ToolRegistry(HomeToolRegistrations.Create(catalog, client ?? Client)),
            new ToolAllowlistPolicy(policyVersion ?? HomeAssistantConfiguration.PolicyVersion(
                catalog, Http.BaseAddress!, new("synthetic-reference")), catalog.Permissions()),
            Journal, Approvals, Clock, TimeSpan.FromSeconds(catalog.Options.VerificationSeconds));

        public ToolDispatchRequest Request(string tool, string args, string? text = null) =>
            new(new TurnId(Guid.NewGuid()), Guid.NewGuid().ToString(), tool, args, "owner", text);

        public ValueTask<ToolDispatchResult> Dispatch(string tool, string args, string? text = null) =>
            Dispatcher.DispatchAsync(Request(tool, args, text), default);

        public async ValueTask DisposeAsync()
        {
            Http.Dispose();
            await app.DisposeAsync();
            file.Dispose();
        }
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = DateTimeOffset.UtcNow;
    }

    private sealed class Secret : ISecretResolver
    {
        public ValueTask<ReadOnlyMemory<char>> ResolveAsync(SecretReference reference, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return ValueTask.FromResult("synthetic-home-token".AsMemory());
        }
    }

    private sealed class Logs : ILoggerProvider, ILogger
    {
        public System.Collections.Concurrent.ConcurrentQueue<string> Messages { get; } = new();
        public ILogger CreateLogger(string category) => this;
        public void Dispose() { }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Enqueue(formatter(state, exception));
    }
}
