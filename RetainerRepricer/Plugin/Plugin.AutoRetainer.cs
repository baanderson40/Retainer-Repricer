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
        ReleaseAutoRetainerHandoff();

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
        if (_autoRetainerHandoffActive)
        {
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
        if (_autoRetainerHandoffActive || IsRunning)
        {
            FinishAutoRetainerRetainerPostProcess();
            return;
        }

        var cid = CurrentCharacterId;
        if (!Configuration.PluginEnabled || cid == 0 || Configuration.IsCharacterBlacklisted(cid) ||
            !Configuration.GetCharacterSettings(cid).ProcessAfterAutoRetainer)
        {
            FinishAutoRetainerRetainerPostProcess();
            return;
        }

        var decision = EvaluateAutoRetainerHandoff(cid, retainerName, out var pendingWork);
        if (decision == AutoRetainerHandoffDecision.Defer)
        {
            LogAutoRetainerHandoffDeferral(retainerName, pendingWork, "callback");
            FinishAutoRetainerRetainerPostProcess();
            return;
        }

        if (decision == AutoRetainerHandoffDecision.Unavailable)
        {
            Log.Warning("[RR][AR] Could not determine remaining enabled-retainer ventures; releasing post-process callback.");
            FinishAutoRetainerRetainerPostProcess();
            return;
        }

        LogAutoRetainerHandoffStart(retainerName, pendingWork, "callback");

        _autoRetainerHandoffActive = true;
        _autoRetainerHandoffStartedUtc = DateTime.UtcNow;
        _autoRetainerSuppressionBeforeHandoff = false;
        _autoRetainerHandoffRetainerName = retainerName;

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

        Log.Information($"[RR][AR] Final retainer callback received for {retainerName}; preparing RetainerList.");
    }

    private AutoRetainerHandoffDecision EvaluateAutoRetainerHandoff(
        ulong cid,
        string completedRetainerName,
        out PendingAutoRetainerWork pendingWork)
    {
        pendingWork = default;
        if (cid == 0)
            return AutoRetainerHandoffDecision.Unavailable;

        try
        {
            var anyAvailable = PluginInterface.GetIpcSubscriber<bool>(
                "AutoRetainer.PluginState.AreAnyRetainersAvailableForCurrentChara").InvokeFunc();
            if (anyAvailable)
            {
                pendingWork = new PendingAutoRetainerWork(string.Empty, 0, true);
                return AutoRetainerHandoffDecision.Defer;
            }

            var waitSeconds = Math.Clamp(Configuration.AutoRetainerNearVentureWaitMinutes, 0, 30) * 60L;
            var enabledRetainersByCharacter = PluginInterface.GetIpcSubscriber<Dictionary<ulong, HashSet<string>>>(
                "AutoRetainer.PluginState.GetEnabledRetainers").InvokeFunc();
            if (enabledRetainersByCharacter == null ||
                !enabledRetainersByCharacter.TryGetValue(cid, out var enabledRetainers))
                return AutoRetainerHandoffDecision.Start;

            var character = _autoRetainerApi?.GetOfflineCharacterData(cid);
            if (character == null)
                return AutoRetainerHandoffDecision.Unavailable;

            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            foreach (var retainer in character.RetainerData)
            {
                var isEnabled = enabledRetainers.Any(
                    name => string.Equals(name, retainer.Name, StringComparison.OrdinalIgnoreCase));
                if (!isEnabled ||
                    string.Equals(retainer.Name, completedRetainerName, StringComparison.OrdinalIgnoreCase) ||
                    !retainer.HasVenture || retainer.VentureEndsAt <= 0)
                {
                    continue;
                }

                var secondsRemaining = retainer.VentureEndsAt - now;
                if (pendingWork.RetainerName.Length == 0 || secondsRemaining < pendingWork.SecondsRemaining)
                {
                    pendingWork = new PendingAutoRetainerWork(retainer.Name, secondsRemaining, false);
                }
            }

            if (pendingWork.RetainerName.Length == 0)
                return AutoRetainerHandoffDecision.Start;

            return pendingWork.SecondsRemaining <= waitSeconds
                ? AutoRetainerHandoffDecision.Defer
                : AutoRetainerHandoffDecision.Start;
        }
        catch (IpcError)
        {
            return EvaluateClosestAutoRetainerVenture(cid, out pendingWork);
        }
    }

    private AutoRetainerHandoffDecision EvaluateClosestAutoRetainerVenture(
        ulong cid,
        out PendingAutoRetainerWork pendingWork)
    {
        pendingWork = default;
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
            return AutoRetainerHandoffDecision.Unavailable;
        }
    }

    private void LogAutoRetainerHandoffDeferral(
        string completedRetainerName,
        PendingAutoRetainerWork pendingWork,
        string source)
    {
        if (pendingWork.IsAlreadyAvailable)
        {
            Log.Information(
                "[RR][AR] Deferring automatic repricing after {CompletedRetainer}; another enabled retainer is already available ({Source}).",
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
        if (pendingWork.RetainerName.Length == 0)
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

        if (IsAddonVisible("ItemSearchResult") || IsAddonVisible("ItemHistory"))
        {
            CloseMarketWindows();
            return;
        }

        if (IsAddonVisible("RetainerSell"))
        {
            CloseRetainerSellIfOpen();
            return;
        }

        if (IsAddonVisible("SelectString"))
        {
            CloseAddonIfOpen("SelectString");
            return;
        }

        if (IsAddonVisible("Talk"))
        {
            TryAdvanceTalk();
            return;
        }

        if (!IsAddonVisible("RetainerList") || !IsRetainerListReady())
            return;

        var decision = EvaluateAutoRetainerHandoff(
            CurrentCharacterId,
            _autoRetainerHandoffRetainerName,
            out var pendingWork);
        if (decision == AutoRetainerHandoffDecision.Defer)
        {
            LogAutoRetainerHandoffDeferral(_autoRetainerHandoffRetainerName, pendingWork, "pre_start");
            ReleaseAutoRetainerHandoff();
            return;
        }

        if (decision == AutoRetainerHandoffDecision.Unavailable)
        {
            Log.Warning("[RR][AR] Could not verify enabled-retainer ventures before starting; releasing AutoRetainer.");
            ReleaseAutoRetainerHandoff();
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
            Log.Information("[RR][AR] RetainerList ready; automatic repricing run started.");
        }
    }

    private void BeginAutoRetainerRecovery(string reason)
    {
        _autoRetainerRecoveryActive = true;
        _autoRetainerHandoffStartedUtc = DateTime.UtcNow;
        Log.Warning($"[RR][AR] Aborting handoff and unwinding retainer UI: {reason}.");
    }

    private void AdvanceAutoRetainerRecovery()
    {
        var elapsed = (DateTime.UtcNow - _autoRetainerHandoffStartedUtc).TotalSeconds;
        if (elapsed >= AutoRetainerRecoveryTimeoutSeconds)
        {
            Log.Warning("[RR][AR] Retainer UI unwind timed out; releasing AutoRetainer.");
            ReleaseAutoRetainerHandoff();
            return;
        }

        if (IsAddonVisible("ItemSearchResult") || IsAddonVisible("ItemHistory"))
        {
            CloseMarketWindows();
            return;
        }

        if (IsAddonVisible("ContextMenu"))
        {
            FireContextMenuDismiss();
            return;
        }

        if (IsAddonVisible("RetainerSell"))
        {
            CloseRetainerSellIfOpen();
            return;
        }

        if (IsAddonVisible("RetainerSellList"))
        {
            CloseAddonIfOpen("RetainerSellList");
            return;
        }

        if (IsAddonVisible("SelectString"))
        {
            CloseAddonIfOpen("SelectString");
            return;
        }

        if (IsAddonVisible("Talk"))
        {
            TryAdvanceTalk();
            return;
        }

        if (IsAddonVisible("RetainerList"))
        {
            CloseAddonIfOpen("RetainerList");
            return;
        }

        Log.Information("[RR][AR] Retainer UI fully unwound; releasing AutoRetainer.");
        ReleaseAutoRetainerHandoff();
    }

    private void CompleteAutoRetainerHandoff()
    {
        if (!_autoRetainerHandoffActive)
            return;

        ReleaseAutoRetainerHandoff();
    }

    private void ReleaseAutoRetainerHandoff()
    {
        if (!_autoRetainerHandoffActive)
            return;

        try
        {
            if (_autoRetainerApi != null)
                _autoRetainerApi.Suppressed = _autoRetainerSuppressionBeforeHandoff;
        }
        catch (IpcError)
        {
            Log.Warning("[RR][AR] Failed to restore AutoRetainer suppression state.");
        }

        try
        {
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
        Unavailable,
    }

    private readonly record struct PendingAutoRetainerWork(
        string RetainerName,
        long SecondsRemaining,
        bool IsAlreadyAvailable);
}
