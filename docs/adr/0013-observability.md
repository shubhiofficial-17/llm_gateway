# 0013 – Observability with OpenTelemetry

**Status:** Accepted

## Context

A gateway that every AI feature passes through has to answer, at any moment:

- What is each team spending?
- Which provider is slow or failing?
- How often do fallbacks, cache hits and limit rejections happen?
- Where did the time go in one slow request?

The Postgres ledger answers spend questions after the fact, but not latency or real-time health questions.

## Decision

- Use **OpenTelemetry** for traces and metrics, so the data can go to any backend (Prometheus, Grafana, Jaeger, Datadog, Azure Monitor) without code changes.
- **Metrics** (meter `LlmGateway`), all tagged by team and model where it makes sense:
  - `gateway.requests` (by status, provider, cache)
  - `gateway.tokens` (input and output)
  - `gateway.cost.micros` and `gateway.cache.saved.micros`
  - `gateway.request.duration` and `gateway.time_to_first_token`. Time to first token is the latency users actually feel in a streamed chat.
  - `gateway.fallbacks`, `gateway.limit.rejections` (by which limit) and `gateway.circuit.state` (0 closed, 1 half-open, 2 open, per provider)
  - Standard ASP.NET Core and HttpClient metrics are included as well.
- Metrics are fed from the same `UsageEvent` that goes to the ledger, through a decorator on the usage recorder. Dashboards and the ledger can't disagree about what a request cost.
- **Traces:**
  - Every provider call is a client span named `chat <model>` with GenAI semantic-convention attributes (`gen_ai.request.model`, `gen_ai.system`) and the attempt number. A fallback shows as a failed span followed by a successful one in the same trace.
  - The HTTP server span is enriched with `gateway.team`, the requested and served models, token counts, cost and cache result.
- **Export:** Prometheus scrapes `GET /metrics`. Traces and metrics are pushed over OTLP when `OTEL_EXPORTER_OTLP_ENDPOINT` is set.
- `/metrics` has no authentication and includes team names and spend. It is meant for an internal network or a scrape port that isn't exposed publicly.

## Consequences

- Team and model tags keep cardinality bounded by the number of teams and configured models. Request IDs and prompts are never used as tags.
- Prompts and completions are deliberately left out of spans. They can contain personal or confidential data, and traces are usually retained and shared more widely than application data.
