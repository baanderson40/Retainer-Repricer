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

        bool anyAvailable;
        try
        {
            anyAvailable = PluginInterface.GetIpcSubscriber<bool>(
                "AutoRetainer.PluginState.AreAnyRetainersAvailableForCurrentChara").InvokeFunc();
        }
        catch (IpcError)
        {
            Log.Warning("[RR][AR] Could not query remaining retainers; releasing post-process callback.");
            FinishAutoRetainerRetainerPostProcess();
            return;
        }

        if (anyAvailable)
        {
            FinishAutoRetainerRetainerPostProcess();
            return;
        }

        _autoRetainerHandoffActive = true;
        _autoRetainerHandoffStartedUtc = DateTime.UtcNow;
        _autoRetainerSuppressionBeforeHandoff = false;

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
}
