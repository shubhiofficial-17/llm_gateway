# 0001 – Speak OpenAI's API format

**Status:** Accepted

## Context

Client teams already use OpenAI SDKs in Python, TypeScript, .NET and Java. A gateway with its own API would mean rewriting every caller. Most other providers and local runtimes (Ollama, Groq, vLLM, LM Studio) also offer an OpenAI-compatible endpoint.

## Decision

The gateway exposes `POST /v1/chat/completions` and `GET /v1/models` in OpenAI's request, response, streaming and error formats. Adopting it is a base-URL change.

Request bodies are handled as `JsonObject`, not typed DTOs. The gateway only reads and rewrites the fields it cares about (`model`, `stream`, `stream_options`) and passes everything else through untouched. New OpenAI parameters work without a gateway release.

Providers with a different native API (Anthropic, milestone 3) get an adapter that translates to and from this format.

## Consequences

- Zero client changes beyond configuration.
- Gateway features cannot add request fields without risking collisions with future OpenAI fields. Gateway metadata travels in `x-gateway-*` response headers.
- We depend on OpenAI's format as the de facto standard. If it changes incompatibly, the adapters absorb it.
