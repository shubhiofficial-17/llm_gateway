-- Gives unused tokens back to a bucket, never above capacity.
-- KEYS[1]  bucket hash (see token_bucket.lua)
-- ARGV[1]  capacity
-- ARGV[2]  tokens to return
-- Returns  1 if refunded, 0 if the bucket no longer exists (it expired, so it's full anyway)

local tokens = tonumber(redis.call('HGET', KEYS[1], 'tokens'))
if not tokens then
  return 0
end

tokens = math.min(tonumber(ARGV[1]), tokens + tonumber(ARGV[2]))
redis.call('HSET', KEYS[1], 'tokens', tokens)
return 1
