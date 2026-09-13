using Exergism.DeploymentAttestation.Agent;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Exergism.DeploymentAttestation.Agent.AgentConstants;

namespace Exergism.DeploymentAttestation.Agent.Tests;

[TestClass]
public sealed class HealthAndStatusTests
{
    [TestMethod]
    public void AgentHealthStorePersistsLifecycleAndAttestationDelivery()
    {
        using var environment = TestEnvironment.Create(withAttestationEndpoint: true);
        var store = new AgentHealthStore(environment.Config);
        store.BeginCycle(AgentAction.Run);
        store.RecordAttestation(true);
        store.CompleteSuccess();

        var state = store.TryRead();
        Assert.IsNotNull(state);
        Assert.AreEqual(HEALTH_STATE_IDLE, state.State);
        Assert.AreEqual(ACTION_RUN, state.Action);
        Assert.IsNotNull(state.LastSuccessAt);
        Assert.IsTrue(state.LastAttestationDelivered);
        Assert.IsNull(state.LastError);
    }

    [TestMethod]
    public void AgentHealthStorePersistsFailureWithoutErasingPriorSuccess()
    {
        using var environment = TestEnvironment.Create();
        var store = new AgentHealthStore(environment.Config);
        store.BeginCycle(AgentAction.Run);
        store.CompleteSuccess();
        var success = store.TryRead();

        store.BeginCycle(AgentAction.Update);
        store.CompleteFailure("boom");
        var failed = store.TryRead();

        Assert.IsNotNull(success);
        Assert.IsNotNull(failed);
        Assert.AreEqual(success.LastSuccessAt, failed.LastSuccessAt);
        Assert.AreEqual(HEALTH_STATE_ERROR, failed.State);
        Assert.AreEqual("boom", failed.LastError);
    }

    [TestMethod]
    public void FreshnessRejectsFutureAndExpiredObservations()
    {
        var now = DateTimeOffset.Parse("2026-09-13T10:00:00Z");
        Assert.IsTrue(AgentSelfHealthService.IsRecent("2026-09-13T09:50:00Z", TimeSpan.FromMinutes(15), now));
        Assert.IsFalse(AgentSelfHealthService.IsRecent("2026-09-13T09:40:00Z", TimeSpan.FromMinutes(15), now));
        Assert.IsFalse(AgentSelfHealthService.IsRecent("2026-09-13T10:01:00Z", TimeSpan.FromMinutes(15), now));
    }

    [TestMethod]
    public void DeploymentStatusDistinguishesHealthyDegradedAndUnhealthy()
    {
        var checks = new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            [CHECK_SYSTEMD] = true,
            [CHECK_LOCAL_HTTP] = true,
            [CHECK_PUBLIC_HTTPS] = true,
            [CHECK_RELEASE_REVISION] = true,
            [CHECK_SERVICE_SMOKE] = true,
            [CHECK_ARTIFACT_FENCE] = true,
            [CHECK_RUNTIME_PROCESS] = true,
            [CHECK_SOURCE_TREE] = true,
            [CHECK_STATE_INTEGRITY] = true,
            [CHECK_RUNTIME_DIGEST] = true,
            [CHECK_RUNTIME_PRESENT] = true
        };

        Assert.AreEqual(STATUS_HEALTHY, DeploymentAgent.StatusFromChecks(checks));
        checks[CHECK_PUBLIC_HTTPS] = false;
        Assert.AreEqual(STATUS_DEGRADED, DeploymentAgent.StatusFromChecks(checks));
        checks[CHECK_SYSTEMD] = false;
        Assert.AreEqual(STATUS_UNHEALTHY, DeploymentAgent.StatusFromChecks(checks));
    }
}
