using System.Diagnostics;

namespace Exergism.DeploymentAttestation.Agent;

internal static class ServiceStartupReadiness
{
    internal static async Task<bool> WaitAsync(
        Func<TimeSpan, Task<bool>> isActive,
        Func<TimeSpan, Task<bool>> probe,
        TimeSpan timeout,
        TimeSpan retryInterval)
    {
        if (timeout <= TimeSpan.Zero || retryInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout), "Readiness deadlines and intervals must be positive");

        var clock = Stopwatch.StartNew();
        TimeSpan Remaining() => timeout - clock.Elapsed;
        TimeSpan Budget(TimeSpan maximum) => TimeSpan.FromTicks(Math.Min(maximum.Ticks, Remaining().Ticks));

        while (Remaining() > TimeSpan.Zero)
        {
            var active = await HealthCheckRunner.RunAsync(() => isActive(Budget(TimeSpan.FromSeconds(10))));
            if (active && Remaining() > TimeSpan.Zero &&
                await HealthCheckRunner.RunAsync(() => probe(Budget(TimeSpan.FromSeconds(15)))) &&
                Remaining() > TimeSpan.Zero &&
                await HealthCheckRunner.RunAsync(() => isActive(Budget(TimeSpan.FromSeconds(10)))) &&
                Remaining() > TimeSpan.Zero)
                return true;

            var remaining = Remaining();
            if (remaining <= TimeSpan.Zero)
                return false;
            await Task.Delay(remaining < retryInterval ? remaining : retryInterval);
        }
        return false;
    }
}