-- Replaces a reservation with the request's real cost.
-- KEYS[1]  budget hash, KEYS[2] reservations (see budget_reserve.lua)
-- ARGV[1]  reservation member "<id>:<amount>"
-- ARGV[2]  reserved amount (micro-USD)
-- ARGV[3]  actual cost (micro-USD)
-- If the reservation already expired and was reclaimed, only the real cost is added,
-- so the reserved amount is never released twice.

if redis.call('ZREM', KEYS[2], ARGV[1]) == 1 then
  redis.call('HINCRBY', KEYS[1], 'reserved', -tonumber(ARGV[2]))
end
redis.call('HINCRBY', KEYS[1], 'used', tonumber(ARGV[3]))
return 1
