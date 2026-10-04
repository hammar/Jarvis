# GitHub Copilot SDK validation (T01 / M0)

## Decision

**No-go for dependent implementation on the current native-runtime isolation assumptions.** The previous conditional-go recommendation is superseded by the broader network experiment below. Local Ollama and live Microsoft Foundry tool calls work, and hostile-tool denial, bounded cancellation, crash/restart, and host budgets now have reproducible actual-runtime checks. However, the strict zero-external-attempt network diagnostic failed: the traced process tree attempted non-loopback DNS connections. Docker network isolation blocked them, but this is not proof of equivalent containment for the selected native macOS runtime.

Issue #2 remains open pending disposition of this isolation blocker. Do not treat a passing provider proxy test, SDK telemetry setting, or an unrelated failure response as proof of no native-runtime egress. A narrowly scoped alternative is to keep the Copilot SDK behind the same application-owned adapter but run its disposable child in an OS-isolated network namespace, with an application-owned, destination-allowlisted Unix-socket gateway for approved inference. That gateway must be tested with real native Ollama before claiming the complete local route works under containment; it is not implemented here. No alternative agent framework is proposed.

## Pinned versions and sources

| Component | Pinned version | Evidence |
|---|---|---|
| .NET SDK | `10.0.100`, roll-forward disabled | `global.json` |
| `GitHub.Copilot.SDK` | exactly `1.0.16` | `tools/sdk-validation/SdkValidation.csproj`, `packages.lock.json` |
| Bundled Copilot CLI runtime | `1.0.90` | SDK `v1.0.16` release notes and tagged package source |

The SDK targets .NET 10. The package lock records its transitive dependencies. The SDK package/runtime source of truth is the [v1.0.16 release](https://github.com/github/copilot-sdk/releases/tag/v1.0.16), the [tagged .NET SDK documentation](https://github.com/github/copilot-sdk/tree/v1.0.16/dotnet), and the [NuGet package metadata](https://www.nuget.org/packages/GitHub.Copilot.SDK/1.0.16). The tagged source and release were checked for this spike; do not rely on mutable `main` documentation when changing the pin.

Sources and SDK compatibility reviewed on 2026-10-04.

## M0 evidence

| M0 item | Result | Evidence and remaining limits |
|---|---|---|
| 1. Local BYOK with Ollama and no GitHub login/ambient credentials | **PASS, live smoke** | `--live-ollama` completed against native Ollama `llama3.2:latest` using an explicit provider and no logged-in-user auth. Runtime child environment is replaced by a small allowlist; the test injects fake GitHub-token sentinels only into the parent and does not observe them at the model endpoint. |
| 2. Host-owned read-only tool call/result | **PASS, contract and live smoke** | The real runtime invoked the registered `read_only_lookup` exactly once and returned its result. The tool has no external side effects. |
| 3. Explicit cloud BYOK | **PASS, live Foundry smoke** | Owner-authorized Foundry Responses API inference invoked exactly one fixed host tool and returned `live-fixture:m0`. The deployment was `gpt-5.6-terra`; the saved API key was loaded locally without printing it or response content. No live cloud credentials are required in CI. |
| 4. Deny unapproved built-ins and subagents | **PASS for tested catalog/denial paths** | Real runtime requests for `unregistered_lookup`, `bash`, `view`, `web_fetch`, `task`, and `install_extension` returned explicit `Tool '<name>' does not exist` results to the provider, without invoking the approved callback or widening the catalog. A separate permission-gated registered-tool contract proves the rejecting permission callback runs once and prevents execution. This tests the configured empty-mode surface, not every SDK feature under permissive configurations. |
| 5. Ignore hostile ambient instructions/configuration | **PASS, bounded contract** | The fixture creates hostile workspace/user instructions, a skill, agent, MCP configs, and credential sentinels. The instrumented model endpoint did not observe the hostile instruction marker or credential sentinel, and only the explicit custom tool was advertised. This is not a general audit of every CLI discovery path. |
| 6. Transcript isolation and no implicit provider fallback | **PARTIAL; containment mitigation tested** | Two explicit fixture sessions did not cross transcript markers. An inference HTTP 400 with a controlled error propagated to the caller instead of a successful fallback response. All synthetic contracts also completed with Docker external networking disabled. This proves the exercised isolated route cannot reach a hosted model, not that every native-runtime failure/discovery path lacks fallback. |
| 7. Streaming, completion, cancellation, timeout, crash, restart, disposal | **PASS with required host deadline** | Existing streaming/abort checks pass. A new test kills only this harness's newly launched runtime child while a tool is blocked; the callback cancels, the pending turn ends at its 10-second deadline, bounded disposal succeeds, and runtime restart/ping succeeds without a second inference request. The SDK does **not** promptly fail the pending wait on process death. The coordinator must enforce its own deadline and classify that result as interrupted/failed; it must not claim an idle event was received on crash. |
| 8. Dedicated runtime state, sanitized environment, telemetry and egress | **FAIL strict zero-attempt diagnostic; containment PASS** | Local Ollama inference was captured through the loopback proxy. Linux ARM64 Docker `--network none` plus `strace -f -e trace=connect` observed only loopback success and blocked external DNS connects to `192.168.65.7:53` (`ENETUNREACH`). These traces cover the harness and its descendants; attribution of every DNS attempt to SDK versus host-library behavior is unresolved. No native macOS full-egress claim is made. |
| 9. Enforceable budgets and context limits | **PASS host-mitigation demonstration; token semantics limited** | The actual runtime's repeated tool requests are capped at one host execution, with explicit `AbortAsync` and exactly one idle event on budget exhaustion. A host prompt boundary accepts 64 UTF-16 code units and rejects 65; it is not a token budget. The tagged provider API exposes `MaxPromptTokens` (compaction threshold) and `MaxOutputTokens` (truncating generation limit); neither proves an overall per-turn cost/tool budget. Complete production context/history bounding and compaction semantics must not be assumed from this small prompt test. |

The commands observed passing on the implementation host were:

```sh
dotnet build tools/sdk-validation/SdkValidation.csproj --no-restore -c Release
dotnet run --project tools/sdk-validation/SdkValidation.csproj -c Release --no-restore -- --contracts
JARVIS_OLLAMA_MODEL=llama3.2:latest dotnet run --no-build --project tools/sdk-validation/SdkValidation.csproj -c Release -- --live-ollama
```

The contract command exercised the actual spawned Copilot runtime against controlled loopback providers; it is not a fake SDK test. The Ollama command exercised actual local inference through the instrumented proxy. The owner-authorized cloud command subsequently passed against Foundry's Responses API. CI's `sdk-contracts` job is configured for Linux and macOS; these workflow runs have not been claimed as executed on GitHub.

This cohesive standalone slice exceeds the approximately 400-line review target:
the actual-runtime provider fixture, live modes, process lifecycle tests, and
network instrumentation are kept together rather than splitting feasibility
evidence across dependent production features. Independent code review found
two defects: automatic redirects in the local proxy and a hardcoded fixture
answer that did not verify tool-result forwarding. Both were fixed. The proxy
now rejects redirects with HTTP 502, with a 307 regression contract; the normal
tool contract verifies a fresh marker in the correlated tool-result message and
constructs its answer from that validated result.

## Network experiment and recorded outcome

```sh
docker build -f tools/sdk-validation/Dockerfile -t jarvis-sdk-egress:t01 .
docker run --rm --network none --cap-add SYS_PTRACE jarvis-sdk-egress:t01
# FAIL: non-loopback connection attempts (blocked DNS) observed.
docker run --rm --network none --cap-add SYS_PTRACE \
  --entrypoint /bin/bash jarvis-sdk-egress:t01 /validation/check-containment.sh
# PASS: all synthetic runtime contracts complete with external connections blocked.
```

The strict diagnostic remains strict and was not weakened to hide the DNS
attempts. The separate containment check verifies no non-loopback interface is
up and requires each observed external connect to return `ENETUNREACH`.
Inactive kernel tunnel interfaces are not evidence of an external network route.
The successful run observed 28 blocked DNS connects; this count is observational,
not a stable acceptance threshold. A representative redacted destination-only
trace line is:

```text
connect(fd, {sa_family=AF_INET, sin_port=htons(53),
            sin_addr=inet_addr("192.168.65.7")}, 16) = -1 ENETUNREACH
```

The image uses a pinned .NET base-image digest. Package and tracing-tool downloads
happen during image construction, separately from the traced runtime execution.
The image copies only the harness and never the evaluation credential file;
`.dockerignore` also excludes `.env*`, Git data, and local build outputs.
These tests are not proof of native macOS containment or a working isolated
gateway to external native Ollama. They do not capture request payloads or keys.

The [tagged SDK client implementation](https://github.com/github/copilot-sdk/blob/v1.0.16/dotnet/src/Client.cs)
defaults empty-mode session telemetry off, skips embedding retrieval, and disables
session store, skills, memory, file hooks, and host Git operations. It always
launches the child with `--no-auto-update`; `UseLoggedInUser=false` adds
`--no-auto-login`. OTLP export is enabled when a `Telemetry` configuration is
supplied; this harness supplies none and clears inherited OTEL variables by
replacing the child environment. These source settings are not a guarantee that
the entire native host/runtime process tree makes no other network attempts.

## Runtime configuration findings

- `CopilotClientMode.Empty` is the selected baseline. Pass an explicit runtime base directory, working directory, child environment, provider, and `SessionConfig.AvailableTools`; do not depend on ambient defaults.
- `CopilotClientOptions.Environment` replaces the inherited child environment rather than extending it. Retain only required OS home/temp/config/cache paths and keep tokens out of this map.
- Set `UseLoggedInUser = false` and use explicit per-session `ProviderConfig`. Do not infer provider choice from login or machine defaults.
- Register only host-owned tools and validate authorization in host code. A model-visible tool list is not an authorization boundary.
- A send timeout bounds the caller's wait; it does not itself cancel the runtime/tool callback. Pair it with `AbortAsync`, and retain a tested process-stop/restart path for an unresponsive runtime.
- Child death cancels the cooperative host callback but did not promptly complete `SendAndWaitAsync`; retain the host deadline even when watching process liveness.
- Runtime sessions are replaceable engine state; conversation, memory, approval, job, and audit authority must remain application-owned.

## Reproduction

Credential-free contract, after installing .NET SDK `10.0.100`:

```sh
dotnet restore tools/sdk-validation/SdkValidation.csproj --locked-mode
dotnet build tools/sdk-validation/SdkValidation.csproj -c Release --no-restore -warnaserror -p:TreatWarningsAsErrors=true
dotnet run --project tools/sdk-validation/SdkValidation.csproj -c Release --no-build --no-restore -- --contracts
```

Opt-in native Ollama smoke:

```sh
JARVIS_OLLAMA_MODEL=llama3.2:latest \
  dotnet run --project tools/sdk-validation/SdkValidation.csproj -c Release -- --live-ollama
```

Set `JARVIS_OLLAMA_BASE_URL` only when Ollama listens on a different local loopback URL. The instrumentation proxy rejects non-loopback HTTP endpoints; it does not support remote Ollama endpoints.

Opt-in cloud smoke:

```sh
JARVIS_CLOUD_BASE_URL=https://<approved-endpoint> \
JARVIS_CLOUD_MODEL=<model> \
JARVIS_CLOUD_API_KEY=<owner-supplied-secret> \
  dotnet run --project tools/sdk-validation/SdkValidation.csproj -c Release -- --live-cloud
```

Supply the key out of band; do not put it in command history, repository files, CI, or logs. The smoke sends a fixed test prompt and the tool returns a fixed fixture value. This is not a privacy/consent workflow or a production routing test.

The cloud mode reads `.env.cloud-evaluation` from the current working directory.
Only the four `JARVIS_CLOUD_*` settings above and below are allowed. Existing
environment variables take precedence. Values can be unquoted, single-quoted,
or double-quoted; quotes are removed without variable expansion or shell
execution. Duplicate keys, unknown keys, and unterminated quoted values fail
with a line number, never the secret value. The file is Git-ignored; keep its
permissions at `600`. Credential-free contracts never read it.
The agent's already-running environment does not inherit later `.zshrc` changes;
for automated runs, supply the non-secret settings explicitly or put them in
this ignored file alongside the key.
Live cloud mode disables runtime logging and omits raw provider exception
messages from console errors; error type and failed status remain explicit.

For Microsoft Foundry's OpenAI v1 endpoint, use the API base URL
`https://<resource>.services.ai.azure.com/openai/v1/`, not the full
`/responses` operation URL, and set `JARVIS_CLOUD_WIRE_API=responses`.
The pinned SDK supports this protocol directly through `ProviderConfig.WireApi`;
no Chat Completions-to-Responses translation proxy is needed.
`JARVIS_CLOUD_MODEL` must be the Azure deployment name (which may match the model
name). Leave the wire API variable unset for the existing default provider
behavior, or explicitly set it to `chat-completions` for that protocol.
See the [tagged SDK BYOK guide](https://github.com/github/copilot-sdk/blob/v1.0.16/docs/auth/byok.md)
and [Microsoft's endpoint guidance](https://learn.microsoft.com/en-us/azure/foundry-classic/openai/how-to/switching-endpoints).

## Follow-up gates

1. Resolve the native-runtime network-isolation blocker before approving T02's dependent assumptions. Either prove the selected native path under a scoped OS control or validate the isolated SDK plus destination-allowlisted inference-gateway alternative.
2. T04 must preserve explicit provider routing, allowlisted tool dispatch, host-owned bounded turns/cancellation, and a runtime crash/interruption outcome.
3. Keep the strict zero-attempt diagnostic and containment test distinct. Do not lower the former's expectation to report a clean network audit.
4. Full application context, approval, durable state, and privacy routing remain out of this standalone feasibility spike.
