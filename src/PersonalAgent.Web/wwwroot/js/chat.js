const authSection = document.getElementById("auth");
const chatSection = document.getElementById("chat");
const errorBox = document.getElementById("error");
const profile = document.getElementById("profile");
const pendingTurnStorageKey = "jarvis.pending-turn.v1";
let csrfToken = "";
let conversationId = "";
let conversationTitle = "";
let activeTurnId = "";
let activeTurnConversationId = "";
let turnSubmissionInFlight = false;
let conversationCreationInFlight = false;
let conversationRenameInFlight = false;
let authenticationInFlight = false;
let pendingTurnSubmission = null;
let conversationSelectionGeneration = 0;
let conversationListGeneration = 0;
let conversationRenameGeneration = 0;
let lastRenamedConversation = null;
let eventSource = null;
let bootstrapRequired = false;
const workspaceViews = ["conversation", "settings", "activity", "approvals", "memory", "household"];

function hideWorkspaceViews() {
    for (const id of workspaceViews) document.getElementById(id).hidden = true;
}

function showError(message) {
    errorBox.textContent = message;
    errorBox.hidden = false;
}

function clearError() {
    errorBox.textContent = "";
    errorBox.hidden = true;
}

function clearPendingTurn() {
    try {
        sessionStorage.removeItem(pendingTurnStorageKey);
        pendingTurnSubmission = null;
        return true;
    } catch (error) {
        showError(`The request could not be cleared from this tab's recovery state: ${error.message}`);
        return false;
    }
}

function setTurnNavigationLocked(locked) {
    document.getElementById("new-conversation").disabled = locked;
    document.getElementById("settings-button").disabled = locked;
    document.getElementById("activity-button").disabled = locked;
    document.getElementById("logout").disabled = locked;
    document.getElementById("rename-conversation").disabled = locked;
    for (const button of document.querySelectorAll("#conversation-list button")) {
        button.disabled = locked;
    }
}

async function csrf() {
    const response = await fetch("/api/auth/csrf", { credentials: "same-origin" });
    if (!response.ok) throw new Error("Could not establish request protection.");
    csrfToken = (await response.json()).token;
}

function setAuthenticationMode(requiresBootstrap) {
    bootstrapRequired = requiresBootstrap;
    profile.textContent = bootstrapRequired ? "Create the local owner account." : "Sign in to your local assistant.";
    document.getElementById("auth-title").textContent = bootstrapRequired ? "Set up owner account" : "Owner sign-in";
    document.getElementById("bootstrap-token").hidden = !bootstrapRequired;
    document.getElementById("bootstrap-token-label").hidden = !bootstrapRequired;
    document.getElementById("passphrase").autocomplete = bootstrapRequired ? "new-password" : "current-password";
    document.getElementById("auth-submit").textContent = bootstrapRequired ? "Create owner account" : "Sign in";
}

async function api(path, options = {}) {
    const headers = new Headers(options.headers || {});
    if (options.method && !["GET", "HEAD"].includes(options.method.toUpperCase())) {
        headers.set("X-CSRF-TOKEN", csrfToken);
    }
    const response = await fetch(path, {
        ...options,
        headers,
        credentials: "same-origin"
    });
    if (response.status === 401 && path !== "/api/auth/login") {
        ++conversationSelectionGeneration;
        chatSection.hidden = true;
        authSection.hidden = true;
        await csrf();
        setAuthenticationMode(bootstrapRequired);
        authSection.hidden = false;
        const error = new Error("Please sign in again.");
        error.status = 401;
        throw error;
    }
    if (!response.ok) {
        const body = await response.json().catch(() => null);
        const error = new Error(body?.title || `Request failed (${response.status}).`);
        error.status = response.status;
        throw error;
    }
    const body = await response.text();
    return body.length === 0 ? null : JSON.parse(body);
}

function renderMessages(messages) {
    const list = document.getElementById("messages");
    list.replaceChildren();
    for (const message of messages) {
        list.append(createMessageItem(message.role, message.content));
    }
}

function createMessageItem(role, text) {
    const isAssistant = role === "assistant";
    const item = document.createElement("li");
    item.className = isAssistant ? "assistant-message" : "user-message";
    const label = document.createElement("strong");
    label.className = "message-label";
    label.textContent = isAssistant ? "Jarvis" : "You";
    const content = document.createElement("span");
    content.className = "message-content";
    content.textContent = text;
    item.append(label, content);
    return item;
}

async function loadConversations() {
    const listGeneration = ++conversationListGeneration;
    const conversations = await api("/api/conversations");
    if (listGeneration !== conversationListGeneration) return;
    const list = document.getElementById("conversation-list");
    list.replaceChildren();
    for (const item of conversations) {
        const entry = document.createElement("li");
        const button = document.createElement("button");
        button.type = "button";
        button.dataset.conversationId = item.id.value || item.id;
        button.textContent = item.title || "New conversation";
        if (button.dataset.conversationId === conversationId) button.setAttribute("aria-current", "page");
        button.disabled = activeTurnId !== "" || turnSubmissionInFlight || conversationCreationInFlight;
        button.addEventListener("click", async () => {
            const opening = openConversation(item.id.value || item.id);
            const selectionGeneration = conversationSelectionGeneration;
            try {
                await opening;
            } catch (error) {
                if (selectionGeneration === conversationSelectionGeneration) showError(error.message);
            }
        });
        const created = document.createElement("time");
        created.id = `conversation-created-${item.id.value || item.id}`;
        created.dateTime = item.createdAtUtc;
        created.textContent = formatConversationTime(item.createdAtUtc);
        button.setAttribute("aria-describedby", created.id);
        entry.append(button, created);
        list.append(entry);
    }
}

function formatConversationTime(value) {
    const instant = new Date(value);
    const zone = Intl.DateTimeFormat().resolvedOptions().timeZone;
    const local = instant.toLocaleString(undefined, {
        year: "numeric",
        month: "short",
        day: "numeric",
        hour: "numeric",
        minute: "2-digit",
        second: "2-digit"
    });
    const milliseconds = String(instant.getMilliseconds()).padStart(3, "0");
    const offsetMinutes = -instant.getTimezoneOffset();
    const offsetSign = offsetMinutes >= 0 ? "+" : "-";
    const offsetHours = String(Math.floor(Math.abs(offsetMinutes) / 60)).padStart(2, "0");
    const offsetRemainder = String(Math.abs(offsetMinutes) % 60).padStart(2, "0");
    return `${local}.${milliseconds} (${zone}, UTC${offsetSign}${offsetHours}:${offsetRemainder})`;
}

async function openConversation(id) {
    const targetId = id.value || id;
    const selectionGeneration = ++conversationSelectionGeneration;
    const renameGeneration = conversationRenameGeneration;
    if ((activeTurnId || turnSubmissionInFlight) && targetId !== activeTurnConversationId) {
        showError("Finish or cancel the active turn before switching conversations.");
        return;
    }
    const data = await api(`/api/conversations/${encodeURIComponent(targetId)}`);
    if (selectionGeneration !== conversationSelectionGeneration) return;
    if ((activeTurnId || turnSubmissionInFlight) && targetId !== activeTurnConversationId) {
        showError("Finish or cancel the active turn before switching conversations.");
        return;
    }
    conversationId = data.conversation.id.value || data.conversation.id;
    for (const button of document.querySelectorAll("#conversation-list button")) {
        if (button.dataset.conversationId === conversationId) button.setAttribute("aria-current", "page");
        else button.removeAttribute("aria-current");
    }
    conversationTitle = renameGeneration !== conversationRenameGeneration &&
        lastRenamedConversation?.id === targetId
        ? lastRenamedConversation.title
        : data.conversation.title || "";
    document.getElementById("conversation-title").textContent = conversationTitle || "New conversation";
    document.getElementById("conversation-created").textContent =
        `Created ${formatConversationTime(data.conversation.createdAtUtc)}`;
    document.getElementById("conversation-title-input").value = conversationTitle;
    document.getElementById("rename-form").hidden = true;
    renderMessages(data.messages);
    hideWorkspaceViews();
    document.getElementById("conversation").hidden = false;
}

async function createConversation() {
    if (activeTurnId || turnSubmissionInFlight || conversationCreationInFlight) return;
    clearError();
    conversationCreationInFlight = true;
    ++conversationSelectionGeneration;
    setTurnNavigationLocked(true);
    document.getElementById("send-turn").disabled = true;
    try {
        const created = await api("/api/conversations", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ title: "" })
        });
        await openConversation(created.id.value || created.id);
        await loadConversations();
    } finally {
        conversationCreationInFlight = false;
        const locked = activeTurnId !== "" || turnSubmissionInFlight;
        setTurnNavigationLocked(locked);
        document.getElementById("send-turn").disabled = locked || conversationRenameInFlight;
    }
}

function beginWorkspaceView(id) {
    const generation = ++conversationSelectionGeneration;
    hideWorkspaceViews();
    document.getElementById(id).hidden = false;
    clearError();
    return generation;
}

function isCurrentView(generation) {
    return generation === conversationSelectionGeneration;
}

function createActivityEntry(title, detail, className = "") {
    const entry = document.createElement("li");
    if (className) entry.className = className;
    const heading = document.createElement("strong");
    heading.textContent = title;
    entry.append(heading);
    if (detail) {
        const description = document.createElement("p");
        description.textContent = detail;
        entry.append(description);
    }
    return entry;
}

function setListMessage(list, message) {
    list.replaceChildren(createActivityEntry(message, ""));
}

async function openApprovals() {
    const generation = beginWorkspaceView("approvals");
    const status = document.getElementById("approvals-status");
    const list = document.getElementById("approval-list");
    status.textContent = "Loading owner approvals…";
    setListMessage(list, "Loading approvals…");
    try {
        const approvals = await api("/api/approvals");
        if (!isCurrentView(generation)) return;
        list.replaceChildren();
        const pending = approvals.filter(item =>
            item.approval.status === "Pending" && item.actionStatus === "AwaitingApproval");
        if (approvals.length === 0) {
            setListMessage(list, "No recent approval requests.");
        } else {
            for (const item of approvals) {
                const approval = item.approval;
                const expiresAt = new Date(approval.expiresAtUtc);
                const expired = Number.isNaN(expiresAt.getTime()) || expiresAt.getTime() <= Date.now();
                const isPending = approval.status === "Pending" && item.actionStatus === "AwaitingApproval";
                const details = [
                    `Tool: ${item.toolName}`,
                    `Operation: ${item.operation}`,
                    item.entityId ? `Entity: ${item.entityId}` : "",
                    `Action status: ${item.actionStatus}`,
                    `Approval status: ${approval.status}`,
                    `Expires: ${Number.isNaN(expiresAt.getTime()) ? approval.expiresAtUtc : expiresAt.toLocaleString()}`
                ].filter(Boolean).join(" · ");
                const entry = createActivityEntry(`${item.actionType} · ${item.actionId.value || item.actionId}`, details,
                    "approval-card");
                entry.dataset.status = item.actionStatus;
                const args = document.createElement("details");
                const summary = document.createElement("summary");
                summary.textContent = "Review exact action details";
                const pre = document.createElement("pre");
                pre.textContent = item.argumentsJson;
                args.append(summary, pre);
                entry.append(args);

                if (isPending && !expired && !activeTurnId && !turnSubmissionInFlight) {
                    const actions = document.createElement("div");
                    actions.className = "activity-actions";
                    for (const [label, approved] of [["Approve exact action", true], ["Reject", false]]) {
                        const button = document.createElement("button");
                        button.type = "button";
                        button.className = approved ? "button button-primary" : "button button-secondary";
                        button.textContent = label;
                        button.addEventListener("click", async () => {
                            button.disabled = true;
                            status.textContent = `${approved ? "Submitting approval" : "Rejecting"}…`;
                            try {
                                const result = await api(
                                    `/api/approvals/${encodeURIComponent(approval.id.value || approval.id)}/decision`,
                                    {
                                        method: "POST",
                                        headers: { "Content-Type": "application/json" },
                                        body: JSON.stringify({ expectedVersion: approval.expectedVersion, approved })
                                    });
                                if (!isCurrentView(generation)) return;
                                const outcome = result.status || result.Status || "Recorded";
                                status.textContent = approved
                                    ? `Approval decision recorded. Durable action outcome: ${outcome}. Check Activity before retrying.`
                                    : `Rejection recorded. Durable action outcome: ${outcome}.`;
                                await openApprovals();
                            } catch (error) {
                                if (!isCurrentView(generation)) return;
                                status.textContent = error.status === 409
                                    ? "This approval changed or expired. The list has been refreshed; review it again."
                                    : `Decision could not be confirmed: ${error.message}. Check Activity before retrying.`;
                                if (error.status === 409) await openApprovals();
                                else button.disabled = false;
                            }
                        });
                        actions.append(button);
                    }
                    entry.append(actions);
                } else if (isPending && expired) {
                    const expiry = document.createElement("p");
                    expiry.className = "approval-expiry";
                    expiry.textContent = "Expired or expiry could not be verified; this request cannot be approved from the UI.";
                    entry.append(expiry);
                } else if (isPending) {
                    const busy = document.createElement("p");
                    busy.className = "approval-expiry";
                    busy.textContent = "Finish the active assistant turn before deciding this request.";
                    entry.append(busy);
                }
                list.append(entry);
            }
        }
        status.textContent = `${pending.length} pending approval${pending.length === 1 ? "" : "s"}. Approval is limited to the exact displayed request.`;
    } catch (error) {
        if (!isCurrentView(generation)) return;
        status.textContent = `Approvals could not be loaded: ${error.message}`;
        setListMessage(list, "Approval data is unavailable. No action was taken.");
    }
}

async function openMemory() {
    const generation = beginWorkspaceView("memory");
    const status = document.getElementById("memory-status");
    status.textContent = "Loading active remembered facts…";
    await loadMemory("", generation);
}

async function loadMemory(query, generation = conversationSelectionGeneration) {
    const list = document.getElementById("memory-list");
    try {
        const encodedQuery = query ? `?query=${encodeURIComponent(query)}` : "";
        const facts = await api(`/api/memory${encodedQuery}`);
        if (!isCurrentView(generation)) return;
        list.replaceChildren();
        if (facts.length === 0) {
            setListMessage(list, query ? "No matching active facts." : "No active remembered facts.");
        }
        for (const fact of facts) {
            const entry = createActivityEntry(`${fact.subject} · ${fact.key}`, "", "memory-card");
            const value = document.createElement("p");
            value.className = "fact-value";
            value.textContent = fact.value;
            const metadata = document.createElement("p");
            metadata.className = "memory-metadata";
            metadata.textContent = [
                `ID ${fact.id.value || fact.id}`,
                `Version ${fact.version}`,
                fact.sourceType === "OwnerMessage" ? "Source: owner message" : "Source: imported/legacy",
                fact.sourceAvailable ? "Original source available" : "Original source unavailable",
                `Updated ${new Date(fact.updatedAtUtc).toLocaleString()}`
            ].join(" · ");
            entry.append(value, metadata);

            const edit = document.createElement("button");
            edit.type = "button";
            edit.className = "button button-secondary";
            edit.textContent = "Propose an edit";
            edit.addEventListener("click", () => {
                document.getElementById("memory-request").value =
                    `Please propose an update to memory fact ${fact.id.value || fact.id} (version ${fact.version}). ` +
                    `Keep its subject "${fact.subject}" and key "${fact.key}". I want its value to be: `;
                document.getElementById("memory-request").focus();
            });
            const remove = document.createElement("button");
            remove.type = "button";
            remove.className = "button button-secondary";
            remove.textContent = "Propose forgetting";
            remove.addEventListener("click", async () => {
                document.getElementById("memory-request").value =
                    `Please propose forgetting memory fact ${fact.id.value || fact.id} at version ${fact.version}.`;
                document.getElementById("memory-request").focus();
            });
            const actions = document.createElement("div");
            actions.className = "activity-actions";
            actions.append(edit, remove);
            entry.append(actions);
            list.append(entry);
        }
        document.getElementById("memory-status").textContent =
            `${facts.length} active fact${facts.length === 1 ? "" : "s"} shown. Changes require an exact owner approval.`;
    } catch (error) {
        if (!isCurrentView(generation)) return;
        document.getElementById("memory-status").textContent = `Memory could not be loaded: ${error.message}`;
        setListMessage(list, "Memory is unavailable. No facts were changed.");
    }
}

async function openHousehold() {
    const generation = beginWorkspaceView("household");
    const status = document.getElementById("household-status");
    const list = document.getElementById("household-list");
    status.textContent = "Loading allowlisted household resources…";
    setListMessage(list, "Loading household status…");
    try {
        const home = await api("/api/household");
        if (!isCurrentView(generation)) return;
        status.textContent = home.configured
            ? `Home Assistant is configured; connection probe: ${home.connectionProbe}. This does not confirm live connectivity.`
            : "Home Assistant is not configured; household actions are unavailable.";
        list.replaceChildren();
        if (!home.entities.length) setListMessage(list, "No allowlisted entities are available.");
        for (const entity of home.entities) {
            const operations = entity.operations.length ? entity.operations.join(", ") : "No operations enabled";
            list.append(createActivityEntry(
                `${entity.friendlyName || entity.entityId}`,
                `${entity.entityId}${entity.area ? ` · ${entity.area}` : ""}${entity.unit ? ` · ${entity.unit}` : ""} · Allowed operations: ${operations}`,
                "household-card"));
        }
    } catch (error) {
        if (!isCurrentView(generation)) return;
        status.textContent = `Household status could not be loaded: ${error.message}`;
        setListMessage(list, "No household action was attempted.");
    }
}

document.getElementById("approvals-button").addEventListener("click", openApprovals);
document.getElementById("memory-button").addEventListener("click", openMemory);
document.getElementById("household-button").addEventListener("click", openHousehold);

document.getElementById("memory-search-form").addEventListener("submit", async event => {
    event.preventDefault();
    const generation = conversationSelectionGeneration;
    document.getElementById("memory-status").textContent = "Searching active local facts…";
    await loadMemory(document.getElementById("memory-query").value.trim(), generation);
});

document.getElementById("memory-request-form").addEventListener("submit", async event => {
    event.preventDefault();
    if (activeTurnId || turnSubmissionInFlight || conversationCreationInFlight) {
        document.getElementById("memory-status").textContent =
            "Finish or cancel the active assistant turn before starting a memory proposal.";
        return;
    }
    const request = document.getElementById("memory-request").value.trim();
    if (!request) return;
    document.getElementById("memory-status").textContent = "Preparing a chat request; no memory is changed yet.";
    clearError();
    try {
        if (!conversationId) await createConversation();
        if (!conversationId) throw new Error("Create a conversation before proposing a memory change.");
        hideWorkspaceViews();
        document.getElementById("conversation").hidden = false;
        const prompt = document.getElementById("prompt");
        prompt.value = `Please propose this memory change for my review; do not save it without my approval: ${request}`;
        document.getElementById("memory-request").value = "";
        document.getElementById("turn-form").requestSubmit();
    } catch (error) {
        document.getElementById("memory-status").textContent = `Could not prepare the memory request: ${error.message}`;
    }
});

document.getElementById("rename-conversation").addEventListener("click", () => {
    if (!conversationId || activeTurnId || turnSubmissionInFlight) return;
    clearError();
    document.getElementById("conversation-title-input").value = conversationTitle;
    document.getElementById("rename-form").hidden = false;
    document.getElementById("conversation-title-input").focus();
});

document.getElementById("cancel-title").addEventListener("click", () => {
    document.getElementById("rename-form").hidden = true;
});

document.getElementById("rename-form").addEventListener("submit", async event => {
    event.preventDefault();
    if (!conversationId || activeTurnId || turnSubmissionInFlight || conversationRenameInFlight) return;
    const id = conversationId;
    const renameSelectionGeneration = ++conversationSelectionGeneration;
    let renameFeedbackGeneration = renameSelectionGeneration;
    const title = document.getElementById("conversation-title-input").value;
    conversationRenameInFlight = true;
    document.getElementById("send-turn").disabled = true;
    document.getElementById("save-title").disabled = true;
    clearError();
    try {
        const renamed = await api(`/api/conversations/${encodeURIComponent(id)}`, {
            method: "PATCH",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ title })
        });
        lastRenamedConversation = { id, title: renamed.title };
        ++conversationRenameGeneration;
        if (id === conversationId) {
            if (renameSelectionGeneration === conversationSelectionGeneration) {
                renameFeedbackGeneration = ++conversationSelectionGeneration;
            }
            conversationTitle = renamed.title;
            document.getElementById("conversation-title").textContent = conversationTitle;
            document.getElementById("rename-form").hidden = true;
        }
        await loadConversations();
    } catch (error) {
        if (id === conversationId && renameFeedbackGeneration === conversationSelectionGeneration) {
            showError(error.message);
        }
    } finally {
        conversationRenameInFlight = false;
        document.getElementById("save-title").disabled = false;
        document.getElementById("send-turn").disabled =
            activeTurnId !== "" || turnSubmissionInFlight || conversationCreationInFlight;
    }
});

async function refreshStatus() {
    const status = await api("/api/status");
    document.getElementById("route-status").textContent =
        `${status.route} route · ${status.provider}${status.model ? ` · ${status.model}` : ""} · ` +
        `${status.endpointConfigured ? "provider configured" : "provider not configured"} · ` +
        `${status.readiness ? "ready" : "not ready"} · model connectivity ${status.modelConnectivity}`;
}

async function showChat() {
    ++conversationSelectionGeneration;
    if (!pendingTurnSubmission) {
        let stored;
        try {
            stored = sessionStorage.getItem(pendingTurnStorageKey);
        } catch (error) {
            showError(`Cannot check this tab's request recovery state: ${error.message}`);
            return;
        }
        if (stored) {
            try {
                const candidate = JSON.parse(stored);
                const isIdentifier = value => typeof value === "string" &&
                    /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(value) &&
                    value !== "00000000-0000-0000-0000-000000000000";
                if (!candidate || !isIdentifier(candidate.conversationId) ||
                    typeof candidate.text !== "string" || candidate.text.trim().length === 0 ||
                    candidate.text.length > 8000 || !isIdentifier(candidate.requestId) ||
                    (candidate.turnId !== undefined && !isIdentifier(candidate.turnId))) {
                    throw new Error("The saved request has an invalid format.");
                }
                pendingTurnSubmission = candidate;
            } catch (error) {
                try {
                    sessionStorage.removeItem(pendingTurnStorageKey);
                } catch (storageError) {
                    showError(`Invalid request recovery state could not be cleared: ${storageError.message}`);
                    return;
                }
                showError(`Invalid request recovery state was cleared: ${error.message}`);
            }
        }
    }
    if (pendingTurnSubmission?.turnId) {
        activeTurnId = pendingTurnSubmission.turnId;
        activeTurnConversationId = pendingTurnSubmission.conversationId;
        conversationId = activeTurnConversationId;
        document.getElementById("conversation-title").textContent = "Recovering conversation";
        setTurnNavigationLocked(true);
        document.getElementById("send-turn").disabled = true;
        document.getElementById("cancel-turn").hidden = false;
    }
    authSection.hidden = true;
    chatSection.hidden = false;
    profile.textContent = "Checking your local assistant session.";
    hideWorkspaceViews();
    if (activeTurnId) {
        document.getElementById("conversation").hidden = false;
        let reconnect = true;
        try {
            await refreshStatus();
            profile.textContent = "Signed in to your local assistant.";
            await loadConversations();
            reconnect = await restoreConversation(activeTurnConversationId);
        } finally {
            if (reconnect && !chatSection.hidden && activeTurnId) connectEvents(activeTurnId, 0);
        }
        return;
    }
    await refreshStatus();
    profile.textContent = "Signed in to your local assistant.";
    await loadConversations();
    if (pendingTurnSubmission) {
        document.getElementById("prompt").value = pendingTurnSubmission.text;
        if (await restoreConversation(pendingTurnSubmission.conversationId)) {
            showError("A previous send may have been accepted. Resend the unchanged message to safely recover it.");
        }
    }
}

async function restoreConversation(id) {
    const recoveredSubmission = pendingTurnSubmission;
    const opening = openConversation(id);
    const recoveryGeneration = conversationSelectionGeneration;
    try {
        await opening;
        return true;
    } catch (error) {
        if (error.status !== 404) throw error;
        clearMissingRecovery(recoveredSubmission, recoveryGeneration);
        return false;
    }
}

function clearMissingRecovery(recoveredSubmission, recoveryGeneration) {
    if (pendingTurnSubmission !== recoveredSubmission) return;
    if (!clearPendingTurn()) return;
    if (recoveryGeneration !== conversationSelectionGeneration) return;
    eventSource?.close();
    eventSource = null;
    activeTurnId = "";
    activeTurnConversationId = "";
    conversationId = "";
    setTurnNavigationLocked(false);
    document.getElementById("send-turn").disabled = conversationRenameInFlight;
    document.getElementById("cancel-turn").hidden = true;
    document.getElementById("conversation").hidden = true;
    showError("The recovered conversation no longer exists. Start or select another conversation.");
}

async function start() {
    await csrf();
    const status = await api("/api/auth/status");
    if (typeof status?.bootstrapRequired !== "boolean") {
        throw new Error("The owner authentication status could not be read.");
    }
    setAuthenticationMode(status.bootstrapRequired);

    try {
        await showChat();
    } catch (error) {
        if (error.status !== 401) {
            showError(error.message);
            return;
        }
        authSection.hidden = false;
        chatSection.hidden = true;
    }
}

document.getElementById("auth-form").addEventListener("submit", async event => {
    event.preventDefault();
    if (authenticationInFlight) return;
    authenticationInFlight = true;
    document.getElementById("auth-submit").disabled = true;
    clearError();
    try {
        const bootstrap = !document.getElementById("bootstrap-token").hidden;
        const body = {
            passphrase: document.getElementById("passphrase").value,
            rememberMe: document.getElementById("remember-me").checked
        };
        if (bootstrap) body.bootstrapToken = document.getElementById("bootstrap-token").value;
        await api(bootstrap ? "/api/auth/bootstrap" : "/api/auth/login", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify(body)
        });
        document.getElementById("passphrase").value = "";
        document.getElementById("bootstrap-token").value = "";
        setAuthenticationMode(false);
        await csrf();
        await showChat();
    } catch (error) {
        showError(error.message);
    } finally {
        authenticationInFlight = false;
        document.getElementById("auth-submit").disabled = false;
    }
});

document.getElementById("new-conversation").addEventListener("click", async () => {
    try {
        await createConversation();
    } catch (error) {
        showError(error.message);
    }
});

document.getElementById("turn-form").addEventListener("submit", async event => {
    event.preventDefault();
    if (!conversationId || activeTurnId || turnSubmissionInFlight || conversationCreationInFlight || conversationRenameInFlight) return;
    clearError();
    const submittedConversationId = conversationId;
    const text = document.getElementById("prompt").value;
    if (pendingTurnSubmission &&
        (pendingTurnSubmission.conversationId !== submittedConversationId || pendingTurnSubmission.text !== text)) {
        showError("Retry the previous request unchanged before sending a different message.");
        return;
    }
    pendingTurnSubmission ??= {
        conversationId: submittedConversationId,
        text,
        requestId: crypto.randomUUID()
    };
    try {
        sessionStorage.setItem(pendingTurnStorageKey, JSON.stringify(pendingTurnSubmission));
    } catch (error) {
        pendingTurnSubmission = null;
        showError(`Cannot safely preserve this request for retry: ${error.message}`);
        return;
    }
    const requestId = pendingTurnSubmission.requestId;
    const submissionSelectionGeneration = ++conversationSelectionGeneration;
    turnSubmissionInFlight = true;
    activeTurnConversationId = submittedConversationId;
    document.getElementById("stream-status").textContent = "Sending your message.";
    setTurnNavigationLocked(true);
    document.getElementById("send-turn").disabled = true;
    let result;
    try {
        result = await api(`/api/conversations/${encodeURIComponent(submittedConversationId)}/turns`, {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ requestId, text })
        });
    } catch (error) {
        turnSubmissionInFlight = false;
        activeTurnConversationId = "";
        document.getElementById("stream-status").textContent = "Message could not be sent.";
        setTurnNavigationLocked(false);
        document.getElementById("send-turn").disabled = false;
        if (error.status >= 400 && error.status < 500 && ![408, 429].includes(error.status)) {
            if (!clearPendingTurn()) return;
        }
        showError(error.message);
        return;
    }
    activeTurnId = result.turnId.value || result.turnId;
    const prompt = document.getElementById("prompt");
    if (prompt.value === text) prompt.value = "";
    document.getElementById("stream-status").textContent = "Jarvis is responding.";
    pendingTurnSubmission.turnId = activeTurnId;
    try {
        sessionStorage.setItem(pendingTurnStorageKey, JSON.stringify(pendingTurnSubmission));
    } catch (error) {
        showError(`Cannot preserve the accepted turn for reload recovery: ${error.message}`);
    }
    turnSubmissionInFlight = false;
    document.getElementById("cancel-turn").hidden = false;
    if (conversationId === submittedConversationId &&
        conversationSelectionGeneration === submissionSelectionGeneration &&
        (result.conversation?.id?.value || result.conversation?.id) === submittedConversationId) {
        conversationTitle = result.conversation.title || "";
        document.getElementById("conversation-title").textContent = conversationTitle || "New conversation";
        document.getElementById("conversation-created").textContent =
            `Created ${formatConversationTime(result.conversation.createdAtUtc)}`;
    }
    try {
        await loadConversations();
        renderMessages([...(await api(`/api/conversations/${encodeURIComponent(submittedConversationId)}`)).messages]);
    } catch (error) {
        showError(error.message);
    }
    connectEvents(activeTurnId, 0);
});

function connectEvents(turnId, lastSequence) {
    eventSource?.close();
    const source = new EventSource(`/api/turns/${encodeURIComponent(turnId)}/events`, { withCredentials: true });
    eventSource = source;
    let streamedReply = null;
    let completedReply = "";
    let terminalReceived = false;
    document.getElementById("response-announcement").textContent = "";
    let recoveryError = "";
    const handleEvent = async event => {
        if (eventSource !== source || activeTurnId !== turnId || terminalReceived) return;
        if (recoveryError && errorBox.textContent === recoveryError) clearError();
        recoveryError = "";
        lastSequence = Number(event.lastEventId || lastSequence);
        const payload = JSON.parse(event.data);
        if (event.type === "TextDelta" && typeof (payload.text || payload.Text) === "string") {
            const text = payload.text || payload.Text;
            completedReply += text;
            const list = document.getElementById("messages");
            if (!streamedReply) {
                streamedReply = createMessageItem("assistant", "");
                list.append(streamedReply);
            }
            streamedReply.querySelector(".message-content").textContent += text;
        } else if (event.type === "AssistantMessage" && typeof (payload.text || payload.Text) === "string") {
            completedReply = payload.text || payload.Text;
            const list = document.getElementById("messages");
            list.append(createMessageItem("assistant", payload.text || payload.Text));
        }
        if (["TurnCompleted", "TurnFailed", "TurnCancelled", "TurnInterrupted"].includes(event.type)) {
            terminalReceived = true;
            if (event.type === "TurnCompleted" && completedReply) {
                document.getElementById("response-announcement").textContent = `Jarvis: ${completedReply}`;
            }
            document.getElementById("stream-status").textContent =
                event.type === "TurnCompleted" ? "Response complete." : "Response ended.";
            if (event.type === "TurnFailed") {
                showError("The assistant could not complete this request. Your conversation is saved; you can try again.");
            } else if (event.type === "TurnInterrupted") {
                showError("This request was interrupted before completion. Your conversation is saved; you can try again.");
            }
            source.close();
            document.getElementById("cancel-turn").hidden = true;
            try {
                await loadConversations();
                await openConversation(activeTurnConversationId);
            } catch (error) {
                showError(error.message);
            } finally {
                if (clearPendingTurn()) {
                    activeTurnId = "";
                    activeTurnConversationId = "";
                    setTurnNavigationLocked(false);
                    document.getElementById("send-turn").disabled = false;
                }
            }
        }
    };
    for (const type of ["TextDelta", "AssistantMessage", "TurnCompleted", "TurnFailed", "TurnCancelled", "TurnInterrupted"]) {
        source.addEventListener(type, handleEvent);
    }
    let recoveryCheckInFlight = false;
    source.onerror = async () => {
        if (recoveryCheckInFlight || eventSource !== source || activeTurnId !== turnId) return;
        recoveryCheckInFlight = true;
        const recoveredSubmission = pendingTurnSubmission;
        const recoveryGeneration = conversationSelectionGeneration;
        try {
            await api(`/api/conversations/${encodeURIComponent(activeTurnConversationId)}`);
            if (eventSource === source && activeTurnId === turnId &&
                recoveryGeneration === conversationSelectionGeneration && source.readyState === EventSource.CLOSED) {
                recoveryError = "The stream connection closed. Reload this page to resume the saved request.";
                showError(recoveryError);
            }
        } catch (error) {
            if (eventSource !== source || activeTurnId !== turnId ||
                recoveryGeneration !== conversationSelectionGeneration) return;
            if (error.status === 404) {
                source.close();
                clearMissingRecovery(recoveredSubmission, recoveryGeneration);
            } else {
                const resumeGuidance = source.readyState === EventSource.CLOSED
                    ? " Reload this page to resume the saved request." : "";
                recoveryError = `Stream recovery could not be checked: ${error.message}${resumeGuidance}`;
                showError(recoveryError);
            }
        } finally {
            recoveryCheckInFlight = false;
        }
        // Connecting sources retain automatic cursor reconnect; closed sources require reload.
    };
}

document.getElementById("cancel-turn").addEventListener("click", async () => {
    if (!activeTurnId) return;
    clearError();
    try {
        await api(`/api/turns/${encodeURIComponent(activeTurnId)}/cancel`, { method: "POST" });
    } catch (error) {
        showError(error.message);
    }
});

document.getElementById("prompt").addEventListener("keydown", event => {
    if (event.key !== "Enter" || event.shiftKey || event.altKey || event.ctrlKey || event.metaKey ||
        event.isComposing || event.keyCode === 229) return;
    event.preventDefault();
    document.getElementById("turn-form").requestSubmit();
});

async function openSettings() {
    if (activeTurnId || turnSubmissionInFlight || conversationCreationInFlight) return;
    const selectionGeneration = beginWorkspaceView("settings");
    try {
        const settings = await api("/api/settings");
        if (!isCurrentView(selectionGeneration)) return;
        document.getElementById("conversation-days").value = settings.conversationDays;
        document.getElementById("audit-days").value = settings.auditDays;
        document.getElementById("settings-status").textContent = "Local profile settings. Provider keys are not displayed.";
    } catch (error) {
        if (isCurrentView(selectionGeneration)) {
            document.getElementById("settings-status").textContent = `Settings could not be loaded: ${error.message}`;
        }
    }
}

document.getElementById("settings-button").addEventListener("click", openSettings);

async function saveRetention(event) {
    event.preventDefault();
    const selectionGeneration = conversationSelectionGeneration;
    clearError();
    try {
        const settings = await api("/api/settings/retention", {
            method: "PUT",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({
                conversationDays: Number(document.getElementById("conversation-days").value),
                auditDays: Number(document.getElementById("audit-days").value)
            })
        });
        if (selectionGeneration !== conversationSelectionGeneration) return;
        document.getElementById("settings-status").textContent =
            `Saved: conversations ${settings.conversationDays} days; audit ${settings.auditDays} days.`;
    } catch (error) {
        if (selectionGeneration === conversationSelectionGeneration) showError(error.message);
    }
}

document.getElementById("retention-form").addEventListener("submit", saveRetention);

async function openActivity() {
    if (activeTurnId || turnSubmissionInFlight || conversationCreationInFlight) return;
    const selectionGeneration = beginWorkspaceView("activity");
    const lists = {
        actions: document.getElementById("action-list"),
        audit: document.getElementById("audit-list"),
        turns: document.getElementById("activity-list")
    };
    for (const [key, list] of Object.entries(lists)) setListMessage(list, `Loading ${key}…`);
    const responses = await Promise.allSettled([
        api("/api/actions"),
        api("/api/audit"),
        api("/api/activity")
    ]);
    if (!isCurrentView(selectionGeneration)) return;
    const renderFailure = (list, result, label) => {
        if (result.status === "rejected") {
            setListMessage(list, `${label} could not be loaded: ${result.reason.message}`);
            return true;
        }
        return false;
    };
    let failed = false;

    if (!renderFailure(lists.actions, responses[0], "Action outcomes")) {
        lists.actions.replaceChildren();
        if (responses[0].value.length === 0) setListMessage(lists.actions, "No recent action outcomes.");
        for (const item of responses[0].value) {
            const title = `${item.actionType} · ${item.status}`;
            const detail = [
                item.reasonCode ? `Reason: ${item.reasonCode}` : "",
                `Updated ${new Date(item.updatedAtUtc).toLocaleString()}`,
                item.memoryInvalidated ? "Sensitive action content invalidated" : ""
            ].filter(Boolean).join(" · ");
            lists.actions.append(createActivityEntry(title, detail));
        }
    } else failed = true;

    if (!renderFailure(lists.audit, responses[1], "Audit history")) {
        lists.audit.replaceChildren();
        const entries = responses[1].value.filter(item =>
            item.eventType.startsWith("approval.") || item.eventType.startsWith("action."));
        if (entries.length === 0) setListMessage(lists.audit, "No recent approval or action decisions.");
        for (const item of entries) {
            lists.audit.append(createActivityEntry(
                item.eventType,
                `${item.subjectId || "Local event"} · ${new Date(item.occurredAtUtc).toLocaleString()}`));
        }
    } else failed = true;

    if (!renderFailure(lists.turns, responses[2], "Recent turns")) {
        lists.turns.replaceChildren();
        if (responses[2].value.length === 0) setListMessage(lists.turns, "No recent turns.");
        for (const item of responses[2].value) {
            lists.turns.append(createActivityEntry(
                `Local turn ${item.turnId.value || item.turnId} · ${item.status}`,
                `Updated ${new Date(item.updatedAtUtc).toLocaleString()}`));
        }
    }
    failed ||= responses[2].status === "rejected";
    document.getElementById("activity-status").textContent =
        failed ? "Some activity sources could not be loaded. Failed entries are identified above." :
            "Recent local action outcomes, decisions, and turns. Prompt and provider payloads are not shown.";
}

document.getElementById("activity-button").addEventListener("click", openActivity);

document.getElementById("logout").addEventListener("click", async () => {
    try {
        await api("/api/auth/logout", { method: "POST" });
        window.location.reload();
    } catch (error) {
        showError(error.message);
    }
});

start().catch(error => showError(error.message));
