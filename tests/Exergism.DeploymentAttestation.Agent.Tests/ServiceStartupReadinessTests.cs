using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Exergism.DeploymentAttestation.Agent;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Exergism.DeploymentAttestation.Agent.Tests;

[TestClass]
public sealed class ServiceStartupReadinessTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(10);

    [TestMethod]
    public async Task HealthyServiceReturnsWithoutRetryAndRechecksActiveState()
    {
        var activeChecks = 0;
        var probes = 0;
        Assert.IsTrue(await ServiceStartupReadiness.WaitAsync(
            _ => Task.FromResult(++activeChecks > 0),
            _ => Task.FromResult(++probes > 0),
            TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(4)));
        Assert.AreEqual(2, activeChecks);
        Assert.AreEqual(1, probes);
    }

    [TestMethod]
    public async Task InactiveServiceIsRetriedWithoutProbingUntilActive()
    {
        var activeChecks = 0;
        var probes = 0;
        Assert.IsTrue(await ServiceStartupReadiness.WaitAsync(
            _ => Task.FromResult(++activeChecks >= 3),
            _ => { Assert.IsTrue(activeChecks >= 3); probes++; return Task.FromResult(true); },
            TimeSpan.FromSeconds(5), Interval));
        Assert.AreEqual(4, activeChecks);
        Assert.AreEqual(1, probes);
    }

    [TestMethod]
    public async Task UnhealthyHttpIsRetriedUntilSuccess()
    {
        var probes = 0;
        Assert.IsTrue(await ServiceStartupReadiness.WaitAsync(
            _ => Task.FromResult(true), _ => Task.FromResult(++probes == 3),
            TimeSpan.FromSeconds(5), Interval));
        Assert.AreEqual(3, probes);
    }

    [TestMethod]
    public async Task ProbeExceptionsCannotBeAcceptedAsHealthy()
    {
        Assert.IsFalse(await ServiceStartupReadiness.WaitAsync(
            _ => Task.FromResult(true), _ => throw new HttpRequestException("refused"),
            TimeSpan.FromMilliseconds(100), Interval));
    }

    [TestMethod]
    public async Task ServiceExitingAfterHttpSuccessIsRejected()
    {
        var activeChecks = 0;
        var probes = 0;
        Assert.IsFalse(await ServiceStartupReadiness.WaitAsync(
            _ => Task.FromResult(++activeChecks == 1),
            _ => { probes++; return Task.FromResult(true); },
            TimeSpan.FromMilliseconds(100), Interval));
        Assert.AreEqual(1, probes);
        Assert.IsTrue(activeChecks >= 2);
    }

    [TestMethod]
    public async Task FailedChecksRespectTotalDeadlineAndRemainingRequestBudget()
    {
        var timeout = TimeSpan.FromMilliseconds(200);
        var budgets = new List<TimeSpan>();
        var clock = Stopwatch.StartNew();
        Assert.IsFalse(await ServiceStartupReadiness.WaitAsync(
            _ => Task.FromResult(true),
            async budget => { budgets.Add(budget); await Task.Delay(budget); return false; },
            timeout, TimeSpan.FromSeconds(10)));
        Assert.AreEqual(1, budgets.Count);
        Assert.IsTrue(budgets[0] > TimeSpan.Zero && budgets[0] <= timeout);
        Assert.IsTrue(clock.Elapsed >= TimeSpan.FromMilliseconds(180));
        Assert.IsTrue(clock.Elapsed < TimeSpan.FromSeconds(3));
    }

    [TestMethod]
    public async Task LateSuccessCannotPassAfterDeadline()
    {
        Assert.IsFalse(await ServiceStartupReadiness.WaitAsync(
            _ => Task.FromResult(true),
            async _ => { await Task.Delay(100); return true; },
            TimeSpan.FromMilliseconds(30), Interval));
    }

    [TestMethod]
    public async Task ConnectionRefusedBeforeSocketOpensIsRetried()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var address = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/");
        listener.Stop();
        listener = new TcpListener(IPAddress.Loopback, address.Port);
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        var refused = false;
        Task? server = null;
        try
        {
            Assert.IsTrue(await ServiceStartupReadiness.WaitAsync(
                _ => Task.FromResult(true),
                async budget =>
                {
                    using var cancellation = new CancellationTokenSource(budget);
                    try
                    {
                        using var response = await http.GetAsync(address, cancellation.Token);
                        return response.IsSuccessStatusCode;
                    }
                    catch (HttpRequestException) when (!refused)
                    {
                        refused = true;
                        listener.Start();
                        server = Task.Run(async () =>
                        {
                            using var client = await listener.AcceptTcpClientAsync();
                            await using var stream = client.GetStream();
                            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                            while (await reader.ReadLineAsync(deadline.Token) is { Length: > 0 }) { }
                            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                                "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
                        });
                        return false;
                    }
                }, TimeSpan.FromSeconds(5), Interval));
            Assert.IsTrue(refused);
            Assert.IsNotNull(server);
            await server.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            listener.Stop();
        }
    }
}
