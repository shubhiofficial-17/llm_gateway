# 0003 – Store API keys as SHA-256 hashes

**Status:** Accepted

## Context

Gateway API keys must not be recoverable from a database dump. Password storage normally uses slow, salted hashes (bcrypt, Argon2), but those cost tens of milliseconds per check. That cost would apply to every request, and a salted hash can't be looked up by an index.

## Decision

- Keys are generated as `gw_` followed by 32 random bytes (256 bits) from a CSPRNG, base64url-encoded.
- Only `SHA-256(key)` is stored, as the primary key of `api_keys`. The plaintext is returned once, at creation.
- Lookups are by hash equality, with an in-memory cache: 60 seconds for valid keys and 5 seconds for unknown keys, so a client retrying with a bad key can't hammer the database.
- The admin key is compared in constant time (`CryptographicOperations.FixedTimeEquals` over hashes).

## Why a fast hash is safe here

Slow hashing protects low-entropy secrets like human passwords from brute force. A 256-bit random key cannot be brute-forced whatever the hash speed, so a fast unsalted hash is the right tool. GitHub tokens and Stripe keys are stored the same way.

## Consequences

- Key checks cost one SHA-256 and, usually, a cache hit.
- Revoking a key (`revoked_at`) can take up to 60 seconds to apply because of the cache. If that is too slow, add cache invalidation over Redis pub/sub.
