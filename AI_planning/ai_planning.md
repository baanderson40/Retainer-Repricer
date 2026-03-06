# Smart Pricing Gate Implementation Plan

## Project Overview

This plan outlines the implementation of a smart pricing gate for the Retainer Repricer Dalamud plugin. The goal is to prevent severe undercutting by integrating with the Universalis API to retrieve average historical item prices. If a market board listing is significantly below a configurable percentage of this Universalis average, the repricing logic adjusts the target price upwards to a "reasonable" level.

## Objective

Prevent severe undercutting by fetching average item prices from Universalis and iteratively finding a "reasonable" market price to undercut, based on a configurable percentage-based gate.

## Phases

### Phase 1: Universalis API Integration

#### 1.1 Create `UniversalisApiClient`

- **Location:** `RetainerRepricer/Services/UniversalisApiClient.cs`
- **Purpose:** Handle all interactions with the Universalis API.
- **Implementation Notes:**
  - Utilize `System.Net.Http.HttpClient` (already used via ECommons) for outbound requests.
  - Implement `Task<decimal?> GetAveragePrice(uint itemId, uint worldId, bool isHq)`:
    - Build URL: `https://universalis.app/api/v6/{worldId}/{itemId}?entriesToReturn=1&isHQ={isHq}&history=1` (respect configurable base URL).
    - Deserialize JSON and extract an average sale price (confirm exact JSON fields during implementation, likely `averagePrice` from `recentHistory`).
    - Include robust exception handling and log failures; return `null` if data cannot be fetched.
    - Consider lightweight response caching (dictionary keyed by item/world/HQ) if API latency becomes noticeable; start without caching.

#### 1.2 Update Configuration

- **File:** `RetainerRepricer/Configuration.cs`
- **Add Properties:**
  - `bool EnableUndercutPreventionGate { get; set; } = true;`
  - `bool UseUniversalisApi { get; set; } = true;`
  - `string UniversalisApiBaseUrl { get; set; } = "https://universalis.app/api/v6/";`
  - `float UndercutPreventionPercent { get; set; } = 0.5f;` (50% default)
- Ensure properties are saved/loaded via existing config serialization.

#### 1.3 Expose Settings in Config Window

- **File:** `RetainerRepricer/Windows/ConfigWindow.cs`
- **Tasks:**
  - Add UI elements (checkboxes, input text, slider) bound to new configuration properties.
  - Provide tooltips describing Universalis usage, the percentage gate, and fallback behavior.
  - Guard edits behind `ImGui.BeginDisabled` if dependent options are toggled off (e.g., disable URL input when Universalis is disabled).

### Phase 2: Pricing Gate Logic

#### 2.1 Access Player World ID

- **File:** `RetainerRepricer/Plugin.cs`
- **Tasks:**
  - Inject `IClientState` via `[PluginService]` to access `ClientState.LocalPlayer?.CurrentWorld.Value.Id`.
  - Add helper `private uint? GetCurrentWorldId()` with null checks; log a warning and skip Universalis calls if unavailable.

#### 2.2 Integrate Gate in `TryPickReferenceListing`

- **File:** `RetainerRepricer/Plugin.cs`
- **Logic Flow:**
  1. Execute existing market list reading to gather up to 10 rows (HQ or NQ subset depending on `_currentIsHq`).
  2. If gate disabled or Universalis disabled/unavailable, preserve current behavior (pick absolute lowest row consistent with HQ requirement).
  3. When gate active:
     - Call `UniversalisApiClient.GetAveragePrice()` with item ID, world ID, and HQ flag.
     - If `averagePrice` is `null`, log and fall back to current behavior.
     - Compute `minimumAllowedPrice = max(1, floor(averagePrice * (1 - UndercutPreventionPercent)))`.
     - Iterate rows sequentially:
       - Skip rows failing HQ criteria (existing behavior remains).
       - Select the first row whose price is >= `minimumAllowedPrice`; use it as reference.
     - If no row meets the threshold, set reference price to `minimumAllowedPrice` (Option A) and seller to placeholder (e.g., "UniversalisFloor").
  4. Return reference price/seller for downstream use.
- **Considerations:**
  - Ensure gil parsing already provided by `_uiReader` remains reliable before applying comparisons.
  - Respect existing logging verbosity; add dedicated logs when the gate adjusts pricing or when Universalis data is used/fails.

#### 2.3 Maintain `DecideNewPrice`

- `DecideNewPrice` already undercuts by `UndercutAmount`. With the gate providing a vetted reference price, no further changes are expected beyond possibly logging when a floor price was used.

### Phase 3: Refinement and Verification

#### 3.1 Logging Enhancements

- Add `Log.Information` or `Log.Debug` statements for:
  - Universalis requests (success/failure, average price returned).
  - Gate activations (e.g., "Row 0 price 1000 below floor 1500; using floor").
  - Fallback scenarios (Universalis disabled, API error, missing world ID).

#### 3.2 Manual Testing Matrix

- **Scenarios:**
  - Gate enabled + Universalis enabled with normal market → ensure slight undercut of first reasonable row.
  - Gate enabled + Universalis enabled with egregious undercut rows → ensure floor price applied.
  - Gate enabled + Universalis disabled → revert to old behavior; confirm config toggles respected.
  - Gate disabled → legacy undercut logic regardless of Universalis availability.
  - HQ vs NQ listings; items with sparse sales history (verify Universalis returns data or falls back gracefully).

#### 3.3 Performance & Robustness Checks

- Monitor latency introduced by API requests; if needed, add simple caching keyed by `(itemId, worldId, isHq)` with short TTL.
- Consider retry/backoff strategy if Universalis returns transient errors (HTTP 5xx or timeouts).
- Ensure plugin remains responsive when Universalis is unreachable (fail fast and fall back within a frame tick).

#### 3.4 Documentation & Future Work

- Update `AGENTS.md` or other contributor docs to mention Universalis integration and relevant configuration.
- Potential future enhancements:
  - Cache Universalis averages per session.
  - Allow switching between historical averages vs. recent listing averages from Universalis.
  - Provide per-item overrides for the gate percentage.
