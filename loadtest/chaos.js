// Chaos: steady traffic to upstream-1 (an HTTP provider with a fallback) while the CI job stops the
// provider's container at ~20s and starts it again at ~40s. Clients should see no failures.
import { check } from 'k6';
import { Counter } from 'k6/metrics';
import { chat, summary, unlimitedDemoTeam } from './common.js';

const servedByPrimary = new Counter('served_by_primary');
const servedByFallback = new Counter('served_by_fallback');

export const options = {
  scenarios: {
    steady: { executor: 'constant-arrival-rate', rate: Number(__ENV.RATE || 100), timeUnit: '1s', duration: '60s',
      preAllocatedVUs: 50, maxVUs: 300 },
  },
  summaryTrendStats: ['avg', 'p(50)', 'p(95)', 'p(99)', 'max'],
  thresholds: { http_req_failed: ['rate<0.001'] },
};

export function setup() {
  unlimitedDemoTeam();
}

export default function () {
  const res = chat('upstream-1', `chaos ${Math.random()}`, { stream: Math.random() < 0.3 });
  check(res, { 'status 200': r => r.status === 200 });
  if (res.headers['X-Gateway-Fallback-From']) {
    servedByFallback.add(1);
  } else {
    servedByPrimary.add(1);
  }
}

export function handleSummary(data) {
  return summary(data, 'chaos');
}
