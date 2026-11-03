using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace RetainerRepricer;

public unsafe sealed partial class Plugin
{
    private static readonly string[] RetainerDumpAddonNames = ["SelectString", "SelectYesno"];
    private readonly Dictionary<string, string> _retainerDumpSnapshots = new();
    private DateTime _retainerDumpExpiresUtc;
    private DateTime _retainerDumpNextSampleUtc;
    private bool _retainerDumpRequested;
    private bool _buybackSheetDumpRequested;
    private int _retainerDumpCount;
    private const int RetainerDumpLimit = 40;

    internal bool RetainerAddonCaptureActive => _retainerDumpExpiresUtc != default;
    internal string RetainerAddonDumpStatus { get; private set; } = "Idle";

    internal void RequestRetainerAddonDump() => _retainerDumpRequested = true;
    internal void RequestBuybackSheetDump() => _buybackSheetDumpRequested = true;

    private void DumpBuybackWarningSheets()
    {
        var clientLanguage = Svc.Data.Language;
        if (!Enum.TryParse<Lumina.Data.Language>(clientLanguage.ToString(), out var language))
        {
            Log.Warning("[RR][BuybackSheets] Unsupported client language: {Language}.", clientLanguage);
            return;
        }

        string? prompt = null;
        var addon = GameGui.GetAddonByName("SelectYesno", 1);
        if (!addon.IsNull)
        {
            var unit = (AtkUnitBase*)addon.Address;
            if (unit->IsVisible && unit->IsReady && unit->AtkValues != null && unit->AtkValuesCount > 0)
            {
                var value = unit->AtkValues[0];
                if (value.Type is AtkValueType.String or AtkValueType.ManagedString or AtkValueType.ConstString &&
                    value.String.Value != null)
                {
                    const int limit = 2048;
                    var length = 0;
                    while (length < limit && value.String.Value[length] != 0) length++;
                    if (length < limit)
                        prompt = Encoding.UTF8.GetString(new ReadOnlySpan<byte>(value.String.Value, length));
                }
            }
        }
        Log.Information("[RR][BuybackSheets] Read-only test; clientLanguage={Language} livePromptAvailable={Available} livePrompt={Prompt}. No callbacks are sent.",
            clientLanguage, prompt != null, prompt ?? "unavailable (absent, unready, unsupported or too long)");

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
                if (!sheet.HasRow(candidate.Row))
                {
                    Log.Warning("[RR][BuybackSheets] sheet={Sheet} row={Row} column=1 requestedLanguage={RequestedLanguage} sheetLanguage={SheetLanguage} error=row missing",
                        candidate.Name, candidate.Row, language, sheet.Language);
                    continue;
                }
                var text = sheet.GetRow(candidate.Row).ReadStringColumn(1).ExtractText();
                Log.Information("[RR][BuybackSheets] sheet={Sheet} row={Row} column=1 requestedLanguage={RequestedLanguage} sheetLanguage={SheetLanguage} text={Text} exactPromptMatch={Match}",
                    candidate.Name, candidate.Row, language, sheet.Language, text,
                    prompt == null ? "unavailable" : string.Equals(text, prompt, StringComparison.Ordinal).ToString());
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[RR][BuybackSheets] sheet={Sheet} row={Row} column=1 language={Language} lookup failed.",
                    candidate.Name, candidate.Row, language);
            }
        }
    }

    internal void ArmRetainerAddonCapture()
    {
        _retainerDumpSnapshots.Clear();
        _retainerDumpCount = 0;
        _retainerDumpNextSampleUtc = default;
        _retainerDumpExpiresUtc = DateTime.UtcNow.AddSeconds(60);
        RetainerAddonDumpStatus = "Watching SelectString / SelectYesno for 60 seconds (read-only).";
        Log.Information("[RR][AddonDump] Armed 60s read-only capture; framework observations, not lifecycle events. No callbacks are sent.");
    }

    internal void StopRetainerAddonCapture()
    {
        _retainerDumpExpiresUtc = default;
        RetainerAddonDumpStatus = "Capture stopped; see Logs ([RR][AddonDump]).";
        Log.Information("[RR][AddonDump] Capture stopped; snapshots={Count}.", _retainerDumpCount);
    }

    private void TickRetainerAddonDump()
    {
        if (_buybackSheetDumpRequested)
        {
            _buybackSheetDumpRequested = false;
            try { DumpBuybackWarningSheets(); }
            catch (Exception ex) { Log.Warning(ex, "[RR][BuybackSheets] Read-only sheet test failed."); }
        }
        var now = DateTime.UtcNow;
        if (RetainerAddonCaptureActive && now >= _retainerDumpExpiresUtc)
            StopRetainerAddonCapture();
        var force = _retainerDumpRequested;
        _retainerDumpRequested = false;
        if (!force && (!RetainerAddonCaptureActive || now < _retainerDumpNextSampleUtc)) return;
        _retainerDumpNextSampleUtc = now.AddMilliseconds(100);
        foreach (var name in RetainerDumpAddonNames)
        {
            if (RetainerAddonCaptureActive && _retainerDumpCount >= RetainerDumpLimit)
            {
                StopRetainerAddonCapture();
                break;
            }
            try
            {
                var snapshot = ReadRetainerAddonSnapshot(name);
                if (!force && _retainerDumpSnapshots.TryGetValue(name, out var previous) && previous == snapshot)
                    continue;
                _retainerDumpSnapshots[name] = snapshot;
                _uiReader.TryGetActiveRetainer(out var retainerId, out var retainerName);
                Log.Information("[RR][AddonDump] at={At:O} addon={Addon} cid={Cid} retainer={Retainer}/{RetainerId} stage={Stage} handoff={Handoff} blocked={Blocked} lastAction={Action}; {Snapshot}",
                    now, name, CurrentCharacterId, retainerName, retainerId, _autoRetainerNavigationStage,
                    _autoRetainerHandoffActive, _autoRetainerAutomationBlocked, _autoRetainerLastAction, snapshot);
                _retainerDumpCount++;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[RR][AddonDump] Capture failed for {Addon}; stopping capture.", name);
                StopRetainerAddonCapture();
                break;
            }
        }
        if (force && !RetainerAddonCaptureActive)
            RetainerAddonDumpStatus = "Snapshot captured; see Logs ([RR][AddonDump]).";
    }

    private string ReadRetainerAddonSnapshot(string name)
    {
        var addon = GameGui.GetAddonByName(name, 1);
        if (addon.IsNull) return "absent";
        var unit = (AtkUnitBase*)addon.Address;
        var text = new StringBuilder($"address=0x{addon.Address:X} visible={unit->IsVisible} ready={unit->IsReady} valueCount={unit->AtkValuesCount}");
        // Never inspect values/components while the addon is being set up or torn down.
        if (!unit->IsVisible || !unit->IsReady) return text.ToString();
        if (unit->AtkValues != null)
        {
            var count = Math.Min((int)unit->AtkValuesCount, 64);
            for (var index = 0; index < count; index++)
            {
                var value = unit->AtkValues[index];
                var decoded = value.Type switch
                {
                    AtkValueType.Bool => value.Bool.ToString(),
                    AtkValueType.Int => value.Int.ToString(CultureInfo.InvariantCulture),
                    AtkValueType.UInt => value.UInt.ToString(CultureInfo.InvariantCulture),
                    AtkValueType.Float => value.Float.ToString(CultureInfo.InvariantCulture),
                    AtkValueType.String or AtkValueType.ManagedString or AtkValueType.ConstString
                        => ReadRetainerDumpString(value.String.Value),
                    _ => "omitted (not dereferenced)",
                };
                text.Append($"\n  value[{index}] type={value.Type} data={decoded}");
            }
            if (unit->AtkValuesCount > count) text.Append($"\n  values truncated at {count}");
        }
        if (name == "SelectYesno")
        {
            var dialog = (AddonSelectYesno*)addon.Address;
            text.Append($"\n  yesPresent={dialog->YesButton != null} noPresent={dialog->NoButton != null}");
            if (dialog->YesButton != null) text.Append($" yesEnabled={dialog->YesButton->IsEnabled}");
            if (dialog->NoButton != null) text.Append($" noEnabled={dialog->NoButton->IsEnabled}");
        }
        return text.ToString();
    }

    private static string ReadRetainerDumpString(byte* value)
    {
        if (value == null) return "null";
        const int limit = 512;
        var length = 0;
        while (length < limit && value[length] != 0) length++;
        var text = Encoding.UTF8.GetString(new ReadOnlySpan<byte>(value, length))
            .Replace("\r", "\\r").Replace("\n", "\\n").Replace("\0", "\\0");
        return $"\"{text}\"{(length == limit ? " [truncated]" : string.Empty)}";
    }
}
