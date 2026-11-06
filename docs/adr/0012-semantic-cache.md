# 0012 – Semantic cache: embeddings compared within a narrow bucket

**Status:** Accepted

## Context

The exact-match cache (ADR 0011) misses as soon as a user rephrases: "What's our refund policy?" and "how do refunds work" are different strings with the same answer. Matching on meaning means comparing embeddings, but a semantic cache that is too eager returns confidently wrong answers. That is worse than no cache at all.

## Decision

- When `Cache:SemanticEnabled` is on and the exact cache misses, the gateway embeds the **last user message** and looks for a cached question with cosine similarity ≥ `SimilarityThreshold` (default 0.95).
- **Buckets keep matches safe.** A bucket is `scache:{team}:<hash of the request without its last user message>`. Two requests share a bucket only if everything else is identical: model, parameters, system prompt and every earlier turn. Some consequences:
  - the same question under a different system prompt never matches
  - a follow-up in a different conversation never matches
  - one team never sees another team's answers
- **Storage:** each bucket is a Redis list of `{embedding, answer}`, newest first, trimmed to `MaxEntriesPerBucket` (500) and expiring with the cache TTL. A lookup reads the bucket and compares vectors in the gateway. Buckets are small by construction, so a linear scan of a few hundred 1,536-dimension vectors takes well under a millisecond. It also needs only plain Redis, with no vector-search module.
- **Embeddings:** any OpenAI-compatible `/embeddings` endpoint (`Cache:Embeddings:Provider` names a configured provider), or a keyless `mock` provider (bag-of-words hashing) for tests and demos.
- Same storage rules as the exact cache: only clean answers from the requested model are stored, and any embedding or cache failure is a miss.
- Hits are reported as `x-gateway-cache: semantic-hit` with `x-gateway-cache-similarity`, and recorded with provider `semantic-cache` and the money they saved.

## Consequences

- Rephrased questions in the same context are served for free.
- The threshold trades hit rate for correctness. 0.95 is deliberately strict. It should be tuned per embedding model against a labelled set of pairs that should and shouldn't match; that evaluation script is planned.
- Embedding calls cost money and add latency to cache misses, and the gateway doesn't yet bill or meter them.
- If buckets grow large (long-lived, high-traffic prompts), the linear scan should move to a vector index: RediSearch, or pgvector next to the ledger.
