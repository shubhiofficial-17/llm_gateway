# 0010 – Circuit breakers per provider

**Status:** Accepted

## Context

Fallback chains (ADR 0009) recover from a provider failure, but every request still tries the failing provider first. During an outage that adds a timeout or an error round trip to every call, and keeps hammering a provider that is already struggling.

## Decision

- Each provider has a circuit breaker with three states:
  - **Closed:** requests flow normally.
  - **Open:** after `FailureThreshold` consecutive failures (default 5), requests skip the provider for `BreakSeconds` (default 30). They go straight to the next model in the chain. If there is none, they fail fast with 503 `provider_unavailable` and a `Retry-After` header.
  - **Half-open:** when the break ends, one trial request is let through. If it succeeds the circuit closes; if it fails the circuit opens for another full break. Other requests keep skipping the provider while the trial runs.
- **What counts as a failure:** connection errors, timeouts, 408 and 5xx. A 429 means the provider is up but busy, and a 4xx means the request was bad, so both count as healthy. Any healthy response resets the failure count.
- A trial cancelled by the client releases the half-open slot without a verdict. A trial that never reports back (lost in a crash) is replaced after one break period, so a provider can't get stuck half-open.
- Circuits are **per gateway instance**, in memory. Each instance learns about an outage from its own traffic within a few requests. Sharing state through Redis would add a round trip to every request to save those few. It would also make Redis a dependency for routing decisions.
- `GET /admin/providers` shows each provider's state, failure count and when an open circuit will retry.
- `FailureThreshold: 0` turns circuit breaking off.

## Consequences

- During an outage, requests to a model with a fallback cost one fast hop instead of a failed call plus a fallback.
- Consecutive-failure counting reacts quickly to hard outages, but may not trip on a provider that fails, say, 30% of requests. A failure-rate window would catch that. It needs a minimum request volume to avoid flapping at low traffic, so it's left for later.
