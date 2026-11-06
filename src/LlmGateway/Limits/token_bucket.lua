-- Token bucket, evaluated atomically inside Redis.
-- KEYS[1]  bucket hash: tokens, ts (ms)
-- ARGV[1]  capacity (max burst)
-- ARGV[2]  refill rate in tokens per millisecond
-- ARGV[3]  cost of this request in tokens
-- Returns  { allowed (1/0), remaining tokens (floor), retry after ms }
--
-- Uses Redis' own clock so every gateway instance agrees on time.

local capacity = tonumber(ARGV[1])
local rate = tonumber(ARGV[2])
local cost = tonumber(ARGV[3])

local t = redis.call('TIME')
local now = tonumber(t[1]) * 1000 + math.floor(tonumber(t[2]) / 1000)

local bucket = redis.call('HMGET', KEYS[1], 'tokens', 'ts')
local tokens = tonumber(bucket[1]) or capacity
local ts = tonumber(bucket[2]) or now

if now > ts then
  tokens = math.min(capacity, tokens + (now - ts) * rate)
  ts = now
end

local allowed = 0
local retry_ms = 0
if tokens >= cost then
  tokens = tokens - cost
  allowed = 1
else
  retry_ms = math.ceil((cost - tokens) / rate)
end

redis.call('HSET', KEYS[1], 'tokens', tokens, 'ts', ts)
-- Idle buckets refill to full anyway, so let them expire.
redis.call('PEXPIRE', KEYS[1], math.ceil(capacity / rate) + 1000)

return { allowed, math.floor(tokens), retry_ms }
