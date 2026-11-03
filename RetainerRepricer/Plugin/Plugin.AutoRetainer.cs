using System;
using System.Collections.Generic;
using System.Linq;

using Dalamud.Plugin.Ipc.Exceptions;
using AutoRetainerAPI;
using AutoRetainerAPI.Configuration;

namespace RetainerRepricer;

public unsafe sealed partial class Plugin
{
    private const double AutoRetainerRegistrationRetrySeconds = 5.0d;
    private const double AutoRetainerRecoveryTimeoutSeconds = 10.0d;

    private AutoRetainerApi? _autoRetainerApi;
    private bool _autoRetainerRegistered;
    private bool _autoRetainerHandoffActive;
    private bool _autoRetainerRunStarted;
    private bool _autoRetainerRecoveryActive;
    private DateTime _autoRetainerRegistrationAttemptUtc = DateTime.MinValue;
    private DateTime _autoRetainerHandoffStartedUtc = DateTime.MinValue;
    private bool _autoRetainerSuppressionBeforeHandoff;
    private string _autoRetainerHandoffRetainerName = string.Empty;
    private ulong _autoRetainerHandoffCharacterId;
    private DateTime _autoRetainerRecoveryStartedUtc = DateTime.MinValue;
    private DateTime _autoRetainerLastPreStartDiagnosticUtc = DateTime.MinValue;
    private bool _autoRetainerDiagnosticsEnabled;
    private ulong _autoRetainerCallbackReconciliationCid;
    private string _autoRetainerCallbackReconciliationRetainer = string.Empty;
    private DateTime _autoRetainerCallbackReconciliationStartedUtc = DateTime.MinValue;
    private DateTime _autoRetainerCallbackReconciliationLastCheckUtc = DateTime.MinValue;
    private bool _autoRetainerCallbackReconciliationPending;

    private static readonly TimeSpan AutoRetainerCallbackReconciliationTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan AutoRetainerCallbackReconciliationInterval = TimeSpan.FromMilliseconds(250);

    internal bool AutoRetainerAvailable => _autoRetainerRegistered;

    internal IReadOnlyList<OfflineCharacterData> GetAutoRetainerCharacters()
    {
        if (_autoRetainerApi?.Ready == true)
        {
            try
            {
                var characters = new List<OfflineCharacterData>();
                foreach (var cid in _autoRetainerApi.GetRegisteredCharacters())
                {
                    var data = _autoRetainerApi.GetOfflineCharacterData(cid);
                    if (data != null)
                        characters.Add(data);
                }

                if (characters.Count > 0)
                {
                    PersistCharacterRoster(characters);
                    return characters;
                }
            }
            catch (IpcError)
            {
                Log.Debug("[RR][AR] Could not read registered character data.");
            }
        }

        var current = new OfflineCharacterData
        {
            CID = CurrentCharacterId,
            Name = CurrentCharacterName,
        };
        current.RetainerData.AddRange(ReadRetainerNames().Select(name => new OfflineRetainerData { Name = name }));
        PersistCharacterRoster(new[] { current });
        return CurrentCharacterId == 0 ? Array.Empty<OfflineCharacterData>() : new[] { current };
    }

    private void PersistCharacterRoster(IEnumerable<OfflineCharacterData> characters)
    {
        var changed = false;
        foreach (var character in characters)
        {
            if (character.CID == 0)
                continue;

            var retainerNames = character.RetainerData
                .Select(retainer => retainer.Name)
                .ToList();
            foreach (var retainerName in retainerNames)
                changed |= Configuration.MigrateRetainerConfiguration(character.CID, retainerName, retainerNames);

            Configuration.Characters ??= new();
            var hadSettings = Configuration.Characters.TryGetValue(character.CID, out var existing);
            var oldName = existing?.Name;
            var oldWorld = existing?.World;
            var settings = Configuration.GetCharacterSettings(character.CID, character.Name, character.World);
            if (!hadSettings || existing == null ||
                !string.Equals(oldName, settings.Name, StringComparison.Ordinal) ||
                !string.Equals(oldWorld, settings.World, StringComparison.Ordinal))
            {
                changed = true;
            }
        }

        if (changed)
            Configuration.Save();
    }

    private void InitializeAutoRetainerIntegration()
    {
        _autoRetainerApi = new AutoRetainerApi();
        _autoRetainerApi.OnRetainerPostprocessStep += OnAutoRetainerAdditionalTask;
        _autoRetainerApi.OnRetainerReadyToPostprocess += OnAutoRetainerRetainerReady;
        TryRegisterAutoRetainerPostProcess(force: true);
    }

    private void DisposeAutoRetainerIntegration()
    {
        if (_autoRetainerHandoffActive)
            FailAutoRetainerHandoff("plugin disposed during handoff");
        else if (_autoRetainerCallbackReconciliationPending)
        {
            Log.Warning("[RR][AR] Releasing pending callback reconciliation during plugin disposal.");
            ClearAutoRetainerCallbackReconciliation();
            FinishAutoRetainerRetainerPostProcess();
        }

        if (_autoRetainerApi != null)
        {
            _autoRetainerApi.OnRetainerPostprocessStep -= OnAutoRetainerAdditionalTask;
            _autoRetainerApi.OnRetainerReadyToPostprocess -= OnAutoRetainerRetainerReady;
            _autoRetainerApi.Dispose();
            _autoRetainerApi = null;
        }
    }

    private void TickAutoRetainerIntegration()
    {
        if (_autoRetainerCallbackReconciliationPending)
        {
            AdvanceAutoRetainerCallbackReconciliation();
            return;
        }

        if (_autoRetainerHandoffActive)
        {
            if (CurrentCharacterId == 0 || CurrentCharacterId != _autoRetainerHandoffCharacterId)
            {
                if (IsRunning)
                    StopRun(RunStopReason.Error);
                FailAutoRetainerHandoff("character changed during handoff");
                return;
            }

            if (_autoRetainerRecoveryActive)
            {
                AdvanceAutoRetainerRecovery();
                return;
            }

            if (_autoRetainerRunStarted)
            {
                if (!IsRunning)
                {
                    if (_runStopReason == RunStopReason.Error)
                    {
                        BeginAutoRetainerRecovery("automatic run stopped because of an error");
                        return;
                    }

                    CompleteAutoRetainerHandoff();
                }
                return;
            }

            if (IsRunning)
                return;

            AdvanceAutoRetainerHandoff();
            return;
        }

        TryRegisterAutoRetainerPostProcess();
    }

    private void TryRegisterAutoRetainerPostProcess(bool force = false)
    {
        var now = DateTime.UtcNow;
        if (!force && (now - _autoRetainerRegistrationAttemptUtc).TotalSeconds < AutoRetainerRegistrationRetrySeconds)
            return;

        _autoRetainerRegistrationAttemptUtc = now;
        try
        {
            _autoRetainerRegistered = _autoRetainerApi?.Ready == true;
        }
        catch (IpcError)
        {
            _autoRetainerRegistered = false;
        }
    }

    private void OnAutoRetainerAdditionalTask(string retainerName)
    {
        if (_autoRetainerAutomationBlocked)
        {
            LogAutoRetainerBlockedRequest(retainerName);
            return;
        }

        try
        {
            _autoRetainerApi?.RequestRetainerPostprocess();
            _autoRetainerRegistered = true;
            Log.Debug($"[RR][AR] Registered for retainer post-process: {retainerName}.");
        }
        catch (IpcError)
        {
            _autoRetainerRegistered = false;
        }
    }

    private void OnAutoRetainerRetainerReady(string retainerName)
    {
        // A duplicate callback must not unlock the handoff we already own.
        if (_autoRetainerHandoffActive || _autoRetainerCallbackReconciliationPending || _autoRetainerAutomationBlocked)
            return;

        if (IsRunning)
        {
            Log.Debug($"[RR][AR] Releasing post-process callback for '{retainerName}': RR is already running.");
            FinishAutoRetainerRetainerPostProcess();
            return;
        }

        var cid = CurrentCharacterId;
        if (!Configuration.PluginEnabled || cid == 0 || Configuration.IsCharacterBlacklisted(cid) ||
            !Configuration.GetCharacterSettings(cid).ProcessAfterAutoRetainer)
        {
            Log.Debug($"[RR][AR] Releasing post-process callback for '{retainerName}': plugin/character automation is disabled or unavailable.");
            FinishAutoRetainerRetainerPostProcess();
            return;
        }

        AutoRetainerHandoffDecision decision;
        PendingAutoRetainerWork pendingWork;
        try
        {
            decision = EvaluateAutoRetainerHandoff(cid, retainerName, out pendingWork, "callback");
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"[RR][AR] Failed to evaluate pending retainers after '{retainerName}' (cid={cid}); releasing AutoRetainer.");
            FinishAutoRetainerRetainerPostProcess();
            return;
        }
        if (decision == AutoRetainerHandoffDecision.Defer)
        {
            LogAutoRetainerHandoffDeferral(retainerName, pendingWork, "callback");
            FinishAutoRetainerRetainerPostProcess();
            return;
        }

        if (decision == AutoRetainerHandoffDecision.ReconcileCallbackVenture)
        {
            BeginAutoRetainerCallbackReconciliation(cid, retainerName);
            return;
        }

        if (decision == AutoRetainerHandoffDecision.Unavailable)
        {
            Log.Warning("[RR][AR] Handoff decision unavailable; releasing post-process callback without starting RR.");
            FinishAutoRetainerRetainerPostProcess();
            return;
        }

        BeginAutoRetainerHandoff(retainerName, cid, pendingWork, "callback");
    }

    private void BeginAutoRetainerHandoff(
        string retainerName,
        ulong cid,
        PendingAutoRetainerWork pendingWork,
        string source)
    {
        LogAutoRetainerHandoffStart(retainerName, pendingWork, source);
        _autoRetainerHandoffActive = true;
        _autoRetainerHandoffStartedUtc = DateTime.UtcNow;
        _autoRetainerSuppressionBeforeHandoff = false;
        _autoRetainerHandoffRetainerName = retainerName;
        _autoRetainerHandoffCharacterId = cid;
        ResetAutoRetainerNavigation();
        SetAutoRetainerNavigationStage(AutoRetainerNavigationStage.PreparingList);

        try
        {
            _autoRetainerSuppressionBeforeHandoff = _autoRetainerApi?.Suppressed == true;
            if (_autoRetainerApi != null)
                _autoRetainerApi.Suppressed = true;
        }
        catch (IpcError)
        {
            Log.Debug("[RR][AR] Suppression IPC unavailable; relying on retainer post-process lock.");
        }

        Log.Information($"[RR][AR] Final retainer callback received for {retainerName}; preparing RetainerList (source={source}).");
    }

    private AutoRetainerHandoffDecision EvaluateAutoRetainerHandoff(
        ulong cid,
        string completedRetainerName,
        out PendingAutoRetainerWork pendingWork,
        string source = "callback")
    {
        pendingWork = PendingAutoRetainerWork.Empty;
        if (cid == 0)
            return AutoRetainerHandoffDecision.Unavailable;

        try
        {
            var anyAvailable = PluginInterface.GetIpcSubscriber<bool>(
                "AutoRetainer.PluginState.AreAnyRetainersAvailableForCurrentChara").InvokeFunc();
            if (_autoRetainerDiagnosticsEnabled && ShouldLogAutoRetainerEvaluationDiagnostics(source))
                TryLogAutoRetainerDiagnosticSnapshot(cid, completedRetainerName, source, anyAvailable);
            if (anyAvailable)
            {
                if (TryClassifyCallbackRetainerOnlyAvailability(cid, completedRetainerName, out var callbackOnlyDiagnostic))
                {
                    if (string.Equals(source, "pre_start", StringComparison.Ordinal))
                    {
                        Log.Information($"[RR][AR] Proceeding after callback-retainer availability revalidation; {callbackOnlyDiagnostic}");
                        pendingWork = PendingAutoRetainerWork.Empty;
                        return AutoRetainerHandoffDecision.Start;
                    }

                    if (!string.Equals(source, "callback_recheck", StringComparison.Ordinal))
                    {
                        Log.Information($"[RR][AR] Availability appears limited to completed callback retainer; holding post-process callback for venture-state reconciliation. {callbackOnlyDiagnostic}");
                    }
                    return AutoRetainerHandoffDecision.ReconcileCallbackVenture;
                }

                pendingWork = new PendingAutoRetainerWork(string.Empty, 0, true);
                return AutoRetainerHandoffDecision.Defer;
            }

            var waitSeconds = Math.Clamp(Configuration.AutoRetainerNearVentureWaitMinutes, 0, 30) * 60L;
            var enabledRetainersByCharacter = PluginInterface.GetIpcSubscriber<Dictionary<ulong, HashSet<string>>>(
                "AutoRetainer.PluginState.GetEnabledRetainers").InvokeFunc();
            if (enabledRetainersByCharacter == null ||
                !enabledRetainersByCharacter.TryGetValue(cid, out var enabledRetainers))
                return AutoRetainerHandoffDecision.Start;

            if (enabledRetainers == null)
                return AutoRetainerHandoffDecision.Unavailable;

            var character = _autoRetainerApi?.GetOfflineCharacterData(cid);
            if (character?.RetainerData == null)
                return AutoRetainerHandoffDecision.Unavailable;

            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            foreach (var retainer in character.RetainerData)
            {
                if (retainer == null || string.IsNullOrWhiteSpace(retainer.Name))
                    continue;

                var isEnabled = enabledRetainers.Any(
                    name => string.Equals(name, retainer.Name, StringComparison.OrdinalIgnoreCase));
                if (!isEnabled ||
                    string.Equals(retainer.Name, completedRetainerName, StringComparison.OrdinalIgnoreCase) ||
                    !retainer.HasVenture || retainer.VentureEndsAt <= 0)
                {
                    continue;
                }

                var secondsRemaining = retainer.VentureEndsAt - now;
                if (string.IsNullOrEmpty(pendingWork.RetainerName) || secondsRemaining < pendingWork.SecondsRemaining)
                {
                    pendingWork = new PendingAutoRetainerWork(retainer.Name, secondsRemaining, false);
                }
            }

            if (string.IsNullOrEmpty(pendingWork.RetainerName))
                return AutoRetainerHandoffDecision.Start;

            return pendingWork.SecondsRemaining <= waitSeconds
                ? AutoRetainerHandoffDecision.Defer
                : AutoRetainerHandoffDecision.Start;
        }
        catch (IpcError ex)
        {
            Log.Warning($"[RR][AR][Diag] Handoff data IPC/API failed: {ex.GetType().Name}: {ex.Message}; using existing venture fallback.");
            if (_autoRetainerDiagnosticsEnabled && ShouldLogAutoRetainerEvaluationDiagnostics(source))
                TryLogAutoRetainerDiagnosticSnapshot(cid, completedRetainerName, $"{source}_ipc_failure");
            return EvaluateClosestAutoRetainerVenture(cid, out pendingWork);
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"[RR][AR] Failed to evaluate pending retainers after '{completedRetainerName}' (cid={cid}).");
            pendingWork = PendingAutoRetainerWork.Empty;
            return AutoRetainerHandoffDecision.Unavailable;
        }
    }

    private bool TryClassifyCallbackRetainerOnlyAvailability(
        ulong cid,
        string completedRetainerName,
        out string diagnostic)
    {
        diagnostic = string.Empty;
        if (cid == 0 || string.IsNullOrWhiteSpace(completedRetainerName))
            return false;

        try
        {
            var enabledByCharacter = PluginInterface.GetIpcSubscriber<Dictionary<ulong, HashSet<string>>>(
                "AutoRetainer.PluginState.GetEnabledRetainers").InvokeFunc();
            if (enabledByCharacter == null || !enabledByCharacter.TryGetValue(cid, out var enabledNames) ||
                enabledNames == null || enabledNames.Count == 0 ||
                !enabledNames.Any(name => string.Equals(
                    name, completedRetainerName, StringComparison.OrdinalIgnoreCase)))
                return false;

            var character = _autoRetainerApi?.GetOfflineCharacterData(cid);
            if (character?.CID != cid || character.RetainerData == null)
                return false;

            var duplicateNames = character.RetainerData
                .Where(retainer => retainer != null && !string.IsNullOrWhiteSpace(retainer.Name))
                .GroupBy(retainer => retainer.Name, StringComparer.OrdinalIgnoreCase)
                .Any(group => group.Count() != 1);
            if (duplicateNames)
                return false;

            var callbackRecords = character.RetainerData
                .Where(retainer => retainer != null && string.Equals(
                    retainer.Name, completedRetainerName, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (callbackRecords.Length != 1 || callbackRecords[0].HasVenture || callbackRecords[0].VentureEndsAt > 0)
                return false;

            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var waitSeconds = Math.Clamp(Configuration.AutoRetainerNearVentureWaitMinutes, 0, 30) * 60L;
            foreach (var enabledName in enabledNames)
            {
                var records = character.RetainerData
                    .Where(retainer => retainer != null && string.Equals(
                        retainer.Name, enabledName, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (records.Length != 1)
                    return false;

                if (string.Equals(enabledName, completedRetainerName, StringComparison.OrdinalIgnoreCase))
                    continue;

                var other = records[0];
                if (!other.HasVenture || other.VentureEndsAt <= 0 || other.VentureEndsAt - now <= waitSeconds)
                    return false;
            }

            diagnostic = $"character={cid} completed='{completedRetainerName}' enabledCount={enabledNames.Count} otherVentureRecords=complete waitWindowSeconds={waitSeconds}";
            return true;
        }
        catch (Exception ex)
        {
            Log.Debug($"[RR][AR] Could not prove callback-retainer-only availability: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private void BeginAutoRetainerCallbackReconciliation(ulong cid, string retainerName)
    {
        _autoRetainerCallbackReconciliationPending = true;
        _autoRetainerCallbackReconciliationCid = cid;
        _autoRetainerCallbackReconciliationRetainer = retainerName;
        _autoRetainerCallbackReconciliationStartedUtc = DateTime.UtcNow;
        _autoRetainerCallbackReconciliationLastCheckUtc = DateTime.UtcNow;
        Log.Information(
            $"[RR][AR] Holding post-process callback for '{retainerName}' up to {AutoRetainerCallbackReconciliationTimeout.TotalSeconds:0}s while AR venture data refreshes.");
    }

    private void AdvanceAutoRetainerCallbackReconciliation()
    {
        var now = DateTime.UtcNow;
        var cid = _autoRetainerCallbackReconciliationCid;
        var retainerName = _autoRetainerCallbackReconciliationRetainer;
        if (CurrentCharacterId == 0 || CurrentCharacterId != cid)
        {
            Log.Warning($"[RR][AR] Releasing callback reconciliation for '{retainerName}': character changed (expected={cid}, actual={CurrentCharacterId}).");
            ClearAutoRetainerCallbackReconciliation();
            FinishAutoRetainerRetainerPostProcess();
            return;
        }

        if (!Configuration.PluginEnabled || Configuration.IsCharacterBlacklisted(cid) ||
            !Configuration.GetCharacterSettings(cid).ProcessAfterAutoRetainer)
        {
            Log.Information($"[RR][AR] Releasing callback reconciliation for '{retainerName}': automation was disabled.");
            ClearAutoRetainerCallbackReconciliation();
            FinishAutoRetainerRetainerPostProcess();
            return;
        }

        if (IsRunning || _autoRetainerAutomationBlocked)
        {
            Log.Information($"[RR][AR] Releasing callback reconciliation for '{retainerName}': RR is running or automation is blocked.");
            ClearAutoRetainerCallbackReconciliation();
            FinishAutoRetainerRetainerPostProcess();
            return;
        }

        if (now - _autoRetainerCallbackReconciliationLastCheckUtc < AutoRetainerCallbackReconciliationInterval)
            return;

        _autoRetainerCallbackReconciliationLastCheckUtc = now;
        AutoRetainerHandoffDecision decision;
        PendingAutoRetainerWork pendingWork;
        try
        {
            decision = EvaluateAutoRetainerHandoff(cid, retainerName, out pendingWork, "callback_recheck");
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"[RR][AR] Callback reconciliation failed for '{retainerName}'; releasing AutoRetainer.");
            ClearAutoRetainerCallbackReconciliation();
            FinishAutoRetainerRetainerPostProcess();
            return;
        }

        if (decision == AutoRetainerHandoffDecision.Start)
        {
            var elapsed = now - _autoRetainerCallbackReconciliationStartedUtc;
            Log.Information($"[RR][AR] Venture state reconciled after {elapsed.TotalMilliseconds:0}ms; beginning handoff for '{retainerName}'.");
            ClearAutoRetainerCallbackReconciliation();
            BeginAutoRetainerHandoff(retainerName, cid, pendingWork, "callback_reconciled");
            return;
        }

        if (decision == AutoRetainerHandoffDecision.Defer)
        {
            LogAutoRetainerHandoffDeferral(retainerName, pendingWork, "callback_recheck");
            ClearAutoRetainerCallbackReconciliation();
            FinishAutoRetainerRetainerPostProcess();
            return;
        }

        if (decision == AutoRetainerHandoffDecision.Unavailable)
        {
            Log.Warning($"[RR][AR] Venture state unavailable after callback for '{retainerName}'; releasing without starting RR.");
            ClearAutoRetainerCallbackReconciliation();
            FinishAutoRetainerRetainerPostProcess();
            return;
        }

        if (now - _autoRetainerCallbackReconciliationStartedUtc < AutoRetainerCallbackReconciliationTimeout)
            return;

        var elapsedMs = (now - _autoRetainerCallbackReconciliationStartedUtc).TotalMilliseconds;
        Log.Warning($"[RR][AR] Venture data stayed stale for {elapsedMs:0}ms; complete records show only callback retainer '{retainerName}' available, so continuing with narrow callback exclusion.");
        ClearAutoRetainerCallbackReconciliation();
        BeginAutoRetainerHandoff(retainerName, cid, pendingWork, "callback_stale_state_excluded");
    }

    private void ClearAutoRetainerCallbackReconciliation()
    {
        _autoRetainerCallbackReconciliationPending = false;
        _autoRetainerCallbackReconciliationCid = 0;
        _autoRetainerCallbackReconciliationRetainer = string.Empty;
        _autoRetainerCallbackReconciliationStartedUtc = DateTime.MinValue;
        _autoRetainerCallbackReconciliationLastCheckUtc = DateTime.MinValue;
    }

    private bool ShouldLogAutoRetainerEvaluationDiagnostics(string source)
    {
        if (string.Equals(source, "callback_recheck", StringComparison.Ordinal))
            return false;

        if (!string.Equals(source, "pre_start", StringComparison.Ordinal))
            return true;

        var now = DateTime.UtcNow;
        if (_autoRetainerLastPreStartDiagnosticUtc != DateTime.MinValue &&
            now - _autoRetainerLastPreStartDiagnosticUtc < TimeSpan.FromSeconds(15))
            return false;

        _autoRetainerLastPreStartDiagnosticUtc = now;
        return true;
    }

    private AutoRetainerHandoffDecision EvaluateClosestAutoRetainerVenture(
        ulong cid,
        out PendingAutoRetainerWork pendingWork)
    {
        pendingWork = PendingAutoRetainerWork.Empty;
        try
        {
            var closestSeconds = PluginInterface.GetIpcSubscriber<ulong, long?>(
                "AutoRetainer.PluginState.GetClosestRetainerVentureSecondsRemaining").InvokeFunc(cid);
            if (closestSeconds is not long seconds || seconds <= 0)
                return AutoRetainerHandoffDecision.Unavailable;

            var waitSeconds = Math.Clamp(Configuration.AutoRetainerNearVentureWaitMinutes, 0, 30) * 60L;
            pendingWork = new PendingAutoRetainerWork("an enabled retainer", seconds, false);
            return seconds <= waitSeconds
                ? AutoRetainerHandoffDecision.Defer
                : AutoRetainerHandoffDecision.Start;
        }
        catch (IpcError)
        {
            pendingWork = PendingAutoRetainerWork.Empty;
            return AutoRetainerHandoffDecision.Unavailable;
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"[RR][AR] Failed to query fallback venture timing (cid={cid}).");
            pendingWork = PendingAutoRetainerWork.Empty;
            return AutoRetainerHandoffDecision.Unavailable;
        }
    }

    private void LogAutoRetainerDiagnosticSnapshot(
        ulong cid,
        string completedRetainerName,
        string source,
        bool? availabilityResult = null)
    {
        var evaluationId = Guid.NewGuid().ToString("N")[..8];
        var capturedAt = DateTimeOffset.UtcNow;
        var waitSeconds = Math.Clamp(Configuration.AutoRetainerNearVentureWaitMinutes, 0, 30) * 60L;
        AutoRetainerAPI.Configuration.OfflineCharacterData? currentCharacter = null;
        var offlineStatus = "unavailable";
        try
        {
            currentCharacter = _autoRetainerApi?.GetOfflineCharacterData(cid);
            offlineStatus = currentCharacter == null ? "missing" : "available";
        }
        catch (Exception ex)
        {
            offlineStatus = $"error({ex.GetType().Name}: {ex.Message})";
        }

        if (availabilityResult is null)
        {
            try
            {
                availabilityResult = PluginInterface.GetIpcSubscriber<bool>(
                    "AutoRetainer.PluginState.AreAnyRetainersAvailableForCurrentChara").InvokeFunc();
            }
            catch (Exception ex)
            {
                Log.Warning($"[RR][AR][Diag] eval={evaluationId} availability=unavailable error={ex.GetType().Name}: {ex.Message}");
            }
        }

        HashSet<string>? enabledNames = null;
        var enabledStatus = "unavailable";
        try
        {
            var enabledByCharacter = PluginInterface.GetIpcSubscriber<Dictionary<ulong, HashSet<string>>>(
                "AutoRetainer.PluginState.GetEnabledRetainers").InvokeFunc();
            if (enabledByCharacter == null)
                enabledStatus = "null_dictionary";
            else if (!enabledByCharacter.TryGetValue(cid, out enabledNames))
                enabledStatus = "character_missing";
            else if (enabledNames == null)
                enabledStatus = "null_set";
            else
                enabledStatus = $"ok(count={enabledNames.Count})";
        }
        catch (Exception ex)
        {
            enabledStatus = $"error({ex.GetType().Name}: {ex.Message})";
        }

        Log.Information(
            $"[RR][AR][Diag] eval={evaluationId} source={source} at={capturedAt:O} character='{CurrentCharacterName}' cid={cid} offlineCharacter='{currentCharacter?.Name ?? offlineStatus}' offlineCid={currentCharacter?.CID.ToString() ?? "unavailable"} completed='{completedRetainerName}' pluginEnabled={Configuration.PluginEnabled} rrRunning={IsRunning} handoffActive={_autoRetainerHandoffActive} waitWindowSeconds={waitSeconds} availability={(availabilityResult?.ToString() ?? "unavailable")} enabled={enabledStatus}.");

        if (enabledNames == null)
        {
            Log.Information($"[RR][AR][Diag] eval={evaluationId} enabledNames=unavailable offlineCharacter={offlineStatus}.");
        }
        else
        {
            var enabledOrdered = enabledNames.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
            Log.Information($"[RR][AR][Diag] eval={evaluationId} enabledNames=[{string.Join(",", enabledOrdered)}] offlineCharacter={offlineStatus}.");
        }
        if (currentCharacter?.RetainerData == null)
            return;

        var now = capturedAt.ToUnixTimeSeconds();
        var knownNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var readyOther = 0;
        var nearOther = 0;
        foreach (var retainer in currentCharacter.RetainerData.OrderBy(item => item?.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (retainer == null || string.IsNullOrWhiteSpace(retainer.Name))
            {
                Log.Information($"[RR][AR][Diag] eval={evaluationId} retainer=<null-or-unnamed> classification=invalid_record.");
                continue;
            }

            var duplicate = !knownNames.Add(retainer.Name);
            var isEnabled = enabledNames?.Any(name => string.Equals(name, retainer.Name, StringComparison.OrdinalIgnoreCase));
            var isCompleted = string.Equals(retainer.Name, completedRetainerName, StringComparison.OrdinalIgnoreCase);
            var secondsRemaining = retainer.VentureEndsAt > 0 ? retainer.VentureEndsAt - now : (long?)null;
            string ventureEndUtc;
            try
            {
                ventureEndUtc = retainer.VentureEndsAt > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(retainer.VentureEndsAt).ToString("O")
                    : "invalid";
            }
            catch (ArgumentOutOfRangeException)
            {
                ventureEndUtc = "out_of_range";
            }

            var classification = isEnabled == null ? "enabled_state_unavailable"
                : !isEnabled.Value ? "disabled"
                : isCompleted ? "completed_callback_retainer"
                : !retainer.HasVenture ? "no_venture"
                : retainer.VentureEndsAt <= 0 ? "invalid_or_missing_end"
                : secondsRemaining <= 0 ? "ready_now"
                : secondsRemaining <= waitSeconds ? "within_wait_window"
                : "outside_wait_window";
            if (isEnabled == true && !isCompleted && classification == "ready_now") readyOther++;
            if (isEnabled == true && !isCompleted && classification == "within_wait_window") nearOther++;

            Log.Information(
                $"[RR][AR][Diag] eval={evaluationId} retainer='{retainer.Name}' enabled={(isEnabled?.ToString() ?? "unknown")} completedMatch={isCompleted} duplicateName={duplicate} hasVenture={retainer.HasVenture} ventureEndsAt={retainer.VentureEndsAt} ventureEndUtc={ventureEndUtc} secondsRemaining={(secondsRemaining?.ToString() ?? "n/a")} classification={classification}.");
        }

        var missingEnabled = enabledNames?.Where(name => !currentCharacter.RetainerData.Any(retainer =>
                retainer != null && string.Equals(retainer.Name, name, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        var completedPresent = currentCharacter.RetainerData.Any(retainer => retainer != null &&
            string.Equals(retainer.Name, completedRetainerName, StringComparison.OrdinalIgnoreCase));
        Log.Information(
            $"[RR][AR][Diag] eval={evaluationId} summary=readyOther:{readyOther},withinWaitOther:{nearOther},enabledNamesMissingOffline:[{(missingEnabled == null ? "unavailable" : string.Join(",", missingEnabled))}],completedRetainerRecordPresent={completedPresent}.");
    }

    private void TryLogAutoRetainerDiagnosticSnapshot(
        ulong cid,
        string completedRetainerName,
        string source,
        bool? availabilityResult = null)
    {
        try
        {
            LogAutoRetainerDiagnosticSnapshot(cid, completedRetainerName, source, availabilityResult);
        }
        catch (Exception ex)
        {
            Log.Warning($"[RR][AR][Diag] Diagnostic snapshot failed; handoff decision remains unchanged: {ex.GetType().Name}: {ex.Message}");
        }
    }

    internal bool AutoRetainerDiagnosticsEnabled => _autoRetainerDiagnosticsEnabled;

    internal void SetAutoRetainerDiagnosticsEnabled(bool enabled)
        => _autoRetainerDiagnosticsEnabled = enabled;

    internal void CaptureAutoRetainerDiagnosticSnapshot()
        => TryLogAutoRetainerDiagnosticSnapshot(CurrentCharacterId, string.Empty, "manual_snapshot");

    private void LogAutoRetainerHandoffDeferral(
        string completedRetainerName,
        PendingAutoRetainerWork pendingWork,
        string source)
    {
        if (pendingWork.IsAlreadyAvailable)
        {
            Log.Information(
                "[RR][AR] Deferring automatic repricing after {CompletedRetainer}; availability IPC returned true, but pending retainer identity is unknown ({Source}).",
                completedRetainerName,
                source);
            return;
        }

        var waitSeconds = Math.Clamp(Configuration.AutoRetainerNearVentureWaitMinutes, 0, 30) * 60L;
        Log.Information(
            "[RR][AR] Deferring automatic repricing after {CompletedRetainer}; {PendingRetainer} completes in {Seconds}s " +
            "(window={Window}s, source={Source}).",
            completedRetainerName,
            pendingWork.RetainerName,
            pendingWork.SecondsRemaining,
            waitSeconds,
            source);
    }

    private void LogAutoRetainerHandoffStart(
        string completedRetainerName,
        PendingAutoRetainerWork pendingWork,
        string source)
    {
        if (string.IsNullOrEmpty(pendingWork.RetainerName))
            return;

        var waitSeconds = Math.Clamp(Configuration.AutoRetainerNearVentureWaitMinutes, 0, 30) * 60L;
        Log.Debug(
            "[RR][AR] {PendingRetainer} completes in {Seconds}s, outside the {Window}s grouping window after {CompletedRetainer} ({Source}).",
            pendingWork.RetainerName,
            pendingWork.SecondsRemaining,
            waitSeconds,
            completedRetainerName,
            source);
    }

    private void AdvanceAutoRetainerHandoff()
    {
        if (!Configuration.PluginEnabled)
        {
            BeginAutoRetainerRecovery("plugin disabled during handoff");
            return;
        }

        var elapsed = (DateTime.UtcNow - _autoRetainerHandoffStartedUtc).TotalSeconds;
        if (elapsed >= Configuration.AutoRetainerHandoffTimeoutSeconds)
        {
            BeginAutoRetainerRecovery("RetainerList preparation timed out");
            return;
        }

        LogAutoRetainerNavigation();
        if (!CanAdvanceAutoRetainerNavigation() || AdvanceAutoRetainerChildCleanup())
            return;

        if (IsAutoRetainerAddonBlocking("SelectString"))
        {
            TryAutoRetainerCloseAddon("SelectString");
            return;
        }

        if (IsAutoRetainerAddonBlocking("Talk"))
        {
            TryAutoRetainerAdvanceTalk();
            return;
        }

        if (!IsAutoRetainerAddonReady("RetainerList") || !IsRetainerListReady())
            return;

        AutoRetainerHandoffDecision decision;
        PendingAutoRetainerWork pendingWork;
        try
        {
            decision = EvaluateAutoRetainerHandoff(
                CurrentCharacterId,
                _autoRetainerHandoffRetainerName,
                out pendingWork,
                "pre_start");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[RR][AR] Failed to verify enabled-retainer ventures before starting.");
            BeginAutoRetainerRecovery("venture verification failed");
            return;
        }
        if (decision == AutoRetainerHandoffDecision.Defer)
        {
            LogAutoRetainerHandoffDeferral(_autoRetainerHandoffRetainerName, pendingWork, "pre_start");
            BeginAutoRetainerRecovery("pending venture work appeared before start");
            return;
        }

        if (decision == AutoRetainerHandoffDecision.Unavailable)
        {
            BeginAutoRetainerRecovery("enabled-retainer venture data unavailable before start");
            return;
        }

        LogAutoRetainerHandoffStart(_autoRetainerHandoffRetainerName, pendingWork, "pre_start");

        if (!StartRunFromRetainerList(RunMode.PriceAndSell))
        {
            Log.Warning("[RR][AR] Could not start automatic run from RetainerList.");
            BeginAutoRetainerRecovery("automatic run could not start");
        }
        else
        {
            _autoRetainerRunStarted = true;
            SetAutoRetainerNavigationStage(AutoRetainerNavigationStage.Running);
            Log.Information("[RR][AR] RetainerList ready; automatic repricing run started.");
        }
    }

    private void BeginAutoRetainerRecovery(string reason)
    {
        if (_autoRetainerRecoveryActive)
            return;

        LogAutoRetainerNavigation(force: true);
        _autoRetainerRecoveryActive = true;
        _autoRetainerRecoveryStartedUtc = DateTime.UtcNow;
        _autoRetainerRecoveryRetainerName = _autoRetainerHandoffRetainerName;
        _autoRetainerMenuStableSinceUtc = DateTime.MinValue;
        _autoRetainerRetainerReopened = false;
        SetAutoRetainerNavigationStage(AutoRetainerNavigationStage.RecoveringMenu);
        Log.Warning($"[RR][AR] Restoring AutoRetainer Quit menu: {reason}.");
    }

    private void AdvanceAutoRetainerRecovery()
    {
        var elapsed = (DateTime.UtcNow - _autoRetainerRecoveryStartedUtc).TotalSeconds;
        if (elapsed >= AutoRetainerRecoveryTimeoutSeconds)
        {
            FailAutoRetainerHandoff("Quit menu recovery timed out");
            return;
        }

        LogAutoRetainerNavigation();
        if (!IsAutoRetainerQuitMenuReady())
            _autoRetainerMenuStableSinceUtc = DateTime.MinValue;

        if (!CanAdvanceAutoRetainerNavigation() || AdvanceAutoRetainerChildCleanup())
            return;

        if (IsAutoRetainerAddonBlocking("SelectString"))
        {
            SetAutoRetainerNavigationStage(AutoRetainerNavigationStage.WaitingForQuitMenu);
            if (IsAutoRetainerQuitMenuStable())
            {
                SetAutoRetainerNavigationStage(AutoRetainerNavigationStage.ReadyToRelease);
                Log.Information("[RR][AR] Stable retainer Quit menu ready; releasing AutoRetainer.");
                ReleaseAutoRetainerHandoff();
            }
            return;
        }

        if (IsAutoRetainerAddonBlocking("Talk"))
        {
            SetAutoRetainerNavigationStage(AutoRetainerNavigationStage.WaitingForQuitMenu);
            TryAutoRetainerAdvanceTalk();
            return;
        }

        if (IsAutoRetainerAddonReady("RetainerList") && IsRetainerListReady() &&
            !_autoRetainerRetainerReopened)
        {
            SetAutoRetainerNavigationStage(AutoRetainerNavigationStage.ReopeningRetainer);
            TryAutoRetainerReopenCallbackRetainer();
        }
        // No visible UI can be a transition, never evidence of a valid handback.
    }

    private void CompleteAutoRetainerHandoff()
    {
        if (!_autoRetainerHandoffActive)
            return;

        BeginAutoRetainerRecovery("automatic run finished; validating handback menu");
    }

    private void ReleaseAutoRetainerHandoff(bool restoreSuppression = true, bool finishPostprocess = true)
    {
        if (!_autoRetainerHandoffActive)
            return;

        try
        {
            if (restoreSuppression && _autoRetainerApi != null)
                _autoRetainerApi.Suppressed = _autoRetainerSuppressionBeforeHandoff;
        }
        catch (IpcError)
        {
            Log.Warning("[RR][AR] Failed to restore AutoRetainer suppression state.");
        }

        try
        {
            if (finishPostprocess)
                _autoRetainerApi?.FinishRetainerPostProcess();
        }
        catch (IpcError)
        {
            Log.Warning("[RR][AR] Failed to release AutoRetainer post-process state.");
        }

        _autoRetainerHandoffActive = false;
        _autoRetainerRunStarted = false;
        _autoRetainerRecoveryActive = false;
        _autoRetainerHandoffStartedUtc = DateTime.MinValue;
        _autoRetainerHandoffRetainerName = string.Empty;
        _autoRetainerHandoffCharacterId = 0;
        _autoRetainerRecoveryStartedUtc = DateTime.MinValue;
        ResetAutoRetainerNavigation();
    }

    private void FinishAutoRetainerRetainerPostProcess()
    {
        try
        {
            _autoRetainerApi?.FinishRetainerPostProcess();
        }
        catch (IpcError)
        {
            Log.Debug("[RR][AR] AutoRetainer post-process completion IPC was unavailable.");
        }
    }

    private enum AutoRetainerHandoffDecision
    {
        Start,
        Defer,
        ReconcileCallbackVenture,
        Unavailable,
    }

    private readonly record struct PendingAutoRetainerWork(
        string RetainerName,
        long SecondsRemaining,
        bool IsAlreadyAvailable)
    {
        public static PendingAutoRetainerWork Empty => new(string.Empty, 0, false);
    }
}
