using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using ECommons.DalamudServices;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace RetainerRepricer;

public unsafe sealed partial class Plugin
{
    private ulong _autoRetainerExitCharacterId;
    private ulong _autoRetainerExitRetainerId;
    private DateTime _autoRetainerExitRequestedUtc;
    private bool _autoRetainerBuybackAttempted;
    private bool _autoRetainerBuybackClosed;
    private string _autoRetainerBuybackDiagnostic = string.Empty;
    private string? _autoRetainerBuybackLanguage;
    private readonly HashSet<string> _autoRetainerBuybackPrompts = new(StringComparer.Ordinal);
    private DateTime _autoRetainerBlockedLogUtc = DateTime.MinValue;

    private void ClearAutoRetainerExit()
    {
        _autoRetainerExitCharacterId = 0;
        _autoRetainerExitRetainerId = 0;
        _autoRetainerExitRequestedUtc = default;
        _autoRetainerBuybackAttempted = false;
        _autoRetainerBuybackClosed = false;
        _autoRetainerBuybackDiagnostic = string.Empty;
    }

    private void LogAutoRetainerBlockedRequest(string retainerName)
    {
        if ((DateTime.UtcNow - _autoRetainerBlockedLogUtc).TotalSeconds < 60) return;
        _autoRetainerBlockedLogUtc = DateTime.UtcNow;
        Log.Warning("[RR][AR] Ignoring post-process request for {Retainer}: a previous handoff failed. Check retainer UI, then reload RetainerRepricer before resuming automation.", retainerName);
    }

    private bool TryAuthorizeAutoRetainerExit()
    {
        // Do not re-dispatch an exit while its dialog/transition is unresolved.
        if (_autoRetainerExitCharacterId != 0 || !_autoRetainerHandoffActive ||
            _autoRetainerAutomationBlocked ||
            _autoRetainerNavigationStage != AutoRetainerNavigationStage.PreparingList ||
            CurrentCharacterId == 0 || CurrentCharacterId != _autoRetainerHandoffCharacterId ||
            !IsAutoRetainerQuitMenuReady() ||
            !_uiReader.TryGetActiveRetainer(out var retainerId, out var retainerName) || retainerId == 0 ||
            !string.Equals(retainerName, _autoRetainerHandoffRetainerName, StringComparison.OrdinalIgnoreCase))
            return false;

        _autoRetainerExitCharacterId = CurrentCharacterId;
        _autoRetainerExitRetainerId = retainerId;
        _autoRetainerExitRequestedUtc = DateTime.UtcNow;
        Log.Information("[RR][AR][Buyback] Requesting verified retainer exit cid={Cid} retainer={Retainer}/{RetainerId} stage={Stage}.",
            CurrentCharacterId, retainerName, retainerId, _autoRetainerNavigationStage);
        return true;
    }

    private void LoadAutoRetainerBuybackPrompts()
    {
        var clientLanguage = Svc.Data.Language.ToString();
        if (_autoRetainerBuybackLanguage == clientLanguage) return;
        _autoRetainerBuybackLanguage = clientLanguage;
        _autoRetainerBuybackPrompts.Clear();
        if (!Enum.TryParse<Lumina.Data.Language>(clientLanguage, out var language))
        {
            Log.Warning("[RR][AR][Buyback] Unsupported client language {Language}; confirmation disabled.", clientLanguage);
            return;
        }
        (string Name, uint Row)[] candidates =
        [
            ("custom/000/CmnDefRetainerCall_00010", 215),
            ("custom/005/CmnDefRetainerBell_00544", 52),
        ];
        foreach (var candidate in candidates)
        {
            try
            {
                var sheet = Svc.Data.Excel.GetSheet<Lumina.Excel.RawRow>(language, candidate.Name);
                if (sheet.Language != language || !sheet.HasRow(candidate.Row)) continue;
                var text = sheet.GetRow(candidate.Row).ReadStringColumn(1).ExtractText();
                if (!string.IsNullOrWhiteSpace(text)) _autoRetainerBuybackPrompts.Add(text);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[RR][AR][Buyback] Localized warning lookup failed: sheet={Sheet} row={Row} language={Language}.",
                    candidate.Name, candidate.Row, language);
            }
        }
        Log.Information("[RR][AR][Buyback] Loaded {Count} distinct localized warning(s), language={Language}. Missing text disables confirmation.",
            _autoRetainerBuybackPrompts.Count, language);
    }

    private void LogAutoRetainerBuybackWait(string reason, string? prompt = null)
    {
        var signature = reason + "|" + prompt;
        if (_autoRetainerBuybackDiagnostic == signature) return;
        _autoRetainerBuybackDiagnostic = signature;
        Log.Warning("[RR][AR][Buyback] {Reason}; stage={Stage} cid={Cid} exitRetainer={RetainerId} attempted={Attempted} exitAgeSeconds={Age:F2} prompt={Prompt}.",
            reason, _autoRetainerNavigationStage, CurrentCharacterId, _autoRetainerExitRetainerId,
            _autoRetainerBuybackAttempted,
            _autoRetainerExitCharacterId == 0 ? 0 : (DateTime.UtcNow - _autoRetainerExitRequestedUtc).TotalSeconds,
            prompt == null ? "unavailable" : prompt.Length > 512 ? prompt[..512] : prompt);
    }

    private bool AdvanceAutoRetainerBuybackConfirmation()
    {
        if (_autoRetainerExitCharacterId != 0 &&
            (!_autoRetainerHandoffActive || CurrentCharacterId != _autoRetainerExitCharacterId ||
             CurrentCharacterId != _autoRetainerHandoffCharacterId ||
             (_uiReader.TryGetActiveRetainer(out var activeId, out _) && activeId != _autoRetainerExitRetainerId)))
        {
            LogAutoRetainerBuybackWait("Exit ownership changed; invalidating confirmation authorization");
            ClearAutoRetainerExit();
            // Do not navigate or authorize another exit on the same tick.
            return true;
        }

        if (!IsAutoRetainerAddonBlocking("SelectYesno"))
        {
            if (_autoRetainerBuybackAttempted && !_autoRetainerBuybackClosed)
            {
                _autoRetainerBuybackClosed = true;
                Log.Information("[RR][AR][Buyback] Confirmation closed; waiting for verified RetainerList. cid={Cid} retainer={RetainerId}.",
                    CurrentCharacterId, _autoRetainerExitRetainerId);
            }
            if (_autoRetainerExitCharacterId != 0 && IsAutoRetainerAddonReady("RetainerList") && IsRetainerListReady())
            {
                Log.Information("[RR][AR][Buyback] Exit completed; RetainerList ready. confirmationAttempted={Attempted}.", _autoRetainerBuybackAttempted);
                ClearAutoRetainerExit();
            }
            return false;
        }

        if (_autoRetainerExitCharacterId == 0 || !_autoRetainerHandoffActive || _autoRetainerAutomationBlocked ||
            _autoRetainerNavigationStage is not (AutoRetainerNavigationStage.PreparingList or AutoRetainerNavigationStage.RecoveringMenu or AutoRetainerNavigationStage.WaitingForQuitMenu))
        {
            LogAutoRetainerBuybackWait("Yes/No is not authorized by an RR-owned retainer exit; leaving untouched");
            return true;
        }
        if (_autoRetainerBuybackAttempted)
        {
            LogAutoRetainerBuybackWait("Yes/No remains unresolved after one confirmation attempt; no retry");
            return true;
        }
        if (!IsAutoRetainerAddonReady("SelectYesno")) return true;

        try
        {
            var addon = GameGui.GetAddonByName("SelectYesno", 1);
            if (addon.IsNull) return true;
            var unit = (AtkUnitBase*)addon.Address;
            if (unit->AtkValues == null || unit->AtkValuesCount == 0)
            {
                LogAutoRetainerBuybackWait("Yes/No has no readable prompt");
                return true;
            }
            var value = unit->AtkValues[0];
            if (value.Type is not (AtkValueType.String or AtkValueType.ConstString or AtkValueType.ManagedString) || value.String.Value == null)
            {
                LogAutoRetainerBuybackWait("Yes/No prompt type is unsupported");
                return true;
            }
            const int limit = 2048;
            var length = 0;
            while (length < limit && value.String.Value[length] != 0) length++;
            if (length == limit)
            {
                LogAutoRetainerBuybackWait("Yes/No prompt exceeds bounded read limit");
                return true;
            }
            var prompt = Encoding.UTF8.GetString(new ReadOnlySpan<byte>(value.String.Value, length));
            LoadAutoRetainerBuybackPrompts();
            if (!_autoRetainerBuybackPrompts.Contains(prompt))
            {
                LogAutoRetainerBuybackWait("Unknown or unlocalized Yes/No prompt; leaving untouched", prompt);
                return true;
            }
            var dialog = (AddonSelectYesno*)addon.Address;
            if (dialog->YesButton == null || !dialog->YesButton->IsEnabled || dialog->NoButton == null ||
                AutoRetainerBlockingDialogs.Any(name => name != "SelectYesno" && IsAutoRetainerAddonBlocking(name)))
            {
                LogAutoRetainerBuybackWait("Recognized buyback warning; waiting for button readiness and other dialogs", prompt);
                return true;
            }
            // Set before dispatch: an exception or ignored click must never cause a retry.
            _autoRetainerBuybackAttempted = true;
            RecordAutoRetainerAction("confirm retainer recall buyback warning");
            Log.Information("[RR][AR][Buyback] Confirming recall once; item buyback will no longer be available. cid={Cid} retainer={RetainerId} stage={Stage} prompt={Prompt}.",
                CurrentCharacterId, _autoRetainerExitRetainerId, _autoRetainerNavigationStage, prompt);
            new AddonMaster.SelectYesno(addon.Address) { RespectDisabledButtons = true, RespectHoldButtons = true }.Yes();
        }
        catch (Exception ex)
        {
            // Invalidate authorization even if reading failed before dispatch; no automatic retry.
            _autoRetainerBuybackAttempted = true;
            Log.Warning(ex, "[RR][AR][Buyback] Confirmation handling failed; waiting for bounded recovery without retry.");
        }
        return true;
    }
}
