import http from 'k6/http';

export const BASE = __ENV.BASE_URL || 'http://localhost:8080';
export const KEY = __ENV.API_KEY || 'gw-demo-key';
export const ADMIN_KEY = __ENV.ADMIN_KEY || 'dev-admin-key';

/** Lift the demo team's limits so the test measures the gateway, not its rate limiter. */
export function unlimitedDemoTeam() {
  const res = http.put(`${BASE}/admin/teams/demo/limits`,
    JSON.stringify({ requestsPerMinute: 0, tokensPerMinute: 0, monthlyBudgetUsd: 0 }),
    { headers: { 'Content-Type': 'application/json', 'X-Admin-Key': ADMIN_KEY } });
  if (res.status !== 200) {
    throw new Error(`could not lift limits: ${res.status} ${res.body}`);
  }
}

export function chat(model, content, { stream = false, cache = false, tags = {} } = {}) {
  const headers = { 'Content-Type': 'application/json', Authorization: `Bearer ${KEY}` };
  if (cache) {
    headers['x-gateway-cache'] = 'on';
  }
  return http.post(`${BASE}/v1/chat/completions`,
    JSON.stringify({ model, stream, messages: [{ role: 'user', content }] }),
    { headers, tags });
}

/** Compact, machine-readable summary printed at the end of a run. */
export function summary(data, name) {
  const pick = (metric, stats) => {
    const m = data.metrics[metric];
    if (!m) return null;
    return Object.fromEntries(stats.map(s => [s, m.values[s]]));
  };
  const result = { test: name, metrics: {} };
  for (const [key, metric] of Object.entries(data.metrics)) {
    if (key.startsWith('http_req_') && !['http_req_duration', 'http_req_failed', 'http_reqs'].some(k => key.startsWith(k))) continue;
    if (metric.type === 'trend') result.metrics[key] = pick(key, ['avg', 'p(50)', 'p(95)', 'p(99)', 'max']);
    if (metric.type === 'counter') result.metrics[key] = pick(key, ['count', 'rate']);
    if (metric.type === 'rate') result.metrics[key] = pick(key, ['rate', 'passes', 'fails']);
  }
  return {
    stdout: `\n===== ${name} =====\n${JSON.stringify(result, null, 2)}\n`,
    [`/scripts/results/${name}.json`]: JSON.stringify(result, null, 2),
  };
}
