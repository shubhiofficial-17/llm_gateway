-- Reserves an estimated cost against a team's monthly budget, atomically.
-- KEYS[1]  budget hash: used, reserved (micro-USD)
-- KEYS[2]  open reservations: sorted set, member "<id>:<amount>", score = expiry (ms)
-- ARGV[1]  budget limit (micro-USD)
-- ARGV[2]  estimated cost (micro-USD)
-- ARGV[3]  reservation id
-- ARGV[4]  reservation lifetime (ms)
-- ARGV[5]  key lifetime (s)
-- Returns  { 1, remaining } reserved
--          { 0, remaining } over budget
--          { -1, 0 }        no spend recorded yet: caller seeds "used" from the ledger and retries

if redis.call('HEXISTS', KEYS[1], 'used') == 0 then
  return { -1, 0 }
end

local t = redis.call('TIME')
local now = tonumber(t[1]) * 1000 + math.floor(tonumber(t[2]) / 1000)

-- Reclaim reservations whose request never settled (e.g. the gateway crashed mid-request).
local expired = redis.call('ZRANGEBYSCORE', KEYS[2], '-inf', now)
for _, member in ipairs(expired) do
  redis.call('HINCRBY', KEYS[1], 'reserved', -tonumber(string.match(member, ':(%d+)$')))
end
if #expired > 0 then
  redis.call('ZREMRANGEBYSCORE', KEYS[2], '-inf', now)
end

local limit = tonumber(ARGV[1])
local estimate = tonumber(ARGV[2])
local used = tonumber(redis.call('HGET', KEYS[1], 'used'))
local reserved = tonumber(redis.call('HGET', KEYS[1], 'reserved') or '0')

if used + reserved + estimate > limit then
  return { 0, limit - used - reserved }
end

redis.call('HINCRBY', KEYS[1], 'reserved', estimate)
redis.call('ZADD', KEYS[2], now + tonumber(ARGV[4]), ARGV[3] .. ':' .. ARGV[2])
redis.call('EXPIRE', KEYS[1], ARGV[5])
redis.call('EXPIRE', KEYS[2], ARGV[5])
return { 1, limit - used - reserved - estimate }
