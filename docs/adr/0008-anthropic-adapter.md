# 0008 – Serve Claude models through an OpenAI-format adapter

**Status:** Accepted

## Context

ADR 0001 made OpenAI's format the gateway's only client-facing API. Anthropic's Messages API differs in several ways:

- System prompts are a top-level field, not messages.
- `max_tokens` is required.
- User and assistant turns must alternate.
- Streams use typed events (`message_start`, `content_block_delta`, `message_delta`, `message_stop`).
- Errors have a different shape, and "overloaded" is the non-standard status 529.

## Decision

- An `anthropic` provider type translates in both directions. `AnthropicTranslator` is pure functions, unit-tested without HTTP.
- **Requests:** system and developer messages are joined into `system`. Consecutive same-role messages are merged so turns alternate. `max_tokens` comes from the client, or the provider's `DefaultMaxTokens`. `temperature`, `top_p` and `stop` are mapped.
- **Responses:** text blocks are concatenated. `stop_reason` becomes `finish_reason` (`end_turn` → `stop`, `max_tokens` → `length`). `input_tokens` and `output_tokens` become `prompt_tokens` and `completion_tokens`.
- **Streams:** Anthropic's events are rewritten as OpenAI `chat.completion.chunk` events while they arrive, through a pipe. The adapter emits the final usage chunk from the token counts in `message_start` and `message_delta`. Because of that, the rest of the gateway (relay, billing, limits) handles Claude exactly like any other provider.
- **Errors** are converted to OpenAI's shape, and 529 is reported as 503, so retry logic treats it as a temporary failure.

## Consequences

- Clients reach Claude with an unchanged OpenAI SDK, just by changing the model name.
- Only text is translated for now. Tool calls, images and Anthropic-only features (prompt caching, extended thinking) aren't mapped yet. Requests that rely on them should go to Anthropic directly until the adapter supports them.
