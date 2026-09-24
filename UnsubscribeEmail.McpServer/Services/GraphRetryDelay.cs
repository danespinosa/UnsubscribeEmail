namespace UnsubscribeEmail.McpServer.Services;

public interface IGraphRetryDelay
{
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

public sealed class GraphRetryDelay : IGraphRetryDelay
{
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        => Task.Delay(delay, cancellationToken);
}

public interface IGraphRetryJitter
{
    TimeSpan Apply(TimeSpan baseDelay);
}

public sealed class GraphRetryJitter : IGraphRetryJitter
{
    public TimeSpan Apply(TimeSpan baseDelay)
    {
        var multiplier = 0.5 + Random.Shared.NextDouble();
        return TimeSpan.FromMilliseconds(baseDelay.TotalMilliseconds * multiplier);
    }
}
