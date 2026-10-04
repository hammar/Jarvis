# ADR 0001: Native Copilot runtime trust boundary

## Status

Accepted by the owner on 2026-10-04.

## Context

Jarvis targets an owner-controlled Apple Silicon Mac with native Ollama inference.
The T01 spike validates the pinned Copilot SDK/runtime through actual-runtime
contracts and live local/cloud smoke tests. Linux network tracing initially
observed DNS queries, subsequently reproduced by the fixture listener without
Copilot. Locally resolving the container hostname eliminates those attempts
and the unchanged strict contract trace passes.

Docker network-none enforces isolation in the synthetic test environment.
Equivalent OS-level enforcement on native macOS has not been established.
Open-source SDK inspection and finite observations do not prove the behavior
of every bundled runtime component or protection against a compromised runtime.

## Decision

Start the MVP with the pinned Copilot runtime running natively as a trusted
dependency. The owner explicitly accepts the residual dependency-trust risk;
OS-enforced network containment against the runtime itself is not an MVP gate.
This resolves the T01 deployment-assurance caveat, not every other completion
or integration gate.

LocalOnly remains an application-enforced policy: select the local provider
explicitly, prohibit application-controlled cloud escalation, and prevent
LocalOnly context from entering cloud packets. It is not a claim of air-gapped
execution or protection from malicious/compromised runtime code.

Model output and retrieved content remain untrusted. This decision does not
authorize arbitrary HTTP, shell, filesystem, installation, or delegation tools,
broaden household permissions, or waive host authorization/approval checks.
The production dispatcher, context policy, consent, and durable state are
future implementation obligations, not protections already supplied by T01.

## Required safeguards

- Preserve explicit provider configuration, empty mode, tool allowlists,
  sanitized child environment, isolated runtime paths, and disabled ambient
  login/configuration discovery.
- Enforce typed tool validation, approved entities, exact consent context,
  bounded turns/tool execution, explicit cancellation, and truthful interruption
  outcomes in application-owned code.
- Keep the runtime replaceable; SQLite owns durable conversations, approvals,
  jobs, memory, and audit.
- Preserve credential-free denial/routing/lifecycle contracts and network
  diagnostics. Re-run them and review relevant source/release changes before
  adopting another SDK/runtime version.
- Never claim native OS-enforced containment from Docker tests or SDK flags.

## Consequences and revisit triggers

Prompt injection cannot grant a capability absent from the configured tool
surface, but can still misuse allowed capabilities; host policy is mandatory.
A runtime vulnerability, configuration regression, or compromised dependency
can have a larger blast radius without OS restrictions. The owner accepts that
residual risk for the initial personal deployment.

Revisit this decision before adding powerful capabilities, exposing the
application to additional users, materially increasing data sensitivity, or
following evidence of unexpected runtime networking. If containment becomes
required, prefer isolating the disposable runtime with an allowlisted inference
gateway while retaining native Ollama GPU acceleration; ordinary Docker
networking alone is not enforcement.

## Evidence

See [T01 SDK validation](../sdk-validation.md) for exact pins, commands,
observations, limitations, and the fixture DNS attribution.
