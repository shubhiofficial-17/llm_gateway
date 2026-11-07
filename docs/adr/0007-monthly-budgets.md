# 0007 – Monthly budgets: reserve, settle, and fail closed

**Status:** Accepted

## Context

Teams need a hard cap on what they can spend each month. Like the token limit, a request's real cost is only known after the response. Checking "spent so far < budget" before each request lets 50 parallel requests all pass and overspend together.

## Decision

- Each team-month has a Redis hash `budget:{team}:2026-11` holding `used` (settled spend) and `reserved` (in-flight spend), in micro-USD.
- **Reserve:** before sending, one Lua script checks `used + reserved + estimate ≤ limit` and, if it fits, adds the estimate to `reserved`. The estimate is the price of the prompt estimate plus `max_tokens`.
- **Settle:** after the response, a second script removes the reservation and adds the real cost to `used`.
- **Crash safety:** every reservation is also a member of a sorted set scored by its expiry time (default 10 minutes). Each reserve call first releases reservations that have expired, so a gateway that dies mid-request can't hold budget forever. A settle that arrives after its reservation expired adds the real cost without releasing the reservation a second time.
- **Rebuild from the ledger:** the reserve script returns "needs seed" when the month has no `used` value. That happens on the first request of a month, or after Redis lost its data. The gateway then sums the month's spend from the Postgres ledger, sets `used` with `HSETNX` so concurrent seeders can't clobber each other, and retries.
- **Fail closed:** if the budget can't be checked, the request is refused with 503 `budget_unavailable`. Rate limits fail open because they protect fairness. Budgets protect money, so the trade-off flips.
- Over budget returns **402** with OpenAI's `insufficient_quota` type and code `budget_exceeded`. A 429 would tell SDKs to retry, which can't help until the budget is raised or the month rolls over.
- Free models (price 0) skip the check, so a team over budget can still use a local model.
- The budget is `Gateway:Budgets:DefaultMonthlyBudgetUsd` (0 means none), and a team can have its own with `PUT /admin/teams/{id}/limits`.

## Consequences

- Concurrent requests can't overspend. The test fires 500 parallel reservations at a budget with room for 50, and exactly 50 succeed.
- Requests without `max_tokens` reserve for 1024 completion tokens, so a team close to its cap may be refused a request that would really have fit.
- If a settle fails, that request's cost is missing from Redis until the next reseed. The Postgres ledger stays correct. A periodic reconciliation job that resets `used` from the ledger would close this gap.
- Months roll over in UTC.
