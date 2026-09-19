using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using static Exergism.DeploymentAttestation.Agent.AgentConstants;

namespace Exergism.DeploymentAttestation.Agent;

internal sealed class DeploymentAgent
{
    private readonly AgentConfig _config;
    private readonly HttpClient _getHttp;
    private readonly HttpClient _attestationHttp;
    private readonly SystemdController _systemd;
    private readonly RuntimeInspector _runtime;
    private readonly GitRepository _git;
    private readonly AgentHealthStore _health;

    public DeploymentAgent(
        AgentConfig config,
        HttpClient getHttp,
        HttpClient attestationHttp,
        AgentHealthStore health)
    {
        _config = config;
        _getHttp = getHttp;
        _attestationHttp = attestationHttp;
        _health = health;
        _systemd = new SystemdController(config);
        _runtime = new RuntimeInspector(config, _systemd);
        _git = new GitRepository(config);
    }

    public async Task<AgentExecutionResult> ExecuteAsync(AgentAction action)
    {
        if (action == AgentAction.Recover)
        {
            await RecoverTransactionAsync();
            return AgentExecutionResult.SUCCESS;
        }

        if (action == AgentAction.Run || action == AgentAction.Update)
        {
            await BootstrapStateAsync();
            await RecoverTransactionAsync();
            await UpdateReleaseAsync();
            return AgentExecutionResult.SUCCESS;
        }

        if (action == AgentAction.Attest)
        {
            await BootstrapStateAsync();
            await RecoverTransactionAsync();
            return AgentExecutionResult.FromAttestation(await AttestAsync());
        }

        throw new AgentException("Unsupported agent action");
    }

    private async Task BootstrapStateAsync()
    {
        if (File.Exists(_config.CurrentStateFile))
            return;

        var activeState = await _systemd.ShowAsync(SYSTEMD_ACTIVE_STATE);
        var pid = await TryMainPidAsync();

        if (pid is not null)
            await _runtime.VerifyLiveRuntimeInvariantsAsync();

        switch (activeState)
        {
            case SYSTEMD_STATE_ACTIVE:
                break;
            case SYSTEMD_STATE_INACTIVE:
            case SYSTEMD_STATE_FAILED:
                if (pid is not null)
                    throw new AgentException("Bootstrap refused: non-active service still has a live MainPID");
                await _systemd.StartAsync();
                if (!await _systemd.IsActiveAsync())
                    throw new AgentException("Bootstrap refused: target service did not become active");
                await _runtime.VerifyLiveRuntimeInvariantsAsync();
                break;
            default:
                throw new AgentException($"Bootstrap refused: target service is in transitional state {activeState}");
        }

        if (!await ProbeAsync(_config.LocalUrl, TimeSpan.FromSeconds(15)))
            throw new AgentException("Bootstrap refused: local health check failed");
        if (!await _systemd.RunSmokeAsync())
            throw new AgentException("Bootstrap refused: semantic smoke check failed");
        await _runtime.VerifyLiveRuntimeInvariantsAsync();

        string commit;
        if (File.Exists(_config.SourceRevisionFile))
            commit = (await File.ReadAllTextAsync(_config.SourceRevisionFile)).Trim();
        else
            commit = await _git.HeadAsync();

        RequireCommit(commit, "Cannot bootstrap deployment revision");
        if (await _git.HeadAsync() != commit)
            throw new AgentException("Bootstrap refused: checkout differs");

        await _git.VerifySourceTreeExactAsync(commit);
        var binary = Durability.Sha256(_config.AppBinary);
        RequireDigest(binary, "Bootstrap refused: runtime digest invalid");
        await _git.FsyncCheckoutAsync(commit);
        VerifyRuntimeExact(binary);
        await _runtime.VerifyLiveRuntimeInvariantsAsync();
        await _git.VerifySourceTreeExactAsync(commit);
        if (Durability.Sha256(_config.AppBinary) != binary)
            throw new AgentException("Bootstrap refused: runtime changed before state commit");

        WriteCurrentState(new CurrentState(commit, binary, null, _config.ReleaseTag));
    }

    private async Task<bool> VerifyBaselineAsync()
    {
        try
        {
            if (await _systemd.ShowAsync(SYSTEMD_LOAD_STATE) != SYSTEMD_STATE_LOADED)
                return false;
            var active = await _systemd.ShowAsync(SYSTEMD_ACTIVE_STATE);
            switch (active)
            {
                case SYSTEMD_STATE_ACTIVE:
                    await _runtime.VerifyLiveRuntimeInvariantsAsync();
                    break;
                case SYSTEMD_STATE_INACTIVE:
                case SYSTEMD_STATE_FAILED:
                    break;
                default:
                    return false;
            }

            var state = Protocol.ReadCurrentState(_config.CurrentStateFile);
            if (await _git.HeadAsync() != state.SourceCommit)
                return false;
            VerifyRuntimeExact(state.BinarySha256);
            await _git.VerifySourceTreeExactAsync(state.SourceCommit);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task ArtifactIntegrityAsync(string commit, string binary)
    {
        await _git.VerifySourceTreeExactAsync(commit);
        await _git.FsyncCheckoutAsync(commit);
        VerifyRuntimeExact(binary);
    }

    private async Task PostStartIntegrityAsync(string commit, string binary)
    {
        var before = await _runtime.LiveRuntimeSnapshotSha256Async();
        if (before != binary)
            throw new AgentException("Running runtime digest differs before post-start integrity traversal");

        await ArtifactIntegrityAsync(commit, binary);

        var after = await _runtime.LiveRuntimeSnapshotSha256Async();
        if (after != binary)
            throw new AgentException("Running runtime digest differs after post-start integrity traversal");
    }

    private async Task FinalizeQuiescentAsync(string commit, string binary, string? manifest)
    {
        await _systemd.StopQuiescentAsync();
        await ArtifactIntegrityAsync(commit, binary);
        WriteCurrentState(new CurrentState(commit, binary, manifest, _config.ReleaseTag));
        await ArtifactIntegrityAsync(commit, binary);
    }

    private async Task ResumeCommittedServiceAsync(string commit, string binary)
    {
        await _systemd.StartAsync();
        if (!await _systemd.IsActiveAsync())
            throw new AgentException("Committed service did not become active");
        if (!await ProbeAsync(_config.LocalUrl, TimeSpan.FromSeconds(15)))
            throw new AgentException("Committed service failed local health check");
        if (!await _systemd.RunSmokeAsync())
            throw new AgentException("Committed service failed semantic smoke check");
        await PostStartIntegrityAsync(commit, binary);
    }

    private async Task RollbackTransactionAsync()
    {
        if (!File.Exists(_config.TransactionFile))
            return;

        var tx = Protocol.ReadTransaction(_config.TransactionFile);
        var backupRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_config.BackupDirectory));
        var backup = Path.GetFullPath(tx.BackupBinary);
        if (!IsUnder(backup, backupRoot) || !File.Exists(backup))
            throw new AgentException("Rollback backup is outside the backup directory or missing");
        if (Durability.Sha256(backup) != tx.OldBinarySha256)
            throw new AgentException("Rollback backup digest mismatch");

        Warn($"Recovering transaction to {tx.OldSourceCommit}");
        await _systemd.StopQuiescentAsync();
        await _git.ReconcileStaleGitLocksAsync(tx.OldSourceCommit);
        await _git.SwitchSourceAsync(tx.OldSourceCommit, fetchFirst: false);

        var rollbackPath = _config.AppBinary + ".rollback";
        File.Copy(backup, rollbackPath, overwrite: true);
        File.SetUnixFileMode(
            rollbackPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        if (Durability.Sha256(rollbackPath) != tx.OldBinarySha256)
            throw new AgentException("Rollback staging digest mismatch");
        Durability.FsyncFileAndParent(rollbackPath);
        File.Move(rollbackPath, _config.AppBinary, overwrite: true);
        Durability.FsyncDirectory(Path.GetDirectoryName(_config.AppBinary)!);
        VerifyRuntimeExact(tx.OldBinarySha256);

        try
        {
            await _systemd.StartAsync();
            if (!await _systemd.IsActiveAsync() ||
                !await ProbeAsync(_config.LocalUrl, TimeSpan.FromSeconds(15)) ||
                !await _systemd.RunSmokeAsync())
                throw new AgentException("Rollback service health validation failed");
            await PostStartIntegrityAsync(tx.OldSourceCommit, tx.OldBinarySha256);
        }
        catch
        {
            await _systemd.StopAsync();
            throw;
        }

        try
        {
            await FinalizeQuiescentAsync(tx.OldSourceCommit, tx.OldBinarySha256, tx.OldReleaseManifestSha256);
        }
        catch
        {
            await _systemd.StopAsync();
            throw;
        }

        MarkTransactionCommitted(tx.OldSourceCommit, tx.OldBinarySha256, tx.OldReleaseManifestSha256);
        try
        {
            await ResumeCommittedServiceAsync(tx.OldSourceCommit, tx.OldBinarySha256);
        }
        catch
        {
            await _systemd.StopAsync();
            throw;
        }

        Durability.DurableDelete(_config.TransactionFile);
    }

    private async Task RecoverTransactionAsync()
    {
        if (!File.Exists(_config.TransactionFile))
            return;

        var tx = Protocol.ReadTransaction(_config.TransactionFile);
        if (tx.Phase == PHASE_COMMITTED)
        {
            var committedCandidateVerified = false;
            try
            {
                var state = Protocol.ReadCurrentState(_config.CurrentStateFile);
                if (CommittedTransactionMatchesState(
                        state,
                        tx,
                        _config.ReleaseTag) &&
                    await VerifyBaselineAsync())
                {
                    await ResumeCommittedServiceAsync(tx.NewSourceCommit, tx.NewBinarySha256);
                    committedCandidateVerified = true;
                }
            }
            catch
            {
                // Verification failure falls through to the durable last-known-good rollback pair.
            }

            if (committedCandidateVerified)
            {
                CommittedTransactionJournal.Cleanup(
                    () => Durability.DurableDelete(_config.TransactionFile));
                return;
            }

            Warn("Committed candidate failed final service verification; restoring last-known-good pair");
        }

        try
        {
            await RollbackTransactionAsync();
        }
        catch (Exception ex)
        {
            throw new AgentException("Interrupted/invalid transaction could not be safely recovered; refusing a new baseline", ex);
        }
    }

    private async Task UpdateReleaseAsync()
    {
        await RecoverTransactionAsync();
        if (!await VerifyBaselineAsync())
        {
            await TryAttestAsync();
            throw new AgentException("Current source/runtime pair differs from durable state or source tree is not byte-exact");
        }

        using var temp = TempDirectory.Create();
        var release = await LoadReleaseSnapshotAsync(temp.Path);
        var current = Protocol.ReadCurrentState(_config.CurrentStateFile);

        if (current.SourceCommit == release.SourceCommit && current.BinarySha256 == release.AssetSha256)
        {
            if (!CommittedReleaseMetadataMatches(current, release, _config.ReleaseTag))
            {
                await ArtifactIntegrityAsync(current.SourceCommit, current.BinarySha256);
                WriteCurrentState(new CurrentState(
                    current.SourceCommit,
                    current.BinarySha256,
                    release.ManifestSha256,
                    _config.ReleaseTag));
                await ArtifactIntegrityAsync(current.SourceCommit, current.BinarySha256);
            }

            Log($"Already running exact source/runtime pair {current.SourceCommit}");
            await TryAttestAsync();
            return;
        }

        var runtimePath = Path.Combine(temp.Path, release.AssetName);
        await DownloadAsync(release.AssetName, runtimePath);
        if (Durability.Sha256(runtimePath) != release.AssetSha256)
            throw new AgentException("Runtime does not match manifest digest");

        var backup = Path.Combine(
            _config.BackupDirectory,
            $"runtime-{current.SourceCommit}-{current.BinarySha256[..16]}");
        File.Copy(_config.AppBinary, backup, overwrite: true);
        File.SetUnixFileMode(
            backup,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        if (Durability.Sha256(backup) != current.BinarySha256)
            throw new AgentException("Backup digest mismatch");
        Durability.FsyncFileAndParent(backup);

        WriteTransaction(new DeploymentTransaction(
            PHASE_ACTIVATING,
            current.SourceCommit,
            current.BinarySha256,
            current.ReleaseManifestSha256,
            backup,
            release.SourceCommit,
            release.AssetSha256,
            release.ManifestSha256));

        try
        {
            await _systemd.StopQuiescentAsync();
        }
        catch (Exception ex)
        {
            await RollbackAfterFailureAsync("Stop/quiescence failed", ex);
            return;
        }

        try
        {
            await _git.SwitchSourceAsync(release.SourceCommit, fetchFirst: true);
        }
        catch (Exception ex)
        {
            await RollbackAfterFailureAsync("Source switch/durability failed", ex);
            return;
        }

        try
        {
            var staging = _config.AppBinary + ".new";
            File.Copy(runtimePath, staging, overwrite: true);
            File.SetUnixFileMode(
                staging,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            if (Durability.Sha256(staging) != release.AssetSha256)
                throw new AgentException("Runtime staging digest mismatch");
            Durability.FsyncFileAndParent(staging);
            File.Move(staging, _config.AppBinary, overwrite: true);
            Durability.FsyncDirectory(Path.GetDirectoryName(_config.AppBinary)!);
            VerifyRuntimeExact(release.AssetSha256);
        }
        catch (Exception ex)
        {
            await RollbackAfterFailureAsync("Runtime switch/durability failed", ex);
            return;
        }

        try
        {
            await _systemd.StartAsync();
            if (!await _systemd.IsActiveAsync() ||
                !await ProbeAsync(_config.LocalUrl, TimeSpan.FromSeconds(15)) ||
                !await _systemd.RunSmokeAsync())
                throw new AgentException("Candidate failed health validation");
            await PostStartIntegrityAsync(release.SourceCommit, release.AssetSha256);
        }
        catch (Exception ex)
        {
            await RollbackAfterFailureAsync("Health/post-start integrity failed", ex);
            return;
        }

        try
        {
            await FinalizeQuiescentAsync(release.SourceCommit, release.AssetSha256, release.ManifestSha256);
        }
        catch (Exception ex)
        {
            await RollbackAfterFailureAsync("Quiescent state commit failed", ex);
            return;
        }

        try
        {
            MarkTransactionCommitted(release.SourceCommit, release.AssetSha256, release.ManifestSha256);
        }
        catch (Exception ex)
        {
            await RollbackAfterFailureAsync("Could not persist committed recovery phase", ex);
            return;
        }

        try
        {
            await ResumeCommittedServiceAsync(release.SourceCommit, release.AssetSha256);
        }
        catch (Exception ex)
        {
            await RollbackAfterFailureAsync("Final service verification failed", ex);
            return;
        }

        Durability.DurableDelete(_config.TransactionFile);
        Log($"Activated release source {release.SourceCommit}");
        await TryAttestAsync();
    }

    private Task RollbackAfterFailureAsync(string context, Exception original)
        => DeploymentFailureFlow.ThrowAfterRollbackAndReportAsync(
            RollbackTransactionAsync,
            TryAttestAsync,
            context,
            original);

    private async Task<AttestationResult> AttestAsync()
    {
        ReleaseSnapshot release;
        try
        {
            using var temp = TempDirectory.Create();
            release = await LoadReleaseSnapshotAsync(temp.Path);
        }
        catch (Exception ex)
        {
            Warn($"Could not load release manifest: {ex.Message}");
            _health.RecordRemoteAttestation(
                delivered: false,
                AttestationReceiverIdentity.FromConfiguredEndpoint(_config.AttestationEndpoint));
            return AttestationResult.FAILED;
        }

        CurrentState state;
        try
        {
            state = Protocol.ReadCurrentState(_config.CurrentStateFile);
        }
        catch (Exception ex)
        {
            Warn($"Could not read current state: {ex.Message}");
            _health.RecordRemoteAttestation(
                delivered: false,
                AttestationReceiverIdentity.FromConfiguredEndpoint(_config.AttestationEndpoint));
            return AttestationResult.FAILED;
        }

        var snapshot = await CollectChecksAsync(release, state);
        var status = StatusFromChecks(snapshot.Checks);
        var observedAt = DateTimeOffset.UtcNow.ToString(
            RFC3339_UTC_FORMAT,
            System.Globalization.CultureInfo.InvariantCulture);
        if (snapshot.ObservedSourceCommit is null)
        {
            Warn("Could not determine the checkout's observed HEAD for attestation");
            _health.RecordRemoteAttestation(
                delivered: false,
                AttestationReceiverIdentity.FromConfiguredEndpoint(_config.AttestationEndpoint));
            return AttestationResult.FAILED;
        }

        var body = Protocol.BuildAttestation(
            _config,
            snapshot.ObservedSourceCommit,
            release.SourceCommit,
            status,
            snapshot.Checks,
            release.ManifestSha256,
            snapshot.RuntimeSha256,
            release.AssetSha256,
            observedAt,
            AGENT_VERSION);

        await SendAttestationAsync(body);
        return status == STATUS_HEALTHY
            ? AttestationResult.HEALTHY
            : AttestationResult.UNHEALTHY;
    }

    private async Task<CheckSnapshot> CollectChecksAsync(ReleaseSnapshot release, CurrentState state)
    {
        var checks = new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            [CHECK_SYSTEMD] = false,
            [CHECK_LOCAL_HTTP] = false,
            [CHECK_PUBLIC_HTTPS] = !_config.CheckPublic,
            [CHECK_RELEASE_REVISION] = false,
            [CHECK_SERVICE_SMOKE] = string.IsNullOrEmpty(_config.SmokeScript),
            [CHECK_ARTIFACT_FENCE] = false,
            [CHECK_RUNTIME_PROCESS] = false,
            [CHECK_SOURCE_TREE] = false,
            [CHECK_STATE_INTEGRITY] = false,
            [CHECK_RUNTIME_DIGEST] = false,
            [CHECK_RUNTIME_PRESENT] = false
        };

        if (!string.IsNullOrEmpty(_config.SmokeScript))
            checks[CHECK_SERVICE_SMOKE] = await _systemd.RunSmokeAsync();

        checks[CHECK_SYSTEMD] = await _systemd.IsActiveAsync();

        string? actual = null;
        try
        {
            actual = await _runtime.LiveRuntimeSnapshotSha256Async();
            checks[CHECK_ARTIFACT_FENCE] = true;
            checks[CHECK_RUNTIME_PROCESS] = true;
        }
        catch
        {
            try { actual = await _runtime.RunningRuntimeSha256Async(); } catch { }
        }

        checks[CHECK_LOCAL_HTTP] = await ProbeAsync(_config.LocalUrl, TimeSpan.FromSeconds(10));
        if (_config.CheckPublic)
            checks[CHECK_PUBLIC_HTTPS] = await ProbeAsync(_config.PublicUrl, TimeSpan.FromSeconds(15));

        var diskActual = HealthCheckRunner.Try(
            () => Durability.ReadRegularFileNoFollow(
                _config.AppBinary,
                "Runtime binary").Snapshot.Sha256);
        checks[CHECK_RUNTIME_PRESENT] = IsDigest(actual);

        string? observedSourceCommit = null;
        try
        {
            observedSourceCommit = await _git.HeadAsync();
            if (IsCommit(observedSourceCommit))
            {
                checks[CHECK_RELEASE_REVISION] =
                    observedSourceCommit == release.SourceCommit;
            }

            if (IsCommit(observedSourceCommit) && observedSourceCommit == state.SourceCommit)
            {
                await _git.VerifySourceTreeExactAsync(state.SourceCommit);
                checks[CHECK_SOURCE_TREE] = true;
            }
            else if (!IsCommit(observedSourceCommit))
            {
                observedSourceCommit = null;
            }
        }
        catch
        {
            observedSourceCommit = null;
        }

        checks[CHECK_STATE_INTEGRITY] =
            checks[CHECK_RUNTIME_PROCESS] &&
            actual == state.BinarySha256 &&
            diskActual == state.BinarySha256 &&
            CommittedReleaseMetadataMatches(state, release, _config.ReleaseTag);
        checks[CHECK_RUNTIME_DIGEST] =
            checks[CHECK_RUNTIME_PROCESS] &&
            actual == release.AssetSha256 &&
            diskActual == release.AssetSha256;

        if (checks[CHECK_RUNTIME_PROCESS])
        {
            try
            {
                var finalActual = await _runtime.LiveRuntimeSnapshotSha256Async();
                if (finalActual != actual)
                    InvalidateRuntimeBoundChecks(checks, ref actual, finalActual);
            }
            catch
            {
                InvalidateRuntimeBoundChecks(checks, ref actual, null);
            }
        }

        try
        {
            var finalObservedSourceCommit = await _git.HeadAsync();
            if (!IsCommit(finalObservedSourceCommit))
            {
                observedSourceCommit = null;
                checks[CHECK_RELEASE_REVISION] = false;
                checks[CHECK_SOURCE_TREE] = false;
                checks[CHECK_STATE_INTEGRITY] = false;
            }
            else
            {
                observedSourceCommit = finalObservedSourceCommit;
                checks[CHECK_RELEASE_REVISION] =
                    observedSourceCommit == release.SourceCommit;

                var finalSourceTreeExact = false;
                if (observedSourceCommit == state.SourceCommit)
                {
                    try
                    {
                        await _git.VerifySourceTreeExactAsync(state.SourceCommit);
                        finalSourceTreeExact = true;
                    }
                    catch
                    {
                        // Preserve the observed HEAD while failing the exact-tree
                        // and state-integrity claims closed.
                    }
                }

                checks[CHECK_SOURCE_TREE] = finalSourceTreeExact;
                if (!finalSourceTreeExact)
                    checks[CHECK_STATE_INTEGRITY] = false;
            }
        }
        catch
        {
            observedSourceCommit = null;
            checks[CHECK_RELEASE_REVISION] = false;
            checks[CHECK_SOURCE_TREE] = false;
            checks[CHECK_STATE_INTEGRITY] = false;
        }

        return new CheckSnapshot(observedSourceCommit, actual, checks);
    }

    internal static bool CommittedTransactionMatchesState(
        CurrentState state,
        DeploymentTransaction transaction,
        string configuredReleaseTag)
        => transaction.Phase == PHASE_COMMITTED &&
           state.SourceCommit == transaction.NewSourceCommit &&
           state.BinarySha256 == transaction.NewBinarySha256 &&
           state.ReleaseManifestSha256 == transaction.NewReleaseManifestSha256 &&
           state.ReleaseTag == configuredReleaseTag;

    internal static bool CommittedReleaseMetadataMatches(
        CurrentState state,
        ReleaseSnapshot release,
        string configuredReleaseTag)
        => state.ReleaseManifestSha256 == release.ManifestSha256 &&
           state.ReleaseTag == configuredReleaseTag;

    private static void InvalidateRuntimeBoundChecks(
        Dictionary<string, bool> checks,
        ref string? actual,
        string? finalActual)
    {
        actual = finalActual;
        checks[CHECK_ARTIFACT_FENCE] = false;
        checks[CHECK_RUNTIME_PROCESS] = false;
        checks[CHECK_STATE_INTEGRITY] = false;
        checks[CHECK_RUNTIME_DIGEST] = false;
        checks[CHECK_RUNTIME_PRESENT] = IsDigest(actual);
    }

    internal static string StatusFromChecks(IReadOnlyDictionary<string, bool> checks)
    {
        var mandatory = new[]
        {
            CHECK_SYSTEMD,
            CHECK_LOCAL_HTTP,
            CHECK_RELEASE_REVISION,
            CHECK_SERVICE_SMOKE,
            CHECK_ARTIFACT_FENCE,
            CHECK_RUNTIME_PROCESS,
            CHECK_SOURCE_TREE,
            CHECK_STATE_INTEGRITY,
            CHECK_RUNTIME_DIGEST,
            CHECK_RUNTIME_PRESENT
        };
        if (mandatory.Any(key => !checks.TryGetValue(key, out var value) || !value))
            return STATUS_UNHEALTHY;
        return checks.TryGetValue(CHECK_PUBLIC_HTTPS, out var publicOk) && !publicOk
            ? STATUS_DEGRADED
            : STATUS_HEALTHY;
    }

    private async Task SendAttestationAsync(byte[] body)
    {
        if (string.IsNullOrEmpty(_config.AttestationEndpoint))
        {
            Console.WriteLine(Encoding.UTF8.GetString(body));
            _health.RecordLocalAttestation();
            return;
        }

        if (!Uri.TryCreate(_config.AttestationEndpoint, UriKind.Absolute, out var endpoint))
            throw new AgentException("Invalid attestation endpoint");

        var receiverIdentity = AttestationReceiverIdentity.FromUri(endpoint);
        if (string.IsNullOrEmpty(_config.HmacSecretFile))
        {
            _health.RecordRemoteAttestation(delivered: false, receiverIdentity);
            throw new AgentException("HMAC secret not configured");
        }

        byte[] key;
        try
        {
            key = TrimAsciiWhitespace(
                Durability.ReadTrustedRegularFileBytes(
                    _config.HmacSecretFile,
                    ENV_HMAC_SECRET_FILE));
        }
        catch (Exception ex)
        {
            _health.RecordRemoteAttestation(delivered: false, receiverIdentity);
            throw new AgentException("HMAC secret not readable", ex);
        }

        if (key.Length == 0)
        {
            _health.RecordRemoteAttestation(delivered: false, receiverIdentity);
            throw new AgentException("HMAC secret is empty");
        }
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        var bodyText = Encoding.UTF8.GetString(body);
        var signed = Encoding.UTF8.GetBytes(timestamp + "." + bodyText);
        var signature = Convert.ToHexStringLower(HMACSHA256.HashData(key, signed));
        var observationId = Protocol.ReadObservationId(body);

        Exception? last = null;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
                request.Content = new ByteArrayContent(body);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                request.Headers.Add(HEADER_TIMESTAMP, timestamp);
                request.Headers.Add(HEADER_SIGNATURE, $"sha256={signature}");
                request.Headers.Add(HEADER_IDEMPOTENCY_KEY, observationId);

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                using var response = await _attestationHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                if (response.IsSuccessStatusCode)
                {
                    _health.RecordRemoteAttestation(
                        delivered: true,
                        receiverIdentity);
                    return;
                }
                last = new AgentException($"Attestation receiver returned {(int)response.StatusCode}");
            }
            catch (Exception ex)
            {
                last = ex;
            }

            if (attempt < 3)
                await Task.Delay(TimeSpan.FromSeconds(2));
        }

        _health.RecordRemoteAttestation(
            delivered: false,
            receiverIdentity);
        throw new AgentException("Attestation delivery failed after retries", last);
    }

    private async Task<ReleaseSnapshot> LoadReleaseSnapshotAsync(string directory)
    {
        var manifestPath = Path.Combine(directory, _config.ReleaseManifestName);
        await DownloadAsync(_config.ReleaseManifestName, manifestPath);
        var bytes = await File.ReadAllBytesAsync(manifestPath);
        return Protocol.ParseReleaseManifest(bytes, _config.Repository, _config.ReleaseTag, Architecture());
    }

    private async Task DownloadAsync(string name, string destination)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                using var cts = new CancellationTokenSource(_config.DownloadTimeout);
                using var response = await _getHttp.GetAsync(
                    _config.ReleaseUri(name),
                    HttpCompletionOption.ResponseHeadersRead,
                    cts.Token);
                response.EnsureSuccessStatusCode();

                await using (var stream = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await response.Content.CopyToAsync(stream, cts.Token);
                    await stream.FlushAsync(cts.Token);
                    stream.Flush(flushToDisk: true);
                }
                return;
            }
            catch (Exception ex)
            {
                last = ex;
                try { File.Delete(destination); } catch { }
                if (attempt < 5)
                    await Task.Delay(TimeSpan.FromSeconds(2));
            }
        }

        throw new AgentException($"Download failed: {name}", last);
    }

    private async Task<bool> ProbeAsync(Uri uri, TimeSpan timeout)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeout);
            using var response = await _getHttp.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private void VerifyRuntimeExact(string expected)
    {
        var verified = Durability.VerifyAndFsyncRegularFileNoFollow(
            _config.AppBinary,
            "Runtime binary",
            expected,
            requireExecutable: true);

        var parent = Path.GetDirectoryName(_config.AppBinary)
            ?? throw new AgentException("Runtime binary has no parent directory");
        Durability.FsyncRequiredDirectory(parent, parent);

        var final = Durability.ReadRegularFileNoFollow(_config.AppBinary, "Runtime binary");
        if (final.Snapshot != verified ||
            !string.Equals(final.Snapshot.Sha256, expected, StringComparison.Ordinal))
            throw new AgentException("Runtime snapshot changed across durability barrier");

        if ((final.Mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) == 0)
            throw new AgentException("Runtime binary lost executable mode across durability barrier");
    }

    private void WriteCurrentState(CurrentState state)
    {
        Durability.AtomicWrite(
            _config.CurrentStateFile,
            Protocol.WriteCurrentState(state),
            UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Durability.AtomicWriteText(
            _config.SourceRevisionFile,
            state.SourceCommit + "\n",
            UnixFileMode.UserRead | UnixFileMode.UserWrite |
            UnixFileMode.GroupRead | UnixFileMode.OtherRead);
    }

    private void WriteTransaction(DeploymentTransaction transaction)
        => Durability.AtomicWrite(
            _config.TransactionFile,
            Protocol.WriteTransaction(transaction),
            UnixFileMode.UserRead | UnixFileMode.UserWrite);

    private void MarkTransactionCommitted(string commit, string binary, string? manifest)
    {
        var tx = Protocol.ReadTransaction(_config.TransactionFile);
        WriteTransaction(tx with
        {
            Phase = PHASE_COMMITTED,
            NewSourceCommit = commit,
            NewBinarySha256 = binary,
            NewReleaseManifestSha256 = manifest
        });
    }

    private async Task<int?> TryMainPidAsync()
    {
        try { return await _systemd.MainPidAsync(); }
        catch { return null; }
    }

    private async Task TryAttestAsync()
    {
        try { _ = await AttestAsync(); }
        catch (Exception ex) { Warn($"Attestation failed: {ex.Message}"); }
    }

    private static string Architecture()
        => RuntimeInformation.ProcessArchitecture switch
        {
            System.Runtime.InteropServices.Architecture.X64 => "amd64",
            System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
            _ => throw new AgentException($"Unsupported architecture: {RuntimeInformation.ProcessArchitecture}")
        };

    private static byte[] TrimAsciiWhitespace(byte[] bytes)
    {
        var start = 0;
        var end = bytes.Length;
        while (start < end && bytes[start] is 9 or 10 or 13 or 32) start++;
        while (end > start && bytes[end - 1] is 9 or 10 or 13 or 32) end--;
        return bytes[start..end];
    }

    private static bool IsUnder(string child, string parent)
    {
        child = Path.TrimEndingDirectorySeparator(Path.GetFullPath(child));
        parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        return child == parent || child.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static bool IsCommit(string? value)
        => value is { Length: 40 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsDigest(string? value)
        => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void RequireCommit(string value, string message)
    {
        if (value.Length != 40 || !value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
            throw new AgentException(message);
    }

    private static void RequireDigest(string value, string message)
    {
        if (!IsDigest(value))
            throw new AgentException(message);
    }

    private static void Log(string message) => Console.WriteLine($"\n==> {message}");
    private static void Warn(string message) => Console.Error.WriteLine($"WARN: {message}");

    private sealed record CheckSnapshot(
        string? ObservedSourceCommit,
        string? RuntimeSha256,
        Dictionary<string, bool> Checks);
}

internal sealed class TempDirectory : IDisposable
{
    private TempDirectory(string path) => Path = path;
    public string Path { get; }

    public static TempDirectory Create()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"ec-attestation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return new TempDirectory(path);
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch { }
    }
}
