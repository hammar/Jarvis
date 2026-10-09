const authSection = document.getElementById("auth");
const chatSection = document.getElementById("chat");
const errorBox = document.getElementById("error");
const profile = document.getElementById("profile");
const pendingTurnStorageKey = "jarvis.pending-turn.v1";
let csrfToken = "";
let conversationId = "";
let activeTurnId = "";
let activeTurnConversationId = "";
let turnSubmissionInFlight = false;
let pendingTurnSubmission = null;
let conversationSelectionGeneration = 0;
let eventSource = null;

function showError(message) {
    errorBox.textContent = message;
    errorBox.hidden = false;
}

function clearError() {
    errorBox.textContent = "";
    errorBox.hidden = true;
}

function clearPendingTurn() {
    pendingTurnSubmission = null;
    try {
        sessionStorage.removeItem(pendingTurnStorageKey);
    } catch (error) {
        showError(`The completed request could not be cleared from this tab's recovery state: ${error.message}`);
    }
}

function setTurnNavigationLocked(locked) {
    document.getElementById("new-conversation").disabled = locked;
    document.getElementById("settings-button").disabled = locked;
    document.getElementById("activity-button").disabled = locked;
    document.getElementById("logout").disabled = locked;
    for (const button of document.querySelectorAll("#conversation-list button")) {
        button.disabled = locked;
    }
}

async function csrf() {
    const response = await fetch("/api/auth/csrf", { credentials: "same-origin" });
    if (!response.ok) throw new Error("Could not establish request protection.");
    csrfToken = (await response.json()).token;
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
    if (response.status === 401) {
        authSection.hidden = false;
        chatSection.hidden = true;
        throw new Error("Please sign in again.");
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
        const item = document.createElement("li");
        const label = document.createElement("strong");
        label.textContent = message.role === "assistant" ? "Jarvis: " : "You: ";
        const content = document.createElement("span");
        content.textContent = message.content;
        item.append(label, content);
        list.append(item);
    }
}

async function loadConversations() {
    const conversations = await api("/api/conversations");
    const list = document.getElementById("conversation-list");
    list.replaceChildren();
    for (const item of conversations) {
        const entry = document.createElement("li");
        const button = document.createElement("button");
        button.type = "button";
        button.textContent = item.title || "New conversation";
        button.disabled = activeTurnId !== "" || turnSubmissionInFlight;
        button.addEventListener("click", () => openConversation(item.id.value || item.id));
        entry.append(button);
        list.append(entry);
    }
}

async function openConversation(id) {
    const targetId = id.value || id;
    const selectionGeneration = ++conversationSelectionGeneration;
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
    document.getElementById("conversation-title").textContent = data.conversation.title || "Conversation";
    renderMessages(data.messages);
    document.getElementById("conversation").hidden = false;
}

async function refreshStatus() {
    const status = await api("/api/status");
    document.getElementById("route-status").textContent =
        `${status.route} route · ${status.provider}${status.model ? ` · ${status.model}` : ""} · ` +
        `${status.endpointConfigured ? "provider configured" : "provider not configured"} · ` +
        `${status.readiness ? "ready" : "not ready"} · model connectivity ${status.modelConnectivity}`;
}

async function showChat() {
    authSection.hidden = true;
    chatSection.hidden = false;
    document.getElementById("settings").hidden = true;
    document.getElementById("activity").hidden = true;
    await refreshStatus();
    await loadConversations();
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
                if (typeof candidate.conversationId !== "string" ||
                    typeof candidate.text !== "string" ||
                    typeof candidate.requestId !== "string") {
                    throw new Error("The saved request has an invalid format.");
                }
                pendingTurnSubmission = candidate;
            } catch (error) {
                showError(`A previous request could not be restored: ${error.message}`);
                return;
            }
        }
    }
    if (pendingTurnSubmission) {
        document.getElementById("prompt").value = pendingTurnSubmission.text;
        await openConversation(pendingTurnSubmission.conversationId);
        showError("A previous send may have been accepted. Resend the unchanged message to safely recover it.");
    }
}

async function start() {
    await csrf();
    const response = await fetch("/api/auth/status", { credentials: "same-origin" });
    const status = await response.json();
    profile.textContent = status.bootstrapRequired ? "Create the local owner account." : "Sign in to your local assistant.";

    try {
        await refreshStatus();
        await showChat();
    } catch {
        authSection.hidden = false;
        chatSection.hidden = true;
        document.getElementById("auth-title").textContent = status.bootstrapRequired ? "Set up owner account" : "Owner sign-in";
        document.getElementById("bootstrap-token").hidden = !status.bootstrapRequired;
        document.getElementById("bootstrap-token-label").hidden = !status.bootstrapRequired;
        document.getElementById("passphrase").autocomplete = status.bootstrapRequired ? "new-password" : "current-password";
        document.getElementById("auth-submit").textContent = status.bootstrapRequired ? "Create owner account" : "Sign in";
    }
}

document.getElementById("auth-form").addEventListener("submit", async event => {
    event.preventDefault();
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
        await csrf();
        await showChat();
    } catch (error) {
        showError(error.message);
    }
});

document.getElementById("new-conversation").addEventListener("click", async () => {
    if (activeTurnId || turnSubmissionInFlight) return;
    clearError();
    try {
        const created = await api("/api/conversations", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ title: "" })
        });
        await openConversation(created.id.value || created.id);
        await loadConversations();
    } catch (error) {
        showError(error.message);
    }
});

document.getElementById("turn-form").addEventListener("submit", async event => {
    event.preventDefault();
    if (!conversationId || activeTurnId || turnSubmissionInFlight) return;
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
    conversationSelectionGeneration++;
    turnSubmissionInFlight = true;
    activeTurnConversationId = submittedConversationId;
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
        setTurnNavigationLocked(false);
        document.getElementById("send-turn").disabled = false;
        if (error.status >= 400 && error.status < 500 && ![408, 409, 429].includes(error.status)) {
            clearPendingTurn();
        }
        showError(error.message);
        return;
    }
    clearPendingTurn();
    activeTurnId = result.turnId.value || result.turnId;
    turnSubmissionInFlight = false;
    document.getElementById("cancel-turn").hidden = false;
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
    const handleEvent = async event => {
        lastSequence = Number(event.lastEventId || lastSequence);
        const payload = JSON.parse(event.data);
        if (event.type === "TextDelta" && typeof (payload.text || payload.Text) === "string") {
            const text = payload.text || payload.Text;
            const list = document.getElementById("messages");
            if (!streamedReply) {
                streamedReply = document.createElement("li");
                streamedReply.textContent = "Jarvis: ";
                list.append(streamedReply);
            }
            streamedReply.textContent += text;
        } else if (event.type === "AssistantMessage" && typeof (payload.text || payload.Text) === "string") {
            const list = document.getElementById("messages");
            const item = document.createElement("li");
            item.textContent = `Jarvis: ${payload.text || payload.Text}`;
            list.append(item);
        }
        if (["TurnCompleted", "TurnFailed", "TurnCancelled", "TurnInterrupted"].includes(event.type)) {
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
                activeTurnId = "";
                activeTurnConversationId = "";
                setTurnNavigationLocked(false);
                document.getElementById("send-turn").disabled = false;
            }
        }
    };
    for (const type of ["TextDelta", "AssistantMessage", "TurnCompleted", "TurnFailed", "TurnCancelled", "TurnInterrupted"]) {
        source.addEventListener(type, handleEvent);
    }
    source.onerror = () => {
        // EventSource reconnects automatically and resends its last event ID.
    };
}

document.getElementById("cancel-turn").addEventListener("click", async () => {
    if (!activeTurnId) return;
    try {
        await api(`/api/turns/${encodeURIComponent(activeTurnId)}/cancel`, { method: "POST" });
    } catch (error) {
        showError(error.message);
    }
});

document.getElementById("settings-button").addEventListener("click", async () => {
    if (activeTurnId || turnSubmissionInFlight) return;
    clearError();
    try {
        const settings = await api("/api/settings");
        if (activeTurnId || turnSubmissionInFlight) return;
        document.getElementById("conversation-days").value = settings.conversationDays;
        document.getElementById("audit-days").value = settings.auditDays;
        document.getElementById("settings-status").textContent = "Local profile settings. Provider keys are not displayed.";
        document.getElementById("settings").hidden = false;
        document.getElementById("activity").hidden = true;
        document.getElementById("conversation").hidden = true;
    } catch (error) {
        showError(error.message);
    }
});

document.getElementById("retention-form").addEventListener("submit", async event => {
    event.preventDefault();
    try {
        const settings = await api("/api/settings/retention", {
            method: "PUT",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({
                conversationDays: Number(document.getElementById("conversation-days").value),
                auditDays: Number(document.getElementById("audit-days").value)
            })
        });
        document.getElementById("settings-status").textContent =
            `Saved: conversations ${settings.conversationDays} days; audit ${settings.auditDays} days.`;
    } catch (error) {
        showError(error.message);
    }
});

document.getElementById("activity-button").addEventListener("click", async () => {
    if (activeTurnId || turnSubmissionInFlight) return;
    clearError();
    try {
        const turns = await api("/api/activity");
        if (activeTurnId || turnSubmissionInFlight) return;
        const list = document.getElementById("activity-list");
        list.replaceChildren();
        for (const item of turns) {
            const entry = document.createElement("li");
            entry.textContent = `Local turn ${item.turnId.value || item.turnId} · ${item.status} · updated ${new Date(item.updatedAtUtc).toLocaleString()}`;
            list.append(entry);
        }
        document.getElementById("activity").hidden = false;
        document.getElementById("settings").hidden = true;
        document.getElementById("conversation").hidden = true;
    } catch (error) {
        showError(error.message);
    }
});

document.getElementById("logout").addEventListener("click", async () => {
    try {
        await api("/api/auth/logout", { method: "POST" });
        window.location.reload();
    } catch (error) {
        showError(error.message);
    }
});

start().catch(error => showError(error.message));
