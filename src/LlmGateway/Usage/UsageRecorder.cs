using System.Threading.Channels;

namespace LlmGateway.Usage;

public interface IUsageRecorder
{
    void Record(UsageEvent usageEvent);
}

/// <summary>
/// Hands usage events to a background writer through a bounded channel, so writing to the
/// database never adds latency to the request path.
/// </summary>
public sealed class ChannelUsageRecorder(ILogger<ChannelUsageRecorder> logger) : IUsageRecorder
{
    public const int Capacity = 10_000;

    private readonly Channel<UsageEvent> _channel = Channel.CreateBounded<UsageEvent>(
        new BoundedChannelOptions(Capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
        });

    public ChannelReader<UsageEvent> Reader => _channel.Reader;

    public void Record(UsageEvent usageEvent)
    {
        if (!_channel.Writer.TryWrite(usageEvent))
        {
            // Only happens if the database has been unreachable long enough to fill the buffer.
            logger.LogError(
                "Usage buffer full; dropped usage event {RequestId} for team {TeamId} ({CostMicros} micro-USD)",
                usageEvent.RequestId, usageEvent.TeamId, usageEvent.CostMicros);
        }
    }

    public void Complete() => _channel.Writer.TryComplete();
}

/// <summary>Drains the usage channel in batches into the usage store.</summary>
public sealed class UsageWriterService(
    ChannelUsageRecorder recorder,
    IUsageStore store,
    ILogger<UsageWriterService> logger) : BackgroundService
{
    private const int MaxBatchSize = 500;
    private const int MaxAttempts = 3;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var batch = new List<UsageEvent>(MaxBatchSize);
        try
        {
            while (await recorder.Reader.WaitToReadAsync(stoppingToken))
            {
                while (batch.Count < MaxBatchSize && recorder.Reader.TryRead(out var item))
                {
                    batch.Add(item);
                }

                await FlushAsync(batch);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down: fall through and drain what is left.
        }

        while (recorder.Reader.TryRead(out var item))
        {
            batch.Add(item);
            if (batch.Count >= MaxBatchSize)
            {
                await FlushAsync(batch);
            }
        }

        await FlushAsync(batch);
    }

    private async Task FlushAsync(List<UsageEvent> batch)
    {
        if (batch.Count == 0)
        {
            return;
        }

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                await store.WriteBatchAsync(batch, CancellationToken.None);
                break;
            }
            catch (Exception ex) when (attempt < MaxAttempts)
            {
                logger.LogWarning(ex, "Writing {Count} usage events failed (attempt {Attempt}); retrying", batch.Count, attempt);
                await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Dropping {Count} usage events after {Attempts} attempts", batch.Count, MaxAttempts);
            }
        }

        batch.Clear();
    }
}
