using System;

using ECommons.Automation;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Component.GUI;
using RetainerRepricer.Services;

namespace RetainerRepricer;

/// <summary>
/// Main state-machine tick that advances repricing/selling flow.
/// </summary>
public unsafe sealed partial class Plugin
{
    #region Run tick (state machine)

    internal unsafe void TickRun()
    {
        if (!IsRunning) return;

        var now = DateTime.UtcNow;
        if ((now - _lastActionUtc).TotalSeconds < ActionIntervalSeconds)
            return;

        // Snapshot addon visibility once per tick (cheaper and keeps checks consistent for this tick).
        var retainerListVisible = IsAddonVisible("RetainerList");
        var talkVisible = IsAddonVisible("Talk");
        var selectStringVisible = IsAddonVisible("SelectString");
        var retainerSellListVisible = IsAddonVisible("RetainerSellList");
        var retainerSellVisible = IsAddonVisible("RetainerSell");
        var marketOpen = IsAddonOpen("ItemSearchResult");
        var itemHistoryOpen = IsAddonOpen("ItemHistory");
        var contextMenuVisible = IsAddonVisible("ContextMenu");

        switch (_runPhase)
        {
            #region Retainer navigation

            case RunPhase.NeedOpen:
                {
                    if (!retainerListVisible) return;

                    if (!IsRetainerListReady())
                    {
                        Log.Verbose("[RR] RetainerList visible but names not populated yet.");
                        _lastActionUtc = now;
                        return;
                    }

                    if (SmartSortEnabled && !_smartSortKickoffDone)
                    {
                        if (_pendingSmartSortTask == null)
                            _pendingSmartSortTask = _smartSorter.ForceSortAsync("run_start");

                        if (_pendingSmartSortTask != null && !_pendingSmartSortTask.IsCompleted)
                        {
                            if (_pendingSmartSortTask.IsFaulted)
                            {
                                Log.Warning(_pendingSmartSortTask.Exception?.GetBaseException(), "[RR][SmartSort] Run-start sort failed.");
                                _pendingSmartSortTask = null;
                                _smartSortKickoffDone = true;
                            }
                            else
                            {
                                _lastActionUtc = now;
                                return;
                            }
                        }

                        if (_pendingSmartSortTask is { IsCompleted: true })
                        {
                            _pendingSmartSortTask = null;
                            _smartSortKickoffDone = true;
                        }
                    }
                    else if (!SmartSortEnabled)
                    {
                        _smartSortKickoffDone = true;
                    }

                    _retainerRowPos++;
                    if (_retainerRowPos >= _retainerRowOrder.Count)
                    {
                        Log.Information("[RR] Finished: processed all enabled retainers.");

                        if (Configuration.CloseRetainerListAddon)
                        {
                            Log.Information("[RR] Closing RetainerList (CloseRetainerListAddon enabled).");
                            CloseAddonIfOpen("RetainerList");
                        }

                        PrintSuccess(GetCompletionMessage());
                        StopRun();
                        return;
                    }

                    // Reset per-retainer counters.
                    _sellListCountCaptured = false;
                    _listedCountThisRetainer = 0;
                    _slotIndexToOpen = 0;

                    // Build per-retainer sell queue (runs after repricing).
                    _sellQueue.Clear();
                    foreach (var e in Configuration.GetSellListOrdered())
                    {
                        if (e.ItemId == 0) continue;

                        _sellQueue.Add(new SellCandidate
                        {
                            ItemId = e.ItemId,
                            IsHq = e.IsHq,
                            MinCountToSell = Math.Max(1, e.MinCountToSell),
                            Name = e.Name ?? string.Empty,
                        });
                    }
                    _sellQueuePos = 0;

                    _sellCapacityThisRetainer = 0;
                    _soldThisRetainer = 0;

                    _processingListedItem = true;
                    _currentSellItemId = 0;
                    _hasPendingSellSlot = false;

                    // Reset pacing per retainer so a throttle hit doesn't poison the next retainer.
                    _mbIntervalSec = MbBaseIntervalSeconds;
                    _lastMbQueryUtc = DateTime.MinValue;

                    var row = _retainerRowOrder[_retainerRowPos];
                    Log.Information($"[RR] Opening retainer row {row} ({_retainerRowPos + 1}/{_retainerRowOrder.Count})");

                    TryClickRetainerListEntry(row);

                    _runPhase = RunPhase.WaitingTalk;
                    _lastActionUtc = now;
                    return;
                }

            case RunPhase.WaitingTalk:
                {
                    // Some setups skip Talk entirely and jump straight to SelectString.
                    if (selectStringVisible)
                    {
                        if (!IsSelectStringReady())
                        {
                            Log.Verbose("[RR] SelectString visible but not ready.");
                            _lastActionUtc = now;
                            return;
                        }

                        Log.Debug($"[RR] SelectString opened for row {_retainerRowOrder[_retainerRowPos]}; selecting Sell items.");
                        TrySelectSellItems();

                        _runPhase = RunPhase.WaitingRetainerSellList;
                        _lastActionUtc = now;
                        return;
                    }

                    if (talkVisible)
                    {
                        Log.Debug($"[RR] Talk open for row {_retainerRowOrder[_retainerRowPos]}; advancing.");
                        var ok = TryAdvanceTalk();
                        Log.Debug(ok ? "[RR] Talk click sent." : "[RR] Talk click failed.");

                        _lastActionUtc = now;
                        return;
                    }

                    _runPhase = RunPhase.WaitingSelectString;
                    _lastActionUtc = now;
                    return;
                }

            case RunPhase.WaitingSelectString:
                {
                    if (selectStringVisible)
                    {
                        if (!IsSelectStringReady())
                        {
                            Log.Verbose("[RR] SelectString visible but not ready.");
                            _lastActionUtc = now;
                            return;
                        }

                        Log.Debug($"[RR] SelectString opened for row {_retainerRowOrder[_retainerRowPos]}; selecting Sell items.");
                        TrySelectSellItems();

                        _runPhase = RunPhase.WaitingRetainerSellList;
                        _lastActionUtc = now;
                        return;
                    }

                    if (talkVisible)
                    {
                        Log.Debug($"[RR] Talk open (late) for row {_retainerRowOrder[_retainerRowPos]}; advancing.");
                        var ok = TryAdvanceTalk();
                        Log.Debug(ok ? "[RR] Talk click sent." : "[RR] Talk click failed.");

                        _lastActionUtc = now;
                        return;
                    }

                    _lastActionUtc = now;
                    return;
                }

            case RunPhase.WaitingRetainerSellList:
                {
                    if (!retainerSellListVisible) return;

                    // Don't latch listed count until we can read it reliably.
                    if (!_sellListCountCaptured)
                    {
                        var raw = _uiReader.ReadRetainerSellListCountText();
                        var listedOpt = _uiReader.ReadRetainerSellListListedCount();

                        if (listedOpt is null || string.IsNullOrWhiteSpace(raw))
                        {
                            Log.Verbose($"[RR] RetainerSellList visible but count not readable (raw='{raw ?? "null"}').");
                            _lastActionUtc = now;
                            return;
                        }

                        _sellListCountCaptured = true;

                        var listed = listedOpt.Value;
                        _listedCountThisRetainer = Math.Clamp(listed, 0, 20);
                        _slotIndexToOpen = 0;

                        _sellCapacityThisRetainer = Math.Max(0, 20 - _listedCountThisRetainer);
                        _soldThisRetainer = 0;

                        Log.Information($"[RR] Entered RetainerSellList. Listed={_listedCountThisRetainer}; new sells capacity={_sellCapacityThisRetainer}");
                    }

                    if (ShouldReprice && _listedCountThisRetainer > 0)
                    {
                        _runPhase = RunPhase.OpeningSellItem;
                    }
                    else if (ShouldSell)
                    {
                        if (!ShouldReprice)
                            Log.Information("[RR] Repricing skipped (run mode = sell-only).");
                        _runPhase = RunPhase.Sell_FindNextItemInInventory;
                    }
                    else
                    {
                        Log.Information("[RR] Skipping repricing/selling (run mode disables both phases).");
                        _runPhase = RunPhase.ExitToRetainerList;
                    }

                    _lastActionUtc = now;
                    return;
                }

            #endregion

            #region Repricing existing listed items

            case RunPhase.OpeningSellItem:
                {
                    if (!ShouldReprice)
                    {
                        _runPhase = ShouldSell
                            ? RunPhase.Sell_FindNextItemInInventory
                            : RunPhase.ExitToRetainerList;
                        _lastActionUtc = now;
                        return;
                    }

                    if (!retainerSellListVisible) return;

                    if (_slotIndexToOpen < 0) _slotIndexToOpen = 0;

                    if (_slotIndexToOpen >= _listedCountThisRetainer)
                    {
                        _runPhase = RunPhase.Sell_FindNextItemInInventory;
                        _lastActionUtc = now;
                        return;
                    }

                    var idx = Math.Clamp(_slotIndexToOpen, 0, 19);
                    Log.Debug($"[RR] Opening sell slot {idx} ({_slotIndexToOpen + 1}/{_listedCountThisRetainer})");

                    _processingListedItem = true;

                    if (!FireRetainerSellListOpenItem(idx))
                        return;

                    _runPhase = RunPhase.WaitingRetainerSell;
                    _lastActionUtc = now;
                    return;
                }

            case RunPhase.WaitingRetainerSell:
                {
                    if (contextMenuVisible)
                    {
                        FireContextMenuDismiss();
                        _lastActionUtc = now;
                        return;
                    }

                    if (!retainerSellVisible) return;

                    Log.Debug($"[RR] RetainerSell opened (listedItem={_processingListedItem}).");

                    _runPhase = RunPhase.CaptureSellContext;
                    _lastActionUtc = now;
                    return;
                }

            #endregion

            #region Repricing pipeline

            case RunPhase.CaptureSellContext:
                {
                    if (!retainerSellVisible) return;

                    // Wait until the fields we rely on are readable.
                    var priceOpt = _uiReader.ReadRetainerSellAskingPrice();
                    if (priceOpt is null)
                    {
                        Log.Verbose("[RR] RetainerSell visible but asking price not readable yet.");
                        _lastActionUtc = now;
                        return;
                    }

                    _currentIsHq = _uiReader.IsRetainerSellItemHq();

                    // Always use the display-safe name (raw payload can leak printable junk).
                    var name = _uiReader.GetRetainerSellItemNameDisplay(stripHqGlyph: true);
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        Log.Verbose("[RR] RetainerSell visible but item name not stable yet.");
                        _lastActionUtc = now;
                        return;
                    }

                    ResetUniversalisGateState();

                    Log.Information($"[RR] Sell capture: item='{name}' currentPrice={priceOpt.Value}");

                    _runPhase = RunPhase.OpenComparePrices;
                    _lastActionUtc = now;
                    return;
                }

            case RunPhase.OpenComparePrices:
                {
                    if (!retainerSellVisible) return;

                    _isrThrottleRetried = false;
                    _isrThrottleUntilUtc = DateTime.MinValue;

                    if (marketOpen)
                    {
                        Log.Debug("[RR] ItemSearchResult already open; skipping Compare Prices click.");

                        _isrOpenedUtc = DateTime.MinValue;
                        _isrNoItemsConfirm = 0;

                        _isrNeedApplyHqFilter = false; // fallback only; don't filter immediately
                        _isrHqFilterApplied = false;
                        _isrHqFilterRequestedUtc = DateTime.MinValue;
                        _isrHqFilterFallbackTried = false;
                        _isrAllowFilterAfterUtc = DateTime.MinValue;
                        _isrHqFilterVisibleUtc = DateTime.MinValue;

                        _runPhase = RunPhase.WaitingItemSearchResult;
                        _lastActionUtc = now;
                        return;
                    }

                    var addon = GameGui.GetAddonByName("RetainerSell", 1);
                    if (addon.IsNull) return;

                    // Space out Compare Prices calls to avoid "Please wait..." throttles.
                    var jitter = Random.Shared.NextDouble() * MbJitterMaxSeconds;
                    if (_lastMbQueryUtc != DateTime.MinValue &&
                        (now - _lastMbQueryUtc).TotalSeconds < (_mbIntervalSec + jitter))
                    {
                        Log.Verbose("[RR] Compare Prices pacing gate hit.");
                        _lastActionUtc = now;
                        return;
                    }

                    _lastMbQueryUtc = now;
                    new AddonMaster.RetainerSell(addon.Address).ComparePrices();

                    _isrOpenedUtc = DateTime.MinValue;
                    _isrNoItemsConfirm = 0;

                    _isrNeedApplyHqFilter = false; // fallback only; don't filter immediately
                    _isrHqFilterApplied = false;
                    _isrHqFilterRequestedUtc = DateTime.MinValue;
                    _isrHqFilterFallbackTried = false;
                    _isrAllowFilterAfterUtc = DateTime.MinValue;
                    _isrHqFilterVisibleUtc = DateTime.MinValue;

                    Log.Debug($"[RR] ComparePrices clicked (interval={_mbIntervalSec:0.00}s).");
                    _runPhase = RunPhase.WaitingItemSearchResult;
                    _lastActionUtc = now;
                    return;
                }

            case RunPhase.WaitingItemSearchResult:
                {
                    if (!marketOpen) return;

                    // If HQ item: apply the HQ filter so HQ rows are rendered immediately.
                    // This avoids list virtualization where only 10 visible rows have populated text.
                    if (_isrNeedApplyHqFilter && !_isrHqFilterApplied)
                    {
                        // Respect global filter timing gate
                        if (_isrAllowFilterAfterUtc != DateTime.MinValue && now < _isrAllowFilterAfterUtc)
                        {
                            _lastActionUtc = now;
                            return;
                        }

                        var filterVisible = IsAddonVisible("ItemSearchFilter");
                        if (!filterVisible)
                            _isrHqFilterVisibleUtc = DateTime.MinValue;

                        // If filter UI is visible, toggle HQ and accept
                        if (filterVisible)
                        {
                            if (_isrHqFilterVisibleUtc == DateTime.MinValue)
                            {
                                _isrHqFilterVisibleUtc = now;
                                _lastActionUtc = now;
                                return;
                            }

                            if ((now - _isrHqFilterVisibleUtc).TotalSeconds < IsrHqFilterUiDebounceSeconds)
                            {
                                _lastActionUtc = now;
                                return;
                            }

                            FireItemSearchFilterToggleHq();
                            FireItemSearchFilterAccept();

                            _isrHqFilterApplied = true;
                            _isrNeedApplyHqFilter = false;

                            // reset ISR tracking so rows repopulate cleanly
                            _isrNoItemsConfirm = 0;
                            _isrOpenedUtc = now;

                            Log.Debug("[RR] HQ filter applied; waiting for ISR to repopulate.");
                            _lastActionUtc = now;
                            return;
                        }

                        // Filter window not open yet → request it
                        if (_isrHqFilterRequestedUtc == DateTime.MinValue ||
                            (now - _isrHqFilterRequestedUtc).TotalSeconds >= IsrHqFilterOpenRetrySeconds)
                        {
                            FireItemSearchResultOpenFilter();
                            _isrHqFilterRequestedUtc = now;

                            // brief pause to allow the filter window to appear
                            _isrAllowFilterAfterUtc = now.AddSeconds(IsrHqFilterPostOpenSeconds);

                            _lastActionUtc = now;
                            return;
                        }

                        _lastActionUtc = now;
                        return;
                    }

                    var status = _uiReader.GetItemSearchResultStatus(out var msg);

                    if (_isrOpenedUtc == DateTime.MinValue)
                        _isrOpenedUtc = now;

                    if (status == Ui.UiReader.ItemSearchResultStatus.NoItemsFound)
                    {
                        var age = (now - _isrOpenedUtc).TotalSeconds;
                        if (age < IsrNoItemsSettleSeconds)
                        {
                            Log.Verbose("[RR] ISR 'No items found' seen too early; waiting.");
                            _lastActionUtc = now;
                            return;
                        }

                        _isrNoItemsConfirm++;
                        if (_isrNoItemsConfirm < 2)
                        {
                            Log.Verbose("[RR] ISR 'No items found' confirm pass 1/2.");
                            _lastActionUtc = now;
                            return;
                        }

                        var listCheck = _uiReader.GetMarketList();
                        var rows = listCheck != null ? listCheck->GetItemCount() : 0;
                        if (rows > 0)
                        {
                            Log.Debug($"[RR] ISR message says none, but rows={rows}; ignoring message.");
                            _isrNoItemsConfirm = 0;
                            _runPhase = RunPhase.ReadMarketAndApplyPrice;
                            _lastActionUtc = now;
                            return;
                        }

                        if (Configuration.UseUniversalisForEmptyMarket && Configuration.UseUniversalisApi)
                        {
                            Log.Information($"[RR] ISR: '{msg}' -> no competing listings; attempting Universalis fallback.");
                            ResetUniversalisGateState();
                            _runPhase = RunPhase.WaitingUniversalisNoItemsFallback;
                        }
                        else
                        {
                            Log.Information($"[RR] ISR: '{msg}' -> no competing listings; skipping repricing.");
                            _runPhase = RunPhase.CleanupAfterItem;
                        }
                        _lastActionUtc = now;
                        return;
                    }
                    else
                    {
                        _isrNoItemsConfirm = 0;
                    }

                    if (status == Ui.UiReader.ItemSearchResultStatus.PleaseWaitRetry)
                    {
                        // If we're in the middle of applying the HQ filter, do NOT retry ComparePrices.
                        // Filtering triggers a repopulation and can temporarily surface "Please wait".
                        if (_isrNeedApplyHqFilter && !_isrHqFilterApplied)
                        {
                            Log.Verbose("[RR] ISR throttle while applying HQ filter; waiting (no ComparePrices retry).");
                            _isrThrottleUntilUtc = now.AddSeconds(ItemSearchResultThrottleBackoffSeconds);
                            _lastActionUtc = now;
                            return;
                        }

                        if (now < _isrThrottleUntilUtc)
                        {
                            Log.Verbose("[RR] ISR throttle backoff window active.");
                            _lastActionUtc = now;
                            return;
                        }

                        if (!_isrThrottleRetried && _isrThrottleUntilUtc == DateTime.MinValue)
                        {
                            _mbIntervalSec = Math.Clamp(_mbIntervalSec + 0.08, MbIntervalMinSeconds, MbIntervalMaxSeconds);

                            _isrThrottleUntilUtc = now.AddSeconds(ItemSearchResultThrottleBackoffSeconds);
                            Log.Debug("[RR] ISR throttle: backing off then retrying once.");
                            return;
                        }

                        if (!_isrThrottleRetried)
                        {
                            var sellAddon = GameGui.GetAddonByName("RetainerSell", 1);
                            if (sellAddon.IsNull) return;

                            _lastMbQueryUtc = now;
                            new AddonMaster.RetainerSell(sellAddon.Address).ComparePrices();

                            _isrThrottleRetried = true;
                            _isrThrottleUntilUtc = DateTime.MinValue;

                            Log.Debug("[RR] ISR throttle: retry sent.");
                            _lastActionUtc = now;
                            return;
                        }

                        Log.Information("[RR] ISR throttle persists after one retry; skipping item.");
                        _runPhase = RunPhase.CleanupAfterItem;
                        _lastActionUtc = now;
                        return;
                    }

                    if (status == Ui.UiReader.ItemSearchResultStatus.OtherMessage)
                    {
                        Log.Information($"[RR] ISR message: '{msg}' -> skipping item.");
                        _runPhase = RunPhase.CleanupAfterItem;
                        _lastActionUtc = now;
                        return;
                    }

                    var list = _uiReader.GetMarketList();
                    if (list == null)
                    {
                        Log.Verbose("[RR] ISR not ready: market list null.");
                        _lastActionUtc = now;
                        return;
                    }

                    var count = list->GetItemCount();
                    if (count <= 0)
                    {
                        Log.Verbose("[RR] ISR not ready: 0 rows.");
                        _lastActionUtc = now;
                        return;
                    }

                    // Renderer can exist before nodes/text are actually populated.
                    var r0 = list->GetItemRenderer(0);
                    if (r0 == null || r0->UldManager.NodeList == null || r0->UldManager.NodeListCount <= 0)
                    {
                        Log.Verbose("[RR] ISR not ready: row0 renderer nodes not ready.");
                        _lastActionUtc = now;
                        return;
                    }

                    var unitRaw = _uiReader.ReadRendererText(r0, Ui.NodePaths.UnitPriceNodeId);
                    if (Ui.UiReader.ParseGil(unitRaw) is null)
                    {
                        Log.Verbose("[RR] ISR not ready: row0 unit price not parsable yet.");
                        _lastActionUtc = now;
                        return;
                    }

                    Log.Debug($"[RR] ItemSearchResult ready (rows={count}).");
                    _mbIntervalSec = Math.Clamp(_mbIntervalSec - 0.02, MbIntervalMinSeconds, MbIntervalMaxSeconds);

                    _runPhase = RunPhase.ReadMarketAndApplyPrice;
                    _lastActionUtc = now;
                    return;
                }

            case RunPhase.ReadMarketAndApplyPrice:
                {
                    if (!retainerSellVisible) return;
                    if (!marketOpen) return;

                    var status = _uiReader.GetItemSearchResultStatus(out var msg);
                    if (_isrOpenedUtc == DateTime.MinValue)
                        _isrOpenedUtc = now;

                    if (status == Ui.UiReader.ItemSearchResultStatus.NoItemsFound)
                    {
                        var age = (now - _isrOpenedUtc).TotalSeconds;
                        if (age < IsrNoItemsSettleSeconds)
                        {
                            Log.Verbose("[RR] ISR 'No items found' seen too early; waiting.");
                            _lastActionUtc = now;
                            return;
                        }

                        _isrNoItemsConfirm++;
                        if (_isrNoItemsConfirm < 2)
                        {
                            Log.Verbose("[RR] ISR 'No items found' confirm pass 1/2.");
                            _lastActionUtc = now;
                            return;
                        }

                        var listCheck = _uiReader.GetMarketList();
                        var rows = listCheck != null ? listCheck->GetItemCount() : 0;
                        if (rows <= 0)
                        {
                            Log.Information($"[RR] ISR: '{msg}' -> no competing listings; skipping repricing.");
                            _runPhase = RunPhase.CleanupAfterItem;
                            _lastActionUtc = now;
                            return;
                        }

                        Log.Debug($"[RR] ISR message says none, but rows={rows}; ignoring message.");
                        _isrNoItemsConfirm = 0;
                    }
                    else
                    {
                        _isrNoItemsConfirm = 0;
                    }

                    if (status == Ui.UiReader.ItemSearchResultStatus.PleaseWaitRetry)
                    {
                        // Optional: backoff a little so we don't hammer the gate
                        _isrThrottleUntilUtc = now.AddSeconds(ItemSearchResultThrottleBackoffSeconds);

                        Log.Debug("[RR] ISR throttle surfaced during read; returning to ISR gate.");
                        _runPhase = RunPhase.WaitingItemSearchResult;
                        _lastActionUtc = now;
                        return;
                    }

                    if (status == Ui.UiReader.ItemSearchResultStatus.OtherMessage)
                    {
                        Log.Information($"[RR] ISR message: '{msg}' -> skipping item.");
                        _runPhase = RunPhase.CleanupAfterItem;
                        _lastActionUtc = now;
                        return;
                    }

                    if (Configuration.EnableUndercutPreventionGate && Configuration.UseUniversalisApi)
                    {
                        var gateStatus = UpdateUniversalisGate(out var floorPrice);
                        if (gateStatus == UniversalisGateStatus.Pending)
                        {
                            _lastActionUtc = now;
                            return;
                        }

                        if (gateStatus == UniversalisGateStatus.Failed)
                        {
                            Log.Information("[RR][Gate] Skipping repricing: Universalis gate failed for this item.");
                            _runPhase = RunPhase.CleanupAfterItem;
                            _lastActionUtc = now;
                            return;
                        }

                        _universalisPriceFloor = gateStatus == UniversalisGateStatus.Ready
                            ? floorPrice
                            : null;
                    }
                    else
                    {
                        _universalisPriceFloor = null;
                    }

                    if (!TryPickReferenceListing(out var lowestPrice, out var lowestSeller))
                    {
                        // If this is an HQ item and we didn't see HQ in the first page,
                        // try applying the HQ filter ONCE as a fallback to avoid list virtualization.
                        if (_currentIsHq && !_isrHqFilterFallbackTried)
                        {
                            if (IsAnyHqVisibleInFirstPage())
                            {
                                Log.Debug("[RR] HQ became visible in first page; retrying pick without filtering.");
                                _lastActionUtc = now;
                                return; // next tick TryPickReferenceListing will succeed
                            }

                            Log.Information("[RR] HQ not visible in first page; applying HQ filter fallback once.");

                            _isrHqFilterFallbackTried = true;

                            // Arm the filter state machine
                            _isrNeedApplyHqFilter = true;
                            _isrHqFilterApplied = false;
                            _isrHqFilterRequestedUtc = DateTime.MinValue;

                            // delay before attempting HQ filter so ISR can settle
                            _isrAllowFilterAfterUtc = now.AddSeconds(IsrHqFilterInitialDelaySeconds);

                            _isrNoItemsConfirm = 0;
                            _isrOpenedUtc = now;

                            _runPhase = RunPhase.WaitingItemSearchResult;
                            _lastActionUtc = now;
                            return;
                        }

                        // If fallback already tried (or NQ item), skip.
                        _runPhase = RunPhase.CleanupAfterItem;
                        _lastActionUtc = now;
                        return;
                    }

                    var referenceIsMine =
                        !string.IsNullOrWhiteSpace(lowestSeller) &&
                        _myRetainers.Contains(lowestSeller);

                    var desired = DecideNewPrice(lowestPrice, referenceIsMine);

                    var sellAddonPeek = GameGui.GetAddonByName("RetainerSell", 1);
                    if (sellAddonPeek.IsNull) return;

                    var current = new AddonMaster.RetainerSell(sellAddonPeek.Address).AskingPrice;

                    if (current == desired)
                    {
                        Log.Information($"[RR] Price unchanged ({desired}); skipping apply.");
                        _runPhase = RunPhase.CleanupAfterItem;
                        _lastActionUtc = now;
                        return;
                    }

                    _stagedDesiredPrice = desired;
                    _stagedReferenceSeller = lowestSeller;
                    _stagedReferenceIsMine = referenceIsMine;
                    _hasAppliedStagedPrice = false;

                    Log.Information($"[RR] Stage apply: current={current} desired={desired} seller='{lowestSeller}' mine={referenceIsMine}");

                    CloseMarketWindows();

                    _runPhase = RunPhase.CloseMarketThenApply;
                    _lastActionUtc = now;
                    return;
                }

            case RunPhase.WaitingUniversalisNoItemsFallback:
                {
                    if (!marketOpen) return;

                    if (!TryGetCurrentMarketItemId(out var itemId))
                    {
                        Log.Warning("[RR][NoItems] Unable to resolve current item id for Universalis lookup; skipping item.");
                        _runPhase = RunPhase.CleanupAfterItem;
                        _lastActionUtc = now;
                        return;
                    }

                    var region = GetWorldDcRegionKey();
                    if (string.IsNullOrWhiteSpace(region))
                    {
                        Log.Warning("[RR][NoItems] Unable to resolve world/DC region; skipping item.");
                        _runPhase = RunPhase.CleanupAfterItem;
                        _lastActionUtc = now;
                        return;
                    }

                    var baseUrl = UniversalisApiClient.AggregatedBaseUrl;
                    var key = new UniversalisGateKey(itemId, _currentIsHq, region, baseUrl);

                    if (_universalisGateKey != key)
                    {
                        _universalisGateKey = key;
                        _universalisGateAverage = null;
                        _universalisGateTask = _universalisClient.GetAveragePriceAsync(baseUrl, region, itemId, _currentIsHq);
                        Log.Debug($"[RR][NoItems] Requesting Universalis average for item {itemId} (HQ={_currentIsHq}, region='{region}').");
                    }

                    var task = _universalisGateTask;
                    if (task == null)
                    {
                        Log.Warning("[RR][NoItems] Universalis task was null; skipping item.");
                        _runPhase = RunPhase.CleanupAfterItem;
                        _lastActionUtc = now;
                        return;
                    }

                    if (!task.IsCompleted)
                    {
                        _lastActionUtc = now;
                        return;
                    }

                    try
                    {
                        var result = task.Result;
                        if (result.HasValue)
                        {
                            _universalisGateAverage = result.Value;
                            var price = (int)Math.Floor(result.Value);
                            if (price < 1) price = 1;

                            _stagedDesiredPrice = price;
                            _stagedReferenceSeller = "Universalis Average";
                            _stagedReferenceIsMine = false;

                            Log.Information($"[RR][NoItems] Using Universalis average {result.Value:0} -> {price} for item {itemId} (HQ={_currentIsHq}).");

                            CloseMarketWindows();
                            _runPhase = RunPhase.CloseMarketThenApply;
                        }
                        else
                        {
                            Log.Information($"[RR][NoItems] Universalis returned no data for item {itemId}; skipping.");
                            _runPhase = RunPhase.CleanupAfterItem;
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, $"[RR][NoItems] Universalis request failed for item {itemId}; skipping.");
                        _runPhase = RunPhase.CleanupAfterItem;
                    }

                    _lastActionUtc = now;
                    return;
                }

            case RunPhase.CloseMarketThenApply:
                {
                    if (MarketWindowsStillOpen())
                        return;

                    if (!retainerSellVisible) return;

                    if (_stagedDesiredPrice is null)
                    {
                        Log.Warning("[RR] Staged price was null; bailing to cleanup.");
                        _runPhase = RunPhase.CleanupAfterItem;
                        _lastActionUtc = now;
                        return;
                    }

                    var current = _uiReader.ReadRetainerSellAskingPrice();
                    if (current is null)
                    {
                        Log.Verbose("[RR] Waiting RetainerSell stable (asking price unreadable).");
                        _lastActionUtc = now;
                        return;
                    }

                    if (_hasAppliedStagedPrice)
                    {
                        if (current.Value != _stagedDesiredPrice.Value)
                        {
                            Log.Verbose($"[RR] Waiting price apply... ui={current.Value} desired={_stagedDesiredPrice.Value}");
                            _lastActionUtc = now;
                            return;
                        }

                        _runPhase = RunPhase.ConfirmAfterApply;
                        _lastActionUtc = now;
                        return;
                    }

                    Log.Debug($"[RR] Apply after market close: {current.Value} -> {_stagedDesiredPrice.Value}");

                    var sellAddon = GameGui.GetAddonByName("RetainerSell", 1);
                    if (sellAddon.IsNull) return;

                    var unit = (AtkUnitBase*)sellAddon.Address;
                    if (unit == null) return;

                    Callback.Fire(unit, updateState: true, 2, _stagedDesiredPrice.Value);

                    _hasAppliedStagedPrice = true;
                    _lastActionUtc = now;
                    return;
                }

            case RunPhase.ConfirmAfterApply:
                {
                    if (!retainerSellVisible) return;

                    if (_stagedDesiredPrice is null)
                    {
                        Log.Warning("[RR] Staged price was null; bailing to cleanup.");
                        _runPhase = RunPhase.CleanupAfterItem;
                        _lastActionUtc = now;
                        return;
                    }

                    if (MarketWindowsStillOpen())
                        return;

                    var desired = _stagedDesiredPrice.Value;
                    var uiPrice = _uiReader.ReadRetainerSellAskingPrice();
                    if (uiPrice == null || uiPrice.Value != desired)
                    {
                        Log.Verbose($"[RR] Waiting price apply... ui={(uiPrice?.ToString() ?? "null")} desired={desired}");
                        _lastActionUtc = now;
                        return;
                    }

                    var sellAddon = GameGui.GetAddonByName("RetainerSell", 1);
                    if (sellAddon.IsNull) return;

                    var unit = (AtkUnitBase*)sellAddon.Address;
                    if (unit == null || !unit->IsVisible) return;

                    Log.Information($"[RR] Confirm price={desired}");

                    Callback.Fire(unit, updateState: true, 0);

                    _stagedDesiredPrice = null;
                    _hasAppliedStagedPrice = false;

                    _runPhase = RunPhase.CleanupAfterItem;
                    _lastActionUtc = now;
                    return;
                }

            case RunPhase.CleanupAfterItem:
                {
                    CloseMarketWindows();
                    CloseRetainerSellIfOpen();

                    Log.Verbose("[RR] Cleanup complete (market closed, exited RetainerSell).");

                    _isrNeedApplyHqFilter = false;
                    _isrHqFilterApplied = false;
                    _isrHqFilterRequestedUtc = DateTime.MinValue;
                    _isrHqFilterFallbackTried = false;
                    _isrAllowFilterAfterUtc = DateTime.MinValue;
                    _isrHqFilterVisibleUtc = DateTime.MinValue;
                    _isrNoItemsConfirm = 0;

                    _runPhase = RunPhase.WaitingRetainerSellListAfterItem;
                    _lastActionUtc = now;
                    return;
                }

            case RunPhase.WaitingRetainerSellListAfterItem:
                {
                    if (!retainerSellListVisible) return;
                    if (retainerSellVisible) return;

                    if (!_processingListedItem)
                    {
                        _processingListedItem = true;
                        _hasPendingSellSlot = false;
                        _currentSellItemId = 0;

                        _soldThisRetainer++;
                        Log.Information($"[RR] New listings: {_soldThisRetainer}/{_sellCapacityThisRetainer}");

                        _runPhase = RunPhase.Sell_FindNextItemInInventory;
                        _lastActionUtc = now;
                        return;
                    }

                    _slotIndexToOpen++;

                    if (_slotIndexToOpen >= _listedCountThisRetainer)
                    {
                        _runPhase = RunPhase.Sell_FindNextItemInInventory;
                        _lastActionUtc = now;
                        return;
                    }

                    _runPhase = RunPhase.OpeningSellItem;
                    _lastActionUtc = now;
                    return;
                }

            #endregion

            #region Selling pipeline (after repricing)

            case RunPhase.Sell_FindNextItemInInventory:
                {
                    if (!ShouldSell)
                    {
                        Log.Information("[RR] Sell phase skipped (run mode = price-only).");
                        _runPhase = RunPhase.ExitToRetainerList;
                        _lastActionUtc = now;
                        return;
                    }

                    if (!retainerSellListVisible) return;

                    if (_sellCapacityThisRetainer <= 0)
                    {
                        Log.Information("[RR] Sell skipped: retainer is full (20/20).");
                        _runPhase = RunPhase.ExitToRetainerList;
                        _lastActionUtc = now;
                        return;
                    }

                    if (_soldThisRetainer >= _sellCapacityThisRetainer)
                    {
                        Log.Information($"[RR] Sell complete: reached capacity {_soldThisRetainer}/{_sellCapacityThisRetainer}.");
                        _runPhase = RunPhase.ExitToRetainerList;
                        _lastActionUtc = now;
                        return;
                    }

                    if (_sellQueue.Count == 0)
                    {
                        Log.Debug("[RR] Sell skipped: SellList is empty.");
                        _runPhase = RunPhase.ExitToRetainerList;
                        _lastActionUtc = now;
                        return;
                    }

                    while (_sellQueuePos < _sellQueue.Count)
                    {
                        var c = _sellQueue[_sellQueuePos];
                        _sellQueuePos++;

                        if (c.ItemId == 0)
                            continue;

                        var threshold = Math.Max(1, c.MinCountToSell);

                        if (!TryFindItemInInventory(c.ItemId, c.IsHq, out var slotRef, out var totalCount))
                        {
                            Log.Verbose($"[RR] Sell candidate not in inventory: itemId={c.ItemId} hq={c.IsHq}");
                            continue;
                        }

                        if (totalCount < threshold)
                        {
                            Log.Verbose($"[RR] Sell candidate skipped: itemId={c.ItemId} hq={c.IsHq} count={totalCount} < threshold={threshold}");
                            continue;
                        }

                        _currentSellItemId = c.ItemId;
                        _pendingSellSlot = slotRef;
                        _hasPendingSellSlot = true;

                        Log.Debug($"[RR] Sell candidate: itemId={c.ItemId} hq={c.IsHq} count={totalCount} threshold={threshold} container={slotRef.Container} slot={slotRef.Slot} (cap {_soldThisRetainer}/{_sellCapacityThisRetainer})");

                        _runPhase = RunPhase.Sell_OpenRetainerSellFromInventory;
                        _lastActionUtc = now;
                        return;

                    }

                    Log.Information("[RR] Sell complete: none of the SellList items were found in inventory.");
                    _runPhase = RunPhase.ExitToRetainerList;
                    _lastActionUtc = now;
                    return;
                }

            case RunPhase.Sell_OpenRetainerSellFromInventory:
                {
                    if (!retainerSellListVisible) return;

                    if (_sellCapacityThisRetainer <= 0 || _soldThisRetainer >= _sellCapacityThisRetainer)
                    {
                        _runPhase = RunPhase.ExitToRetainerList;
                        _lastActionUtc = now;
                        return;
                    }

                    if (!_hasPendingSellSlot)
                    {
                        Log.Warning("[RR] Sell_Open: no pending slot; returning to scan.");
                        _runPhase = RunPhase.Sell_FindNextItemInInventory;
                        _lastActionUtc = now;
                        return;
                    }

                    _processingListedItem = false;

                    var ok = TryOpenRetainerSellFromInventory(_pendingSellSlot);
                    if (!ok)
                    {
                        Log.Information($"[RR] Sell_Open: failed to open RetainerSell from inventory (itemId={_currentSellItemId}); skipping.");
                        _hasPendingSellSlot = false;
                        _pendingSellSlot = default;
                        _currentSellItemId = 0;

                        _runPhase = RunPhase.Sell_FindNextItemInInventory;
                        _lastActionUtc = now;
                        return;
                    }

                    Log.Debug($"[RR] Sell_Open: requested RetainerSell from inventory (itemId={_currentSellItemId}).");

                    _hasPendingSellSlot = false;
                    _pendingSellSlot = default;

                    _runPhase = RunPhase.WaitingRetainerSell;
                    _lastActionUtc = now;
                    return;
                }

            #endregion

            #region Exit unwind

            case RunPhase.ExitToRetainerList:
                {
                    if (marketOpen || itemHistoryOpen)
                    {
                        Log.Debug("[RR] Exit: closing market windows.");
                        CloseMarketWindows();
                        _lastActionUtc = now;
                        return;
                    }

                    if (contextMenuVisible)
                    {
                        Log.Debug("[RR] Exit: dismissing ContextMenu.");
                        FireContextMenuDismiss();
                        _lastActionUtc = now;
                        return;
                    }

                    if (retainerSellVisible)
                    {
                        Log.Debug("[RR] Exit: closing RetainerSell.");
                        CloseRetainerSellIfOpen();
                        _lastActionUtc = now;
                        return;
                    }

                    if (retainerSellListVisible)
                    {
                        Log.Debug("[RR] Exit: closing RetainerSellList.");
                        CloseAddonIfOpen("RetainerSellList");
                        _lastActionUtc = now;
                        return;
                    }

                    if (selectStringVisible)
                    {
                        Log.Debug("[RR] Exit: closing SelectString.");
                        CloseAddonIfOpen("SelectString");
                        _lastActionUtc = now;
                        return;
                    }

                    if (talkVisible)
                    {
                        Log.Debug("[RR] Exit: advancing/closing Talk.");
                        TryAdvanceTalk();
                        _lastActionUtc = now;
                        return;
                    }

                    if (retainerListVisible)
                    {
                        Log.Debug("[RR] Exit: back on RetainerList; continuing.");
                        _runPhase = RunPhase.NeedOpen;
                        _lastActionUtc = now;
                        return;
                    }

                    _lastActionUtc = now;
                    return;
                }

            #endregion

            default:
                return;
        }
    }

    #endregion
}
