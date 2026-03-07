# Retainer Repricer

Fully automated Dalamud plugin for Final Fantasy XIV that reprices and relists retainer market entries using deterministic UI automation.

---

## Key Features
- Cycles every enabled retainer at a summoning bell with zero manual input.
- Reprices existing listings using Universalis market data and applies a fixed 1-gil undercut to the validated lowest listing.
- Optionally lists new inventory items through a configurable Sell List with HQ/NQ awareness.
- Navigates, confirms, and closes all FFXIV UI panels safely, guaranteeing a clean return path.
- Persists configuration immediately so stop/start cycles never lose selections.

---

## Requirements & Compatibility
- Final Fantasy XIV retail client with Dalamud available through XIVLauncher.
- Character retainers must already be unlocked and accessible at a summoning bell.
- Access to Universalis API (public) for price retrieval; the plugin handles caching and rate limits internally.

---

## Configuration
- **Retainer Enablement:** Enable specific retainers from the main window; only checked retainers participate in automation.
- **Sell List:** Use the inventory context-menu integration to add or remove items the plugin may list automatically when inventory has stock.
- **Pricing Gate:** Toggle whether Universalis floor averages inform listings, set the percent threshold for skipping thin markets, and opt into Universalis fallback when the board is empty.
- **Status Overlay:** Displays current state machine phase, latest action timestamp, and any throttling timer.
- All settings persist instantly; no explicit save step is required.

---

## Operation Flow
1. Stand at a summoning bell with retainer list open and start the plugin.
2. The state machine starts, interacts with talk windows, and selects the first enabled retainer.
3. Within each enabled retainer:
   - Reprice every slot in the sell list using live data.
   - Optionally list new items queued in the Sell List.
   - Confirm compare windows and market confirmations before proceeding.
4. The plugin unwinds UI windows (Sell → Retainer → Talk) and advances to the next retainer.
5. When all retainers are processed or the user stops the plugin, the state machine returns to Idle after closing any open addons.

---

## Commands
- `/repricer` or `/rr` — show the same help output as `/repricer help`.
- `/repricer start [mode]` — begin automation; omit `mode` to run both repricing and selling. Supported modes: `price` (reprice existing listings only) and `sell` (process Sell List inventory only).
- `/repricer stop` — halt the current run and unwind open UI windows.
- `/repricer config` or `/repricer c` — toggle the configuration window.

---

## Architecture Highlights
- **Deterministic State Machine:** `RunPhase` governs every action. Transitions occur only when addons are both visible and populated.
- **Readiness Guards:** All pointer accesses check `addon.IsNull`, visibility, and node counts before use.
- **UI Automation:** Uses AddonMaster wrappers and `Callback.Fire` for button presses; context menus are cleared before continuing.
- **Timing Discipline:** Timestamp comparison fields (`ActionIntervalSeconds`, `MbBaseIntervalSeconds`, `FrameworkTickIntervalSeconds`) pace actions instead of `Thread.Sleep`.
- **Data Source:** `UniversalisApiClient` handles HTTP requests with timeouts, caching, and cancellation so the UI thread remains responsive.

---

## Safety & Limitations
- Designed for the Dalamud sandbox; other automation plugins can coexist as long as they do not hijack the same addons simultaneously.
- Requires manual positioning at a summoning bell; the plugin does not auto-navigate the world.
- Inventory order is not assumed—every listing step refreshes node data before making decisions.
- HQ and NQ queues are processed separately to prevent cross-quality pricing errors.

---

## Troubleshooting & FAQ
- **Nothing happens when I press Start:** Confirm at least one retainer is enabled and the character is engaged with a summoning bell.
- **Plugin stops mid-retainer:** Check the Dalamud logs for `[RR]` or `[RL]` messages; most errors indicate a blocked addon (usually an unclosed context menu).
- **Prices seem stale:** Universalis responses are cached per item; manually refresh by waiting for the next cycle or clearing the cache via the configuration window.
- **Context menu entries missing:** Make sure Dalamud's inventory context menu integration is permitted and no conflicting plugins override it.
- **Retainer slots skipped:** Ensure the Sell List matches actual inventory items and that HQ/NQ filters are set appropriately.

---

## License
Retainer Repricer is distributed under the AGPL-3.0-or-later license. See `LICENSE.md` for full terms.
