# 0009 – Fallback chains between models

**Status:** Accepted

## Context

Every provider has outages, overload periods and rate limits. Without a gateway, each app needs its own retry-on-another-provider logic, and most don't have any.

## Decision

- Each model can list `Fallbacks`: other public model names to try in order, e.g. `gpt-4o-mini` → `claude-haiku-5-5` → `llama3.2`. Unknown or self-referencing fallbacks stop the gateway at startup.
- `FallbackSender` moves to the next model when a provider:
  - can't be reached (connection error) or times out
  - returns 408, 429 or any 5xx
- **Client errors (400, 401, 403, 404, 422) are passed straight through.** A malformed request will be just as malformed for the next provider, and a 401 means the gateway's own key is wrong. Retrying would only hide the bug.
- **Fallback only happens before the response starts.** Once stream chunks have reached the client, the answer can't be restarted on another model without the client seeing a mix of two answers. A stream that breaks midway ends as it does today.
- When every model in the chain fails, the client gets the last model's error.
- Chains are one level deep: a fallback's own fallbacks are not followed. That keeps the worst case at N attempts and rules out cycles.
- **Billing:** the request is billed at the price of the model that answered and recorded under that model. The monthly budget reservation uses the most expensive model in the chain, so a fallback to a pricier model can't overspend.
- Response headers `x-gateway-model` and `x-gateway-fallback-from` show what happened.

## Consequences

- A provider outage turns into slightly higher latency instead of errors, for any model with a fallback.
- A fallback model may answer differently (quality, style, context length). Teams choose chains per model, so it's their trade-off to make.
- Every request still tries the primary first, even during a long outage. That wastes time on a provider known to be down; circuit breakers (next milestone) will skip it.
