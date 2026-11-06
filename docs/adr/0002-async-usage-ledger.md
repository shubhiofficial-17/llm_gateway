# 0002 – Write usage asynchronously, store money as integer micro-dollars

**Status:** Accepted

## Context

Every request produces a usage record: team, model, tokens, cost, status, latency. Writing it to Postgres inside the request would add a database round trip to every call, and a slow database would slow every LLM request.

## Decision

- The request path puts a `UsageEvent` into a **bounded in-memory channel** (10,000 events) and returns immediately.
- `UsageWriterService` drains the channel in batches of up to 500 and writes each batch with **Postgres binary `COPY`**, one round trip per batch. It retries 3 times and drains the channel on graceful shutdown.
- `usage_events` is an **append-only ledger** and the source of truth for spend. Summaries are aggregated from it.
- Money is stored as **integer micro-dollars** (`BIGINT`, 1 USD = 1,000,000). Prices are configured as `decimal` USD per million tokens, so `tokens × price` gives micro-dollars directly. Floats never touch money.

## Consequences

- Recording usage adds microseconds to a request instead of milliseconds.
- If the process crashes, events still in the channel are lost: at most a few seconds of records. If the database is down long enough to fill the buffer, new events are dropped and logged as errors. For a cost ledger this is an accepted trade-off. Hard budget enforcement (milestone 2) happens in Redis at request time, so it doesn't depend on the ledger being up to the second.
- A durable queue (Kafka, or an outbox table) would remove the loss window at the cost of another component. Revisit if usage records are ever used for external billing.
