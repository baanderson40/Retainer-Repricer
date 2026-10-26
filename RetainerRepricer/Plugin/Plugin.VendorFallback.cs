using System;
using System.Linq;

using FFXIVClientStructs.FFXIV.Client.Game;
using RetainerRepricer.Services;

namespace RetainerRepricer;

public unsafe sealed partial class Plugin
{
    private void ResetAutoVendorFallbackState()
    {
        _vendorFallbackStage = VendorFallbackStage.None;
        _vendorFallbackSelectionAttempted = false;
        _vendorFallbackBaselineQuantity = 0;
        _vendorFallbackExpectedQuantity = 0;
        _vendorFallbackUnitPrice = 0;
        _vendorFallbackRejectedStackValue = 0;
        _vendorFallbackStartedUtc = DateTime.MinValue;
        _vendorFallbackNextSelectionCheckUtc = DateTime.MinValue;
        _vendorFallbackRetainerId = 0;
        _sellState.VendorFallbackLastContextDiagnostic = string.Empty;
    }

    private bool TryBeginAutoVendorFallback(InventorySlotRef slotRef, int unitPrice, long rejectedStackValue,
        AutoListPriceSource source, DateTime now)
    {
        if (!_currentSellItemIsAutoDiscovered || slotRef.Slot < 0 ||
            !Enum.IsDefined((InventoryType)slotRef.Container) || unitPrice <= 0 || rejectedStackValue < 0)
            return false;

        if (_currentRetainerId == 0)
        {
            var hasActiveRetainer = _uiReader.TryGetActiveRetainer(out var actualRetainerId, out var actualRetainerName);
            Log.Debug("[RR][VendorFallback] Active retainer identity was not captured; expectedName={ExpectedName} activeAvailable={Available} actualName={ActualName} actualRetainer={ActualRetainerId}; not vending item={ItemId} HQ={Hq}.",
                _currentRetainerName, hasActiveRetainer, actualRetainerName, actualRetainerId,
                _currentSellItemId, _currentSellItemIsHq);
            return false;
        }

        var key = Configuration.MakeSellKey(_currentSellItemId, _currentSellItemIsHq);
        var allowlist = Configuration.GetEffectiveVendorAllowlist(_runCharacterId);
        var threshold = Configuration.GetEffectiveVendorThreshold(_runCharacterId, key);
        if (!allowlist.Contains(key) || threshold <= 0 || rejectedStackValue >= threshold ||
            Configuration.GetEffectiveAutoListBlocklist(_runCharacterId).Contains(key) ||
            Configuration.GetSellListOrdered(_runCharacterId).Any(entry =>
                Configuration.MakeSellKey(entry.ItemId, entry.IsHq) == key))
        {
            Log.Debug("[RR][VendorFallback] Threshold miss not vendor-eligible; item={ItemId} HQ={Hq} stackValue={StackValue} vendorLimit={VendorLimit} assigned={Assigned}.",
                _currentSellItemId, _currentSellItemIsHq, rejectedStackValue, threshold, allowlist.Contains(key));
            return false;
        }

        var snapshot = _uiReader.GetVendorInventorySnapshot();
        if (!snapshot.IsComplete)
        {
            Log.Warning("[RR][VendorFallback] Inventory snapshot incomplete; not vending item={ItemId} HQ={Hq}.",
                _currentSellItemId, _currentSellItemIsHq);
            return false;
        }

        var stack = snapshot.Stacks.FirstOrDefault(candidate =>
            candidate.Container == slotRef.Container && candidate.Slot == slotRef.Slot);
        if (stack.ItemId != _currentSellItemId || stack.IsHq != _currentSellItemIsHq ||
            stack.Quantity != _currentSellStackSize || !stack.ItemExists || !stack.IsTradable ||
            stack.IsCollectable || stack.IsBound || stack.NpcSalePrice <= 0)
            return false;

        _vendorFallbackBaselineQuantity = snapshot.Stacks
            .Where(candidate => candidate.ItemId == stack.ItemId && candidate.IsHq == stack.IsHq)
            .Sum(candidate => (long)candidate.Quantity);
        _vendorFallbackExpectedQuantity = stack.Quantity;
        _vendorFallbackUnitPrice = unitPrice;
        _vendorFallbackRejectedStackValue = rejectedStackValue;
        _vendorFallbackRetainerId = _currentRetainerId;
        _vendorFallbackSelectionAttempted = false;
        _vendorFallbackStartedUtc = now;
        _vendorFallbackNextSelectionCheckUtc = DateTime.MinValue;
        _sellState.VendorFallbackLastContextDiagnostic = string.Empty;
        _vendorFallbackStage = VendorFallbackStage.Prepare;
        _processingListedItem = false;
        _pendingSellSlot = slotRef;
        _hasPendingSellSlot = true;
        if (!_hasAttemptedSellSlot || _attemptedSellSlot.Container != slotRef.Container ||
            _attemptedSellSlot.Slot != slotRef.Slot || _attemptedSellItemId != stack.ItemId ||
            _attemptedSellItemIsHq != stack.IsHq || _attemptedSellSlotQuantityBefore != stack.Quantity)
        {
            BeginNewListingAttempt(slotRef, stack.ItemId, stack.IsHq, stack.Quantity,
                GetCurrentExpectedListedCount(), stack.Quantity, now);
        }
        _runPhase = RunPhase.Sell_VendorFallback;
        _lastActionUtc = now;

        Log.Information("[RR][VendorFallback] Eligible threshold miss; item={ItemId} HQ={Hq} qty={Quantity} unitPrice={UnitPrice} stackValue={StackValue} autoListMinimum={AutoListMinimum} vendorLimit={VendorLimit} source={PriceSource}.",
            stack.ItemId, stack.IsHq, stack.Quantity, unitPrice, rejectedStackValue,
            _currentSellMinimumStackValue, threshold, source);
        return true;
    }

    private void TickAutoVendorFallback(DateTime now, bool retainerSellVisible)
    {
        if (_vendorFallbackStage == VendorFallbackStage.None || !_hasPendingSellSlot || !_currentSellItemIsAutoDiscovered)
        {
            StopRun(RunStopReason.Error);
            return;
        }

        var characterMatches = CurrentCharacterId == _runCharacterId;
        var retainerId = 0UL;
        var retainerName = string.Empty;
        var hasActiveRetainer = _uiReader.TryGetActiveRetainer(out retainerId, out retainerName);
        if (!characterMatches || !IsRunning || !hasActiveRetainer || retainerId != _vendorFallbackRetainerId)
        {
            if (_vendorFallbackSelectionAttempted)
            {
                Log.Error("[RR][VendorFallback] Retainer or character changed after selection; expectedRetainer={ExpectedRetainerId} actualRetainer={ActualRetainerId}; outcome uncertain. Stopping without retry.",
                    _vendorFallbackRetainerId, retainerId);
                if (IsAddonVisible("ContextMenu")) _uiReader.DismissInventoryContextMenu();
                StopRun(RunStopReason.Error);
            }
            else
            {
                Log.Warning("[RR][VendorFallback] Identity validation failed before selection; expectedRetainer={ExpectedRetainerId} actualRetainer={ActualRetainerId} actualName={ActualName} characterMatches={CharacterMatches}.",
                    _vendorFallbackRetainerId, retainerId, retainerName, characterMatches);
                FinishAutoVendorFallbackWithoutDispatch(now, "retainer or character changed before selection");
            }
            return;
        }

        switch (_vendorFallbackStage)
        {
            case VendorFallbackStage.Prepare:
                if (now - _vendorFallbackStartedUtc > TimeSpan.FromSeconds(15))
                {
                    FinishAutoVendorFallbackWithoutDispatch(now, "could not prepare inventory context menu");
                    return;
                }
                if (MarketWindowsStillOpen())
                {
                    CloseMarketWindows();
                    return;
                }
                if (retainerSellVisible)
                {
                    CloseRetainerSellIfOpen();
                    return;
                }
                if (IsAddonVisible("ContextMenu"))
                {
                    _uiReader.DismissInventoryContextMenu();
                    return;
                }
                if (!TryReadInventorySlotState(_pendingSellSlot, out var liveItemId, out var liveHq, out var liveQuantity) ||
                    liveItemId != _currentSellItemId || liveHq != _currentSellItemIsHq ||
                    liveQuantity != _vendorFallbackExpectedQuantity)
                {
                    FinishAutoVendorFallbackWithoutDispatch(now, "inventory stack changed before menu open");
                    return;
                }
                if (_uiReader.TryOpenInventoryContextMenu((InventoryType)_pendingSellSlot.Container, _pendingSellSlot.Slot))
                {
                    _vendorFallbackStage = VendorFallbackStage.SelectAction;
                    _vendorFallbackStartedUtc = now;
                    _lastActionUtc = now;
                    Log.Debug("[RR][VendorFallback] Context menu requested for item={ItemId} HQ={Hq} container={Container} slot={Slot}.",
                        _currentSellItemId, _currentSellItemIsHq, _pendingSellSlot.Container, _pendingSellSlot.Slot);
                }
                return;

            case VendorFallbackStage.SelectAction:
                if (now - _vendorFallbackStartedUtc > TimeSpan.FromSeconds(5))
                {
                    FinishAutoVendorFallbackWithoutDispatch(now, "enabled Have Retainer Sell Items action was not resolved");
                    return;
                }
                if (now < _vendorFallbackNextSelectionCheckUtc) return;
                _vendorFallbackNextSelectionCheckUtc = now + TimeSpan.FromMilliseconds(250);
                if (!IsAutoVendorFallbackStillEligible())
                {
                    FinishAutoVendorFallbackWithoutDispatch(now, "vendor permission, protection, price threshold, or inventory changed before selection");
                    return;
                }
                var selected = _uiReader.TrySelectLiveRetainerVendorSell(
                    _pendingSellSlot.Container, _pendingSellSlot.Slot, _currentSellItemId,
                    _currentSellItemIsHq, _vendorFallbackExpectedQuantity, _vendorFallbackRetainerId,
                    out var selectionAttempted, out var diagnostic,
                    () =>
                    {
                        _vendorFallbackSelectionAttempted = true;
                        _vendorFallbackStartedUtc = DateTime.UtcNow;
                    });
                if (!string.IsNullOrEmpty(diagnostic) &&
                    !string.Equals(diagnostic, _sellState.VendorFallbackLastContextDiagnostic, StringComparison.Ordinal))
                {
                    _sellState.VendorFallbackLastContextDiagnostic = diagnostic;
                    Log.Debug("[RR][VendorFallback] Context resolution: {Diagnostic}", diagnostic);
                }
                if (!selectionAttempted) return;
                _vendorFallbackStage = VendorFallbackStage.Verify;
                _vendorFallbackStartedUtc = now;
                _lastActionUtc = now;
                Log.Information("[RR][VendorFallback] Selection attempted; result={Selected}; item={ItemId} HQ={Hq} qty={Quantity}; verifying inventory.",
                    selected, _currentSellItemId, _currentSellItemIsHq, _vendorFallbackExpectedQuantity);
                return;

            case VendorFallbackStage.Verify:
                if (!_vendorFallbackSelectionAttempted)
                {
                    if (IsAddonVisible("ContextMenu")) _uiReader.DismissInventoryContextMenu();
                    StopRun(RunStopReason.Error);
                    return;
                }
                var after = _uiReader.GetVendorInventorySnapshot();
                if (after.IsComplete)
                {
                    var remaining = after.Stacks.Where(candidate => candidate.ItemId == _currentSellItemId &&
                        candidate.IsHq == _currentSellItemIsHq).Sum(candidate => (long)candidate.Quantity);
                    var removed = _vendorFallbackBaselineQuantity - remaining;
                    if (removed == _vendorFallbackExpectedQuantity)
                    {
                        Log.Information("[RR][VendorFallback] Verified item={ItemId} HQ={Hq} expectedRemoval={Expected} observedRemoval={Observed}.",
                            _currentSellItemId, _currentSellItemIsHq, _vendorFallbackExpectedQuantity, removed);
                        _sellState.VendorSaleVerifiedThisRetainer = true;
                        FinishAutoVendorFallbackAsSkipped(now);
                        return;
                    }
                    if (removed != 0)
                    {
                        Log.Error("[RR][VendorFallback] Unexpected inventory delta after selection; item={ItemId} HQ={Hq} expectedRemoval={Expected} observedRemoval={Observed}. Outcome uncertain; stopping without retry.",
                            _currentSellItemId, _currentSellItemIsHq, _vendorFallbackExpectedQuantity, removed);
                        if (IsAddonVisible("ContextMenu")) _uiReader.DismissInventoryContextMenu();
                        StopRun(RunStopReason.Error);
                        return;
                    }
                }
                if (now - _vendorFallbackStartedUtc > TimeSpan.FromSeconds(10))
                {
                    Log.Error("[RR][VendorFallback] Verification timed out after selection for item={ItemId} HQ={Hq}; outcome uncertain. Stopping without retry.",
                        _currentSellItemId, _currentSellItemIsHq);
                    if (IsAddonVisible("ContextMenu")) _uiReader.DismissInventoryContextMenu();
                    StopRun(RunStopReason.Error);
                }
                return;
        }
    }

    private void FinishAutoVendorFallbackWithoutDispatch(DateTime now, string reason)
    {
        Log.Warning("[RR][VendorFallback] Not dispatched; {Reason}. Continuing RR run without retry.", reason);
        FinishAutoVendorFallbackAsSkipped(now);
    }

    private void FinishAutoVendorFallbackAsSkipped(DateTime now)
    {
        if (IsAddonVisible("ContextMenu")) _uiReader.DismissInventoryContextMenu();
        _vendorFallbackStage = VendorFallbackStage.None;
        _vendorFallbackSelectionAttempted = false;
        _newListingAttemptState = NewListingAttemptState.Skipped;
        RememberFailedAttemptedSlot();
        _runPhase = RunPhase.CleanupAfterItem;
        _lastActionUtc = now;
    }

    private bool IsAutoVendorFallbackStillEligible()
    {
        if (CurrentCharacterId != _runCharacterId || !_hasPendingSellSlot || !_currentSellItemIsAutoDiscovered ||
            !Configuration.GetEffectiveVendorAllowlist(_runCharacterId).Contains(
                Configuration.MakeSellKey(_currentSellItemId, _currentSellItemIsHq)))
            return false;

        var key = Configuration.MakeSellKey(_currentSellItemId, _currentSellItemIsHq);
        var threshold = Configuration.GetEffectiveVendorThreshold(_runCharacterId, key);
        if (threshold <= 0 || _vendorFallbackRejectedStackValue >= threshold ||
            _vendorFallbackRejectedStackValue >= _currentSellMinimumStackValue ||
            Configuration.GetEffectiveAutoListBlocklist(_runCharacterId).Contains(key) ||
            Configuration.GetSellListOrdered(_runCharacterId).Any(entry =>
                Configuration.MakeSellKey(entry.ItemId, entry.IsHq) == key))
            return false;

        if (!TryReadInventorySlotState(_pendingSellSlot, out var itemId, out var isHq, out var quantity) ||
            itemId != _currentSellItemId || isHq != _currentSellItemIsHq ||
            quantity != _vendorFallbackExpectedQuantity || quantity != _currentSellStackSize)
            return false;

        return (long)_vendorFallbackUnitPrice * quantity == _vendorFallbackRejectedStackValue;
    }
}
