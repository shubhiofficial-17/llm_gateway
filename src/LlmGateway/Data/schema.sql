-- Idempotent schema. Applied by DatabaseInitializer on startup.

CREATE TABLE IF NOT EXISTS teams (
    id          TEXT PRIMARY KEY,
    name        TEXT        NOT NULL,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- Per-team limit overrides. NULL means "use the gateway default".
ALTER TABLE teams ADD COLUMN IF NOT EXISTS requests_per_minute INTEGER;
ALTER TABLE teams ADD COLUMN IF NOT EXISTS tokens_per_minute INTEGER;
ALTER TABLE teams ADD COLUMN IF NOT EXISTS monthly_budget_micros BIGINT;

-- Only the SHA-256 hash of an API key is stored. The plaintext key is shown once, at creation.
CREATE TABLE IF NOT EXISTS api_keys (
    key_hash    BYTEA PRIMARY KEY,
    team_id     TEXT        NOT NULL REFERENCES teams(id),
    label       TEXT,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    revoked_at  TIMESTAMPTZ
);

-- Append-only ledger: one row per proxied request. Source of truth for spend.
-- Money is stored as integer micro-dollars (1 USD = 1,000,000) to avoid floating-point drift.
CREATE TABLE IF NOT EXISTS usage_events (
    id                 BIGSERIAL PRIMARY KEY,
    request_id         TEXT        NOT NULL,
    team_id            TEXT        NOT NULL,
    model              TEXT        NOT NULL,
    provider           TEXT        NOT NULL,
    prompt_tokens      INTEGER     NOT NULL,
    completion_tokens  INTEGER     NOT NULL,
    cost_micros        BIGINT      NOT NULL,
    status_code        INTEGER     NOT NULL,
    latency_ms         INTEGER     NOT NULL,
    streamed           BOOLEAN     NOT NULL,
    usage_estimated    BOOLEAN     NOT NULL,
    created_at         TIMESTAMPTZ NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_usage_events_team_time ON usage_events (team_id, created_at);
