# 0005 – Per-team rate limits with a Redis token bucket

**Status:** Accepted

## Context

One team's batch job shouldn't be able to use up the provider's rate limit for everyone else. The gateway runs as several instances, so the limit must be shared between them. Per-instance counters would let each instance allow the full limit.

## Decision

- Each team gets a **token bucket** for chat completions: up to `RequestsPerMinute` requests in a burst, refilling at `RequestsPerMinute / 60` per second. A token bucket allows short bursts but holds the long-run rate, which suits interactive traffic better than a fixed window that resets on the minute.
- The bucket lives in **Redis** and is checked and updated by one **Lua script**, so it is atomic across instances. Two gateways can't both read "1 left" and both let a request through. The script uses Redis' `TIME`, so instances with drifting clocks still agree.
- Keys look like `rl:{team-id}:rpm`. The `{...}` hash tag keeps all of a team's keys on the same Redis Cluster shard, which later multi-key scripts (budgets) will need.
- Idle buckets get a TTL equal to their full refill time. They'd be full again by then anyway, so Redis doesn't keep keys for inactive teams.
- Responses carry `x-ratelimit-limit-requests` and `x-ratelimit-remaining-requests`. Rejections are `429` with `Retry-After` and OpenAI's `rate_limit_exceeded` error code, so OpenAI SDKs back off and retry on their own.
- **If Redis is unreachable, requests are allowed (fail open)** and a warning is logged. Losing rate limiting for a few minutes is better than taking every AI feature offline. Monthly budgets protect money rather than fairness, and will make the opposite choice.
- Without a Redis connection string the gateway uses an in-process bucket, for tests and single-instance local runs.

## Consequences

- Each chat request adds one Redis round trip, under a millisecond on the same network.
- The default comes from `Gateway:RateLimits:RequestsPerMinute`. A team can be given its own value with `PUT /admin/teams/{id}/limits`. Because team lookups are cached per API key for 60 seconds, a change takes up to a minute to apply.
