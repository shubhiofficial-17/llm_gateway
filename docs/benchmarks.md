# Benchmarks

Measured by the [load-test workflow](../.github/workflows/load-test.yml) on a standard GitHub Actions runner (4 vCPUs). The gateway, Postgres, Redis, the mock upstream and k6 all share that one machine, so these are conservative numbers. Re-run them any time from the Actions tab ("Load test" → "Run workflow"). Each run's raw results are attached as an artifact.

## Gateway overhead

[`loadtest/overhead.js`](../loadtest/overhead.js) sends **300 requests/s for 60 s** to `mock-1`, the in-process mock provider. The mock answers instantly, so the latency is the gateway's own work on every request:

- API key auth (cached team lookup)
- requests-per-minute check, token reservation and budget reservation: three Redis Lua scripts
- routing and the fallback/circuit-breaker path
- the SSE relay for streams
- settling the token and budget reservations: two more Redis calls
- usage recording to Postgres in the background

The demo team's limits are raised far above the test traffic rather than switched off, so every check actually runs.

| Traffic (share of 300 req/s) | p50 | p95 | p99 |
|---|---|---|---|
| Non-streaming (60%) | 1.76 ms | 2.57 ms | 3.70 ms |
| Streaming (20%) | 2.15 ms | 3.07 ms | 4.50 ms |
| Exact-match cache hits (20%) | 1.28 ms | 1.81 ms | 2.93 ms |
| **All** | **1.77 ms** | **2.62 ms** | **3.82 ms** |

**18,003 requests, 0 failed.** Each recorded request appears in the Postgres ledger with its tokens and cost. Cache hits are recorded at zero cost, together with what they saved.

## Provider outage

[`loadtest/chaos.js`](../loadtest/chaos.js) sends **100 requests/s for 60 s** to `upstream-1`. That model is served by an OpenAI-compatible HTTP mock with 50 ms of simulated latency, and `mock-1` is its fallback; 30% of requests stream. The workflow **stops the provider's container at ~20 s and starts it again at ~40 s**.

| Requests | Failed | Answered by `upstream-1` | Answered by the fallback |
|---|---|---|---|
| 6,002 | **0** | 3,485 | 2,516 |

During the outage the first few requests failed over to the fallback per request (ADR 0009). After 5 consecutive failures the provider's circuit opened (ADR 0010), and later requests skipped it without paying for a failed call. Once the container was back, a trial request closed the circuit and traffic returned to `upstream-1`. The provider's circuit was `Closed` again at the end of the run.

## Caveats

- A stream that is already flowing when its provider dies can't be moved to another model (ADR 0009). The client sees a truncated stream with status 200, and the chaos test doesn't count that as a failure. Stopping a container at a random moment can catch at most a handful of in-flight streams.
- Real provider latency (hundreds of ms to seconds) dwarfs the gateway's few milliseconds. The overhead numbers matter for throughput and cost per request, not for the latency users notice.
