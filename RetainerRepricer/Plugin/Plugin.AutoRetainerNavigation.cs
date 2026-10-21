using System;
using System.Linq;

using Dalamud.Utility;
using ECommons.DalamudServices;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace RetainerRepricer;

public unsafe sealed partial class Plugin
{
    private const double AutoRetainerDiagnosticIntervalSeconds = 2.0d;
    private const double AutoRetainerMenuStabilitySeconds = 0.2d;
    private static readonly string[] AutoRetainerChildAddons =
        ["ItemHistory", "ItemSearchResult", "ContextMenu", "RetainerSell", "RetainerSellList"];
    // These require context-specific handling. Never blindly cancel/confirm them.
    private static readonly string[] AutoRetainerBlockingDialogs =
        ["SelectYesno", "SelectIconString", "RetainerTaskAsk", "RetainerTaskResult", "RetainerTaskSupply", "RetainerHistory"];

    private enum AutoRetainerNavigationStage
    {
        Idle,
        PreparingList,
        Running,
        RecoveringMenu,
        ReopeningRetainer,
        WaitingForQuitMenu,
        ReadyToRelease,
        Failed,
    }

    private AutoRetainerNavigationStage _autoRetainerNavigationStage;
    private DateTime _autoRetainerStageStartedUtc = DateTime.MinValue;
    private DateTime _autoRetainerLastActionUtc = DateTime.MinValue;
    private DateTime _autoRetainerLastDiagnosticUtc = DateTime.MinValue;
    private DateTime _autoRetainerMenuStableSinceUtc = DateTime.MinValue;
    private string _autoRetainerLastAction = "none";
    private string _autoRetainerRecoveryRetainerName = string.Empty;
    private bool _autoRetainerRetainerReopened;
    private bool _autoRetainerAutomationBlocked;

    private void ResetAutoRetainerNavigation()
    {
        _autoRetainerNavigationStage = AutoRetainerNavigationStage.Idle;
        _autoRetainerStageStartedUtc = DateTime.MinValue;
        _autoRetainerLastActionUtc = DateTime.MinValue;
        _autoRetainerLastDiagnosticUtc = DateTime.MinValue;
        _autoRetainerMenuStableSinceUtc = DateTime.MinValue;
        _autoRetainerLastAction = "none";
        _autoRetainerRecoveryRetainerName = string.Empty;
        _autoRetainerRetainerReopened = false;
        // A failed fail-safe requires manual intervention/reload, not another handoff.
    }

    private void SetAutoRetainerNavigationStage(AutoRetainerNavigationStage stage)
    {
        if (_autoRetainerNavigationStage == stage)
            return;

        _autoRetainerNavigationStage = stage;
        _autoRetainerStageStartedUtc = DateTime.UtcNow;
        LogAutoRetainerNavigation(force: true);
    }

    private bool CanAdvanceAutoRetainerNavigation()
        => (DateTime.UtcNow - _autoRetainerLastActionUtc).TotalSeconds >= ActionIntervalSeconds;

    private void RecordAutoRetainerAction(string action)
    {
        _autoRetainerLastAction = action;
        _autoRetainerLastActionUtc = DateTime.UtcNow;
        _autoRetainerMenuStableSinceUtc = DateTime.MinValue;
    }

    private bool IsAutoRetainerAddonReady(string name)
    {
        var addon = GameGui.GetAddonByName(name, 1);
        if (addon.IsNull)
            return false;

        var unit = (AtkUnitBase*)addon.Address;
        return unit->IsVisible && unit->IsReady;
    }

    private bool IsAutoRetainerAddonBlocking(string name)
    {
        var addon = GameGui.GetAddonByName(name, 1);
        if (addon.IsNull)
            return false;

        var unit = (AtkUnitBase*)addon.Address;
        // Hidden, ready addons may be retained by the game. An unready addon may
        // be opening/closing: don't operate on its parent during that transition.
        return unit->IsVisible || !unit->IsReady;
    }

    private void TryAutoRetainerCloseAddon(string name)
    {
        if (!IsAutoRetainerAddonReady(name) ||
            (name == "SelectString" && !IsSelectStringReady()))
            return;

        RecordAutoRetainerAction($"close {name}");
        if (name == "RetainerSell")
            CloseRetainerSellIfOpen();
        else
            CloseAddonIfOpen(name); // Preserve the existing -1 callback.
    }

    private void TryAutoRetainerAdvanceTalk()
    {
        if (!IsAutoRetainerAddonReady("Talk"))
            return;

        RecordAutoRetainerAction("advance Talk");
        TryAdvanceTalk();
    }

    private bool AdvanceAutoRetainerChildCleanup()
    {
        if (AutoRetainerBlockingDialogs.Any(IsAutoRetainerAddonBlocking))
            return true;

        foreach (var name in AutoRetainerChildAddons)
        {
            if (!IsAutoRetainerAddonBlocking(name))
                continue;

            TryAutoRetainerCloseAddon(name);
            return true;
        }

        return false;
    }

    private bool IsAutoRetainerQuitMenuReady()
    {
        if (!IsAutoRetainerAddonReady("SelectString") ||
            IsAutoRetainerAddonBlocking("Talk") ||
            AutoRetainerChildAddons.Any(IsAutoRetainerAddonBlocking) ||
            AutoRetainerBlockingDialogs.Any(IsAutoRetainerAddonBlocking))
        {
            return false;
        }

        // Verify this is a retainer menu, not some other SelectString with Quit.
        if (!_uiReader.TryGetActiveRetainer(out _, out _))
            return false;

        try
        {
            var quit = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Addon>()
                .GetRow(2383).Text.ToDalamudString().TextValue;
            if (string.IsNullOrWhiteSpace(quit))
                return false;

            var menu = new AddonMaster.SelectString(GameGui.GetAddonByName("SelectString", 1).Address);
            if (menu.EntryCount <= 0 || menu.EntryCount > 32)
                return false;

            return menu.Entries.Any(entry => string.Equals(entry.Text, quit, StringComparison.Ordinal));
        }
        catch
        {
            return false;
        }
    }

    private bool IsAutoRetainerQuitMenuStable()
    {
        if (!IsAutoRetainerQuitMenuReady())
        {
            _autoRetainerMenuStableSinceUtc = DateTime.MinValue;
            return false;
        }

        var now = DateTime.UtcNow;
        if (_autoRetainerMenuStableSinceUtc == DateTime.MinValue)
        {
            _autoRetainerMenuStableSinceUtc = now;
            return false;
        }

        return (now - _autoRetainerMenuStableSinceUtc).TotalSeconds >=
            Math.Max(AutoRetainerMenuStabilitySeconds, ActionIntervalSeconds);
    }

    private void TryAutoRetainerReopenCallbackRetainer()
    {
        if (CurrentCharacterId != _autoRetainerHandoffCharacterId)
            return;

        var list = _uiReader.GetRetainerList();
        if (list == null)
            return;

        for (var index = 0; index < list->GetItemCount(); index++)
        {
            var renderer = list->GetItemRenderer(index);
            if (renderer == null)
                continue;

            var name = _uiReader.ReadRendererText(renderer, Ui.NodePaths.RetainerNameNodeId);
            if (string.IsNullOrWhiteSpace(name) || !string.Equals(
                    Configuration.NormalizeRetainerName(name),
                    Configuration.NormalizeRetainerName(_autoRetainerRecoveryRetainerName),
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            RecordAutoRetainerAction($"reopen retainer row {index}");
            if (TryClickRetainerListEntry(index))
                _autoRetainerRetainerReopened = true;
            return;
        }
    }

    private string DescribeAutoRetainerAddon(string name)
    {
        var addon = GameGui.GetAddonByName(name, 1);
        if (addon.IsNull)
            return $"{name}=absent";

        var unit = (AtkUnitBase*)addon.Address;
        return $"{name}=present/{(unit->IsVisible ? "visible" : "hidden")}/{(unit->IsReady ? "ready" : "not-ready")}";
    }

    private void LogAutoRetainerNavigation(bool force = false)
    {
        var now = DateTime.UtcNow;
        if (!force && (now - _autoRetainerLastDiagnosticUtc).TotalSeconds < AutoRetainerDiagnosticIntervalSeconds)
            return;

        _autoRetainerLastDiagnosticUtc = now;
        var menuDescription = "unreadable";
        if (IsAutoRetainerAddonReady("SelectString"))
        {
            try
            {
                var menu = new AddonMaster.SelectString(GameGui.GetAddonByName("SelectString", 1).Address);
                var count = menu.EntryCount;
                var entries = count is > 0 and <= 32
                    ? string.Join(" | ", menu.Entries.Take(12).Select(entry =>
                        entry.Text.Replace('\r', ' ').Replace('\n', ' ')).Select(text =>
                        text.Length > 100 ? text[..100] : text))
                    : string.Empty;
                menuDescription = $"count={count} entries=[{entries}]";
            }
            catch
            {
                // Native menu may still be transitioning. The readiness snapshot
                // and unreadable marker are sufficient; never log per-frame errors.
            }
        }

        var addons = string.Join(", ", AutoRetainerChildAddons
            .Concat(AutoRetainerBlockingDialogs).Concat(["SelectString", "Talk", "RetainerList"])
            .Select(DescribeAutoRetainerAddon));
        var actionAge = _autoRetainerLastActionUtc == DateTime.MinValue
            ? "never" : $"{(now - _autoRetainerLastActionUtc).TotalSeconds:F2}s";
        Log.Debug($"[RR][AR] Navigation stage={_autoRetainerNavigationStage} " +
            $"stageAge={(now - _autoRetainerStageStartedUtc).TotalSeconds:F2}s " +
            $"handoffAge={(now - _autoRetainerHandoffStartedUtc).TotalSeconds:F2}s " +
            $"cid={CurrentCharacterId}/{_autoRetainerHandoffCharacterId} " +
            $"callback='{_autoRetainerHandoffRetainerName}' recovery='{_autoRetainerRecoveryRetainerName}' " +
            $"lastAction='{_autoRetainerLastAction}' actionAge={actionAge}; {addons}; " +
            $"menu={menuDescription} quitReady={IsAutoRetainerQuitMenuReady()} " +
            $"listReady={IsAutoRetainerAddonReady("RetainerList") && IsRetainerListReady()}");
    }

    private void FailAutoRetainerHandoff(string reason)
    {
        SetAutoRetainerNavigationStage(AutoRetainerNavigationStage.Failed);
        LogAutoRetainerNavigation(force: true);
        var disabled = TryAutoRetainerFailSafeIpc("AutoRetainer.PluginState.DisableAllFunctions");
        var aborted = TryAutoRetainerFailSafeIpc("AutoRetainer.PluginState.AbortAllTasks");
        _autoRetainerAutomationBlocked = true;
        if (!disabled || !aborted)
        {
            // Keep suppression (and, if abort failed, the postprocess lock) rather
            // than knowingly resume the invalid queued SelectQuit operation.
            try
            {
                if (_autoRetainerApi != null)
                    _autoRetainerApi.Suppressed = true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[RR][AR] Could not suppress AutoRetainer after fail-safe failure.");
            }
        }

        Log.Warning($"[RR][AR] Handoff failed: {reason}; automationDisabled={disabled}, tasksAborted={aborted}. " +
            "Automatic repricing handoffs are blocked until RetainerRepricer is reloaded.");
        PrintError(disabled && aborted
            ? "AutoRetainer handoff failed. AutoRetainer, MultiMode and voyages were stopped. Check the retainer UI, then reload RetainerRepricer before resuming."
            : "AutoRetainer handoff failed and could not be safely stopped. Stop AutoRetainer manually, check its suppression/UI state, then reload RetainerRepricer before resuming.");
        ReleaseAutoRetainerHandoff(restoreSuppression: disabled && aborted, finishPostprocess: aborted);
    }

    private bool TryAutoRetainerFailSafeIpc(string endpoint)
    {
        try
        {
            PluginInterface.GetIpcSubscriber<object>(endpoint).InvokeAction();
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"[RR][AR] Fail-safe IPC failed: {endpoint}.");
            return false;
        }
    }
}
