using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;
using PersonalAgent.Application;
using PersonalAgent.Application.Approvals;
using PersonalAgent.Application.Memory;
using PersonalAgent.Application.TurnCoordination;
using PersonalAgent.Application.Tools.Home;
using PersonalAgent.Domain;

namespace PersonalAgent.Web;

internal static class ChatApi
{
    internal static void Map(WebApplication app)
    {
        var auth = app.MapGroup("/api/auth");
        auth.MapGet("/status", async (OwnerAuthenticationService service, CancellationToken ct) =>
            Results.Ok(new { bootstrapRequired = !await service.IsOwnerConfiguredAsync(ct) }));
        auth.MapGet("/csrf", (HttpContext context, IAntiforgery antiforgery) =>
        {
            var tokens = antiforgery.GetAndStoreTokens(context);
            return Results.Ok(new { token = tokens.RequestToken });
        });
        auth.MapPost("/bootstrap", async (
            BootstrapRequest request,
            HttpContext context,
            IAntiforgery antiforgery,
            OwnerAuthenticationService service,
            CancellationToken ct) =>
        {
            if (!await OwnerHttpSecurity.ValidateCsrfAsync(antiforgery, context, ct))
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid request token.");
            }

            if (!await service.BootstrapAsync(request.BootstrapToken, request.Passphrase, ct))
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Bootstrap was rejected.");
            }

            await service.SignInAsync(context, request.RememberMe, ct);
            return Results.Ok(new { authenticated = true });
        }).RequireRateLimiting("owner-auth");
        auth.MapPost("/login", async (
            LoginRequest request,
            HttpContext context,
            IAntiforgery antiforgery,
            OwnerAuthenticationService service,
            CancellationToken ct) =>
        {
            if (!await OwnerHttpSecurity.ValidateCsrfAsync(antiforgery, context, ct))
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid request token.");
            }

            if (!await service.VerifyAsync(request.Passphrase, ct))
            {
                return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Sign-in failed.");
            }

            await service.SignInAsync(context, request.RememberMe, ct);
            return Results.Ok(new { authenticated = true });
        }).RequireRateLimiting("owner-auth");
        auth.MapPost("/logout", async (HttpContext context, IAntiforgery antiforgery, CancellationToken ct) =>
        {
            if (!await OwnerHttpSecurity.ValidateCsrfAsync(antiforgery, context, ct))
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid request token.");
            }

            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Ok(new { authenticated = false });
        }).RequireAuthorization();

        var api = app.MapGroup("/api").RequireAuthorization();
        api.MapGet("/status", (
            HttpContext context,
            IConfiguration configuration,
            ILocalTurnCoordinator readiness) =>
        {
            var endpoint = configuration["JARVIS_OLLAMA_BASE_URL"];
            var model = configuration["JARVIS_PROFILE"] is "Simulator" or "E2E"
                ? "simulator-model"
                : configuration["JARVIS_OLLAMA_MODEL"];
            return Results.Ok(new
            {
                route = "Local",
                provider = configuration["JARVIS_PROFILE"] is "Simulator" or "E2E" ? "Simulator" : "Ollama",
                model = string.IsNullOrWhiteSpace(model) ? null : model,
                endpointConfigured = !string.IsNullOrWhiteSpace(endpoint) || configuration["JARVIS_PROFILE"] is "Simulator" or "E2E",
                readiness = readiness.IsReady,
                modelConnectivity = "not_probed",
                identity = OwnerHttpSecurity.GetOwnerId(context.User)
            });
        });

        api.MapGet("/conversations", async (
            HttpContext context,
            IConversationStore conversations,
            CancellationToken ct) =>
        {
            var owner = OwnerHttpSecurity.GetOwnerId(context.User)!;
            return Results.Ok(await conversations.ReadRecentConversationsAsync(owner, 100, ct));
        });
        api.MapGet("/activity", async (
            HttpContext context,
            IConversationStore conversations,
            CancellationToken ct) =>
        {
            var owner = OwnerHttpSecurity.GetOwnerId(context.User)!;
            var turns = await conversations.ReadRecentTurnsForOwnerAsync(owner, 100, ct);
            return Results.Ok(turns.Select(turn => new
            {
                conversationId = turn.ConversationId,
                turnId = turn.Id,
                status = turn.Status.ToString(),
                updatedAtUtc = turn.UpdatedAtUtc
            }));
        });
        api.MapGet("/actions", async (
            HttpContext context,
            IActionJournalStore journal,
            ApprovalWorkflowService approvalWorkflow,
            CancellationToken ct) =>
        {
            var owner = OwnerHttpSecurity.GetOwnerId(context.User)!;
            var recoveryIssues = await approvalWorkflow.RecoverUnapprovedExecutingActionsAsync(owner, ct);
            var recoveryIssueIds = recoveryIssues.Select(issue => issue.ActionId).ToHashSet();
            var actions = await journal.ReadRecentForOwnerAsync(owner, 100, ct);
            return Results.Ok(actions.Select(action => new
            {
                actionId = action.Id.Value,
                actionType = action.ActionType,
                status = action.Status switch
                {
                    "Succeeded" => "Verified",
                    "Failed" => "Failed",
                    "Unknown" => "Unknown",
                    "AwaitingApproval" => "Awaiting approval",
                    "Rejected" => "Rejected",
                    "Expired" => "Expired",
                    _ => action.Status
                },
                reasonCode = action.Outcome?.ReasonCode,
                recoveryError = recoveryIssueIds.Contains(action.Id) ? "invalid_action_journal" : null,
                updatedAtUtc = action.UpdatedAtUtc,
                memoryInvalidated = action.MemoryInvalidated
            }));
        });
        api.MapGet("/approvals", async (
            HttpContext context,
            ApprovalWorkflowService workflow,
            CancellationToken ct) =>
        {
            var owner = OwnerHttpSecurity.GetOwnerId(context.User)!;
            return Results.Ok(await workflow.ReadRecentAsync(owner, 100, ct));
        });
        api.MapPost("/approvals/{id:guid}/decision", async (
            Guid id,
            ApprovalDecisionRequest request,
            HttpContext context,
            IAntiforgery antiforgery,
            ApprovalWorkflowService workflow,
            IAuditStore audit,
            IClock clock,
            CancellationToken ct) =>
        {
            if (!await OwnerHttpSecurity.ValidateCsrfAsync(antiforgery, context, ct))
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid request token.");
            }
            if (request.ExpectedVersion is null or < 1 || request.Approved is null)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "An approval version and decision are required.");
            }

            var owner = OwnerHttpSecurity.GetOwnerId(context.User)!;
            var approvalId = new ApprovalId(id);
            await audit.AppendAsync(new(
                Guid.NewGuid(),
                "approval.decision_requested",
                id.ToString("D"),
                JsonSerializer.Serialize(new { expectedVersion = request.ExpectedVersion, approved = request.Approved }),
                clock.UtcNow), ct);
            try
            {
                var result = await workflow.ResolveAsync(
                    owner, approvalId, request.ExpectedVersion.Value, request.Approved.Value, ct);
                await audit.AppendAsync(new(
                    Guid.NewGuid(),
                    "approval.decision_completed",
                    id.ToString("D"),
                    JsonSerializer.Serialize(new { status = result.Status, actionId = result.ActionId?.Value }),
                    clock.UtcNow), ct);
                return Results.Ok(result);
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
            catch (PersistenceConcurrencyException)
            {
                return Results.Conflict(new { title = "The approval changed or is no longer available." });
            }
        });
        api.MapPost("/approvals/{id:guid}/expire", async (
            Guid id,
            ApprovalExpiryRequest request,
            HttpContext context,
            IAntiforgery antiforgery,
            ApprovalWorkflowService workflow,
            IAuditStore audit,
            IClock clock,
            CancellationToken ct) =>
        {
            if (!await OwnerHttpSecurity.ValidateCsrfAsync(antiforgery, context, ct))
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid request token.");
            }
            if (request.ExpectedVersion is null or < 1)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "An approval version is required.");
            }

            var owner = OwnerHttpSecurity.GetOwnerId(context.User)!;
            var approvalId = new ApprovalId(id);
            await audit.AppendAsync(new(
                Guid.NewGuid(),
                "approval.expiry_finalization_requested",
                id.ToString("D"),
                JsonSerializer.Serialize(new { expectedVersion = request.ExpectedVersion }),
                clock.UtcNow), ct);
            try
            {
                var result = await workflow.ExpireApprovedUnclaimedAsync(
                    owner, approvalId, request.ExpectedVersion.Value, ct);
                await audit.AppendAsync(new(
                    Guid.NewGuid(),
                    "approval.expiry_finalization_completed",
                    id.ToString("D"),
                    JsonSerializer.Serialize(new { status = result.Status, actionId = result.ActionId?.Value }),
                    clock.UtcNow), ct);
                return Results.Ok(result);
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
            catch (PersistenceConcurrencyException)
            {
                return Results.Conflict(new { title = "The approval changed, is not expired, or is no longer available." });
            }
        });
        api.MapGet("/memory", async (
            string? query,
            int? limit,
            HttpContext context,
            IConfirmedMemoryStore memories,
            CancellationToken ct) =>
        {
            var maximum = limit ?? 100;
            if (maximum is < 1 or > 100 || query?.Length > 512)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Memory search bounds are invalid.");
            }

            var owner = OwnerHttpSecurity.GetOwnerId(context.User)!;
            var facts = string.IsNullOrWhiteSpace(query)
                ? await memories.ReadActiveAsync(owner, maximum, ct)
                : await memories.SearchAsync(owner, query, maximum, ct);
            return Results.Ok(facts);
        });
        api.MapGet("/memory/{id:guid}", async (
            Guid id,
            HttpContext context,
            IConfirmedMemoryStore memories,
            CancellationToken ct) =>
        {
            var fact = await memories.GetAsync(
                OwnerHttpSecurity.GetOwnerId(context.User)!, new MemoryFactId(id), ct);
            return fact is null ? Results.NotFound() : Results.Ok(fact);
        });
        api.MapGet("/memory/conflicts", async (
            string? subject,
            string? key,
            Guid? excludeId,
            HttpContext context,
            MemoryProposalService proposals,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(key)
                || subject.Length > 256 || key.Length > 128)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Memory conflict query is invalid.");
            }
            var conflicts = await proposals.ReadConflictsAsync(
                OwnerHttpSecurity.GetOwnerId(context.User)!,
                subject,
                key,
                excludeId is null ? null : new MemoryFactId(excludeId.Value),
                ct);
            return Results.Ok(conflicts);
        });
        api.MapPost("/memory/proposals", async (
            MemoryProposalRequest request,
            HttpContext context,
            IAntiforgery antiforgery,
            MemoryProposalService proposals,
            IAuditStore audit,
            IClock clock,
            CancellationToken ct) =>
        {
            if (!await OwnerHttpSecurity.ValidateCsrfAsync(antiforgery, context, ct))
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid request token.");
            }
            if (string.IsNullOrWhiteSpace(request.Operation)
                || request.RequestId == Guid.Empty
                || request.ConversationId == Guid.Empty
                || request.ExpectedVersion < 0
                || request.Conflicts?.Count > 100
                || request.Conflicts?.Any(conflict => conflict is null) == true)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Memory proposal is invalid.");
            }

            var owner = OwnerHttpSecurity.GetOwnerId(context.User)!;
            var change = new MemoryChange(
                request.Operation,
                request.FactId is { } factId ? new MemoryFactId(factId) : new MemoryFactId(request.RequestId),
                request.ExpectedVersion,
                request.Subject,
                request.Key,
                request.Value,
                request.Conflicts?.Select(conflict =>
                    new MemoryReference(new MemoryFactId(conflict.Id), conflict.Version)).ToArray() ?? [],
                request.DistinguishingContext);
            await audit.AppendAsync(new(
                Guid.NewGuid(),
                "memory.proposal_requested",
                request.Operation,
                JsonSerializer.Serialize(new { conversationId = request.ConversationId }),
                clock.UtcNow), ct);
            try
            {
                var result = await proposals.ProposeAsync(
                    owner,
                    new ConversationId(request.ConversationId),
                    request.RequestId,
                    change,
                    ct);
                await audit.AppendAsync(new(
                    Guid.NewGuid(),
                    "memory.proposal_completed",
                    result.Result.ActionId?.Value.ToString("D") ?? string.Empty,
                    JsonSerializer.Serialize(new { status = result.Result.Status, result.ConversationId }),
                    clock.UtcNow), ct);
                return Results.Ok(result);
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
            catch (PersistenceConcurrencyException)
            {
                return Results.Conflict(new { title = "The memory fact or conflict set changed. Review current facts again." });
            }
            catch (TurnRequestConflictException)
            {
                return Results.Conflict(new { title = "This proposal request ID was already used for different content." });
            }
            catch (ArgumentException)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Memory proposal is invalid.");
            }
        });
        api.MapGet("/household", (HomeUiStatus home) =>
        {
            var catalog = home.Catalog;
            if (catalog is null)
            {
                return Results.Ok(new
                {
                    configured = false,
                    connectionStatus = "not-configured",
                    connectionProbe = "not-run",
                    entities = Array.Empty<object>()
                });
            }

            var entities = catalog.Entities.Select(entity => new
            {
                entity.EntityId,
                entity.FriendlyName,
                entity.Area,
                entity.Unit,
                operations = catalog.Permissions()
                    .Where(permission => permission.EntityId == entity.EntityId)
                    .Select(permission => permission.Operation)
                    .ToArray()
            }).ToArray();
            return Results.Ok(new
            {
                configured = true,
                connectionStatus = "configured",
                connectionProbe = "not-run",
                entities
            });
        });
        api.MapGet("/audit", async (
            IAuditStore audit,
            IClock clock,
            CancellationToken ct) =>
            Results.Ok(await audit.ReadRecentActivityAsync(clock.UtcNow.AddDays(-30), 200, ct)));
        api.MapPost("/conversations", async (
            HttpContext context,
            CreateConversationRequest request,
            IAntiforgery antiforgery,
            IConversationStore conversations,
            IClock clock,
            CancellationToken ct) =>
        {
            if (!await OwnerHttpSecurity.ValidateCsrfAsync(antiforgery, context, ct))
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid request token.");
            }

            string title;
            try
            {
                title = string.IsNullOrWhiteSpace(request.Title)
                    ? string.Empty
                    : ConversationTitlePolicy.ValidateManualTitle(request.Title);
            }
            catch (ArgumentException)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Conversation title is invalid.");
            }

            var now = clock.UtcNow;
            var conversation = new ConversationRecord(
                ConversationId.New(),
                OwnerHttpSecurity.GetOwnerId(context.User)!,
                title,
                now,
                now,
                TitleIsManual: title.Length > 0);
            var created = await conversations.CreateConversationAsync(conversation, ct);
            return Results.Created($"/api/conversations/{created.Id.Value:D}", created);
        });
        api.MapPatch("/conversations/{id:guid}", async (
            Guid id,
            RenameConversationRequest request,
            HttpContext context,
            IAntiforgery antiforgery,
            IConversationStore conversations,
            IClock clock,
            CancellationToken ct) =>
        {
            if (!await OwnerHttpSecurity.ValidateCsrfAsync(antiforgery, context, ct))
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid request token.");
            }

            if (request.Title is null)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Conversation title is invalid.");
            }

            string title;
            try
            {
                title = ConversationTitlePolicy.ValidateManualTitle(request.Title);
            }
            catch (ArgumentException)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Conversation title is invalid.");
            }

            var renamed = await conversations.RenameConversationAsync(
                new ConversationId(id),
                OwnerHttpSecurity.GetOwnerId(context.User)!,
                title,
                clock.UtcNow,
                ct);
            return renamed is null ? Results.NotFound() : Results.Ok(renamed);
        });
        api.MapGet("/conversations/{id:guid}", async (
            Guid id,
            HttpContext context,
            IConversationStore conversations,
            CancellationToken ct) =>
        {
            var owner = OwnerHttpSecurity.GetOwnerId(context.User)!;
            var conversationId = new ConversationId(id);
            var conversation = await conversations.GetConversationAsync(conversationId, owner, ct);
            if (conversation is null)
            {
                return Results.NotFound();
            }

            var messages = await conversations.ReadRecentAsync(conversationId, 200, ct);
            return Results.Ok(new { conversation, messages });
        });
        api.MapPost("/conversations/{id:guid}/turns", async (
            Guid id,
            HttpContext context,
            SubmitTurnRequest request,
            IAntiforgery antiforgery,
            IConversationStore conversations,
            ILocalTurnCoordinator coordinator,
            LocalTurnCoordinatorOptions coordinatorOptions,
            CancellationToken ct) =>
        {
            if (!await OwnerHttpSecurity.ValidateCsrfAsync(antiforgery, context, ct))
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid request token.");
            }

            if (string.IsNullOrWhiteSpace(request.RequestId)
                || request.RequestId.Length > coordinatorOptions.MaximumRequestIdCharacters
                || string.IsNullOrWhiteSpace(request.Text)
                || request.Text.Length > LocalContextLimits.MaximumTaskTextCharacters)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Turn request is invalid or exceeds the supported size.");
            }

            var conversationId = new ConversationId(id);
            var owner = OwnerHttpSecurity.GetOwnerId(context.User)!;
            if (await conversations.GetConversationAsync(conversationId, owner, ct) is null)
            {
                return Results.NotFound();
            }

            try
            {
                var submitted = await coordinator.SubmitAsync(
                    new LocalTurnRequest(
                        conversationId,
                        request.RequestId,
                        request.Text,
                        RoutingTaskKind.TextConversation,
                        RequiredOwnerId: owner),
                    ct);
                var acceptedConversation = await conversations.GetConversationAsync(conversationId, owner, ct)
                    ?? throw new InvalidOperationException("Accepted conversation was unavailable after durable submission.");
                return Results.Accepted(
                    $"/api/turns/{submitted.Turn.Id.Value:D}/events",
                    new { turnId = submitted.Turn.Id, submitted.Turn.Status, submitted.IsDuplicate, conversation = acceptedConversation });
            }
            catch (ConversationNotFoundException)
            {
                return Results.NotFound();
            }
            catch (TurnQueueFullException)
            {
                return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "The local turn queue is full.");
            }
            catch (TurnRequestConflictException)
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    title: "Request ID was already used for different content.");
            }
            catch (ArgumentOutOfRangeException)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Turn request is outside the supported limits.");
            }
        });
        api.MapPost("/turns/{id:guid}/cancel", async (
            Guid id,
            HttpContext context,
            IAntiforgery antiforgery,
            IConversationStore conversations,
            ILocalTurnCoordinator coordinator,
            CancellationToken ct) =>
        {
            if (!await OwnerHttpSecurity.ValidateCsrfAsync(antiforgery, context, ct))
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid request token.");
            }

            var turnId = new TurnId(id);
            var owner = OwnerHttpSecurity.GetOwnerId(context.User)!;
            if (await conversations.GetTurnForOwnerAsync(turnId, owner, ct) is null)
            {
                return Results.NotFound();
            }

            await coordinator.CancelAsync(turnId, ct);
            return Results.Accepted();
        });
        api.MapGet("/turns/{id:guid}/events", StreamEventsAsync);
        api.MapGet("/settings", async (
            IOwnerSettingsService settings,
            IConfiguration configuration,
            CancellationToken ct) =>
            Results.Ok(await settings.GetRetentionAsync(ReadRetentionDefaults(configuration), ct)));
        api.MapPut("/settings/retention", async (
            HttpContext context,
            [Microsoft.AspNetCore.Mvc.FromBody] RetentionSettings settings,
            IAntiforgery antiforgery,
            IOwnerSettingsService store,
            IClock clock,
            CancellationToken ct) =>
        {
            if (!await OwnerHttpSecurity.ValidateCsrfAsync(antiforgery, context, ct))
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid request token.");
            }

            try
            {
                settings.Validate();
                return Results.Ok(await store.UpdateRetentionAsync(settings, clock.UtcNow, ct));
            }
            catch (ArgumentOutOfRangeException)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Retention must be from 1 through 3650 days.");
            }
        });
    }

    internal static RetentionSettings ReadRetentionDefaults(IConfiguration configuration) =>
        new(
            ReadBoundedDays(configuration["JARVIS_CONVERSATION_RETENTION_DAYS"], 90, "JARVIS_CONVERSATION_RETENTION_DAYS"),
            ReadBoundedDays(configuration["JARVIS_AUDIT_RETENTION_DAYS"], 30, "JARVIS_AUDIT_RETENTION_DAYS"));

    private static int ReadBoundedDays(string? value, int fallback, string key)
    {
        if (value is null)
        {
            return fallback;
        }

        if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var days)
            && days is >= 1 and <= 3650)
        {
            return days;
        }

        throw new InvalidOperationException($"{key} must be an integer from 1 through 3650 days.");
    }

    private static async Task StreamEventsAsync(
        Guid id,
        HttpContext context,
        IConversationStore conversations,
        ILocalTurnCoordinator coordinator)
    {
        var turnId = new TurnId(id);
        var owner = OwnerHttpSecurity.GetOwnerId(context.User)!;
        var turn = await conversations.GetTurnForOwnerAsync(turnId, owner, context.RequestAborted);
        if (turn is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (!TryGetCursor(context.Request.Headers["Last-Event-ID"], out var cursor))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache, no-store";
        context.Response.Headers.Append("X-Accel-Buffering", "no");
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        var terminalObserved = false;
        while (!context.RequestAborted.IsCancellationRequested)
        {
            var events = await coordinator.ReadEventsAfterAsync(turnId, cursor, 100, context.RequestAborted);
            foreach (var item in events)
            {
                await context.Response.WriteAsync(
                    $"id: {item.Sequence.ToString(CultureInfo.InvariantCulture)}\nevent: {item.EventType}\ndata: {item.PayloadJson}\n\n",
                    context.RequestAborted);
                await context.Response.Body.FlushAsync(context.RequestAborted);
                cursor = item.Sequence;
            }

            if (events.Count == 100)
            {
                continue;
            }

            turn = await conversations.GetTurnAsync(turnId, context.RequestAborted);
            if (turn is null)
            {
                return;
            }

            if (turn.Status is TurnStatus.Completed or TurnStatus.Failed or TurnStatus.Cancelled or TurnStatus.Interrupted)
            {
                if (terminalObserved && events.Count == 0)
                {
                    return;
                }

                terminalObserved = true;
                continue;
            }

            await timer.WaitForNextTickAsync(context.RequestAborted);
        }
    }

    private static bool TryGetCursor(string? value, out long cursor)
    {
        cursor = 0;
        return string.IsNullOrEmpty(value)
            || (long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out cursor) && cursor >= 0);
    }

    internal sealed record BootstrapRequest(string BootstrapToken, string Passphrase, bool RememberMe);
    internal sealed record LoginRequest(string Passphrase, bool RememberMe);
    internal sealed record CreateConversationRequest(string? Title);
    internal sealed record RenameConversationRequest(string? Title);
    internal sealed record SubmitTurnRequest(string RequestId, string Text);
    internal sealed record ApprovalDecisionRequest(long? ExpectedVersion, bool? Approved);
    internal sealed record ApprovalExpiryRequest(long? ExpectedVersion);
    internal sealed record MemoryProposalRequest(
        Guid ConversationId,
        Guid RequestId,
        string Operation,
        Guid? FactId,
        long ExpectedVersion,
        string? Subject,
        string? Key,
        string? Value,
        IReadOnlyList<MemoryConflictRequest>? Conflicts,
        string? DistinguishingContext);
    internal sealed record MemoryConflictRequest(Guid Id, long Version);
}

internal sealed record HomeUiStatus(HomeEntityCatalog? Catalog);

internal static class OwnerAuthenticationRegistration
{
    internal static IServiceCollection AddOwnerAuthentication(
        this IServiceCollection services,
        string dataDirectory)
    {
        var instancePath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDirectory));
        if (OperatingSystem.IsWindows())
        {
            instancePath = instancePath.ToUpperInvariant();
        }
        var cookieNamespace = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(instancePath)));
        var keyDirectory = Path.Combine(dataDirectory, "data-protection-keys");
        Directory.CreateDirectory(keyDirectory);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(keyDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        services.AddDataProtection()
            .SetApplicationName("Jarvis")
            .PersistKeysToFileSystem(new DirectoryInfo(keyDirectory));
        services.AddAntiforgery(options =>
        {
            options.HeaderName = "X-CSRF-TOKEN";
            options.Cookie.Name = $"Jarvis.Antiforgery.{cookieNamespace}";
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.Path = "/";
        });
        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options =>
            {
                options.Cookie.Name = $"Jarvis.Session.{cookieNamespace}";
                options.Cookie.HttpOnly = true;
                options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                options.Cookie.SameSite = SameSiteMode.Strict;
                options.Cookie.Path = "/";
                options.SlidingExpiration = true;
                options.ExpireTimeSpan = TimeSpan.FromHours(12);
                options.Events.OnRedirectToLogin = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                };
                options.Events.OnRedirectToAccessDenied = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                };
            });
        services.AddAuthorization();
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy("owner-auth", context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 20,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                        AutoReplenishment = true
                    }));
        });
        services.AddSingleton<IPasswordHasher<OwnerIdentity>, PasswordHasher<OwnerIdentity>>();
        return services;
    }
}
