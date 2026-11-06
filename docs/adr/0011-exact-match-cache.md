# 0011 – Exact-match response cache

**Status:** Accepted

## Context

Many LLM calls repeat: the same classification prompt over the same ticket, a retry after a client timeout, a test suite run again. Each repeat costs the full price and the full latency.

## Decision

- A **non-streaming** request can be answered from a cache in Redis. Requests opt in with the `x-gateway-cache: on` header. `Cache:DefaultOn` makes caching the default, and `x-gateway-cache: off` then opts out.
- The key is `cache:{team}:<sha256>` of the request JSON:
  - Keys are sorted recursively, so field order doesn't matter.
  - Fields that only change how the answer is delivered (`stream`, `stream_options`, `user`) are left out.
  - Everything that can change the answer (model, messages, temperature, max_tokens, ...) is included.
- **The team id is part of the key.** One team can never receive an answer generated from another team's prompt, even for an identical request. This matters more than the extra hits sharing would give.
- Only clean answers are stored: status 200 with a usage block, from the **requested** model. A fallback model's answer is returned to the client but not cached, so a short outage doesn't pin lower-quality answers for the whole TTL (default 1 hour).
- A hit:
  - returns the stored body with `x-gateway-cache: hit`
  - doesn't touch the token limit or the budget
  - is recorded in the ledger with `cost_micros = 0`, `cache_hit = true` and `saved_micros` = what the answer cost the first time
  - still counts against the team's requests-per-minute limit
  - shows up in the usage summary as `cacheHits` and `savedUsd`
- Cache failures are treated as misses: like rate limiting, the cache is an optimisation and fails open.

## Consequences

- Repeated requests cost nothing and return in milliseconds, and the savings are measurable per team and per model.
- Sampling is lost on hits: with `temperature > 0`, a repeated request returns the same answer instead of a fresh one. That's why caching is opt-in.
- Only identical requests hit. A semantic cache (next milestone) would also catch rephrasings.
