using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ECommons.GameHelpers;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using RetainerRepricer.Services;

namespace RetainerRepricer;

public unsafe sealed partial class Plugin
{
    private CancellationTokenSource? _vendorPreviewCancellation;
    private Task<List<VendorPreviewResult>>? _vendorPreviewTask;
    private List<VendorPreviewResult> _vendorPreviewResults = new();
    private bool _vendorPreviewComplete;
    private bool _vendorPreviewInventoryComplete;
    private bool _vendorPreviewAutoSellEnabledAtCapture;
    private ulong _vendorPreviewCharacter;
    private DateTime _vendorPreviewCapturedUtc;
    private string _vendorPreviewStatus = "No preview captured.";
    private string _vendorPreviewConfigFingerprint = string.Empty;
    private string? _vendorPreviewListId;
    private bool _vendorPreviewAutoSaleOnCompletion;
    private bool _vendorPreviewStale;
    private enum VendorSalePhase { Idle, EnsureInventory, FetchPrice, OpenMenu, SelectAction, VerifySale }
    private VendorSalePhase _vendorSalePhase;
    private readonly List<VendorPreviewResult> _vendorSaleQueue = new();
    private int _vendorSaleIndex;
    private ulong _vendorSaleCharacter;
    private ulong _vendorSaleRetainerId;
    private bool _vendorSaleStopRequested;
    private bool _vendorSaleDispatched;
    private DateTime _vendorSalePhaseStartedUtc;
    private DateTime _vendorSaleNextContextCheckUtc;
    private long _vendorSaleBaselineQuantity;
    private Task<VendorPreviewPrice?>? _vendorSalePriceTask;
    private CancellationTokenSource? _vendorSalePriceCancellation;
    private VendorPreviewResult? _vendorSaleCurrent;
    private int _vendorSaleSucceeded;
    private int _vendorSaleUncertain;
    private string _vendorSaleStatus = "No vendor sale started.";
    private string _vendorSaleLastContextDiagnostic = "No context-menu snapshot captured.";
    private DateTime _vendorAutoSaleAttemptedPreviewUtc;
    private ulong _vendorAutoSaleAttemptedRetainerId;

    internal IReadOnlyList<VendorPreviewResult> VendorPreviewResults => _vendorPreviewResults;
    internal bool VendorPreviewRunning => _vendorPreviewTask is { IsCompleted: false };
    internal bool VendorPreviewComplete => _vendorPreviewComplete;
    internal bool VendorPreviewInventoryComplete => _vendorPreviewInventoryComplete;
    internal DateTime VendorPreviewCapturedUtc => _vendorPreviewCapturedUtc;
    internal string VendorPreviewStatus => _vendorPreviewStatus;
    internal bool VendorPreviewStale => _vendorPreviewStale;
    internal string? VendorPreviewListId => _vendorPreviewListId;
    internal bool VendorSaleRunning => _vendorSalePhase != VendorSalePhase.Idle;
    internal string VendorSaleStatus => _vendorSaleStatus;
    internal int VendorSaleCandidateCount => _vendorSaleQueue.Count;

    internal bool CanStartVendorSale
        => !VendorSaleRunning && !VendorPreviewRunning && _vendorPreviewComplete && _vendorPreviewInventoryComplete &&
           !_vendorPreviewStale && _vendorPreviewCharacter == CurrentCharacterId &&
            DateTime.UtcNow - _vendorPreviewCapturedUtc <= TimeSpan.FromMinutes(5) &&
            !IsRunning && !_autoRetainerHandoffActive && Player.Available &&
            Configuration.PluginEnabled &&
            (_vendorPreviewListId == null ||
             Configuration.GetCharacterSettings(CurrentCharacterId).VendorAllowlistIds.Contains(_vendorPreviewListId)) &&
            _uiReader.TryGetActiveRetainer(out _, out _);

    internal void StartVendorSale(bool automatic = false)
    {
        if (automatic && !Configuration.AutoSellVendorPreviewCandidates) return;
        if (!CanStartVendorSale || !Player.Available)
        {
            _vendorSaleStatus = "Cannot start: refresh a complete preview, stop RR/AutoRetainer, and open a retainer.";
            return;
        }
        if (!_uiReader.TryGetActiveRetainer(out var retainerId, out _))
        {
            _vendorSaleStatus = "Cannot start: open a retainer first.";
            return;
        }

        var candidates = _vendorPreviewResults.Where(result => result.IsCandidate).ToList();
        if (candidates.Count == 0)
        {
            _vendorSaleStatus = "No vendor candidates in the completed preview.";
            return;
        }

        // A preview may launch at most one batch, whether started manually or automatically.
        _vendorAutoSaleAttemptedPreviewUtc = _vendorPreviewCapturedUtc;
        _vendorAutoSaleAttemptedRetainerId = retainerId;
        _vendorSaleQueue.Clear();
        _vendorSaleQueue.AddRange(candidates);
        _vendorSaleIndex = 0;
        _vendorSaleCharacter = CurrentCharacterId;
        _vendorSaleRetainerId = retainerId;
        _vendorSaleSucceeded = 0;
        _vendorSaleUncertain = 0;
        _vendorSaleStopRequested = false;
        _vendorSaleDispatched = false;
        _vendorSaleCurrent = null;
        _vendorSaleLastContextDiagnostic = "No context-menu snapshot captured.";
        _vendorSalePhase = VendorSalePhase.EnsureInventory;
        _vendorSalePhaseStartedUtc = DateTime.UtcNow;
        _vendorSaleStatus = $"Sale batch started: {_vendorSaleQueue.Count} stack(s) queued. Keep character and retainer open.";
        Log.Information("[RR][VendorSale] Batch started for retainer {RetainerId}; candidateStacks={Count}; previewAgeSeconds={AgeSeconds:0.0}.",
            retainerId, _vendorSaleQueue.Count, (DateTime.UtcNow - _vendorPreviewCapturedUtc).TotalSeconds);
        if (automatic) Log.Information("[RR][VendorSale] Automatic batch started from completed preview.");
    }

    internal void StopVendorSale()
    {
        if (!VendorSaleRunning) return;
        _vendorSaleStopRequested = true;
        _vendorSaleStatus = _vendorSaleDispatched
            ? "Stop requested; verifying the already-dispatched sale. No further sales will start."
            : "Stop requested; no further sales will start.";
    }

    internal void StartVendorPreview(string? selectedListId = null, bool autoSellWhenComplete = false)
    {
        CancelVendorPreview();
        var cid = CurrentCharacterId;
        var snapshot = _uiReader.GetVendorInventorySnapshot();
        HashSet<ulong> allowed;
        Dictionary<ulong, int> thresholds;
        if (!string.IsNullOrWhiteSpace(selectedListId) && Configuration.VendorAllowlists?.TryGetValue(selectedListId, out var selectedList) == true)
        {
            var characterLimit = Configuration.GetCharacterSettings(cid).AutoListMinimumStackValue;
            allowed = selectedList.ItemKeys.ToHashSet();
            thresholds = allowed.ToDictionary(key => key, key => Math.Min(selectedList.MaximumStackMarketValue, characterLimit));
        }
        else
        {
            selectedListId = null;
            allowed = Configuration.GetEffectiveVendorAllowlist(cid);
            thresholds = allowed.ToDictionary(key => key, key => Configuration.GetEffectiveVendorThreshold(cid, key));
        }
        var blocked = Configuration.GetEffectiveAutoListBlocklist(cid);
        var protectedItems = Configuration.GetSellListOrdered(cid)
            .Select(entry => Configuration.MakeSellKey(entry.ItemId, entry.IsHq)).ToHashSet();
        var stacks = snapshot.Stacks;
        _vendorPreviewListId = selectedListId;
        _vendorPreviewAutoSaleOnCompletion = autoSellWhenComplete && selectedListId == null && Configuration.AutoSellVendorPreviewCandidates;
        _vendorPreviewConfigFingerprint = GetVendorPreviewConfigFingerprint(cid, selectedListId);
        _vendorPreviewStale = false;
        var region = Configuration.UseUniversalisApi ? GetWorldDcRegionKey() : null;
        var cancellation = new CancellationTokenSource();
        _vendorPreviewCancellation = cancellation;
        _vendorPreviewCharacter = cid;
        _vendorPreviewCapturedUtc = DateTime.UtcNow;
        _vendorPreviewAutoSellEnabledAtCapture = Configuration.AutoSellVendorPreviewCandidates;
        _vendorPreviewInventoryComplete = snapshot.IsComplete;
        _vendorPreviewComplete = false;
        _vendorPreviewStatus = snapshot.IsComplete ? "Resolving market prices…" : "Inventory snapshot incomplete.";
        var capturedUtc = _vendorPreviewCapturedUtc;
        _vendorPreviewTask = VendorPreviewResolver.ResolveAsync(
            _universalisClient, stacks, allowed, thresholds, blocked, protectedItems, region, cancellation.Token);
    }

    internal void CancelVendorPreview()
    {
        _vendorPreviewCancellation?.Cancel();
        _vendorPreviewCancellation?.Dispose();
        _vendorPreviewCancellation = null;
        if (_vendorPreviewTask is { IsCompleted: false })
            _vendorPreviewStatus = "Preview cancelled.";
    }

    private void TickVendorPreview()
    {
        if (_vendorPreviewCapturedUtc != default && !_vendorPreviewStale &&
            (_vendorPreviewCharacter != CurrentCharacterId ||
              _vendorPreviewConfigFingerprint != GetVendorPreviewConfigFingerprint(_vendorPreviewCharacter, _vendorPreviewListId)))
        {
            _vendorPreviewStale = true;
            _vendorPreviewStatus = "Preview stale: vendor settings or protections changed; refresh to update.";
        }
        var task = _vendorPreviewTask;
        if (task is not { IsCompleted: true }) return;
        _vendorPreviewTask = null;
        if (task.IsCanceled)
        {
            _vendorPreviewComplete = false;
            _vendorPreviewStatus = "Preview cancelled.";
        }
        else if (task.IsFaulted)
        {
            _vendorPreviewComplete = false;
            _vendorPreviewStatus = "Preview failed: " + task.Exception?.GetBaseException().Message;
        }
        else if (_vendorPreviewCharacter != CurrentCharacterId)
        {
            _vendorPreviewComplete = false;
            _vendorPreviewStatus = "Preview discarded: character changed.";
        }
        else
        {
            _vendorPreviewResults = task.Result;
            _vendorPreviewComplete = _vendorPreviewInventoryComplete;
            _vendorPreviewStatus = $"Preview captured: {_vendorPreviewResults.Count(r => r.IsCandidate)} candidate stack(s).";
        }
        _vendorPreviewCancellation?.Dispose();
        _vendorPreviewCancellation = null;
    }

    private void TickAutomaticVendorSale()
    {
        if (!Configuration.AutoSellVendorPreviewCandidates || !_vendorPreviewAutoSaleOnCompletion || !_vendorPreviewAutoSellEnabledAtCapture || VendorSaleRunning || VendorPreviewRunning ||
            !_vendorPreviewComplete || !_vendorPreviewInventoryComplete || _vendorPreviewStale ||
            _vendorPreviewCharacter != CurrentCharacterId ||
            DateTime.UtcNow - _vendorPreviewCapturedUtc > TimeSpan.FromMinutes(5) ||
            !Configuration.PluginEnabled || !Player.Available || IsRunning || _autoRetainerHandoffActive ||
            !_uiReader.IsInventoryWindowVisible() || !_uiReader.TryGetActiveRetainer(out var retainerId, out _))
            return;

        if (_vendorAutoSaleAttemptedPreviewUtc == _vendorPreviewCapturedUtc &&
            _vendorAutoSaleAttemptedRetainerId == retainerId)
            return;

        if (!_vendorPreviewResults.Any(result => result.IsCandidate))
        {
            _vendorAutoSaleAttemptedPreviewUtc = _vendorPreviewCapturedUtc;
            _vendorAutoSaleAttemptedRetainerId = retainerId;
            return;
        }

        if (!CanStartVendorSale) return;
        _vendorAutoSaleAttemptedPreviewUtc = _vendorPreviewCapturedUtc;
        _vendorAutoSaleAttemptedRetainerId = retainerId;
        StartVendorSale(automatic: true);
    }

    internal void StartVendorPreviewForAppliedList(ulong characterId)
    {
        if (characterId != CurrentCharacterId || !Player.Available || IsRunning || _autoRetainerHandoffActive ||
            VendorSaleRunning || !_uiReader.TryGetActiveRetainer(out _, out _))
            return;
        StartVendorPreview(autoSellWhenComplete: true);
        Log.Information("[RR][VendorSale] Preview refreshed after vendor list assignment for active character {CharacterId}.", characterId);
    }

    private void TickVendorSale()
    {
        if (!VendorSaleRunning) return;
        if (_vendorSaleCharacter != CurrentCharacterId || !_uiReader.TryGetActiveRetainer(out var activeRetainer, out _) || activeRetainer != _vendorSaleRetainerId)
        {
            if (_vendorSaleDispatched) _vendorSaleUncertain++;
            FinishVendorSale(_vendorSaleDispatched
                ? "Stopped: retainer/character changed during sale; outcome uncertain. No retry attempted."
                : "Stopped: retainer/character changed before sale.");
            return;
        }
        if (_vendorSalePhase != VendorSalePhase.VerifySale &&
            (IsRunning || _autoRetainerHandoffActive || _vendorPreviewStale ||
             _vendorPreviewConfigFingerprint != GetVendorPreviewConfigFingerprint(_vendorSaleCharacter, _vendorPreviewListId)))
        {
            FinishVendorSale("Stopped: automation started or preview protections/settings changed.");
            return;
        }

        if (_vendorSaleStopRequested && !_vendorSaleDispatched)
        {
            FinishVendorSale($"Stopped by user. Confirmed {_vendorSaleSucceeded}; uncertain {_vendorSaleUncertain}.");
            return;
        }

        switch (_vendorSalePhase)
        {
            case VendorSalePhase.EnsureInventory:
                if (DateTime.UtcNow - _vendorSalePhaseStartedUtc > TimeSpan.FromSeconds(15))
                {
                    FinishVendorSale("Stopped: inventory window failed to become ready.");
                    return;
                }
                if (_uiReader.TryEnsureInventoryWindowOpen())
                {
                    Log.Information("[RR][VendorSale] Phase=InventoryReady; inventory window visible; stackIndex={Index}.", _vendorSaleIndex + 1);
                    if (_vendorSaleIndex >= _vendorSaleQueue.Count)
                    {
                        FinishVendorSale($"Batch complete. Confirmed {_vendorSaleSucceeded}; uncertain {_vendorSaleUncertain}.");
                        return;
                    }
                    _vendorSaleCurrent = _vendorSaleQueue[_vendorSaleIndex];
                    _vendorSaleDispatched = false;
                    var region = Configuration.UseUniversalisApi ? GetWorldDcRegionKey() : null;
                    _vendorSalePriceCancellation = new CancellationTokenSource();
                    _vendorSalePriceTask = VendorPreviewResolver.FetchPriceAsync(_universalisClient,
                        _vendorSaleCurrent.Stack.ItemId, _vendorSaleCurrent.Stack.IsHq, region, _vendorSalePriceCancellation.Token);
                    _vendorSalePhase = VendorSalePhase.FetchPrice;
                    _vendorSalePhaseStartedUtc = DateTime.UtcNow;
                }
                break;

            case VendorSalePhase.FetchPrice:
                if (DateTime.UtcNow - _vendorSalePhaseStartedUtc > TimeSpan.FromSeconds(20))
                {
                    FinishVendorSale("Stopped: fresh price check timed out; no sale dispatched.");
                    return;
                }
                if (_vendorSalePriceTask is not { IsCompleted: true } priceTask) break;
                _vendorSalePriceTask = null;
                _vendorSalePriceCancellation?.Dispose();
                _vendorSalePriceCancellation = null;
                if (priceTask.IsCanceled || priceTask.IsFaulted || priceTask.Result is not { } price ||
                    !TryGetLiveVendorStack(_vendorSaleCurrent!.Stack, out var liveStack))
                {
                    FinishVendorSale("Stopped: price unavailable or inventory stack changed; no sale dispatched.");
                    return;
                }
                var cid = _vendorSaleCharacter;
                var key = Configuration.MakeSellKey(liveStack.ItemId, liveStack.IsHq);
                var freshDecision = VendorPreviewEvaluator.Evaluate(liveStack,
                    Configuration.GetEffectiveVendorAllowlist(cid).Contains(key),
                    Configuration.GetEffectiveAutoListBlocklist(cid).Contains(key),
                    Configuration.HasSellItem(liveStack.ItemId, liveStack.IsHq, cid),
                    Configuration.GetEffectiveVendorThreshold(cid, key),
                    price, DateTime.UtcNow);
                if (!freshDecision.IsCandidate)
                {
                    FinishVendorSale($"Stopped: {_vendorSaleCurrent.Stack.Name} no longer qualifies ({freshDecision.Reason}); no sale dispatched.");
                    return;
                }
                Log.Information("[RR][VendorSale] Phase=PriceValidated; item={ItemId} HQ={Hq} container={Container} slot={Slot} qty={Quantity} unitMarket={UnitMarket} stackMarket={StackMarket} limit={Limit} npcEstimate={NpcEstimate} source={Source} priceUtc={PriceUtc:o}.",
                    liveStack.ItemId, liveStack.IsHq, liveStack.Container, liveStack.Slot, liveStack.Quantity,
                    freshDecision.UnitMarketValue, freshDecision.StackMarketValue,
                    Configuration.GetEffectiveVendorThreshold(cid, key), freshDecision.EstimatedNpcProceeds,
                    freshDecision.PriceSource, freshDecision.PriceTimestampUtc);
                var currentBag = _uiReader.GetVendorInventorySnapshot();
                if (!currentBag.IsComplete)
                {
                    FinishVendorSale("Stopped: inventory snapshot became incomplete; no sale dispatched.");
                    return;
                }
                _vendorSaleBaselineQuantity = currentBag.Stacks
                    .Where(stack => stack.ItemId == liveStack.ItemId && stack.IsHq == liveStack.IsHq).Sum(stack => (long)stack.Quantity);
                _vendorSalePhase = VendorSalePhase.OpenMenu;
                _vendorSalePhaseStartedUtc = DateTime.UtcNow;
                Log.Information("[RR][VendorSale] Phase=OpenMenu; item={ItemId} HQ={Hq} container={Container} slot={Slot} qty={Quantity} totalQualityQuantity={Baseline}.",
                    liveStack.ItemId, liveStack.IsHq, liveStack.Container, liveStack.Slot, liveStack.Quantity, _vendorSaleBaselineQuantity);
                break;

            case VendorSalePhase.OpenMenu:
                if (!TryGetLiveVendorStack(_vendorSaleCurrent!.Stack, out _))
                {
                    FinishVendorSale("Stopped: stack changed before opening its menu.");
                    return;
                }
                if (_uiReader.TryOpenInventoryContextMenu((InventoryType)_vendorSaleCurrent.Stack.Container, _vendorSaleCurrent.Stack.Slot))
                {
                    _vendorSalePhase = VendorSalePhase.SelectAction;
                    _vendorSalePhaseStartedUtc = DateTime.UtcNow;
                    _vendorSaleNextContextCheckUtc = DateTime.MinValue;
                    Log.Information("[RR][VendorSale] Phase=ContextMenuRequested; item={ItemId} HQ={Hq} container={Container} slot={Slot} qty={Quantity}; awaiting populated menu.",
                        _vendorSaleCurrent.Stack.ItemId, _vendorSaleCurrent.Stack.IsHq,
                        _vendorSaleCurrent.Stack.Container, _vendorSaleCurrent.Stack.Slot, _vendorSaleCurrent.Stack.Quantity);
                }
                else if (DateTime.UtcNow - _vendorSalePhaseStartedUtc > TimeSpan.FromSeconds(15))
                {
                    Log.Warning("[RR][VendorSale] Context menu open failed; item={ItemId} HQ={Hq} container={Container} slot={Slot}; inventory/context agents unavailable or inventory window not ready.",
                        _vendorSaleCurrent.Stack.ItemId, _vendorSaleCurrent.Stack.IsHq,
                        _vendorSaleCurrent.Stack.Container, _vendorSaleCurrent.Stack.Slot);
                    FinishVendorSale("Stopped: unable to open inventory context menu.");
                }
                break;

            case VendorSalePhase.SelectAction:
                if (DateTime.UtcNow - _vendorSalePhaseStartedUtc > TimeSpan.FromSeconds(5))
                {
                    Log.Warning("[RR][VendorSale] Context action timeout; final menu snapshot: {Diagnostic}", _vendorSaleLastContextDiagnostic);
                    FinishVendorSale("Stopped: retainer sell action did not appear; no sale dispatched.");
                    return;
                }
                if (DateTime.UtcNow < _vendorSaleNextContextCheckUtc) break;
                _vendorSaleNextContextCheckUtc = DateTime.UtcNow + TimeSpan.FromMilliseconds(250);
                _vendorSaleDispatched = true;
                if (!TryDispatchRetainerVendorSell(_vendorSaleCurrent!.Stack))
                {
                    _vendorSaleDispatched = false;
                    break;
                }
                _vendorSalePhase = VendorSalePhase.VerifySale;
                _vendorSalePhaseStartedUtc = DateTime.UtcNow;
                _vendorSaleStatus = $"Sale dispatched for {_vendorSaleCurrent.Stack.Name}; verifying inventory change…";
                Log.Information("[RR][VendorSale] Phase=SelectionAttempted; action=Have Retainer Sell Items; item={ItemId} HQ={Hq} container={Container} slot={Slot} qty={Quantity}.",
                    _vendorSaleCurrent.Stack.ItemId, _vendorSaleCurrent.Stack.IsHq,
                    _vendorSaleCurrent.Stack.Container, _vendorSaleCurrent.Stack.Slot, _vendorSaleCurrent.Stack.Quantity);
                break;

            case VendorSalePhase.VerifySale:
                if (!_vendorSaleDispatched || _vendorSaleCurrent == null)
                {
                    FinishVendorSale("Stopped: invalid sale verification state.");
                    return;
                }
                var verifySnapshot = _uiReader.GetVendorInventorySnapshot();
                if (verifySnapshot.IsComplete)
                {
                    var remaining = verifySnapshot.Stacks.Where(stack => stack.ItemId == _vendorSaleCurrent.Stack.ItemId &&
                        stack.IsHq == _vendorSaleCurrent.Stack.IsHq).Sum(stack => (long)stack.Quantity);
                    var removed = _vendorSaleBaselineQuantity - remaining;
                    if (removed == _vendorSaleCurrent.Stack.Quantity)
                    {
                        Log.Information("[RR][VendorSale] Phase=Verified; item={ItemId} HQ={Hq} expectedRemoval={Expected} observedRemoval={Observed} totalBefore={Before} totalAfter={After}.",
                            _vendorSaleCurrent.Stack.ItemId, _vendorSaleCurrent.Stack.IsHq,
                            _vendorSaleCurrent.Stack.Quantity, removed, _vendorSaleBaselineQuantity, remaining);
                        _vendorSaleSucceeded++;
                        _vendorSaleIndex++;
                        _vendorSaleDispatched = false;
                        _vendorSaleCurrent = null;
                        _vendorSalePhase = _vendorSaleStopRequested ? VendorSalePhase.Idle : VendorSalePhase.EnsureInventory;
                        _vendorSalePhaseStartedUtc = DateTime.UtcNow;
                        if (_vendorSaleStopRequested)
                            _vendorSaleStatus = $"Stopped by user after confirmed sale. Confirmed {_vendorSaleSucceeded}; uncertain {_vendorSaleUncertain}.";
                        else
                            _vendorSaleStatus = $"Confirmed sale {_vendorSaleSucceeded}/{_vendorSaleQueue.Count}.";
                        return;
                    }
                    if (removed < 0 || removed > _vendorSaleCurrent.Stack.Quantity)
                    {
                        _vendorSaleUncertain++;
                        Log.Warning("[RR][VendorSale] Unexpected inventory delta; item={ItemId} HQ={Hq} expectedRemoval={Expected} observedRemoval={Observed} totalBefore={Before} totalAfter={After}; outcome uncertain.",
                            _vendorSaleCurrent.Stack.ItemId, _vendorSaleCurrent.Stack.IsHq,
                            _vendorSaleCurrent.Stack.Quantity, removed, _vendorSaleBaselineQuantity, remaining);
                        FinishVendorSale("Stopped: unexpected inventory delta after sale; outcome uncertain. No retry attempted.");
                        return;
                    }
                }
                if (DateTime.UtcNow - _vendorSalePhaseStartedUtc > TimeSpan.FromSeconds(10))
                {
                    _vendorSaleUncertain++;
                    Log.Warning("[RR][VendorSale] Verification timeout; item={ItemId} HQ={Hq} expectedRemoval={Expected} baselineTotal={Before}; inventoryComplete={Complete}; outcome uncertain.",
                        _vendorSaleCurrent.Stack.ItemId, _vendorSaleCurrent.Stack.IsHq,
                        _vendorSaleCurrent.Stack.Quantity, _vendorSaleBaselineQuantity, verifySnapshot.IsComplete);
                    FinishVendorSale("Stopped: sale outcome could not be verified. No retry attempted.");
                }
                break;
        }
    }

    private bool TryGetLiveVendorStack(VendorInventoryStack expected, out VendorInventoryStack live)
    {
        live = default;
        if (!_uiReader.TryGetInventorySlot((InventoryType)expected.Container, expected.Slot, out var slot) || slot == null ||
            slot->Quantity != expected.Quantity || slot->GetBaseItemId() != expected.ItemId || slot->IsHighQuality() != expected.IsHq)
            return false;
        var snapshot = _uiReader.GetVendorInventorySnapshot();
        if (!snapshot.IsComplete) return false;
        live = snapshot.Stacks.FirstOrDefault(stack => stack.Container == expected.Container && stack.Slot == expected.Slot);
        return live.ItemId == expected.ItemId && live.IsHq == expected.IsHq && live.Quantity == expected.Quantity;
    }

    private bool TryDispatchRetainerVendorSell(VendorInventoryStack stack)
    {
        if (CurrentCharacterId != _vendorSaleCharacter || IsRunning || _autoRetainerHandoffActive ||
            !_uiReader.TryGetActiveRetainer(out var activeRetainerId, out _) || activeRetainerId != _vendorSaleRetainerId ||
            !TryGetLiveVendorStack(stack, out _))
        {
            RecordRetainerContextDiagnostic("pre-selection revalidation failed: character, retainer, automation state, or inventory stack changed");
            return false;
        }
        var selected = _uiReader.TrySelectLiveRetainerVendorSell(stack.Container, stack.Slot, stack.ItemId,
            stack.IsHq, stack.Quantity, _vendorSaleRetainerId, out var attempted, out var diagnostic);
        RecordRetainerContextDiagnostic(diagnostic);
        if (!attempted) return false;
        Log.Information("[RR][VendorSale] Visible action selection attempted; result={Selected}; item={ItemId} HQ={Hq} slot={Slot}.",
            selected, stack.ItemId, stack.IsHq, stack.Slot);
        return true;
    }

    private void RecordRetainerContextDiagnostic(string diagnostic)
    {
        if (string.IsNullOrEmpty(diagnostic) || string.Equals(diagnostic, _vendorSaleLastContextDiagnostic, StringComparison.Ordinal)) return;
        _vendorSaleLastContextDiagnostic = diagnostic;
        Log.Debug("[RR][VendorSale] Context diagnostic: {Diagnostic}", diagnostic);
    }

    private void FinishVendorSale(string reason)
    {
        if (!_vendorSaleDispatched) _uiReader.DismissInventoryContextMenu();
        _vendorSalePriceCancellation?.Cancel();
        _vendorSalePriceCancellation?.Dispose();
        _vendorSalePriceCancellation = null;
        _vendorSalePhase = VendorSalePhase.Idle;
        _vendorSalePriceTask = null;
        _vendorSaleDispatched = false;
        _vendorSaleCurrent = null;
        var summary = $"Confirmed {_vendorSaleSucceeded}; uncertain {_vendorSaleUncertain}.";
        _vendorSaleStatus = reason.Contains(summary, StringComparison.Ordinal)
            ? reason
            : $"{reason} {summary}";
        if (reason.StartsWith("Batch complete.", StringComparison.Ordinal))
            Log.Information("[RR][VendorSale] {Status}", _vendorSaleStatus);
        else
            Log.Warning("[RR][VendorSale] {Status}", _vendorSaleStatus);
    }

    private string GetVendorPreviewConfigFingerprint(ulong cid, string? previewListId = null)
    {
        var settings = Configuration.GetCharacterSettings(cid);
        var keys = Configuration.GetEffectiveVendorAllowlist(cid).OrderBy(k => k);
        var blocked = Configuration.GetEffectiveAutoListBlocklist(cid).OrderBy(k => k);
        var sell = Configuration.GetSellListOrdered(cid).Select(e => Configuration.MakeSellKey(e.ItemId, e.IsHq)).OrderBy(k => k);
        var listThresholds = settings.VendorAllowlistIds
            .Select(id => (Id: id, List: Configuration.VendorAllowlists?.GetValueOrDefault(id)))
            .Where(pair => pair.List != null)
            .Select(pair => $"{pair.Id}:{pair.List!.MaximumStackMarketValue}")
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase);
        var previewList = previewListId != null && Configuration.VendorAllowlists?.TryGetValue(previewListId, out var selectedList) == true
            ? $"{previewListId}:{selectedList.MaximumStackMarketValue}:{string.Join(",", selectedList.ItemKeys.OrderBy(k => k))}"
            : string.Empty;
        return $"{settings.AutoListMinimumStackValue}|{string.Join(",", listThresholds)}|{string.Join(",", settings.VendorAllowlistIds.OrderBy(x => x))}|{string.Join(",", keys)}|{previewList}|{string.Join(",", blocked)}|{string.Join(",", sell)}";
    }
}
