# 0004 – Bill streamed requests from the provider's usage chunk

**Status:** Accepted

## Context

For streamed responses, OpenAI-compatible providers only report token usage if the request sets `stream_options.include_usage`. They then send one extra chunk with an empty `choices` array and a `usage` object. Most clients don't set this, and some client code breaks on a chunk with no choices.

Counting tokens ourselves would need each model's tokenizer and still wouldn't match the provider's bill exactly.

## Decision

- The gateway **always** sets `stream_options.include_usage = true` on streamed requests it sends upstream.
- `SseRelay` reads usage from that chunk while relaying, and **drops** the chunk if the client didn't ask for it. The client gets exactly the stream it requested.
- If no usage arrives (the client disconnected, the stream broke, or the provider doesn't support it), tokens are estimated at about 4 characters per token. The usage row is marked `usage_estimated = true` so reports can tell estimates from exact counts.
- Requests the client abandons are still billed (status `499`), because the provider charged for the tokens it generated.

## Consequences

- Streamed and non-streamed requests are billed on the provider's own numbers.
- The relay parses each SSE event as JSON. That costs a little CPU per chunk, but it's negligible next to network time.
