# 0006 – Tokens-per-minute limits: reserve an estimate, refund the rest

**Status:** Accepted

## Context

Providers limit tokens per minute as well as requests per minute, and cost follows tokens. A few requests with huge prompts can use up a whole organisation's provider quota while staying well under a request limit.

The problem is that a request's real token count is only known after the response. Checking afterwards would let a burst of parallel requests all pass.

## Decision

- Before sending a request, reserve an **upper estimate**: the prompt estimate (about 4 characters per token) plus `max_completion_tokens`, or `max_tokens`, or `DefaultMaxTokensEstimate` (1024) if the client sets neither. The reservation comes from a per-team token bucket (`rl:{team}:tpm`), using the same atomic Lua script as the request limit.
- After the response, **refund** `reserved − actual` with a second small Lua script, capped at the bucket's capacity. Failed or rejected upstream calls refund everything.
- A request whose estimate is larger than the team's whole per-minute allowance gets **400 `request_too_large`**, not 429. It could never succeed, and a 429 would make SDKs retry it forever.
- Responses carry `x-ratelimit-limit-tokens` and `x-ratelimit-remaining-tokens`, matching OpenAI's header names.
- Like the request limit, this fails open if Redis is down. A failed refund is only logged: it makes the limit briefly stricter, never looser.

## Consequences

- Concurrent requests can't overshoot, because each one holds its worst case until it finishes.
- Clients that don't set `max_tokens` reserve 1024 completion tokens each, so a team near its limit may get 429s that a perfect estimate would have avoided. Setting `max_tokens` fixes that, and the error message says so.
- If a bucket expires mid-request (only possible for requests longer than a full refill period), the refund is skipped. The bucket is full again by then anyway.
