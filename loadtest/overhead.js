// Gateway overhead: the in-process mock provider answers instantly, so latency here is the gateway's own
// work (auth, Redis rate limit + token limit + budget scripts, routing, relay, usage recording).
import { check } from 'k6';
import { chat, summary, unlimitedDemoTeam } from './common.js';

const RATE = Number(__ENV.RATE || 300);

export const options = {
  scenarios: {
    non_streaming: { executor: 'constant-arrival-rate', rate: Math.round(RATE * 0.6), timeUnit: '1s', duration: '60s',
      preAllocatedVUs: 50, maxVUs: 300, exec: 'nonStreaming' },
    streaming: { executor: 'constant-arrival-rate', rate: Math.round(RATE * 0.2), timeUnit: '1s', duration: '60s',
      preAllocatedVUs: 20, maxVUs: 200, exec: 'streaming' },
    cache_hits: { executor: 'constant-arrival-rate', rate: Math.round(RATE * 0.2), timeUnit: '1s', duration: '60s',
      preAllocatedVUs: 20, maxVUs: 200, exec: 'cached' },
  },
  summaryTrendStats: ['avg', 'p(50)', 'p(95)', 'p(99)', 'max'],
  thresholds: {
    http_req_failed: ['rate<0.01'],
    // Per-scenario latency breakdowns in the summary.
    'http_req_duration{scenario:non_streaming}': ['p(99)>=0'],
    'http_req_duration{scenario:streaming}': ['p(99)>=0'],
    'http_req_duration{scenario:cache_hits}': ['p(99)>=0'],
  },
};

export function setup() {
  unlimitedDemoTeam();
}

export function nonStreaming() {
  const res = chat('mock-1', `question ${Math.random()}`);
  check(res, { 'status 200': r => r.status === 200 });
}

export function streaming() {
  const res = chat('mock-1', `stream ${Math.random()}`, { stream: true });
  check(res, { 'status 200': r => r.status === 200, 'stream completed': r => r.body.includes('[DONE]') });
}

export function cached() {
  // 10 distinct questions asked over and over: after the first round, all hits.
  const res = chat('mock-1', `cached question ${Math.floor(Math.random() * 10)}`, { cache: true });
  check(res, { 'status 200': r => r.status === 200 });
}

export function handleSummary(data) {
  return summary(data, 'overhead');
}
