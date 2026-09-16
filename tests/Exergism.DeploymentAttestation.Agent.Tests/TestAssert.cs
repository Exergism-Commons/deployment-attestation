using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Exergism.DeploymentAttestation.Agent.Tests;

internal static class TestAssert
{
    internal static void Throws<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        catch (Exception exception)
        {
            Assert.Fail(
                $"Expected {typeof(TException).Name}, got {exception.GetType().Name}: {exception.Message}");
        }

        Assert.Fail($"Expected {typeof(TException).Name}");
    }

    internal static async Task ThrowsAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }
        catch (Exception exception)
        {
            Assert.Fail(
                $"Expected {typeof(TException).Name}, got {exception.GetType().Name}: {exception.Message}");
        }

        Assert.Fail($"Expected {typeof(TException).Name}");
    }
}
