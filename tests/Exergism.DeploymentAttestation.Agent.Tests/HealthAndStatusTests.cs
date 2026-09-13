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

    [TestMethod]
    public void SelfHealthStatusTreatsActiveJournalAsDegradedAndAbandonedJournalAsUnhealthy()
    {
        var checks = new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            [SELF_CHECK_STATE_FILE] = true,
            [SELF_CHECK_VERSION] = true,
            [SELF_CHECK_RECENT_SUCCESS] = true,
            [SELF_CHECK_CYCLE_NOT_STUCK] = true,
            [SELF_CHECK_TRANSACTION_CLEAR] = false,
            [SELF_CHECK_TIMER_ACTIVE] = true,
            [SELF_CHECK_TIMER_ENABLED] = true,
            [SELF_CHECK_ATTESTATION_DELIVERY] = true
        };

        var running = new AgentHealthState(
            AGENT_VERSION,
            "id.exergism.org",
            HEALTH_STATE_RUNNING,
            ACTION_RUN,
            "2026-09-13T10:00:00Z",
            null,
            "2026-09-13T09:59:00Z",
            "2026-09-13T09:59:00Z",
            true,
            null);
        Assert.AreEqual(
            STATUS_DEGRADED,
            AgentSelfHealthService.StatusFromChecks(checks, running));

        var idle = running with { State = HEALTH_STATE_IDLE };
        Assert.AreEqual(
            STATUS_UNHEALTHY,
            AgentSelfHealthService.StatusFromChecks(checks, idle));

        checks[SELF_CHECK_TRANSACTION_CLEAR] = true;
        Assert.AreEqual(
            STATUS_HEALTHY,
            AgentSelfHealthService.StatusFromChecks(checks, idle));
    }

    [TestMethod]
    public void SelfHealthStateRejectsWrongServiceIdentity()
    {
        using var environment = TestEnvironment.Create();
        File.WriteAllText(
            environment.Config.AgentHealthFile,
            """
            {
              "schema_version": "0.1",
              "agent_version": "0.2.0-aot-pre2",
              "service": "other.example",
              "state": "idle",
              "action": null,
              "cycle_started_at": null,
              "last_completed_at": null,
              "last_success_at": null,
              "last_attestation_at": null,
              "last_attestation_delivered": null,
              "last_error": null
            }
            """);

        TestAssert.Throws<AgentException>(
            () => new AgentHealthStore(environment.Config).TryRead());
    }


    [TestMethod]
    public void NewCycleRepairsCorruptedSelfHealthState()
    {
        using var environment = TestEnvironment.Create();
        File.WriteAllText(environment.Config.AgentHealthFile, "{not-json");

        var store = new AgentHealthStore(environment.Config);
        store.BeginCycle(AgentAction.Run);

        var state = store.TryRead();
        Assert.IsNotNull(state);
        Assert.AreEqual(HEALTH_STATE_RUNNING, state.State);
        Assert.AreEqual(ACTION_RUN, state.Action);
    }


    [TestMethod]
    public void FailedAttestationCycleDoesNotAdvanceLastSuccess()
    {
        using var environment = TestEnvironment.Create();
        var store = new AgentHealthStore(environment.Config);

        store.BeginCycle(AgentAction.Run);
        store.CompleteSuccess();
        var before = store.TryRead();
        Assert.IsNotNull(before);

        store.BeginCycle(AgentAction.Attest);
        AgentCycleLifecycle.Complete(
            store,
            AgentExecutionResult.FromAttestation(AttestationResult.FAILED));

        var after = store.TryRead();
        Assert.IsNotNull(after);
        Assert.AreEqual(before.LastSuccessAt, after.LastSuccessAt);
        Assert.AreEqual(HEALTH_STATE_ERROR, after.State);
        Assert.AreEqual("Attestation could not be produced", after.LastError);
    }

    [TestMethod]
    public void ProducedUnhealthyObservationIsStillSuccessfulAgentCycle()
    {
        using var environment = TestEnvironment.Create();
        var store = new AgentHealthStore(environment.Config);

        store.BeginCycle(AgentAction.Attest);
        var result = AgentExecutionResult.FromAttestation(AttestationResult.UNHEALTHY);
        AgentCycleLifecycle.Complete(store, result);

        var state = store.TryRead();
        Assert.IsNotNull(state);
        Assert.AreEqual(1, result.ExitCode);
        Assert.IsTrue(result.CycleSucceeded);
        Assert.AreEqual(HEALTH_STATE_IDLE, state.State);
        Assert.IsNotNull(state.LastSuccessAt);
        Assert.IsNull(state.LastError);
    }

}
