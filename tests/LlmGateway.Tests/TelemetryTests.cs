using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using LlmGateway.Auth;
using LlmGateway.Telemetry;
using LlmGateway.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace LlmGateway.Tests;

public sealed class TelemetryTests(GatewayFactory factory) : IClassFixture<GatewayFactory>, IDisposable
{
    private readonly ConcurrentBag<(string Instrument, long Value, Dictionary<string, object?> Tags)> _measurements = new();
    private readonly ConcurrentBag<Activity> _activities = new();
    private MeterListener? _meterListener;
    private ActivityListener? _activityListener;

    private void Listen()
    {
        _meterListener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == GatewayTelemetry.Name)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            },
        };
        _meterListener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            _measurements.Add((instrument.Name, value, tags.ToArray().ToDictionary(t => t.Key, t => t.Value))));
        _meterListener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            _measurements.Add((instrument.Name, (long)value, tags.ToArray().ToDictionary(t => t.Key, t => t.Value))));
        _meterListener.Start();

        _activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == GatewayTelemetry.Name || source.Name == "Microsoft.AspNetCore",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => _activities.Add(activity),
        };
        ActivitySource.AddActivityListener(_activityListener);
    }

    private async Task<HttpClient> ClientForNewTeamAsync(string teamId)
    {
        var teams = factory.Services.GetRequiredService<ITeamStore>();
        await teams.UpsertTeamAsync(new Team(teamId, teamId), CancellationToken.None);
        await teams.AddKeyAsync(teamId, ApiKeys.Hash("gw_" + teamId), null, CancellationToken.None);
        return factory.CreateAuthorizedClient("gw_" + teamId);
    }

    private static async Task Eventually(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(50);
        }

        Assert.True(condition());
    }

    [Fact]
    public async Task Requests_tokens_and_cost_are_counted_per_team_and_model()
    {
        Listen();
        var client = await ClientForNewTeamAsync("metrics-team");

        var response = await client.PostAsJsonAsync("/v1/chat/completions", new
        {
            model = "mock-1",
            messages = new[] { new { role = "user", content = "count me" } },
        });
        var usage = await factory.WaitForUsageAsync(response.RequestId());

        List<(string Instrument, long Value, Dictionary<string, object?> Tags)> Mine() =>
            _measurements.Where(m => Equals(m.Tags.GetValueOrDefault("team"), "metrics-team")).ToList();

        await Eventually(() => Mine().Any(m => m.Instrument == "gateway.cost.micros"));
        var mine = Mine();
        Assert.Contains(mine, m => m.Instrument == "gateway.requests" && m.Value == 1 && Equals(m.Tags["model"], "mock-1"));
        Assert.Contains(mine, m => m.Instrument == "gateway.tokens" && Equals(m.Tags["type"], "input") && m.Value == usage.PromptTokens);
        Assert.Contains(mine, m => m.Instrument == "gateway.tokens" && Equals(m.Tags["type"], "output") && m.Value == usage.CompletionTokens);
        Assert.Contains(mine, m => m.Instrument == "gateway.cost.micros" && m.Value == usage.CostMicros);
    }

    [Fact]
    public async Task Limit_rejections_are_counted()
    {
        Listen();
        var teams = factory.Services.GetRequiredService<ITeamStore>();
        var client = await ClientForNewTeamAsync("limited-team");
        await teams.SetLimitsAsync("limited-team", new TeamLimits(null, 10, null), CancellationToken.None);
        var limited = await ClientForNewTeamAsync("limited-team"); // new client so the limits are re-read

        var response = await limited.PostAsJsonAsync("/v1/chat/completions", new
        {
            model = "mock-1",
            max_tokens = 100,
            messages = new[] { new { role = "user", content = "too big" } },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await Eventually(() => _measurements.Any(m => m.Instrument == "gateway.limit.rejections"
                                                      && Equals(m.Tags.GetValueOrDefault("team"), "limited-team")
                                                      && Equals(m.Tags["limit"], "request_too_large")));
    }

    [Fact]
    public async Task Fallback_shows_up_as_two_provider_spans_in_one_trace()
    {
        Listen();
        var traceId = ActivityTraceId.CreateRandom();
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = JsonContent.Create(new { model = "mock-down", messages = new[] { new { role = "user", content = "trace me" } } }),
        };
        request.Headers.Add("traceparent", $"00-{traceId}-{ActivitySpanId.CreateRandom()}-01");

        var response = await factory.CreateAuthorizedClient().SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        List<Activity> Trace() => _activities.Where(a => a.TraceId == traceId).ToList();
        await Eventually(() => Trace().Any(a => a.Kind == ActivityKind.Server));

        var calls = Trace().Where(a => a.Source.Name == GatewayTelemetry.Name).OrderBy(a => a.StartTimeUtc).ToList();
        Assert.Equal(2, calls.Count);
        Assert.Equal("chat mock-down", calls[0].DisplayName);
        Assert.Equal(ActivityStatusCode.Error, calls[0].Status);
        Assert.Equal("chat mock-1", calls[1].DisplayName);
        Assert.Equal(2, calls[1].GetTagItem("gateway.attempt"));

        var server = Trace().Single(a => a.Kind == ActivityKind.Server);
        Assert.Equal("mock-down", server.GetTagItem("gen_ai.request.model"));
        Assert.Equal("mock-1", server.GetTagItem("gen_ai.response.model"));
        Assert.Equal(GatewayFactory.TeamId, server.GetTagItem("gateway.team"));
    }

    [Fact]
    public async Task Prometheus_endpoint_exposes_gateway_metrics()
    {
        var client = factory.CreateAuthorizedClient();
        await client.PostAsJsonAsync("/v1/chat/completions", new
        {
            model = "mock-1",
            stream = true,
            messages = new[] { new { role = "user", content = "scrape me" } },
        });

        var metrics = await factory.CreateClient().GetStringAsync("/metrics");

        Assert.Contains("gateway_requests_total", metrics);
        Assert.Contains("gateway_time_to_first_token", metrics);
    }

    public void Dispose()
    {
        _meterListener?.Dispose();
        _activityListener?.Dispose();
    }
}
