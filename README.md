# Retainer Repricer

Retainer Repricer is a fully automated Dalamud plugin for Final Fantasy XIV that manages retainer market activity end-to-end. It safely reprices existing listings and can optionally list new items from player inventory, all without manual UI interaction.

---

## What It Does

- Automatically cycles through enabled retainers at a summoning bell  
- Reprices all existing listings using live market data  
- Optionally lists new items from player inventory after repricing current items
- Handles all UI navigation, application, and cleanup deterministically  
- Returns cleanly to the Retainer List after each retainer and on completion  

Once started, the plugin runs unattended.

---

## Core Design

- **Deterministic state machine**  
  Advances only when the expected addon is visible *and ready*. No blind delays or timing assumptions.

- **UI-safe automation**  
  Uses AddonMaster where available and direct callbacks where required. All panels are closed explicitly and in order.

- **Strict readiness gates**  
  Addons must have populated data (not just visibility) before progressing, preventing skipped retainers or items.

- **Market safety checks**  
  Handles throttling, empty results, and unexpected messages gracefully. Prices are applied only after verification.

---

## Selling + Repricing Pipeline

1. Enter retainer sell list  
3. Reprice all existing listings slot-by-slot  
2. (Optional) List new items from inventory based on a configured Sell List  
4. Unwind UI back to Retainer List  
5. Proceed to next retainer  

New listings and repricing share the same pricing logic and safety checks.

---

## Configuration & UX

- Persistent Sell List with inventory context-menu integration  
- Native-style context menu entries with proper icon prefixes  
- Immediate config saves on add/remove actions  
- Minimal overlay for start/stop and status   

---

## Compatibility & Stability

- Hardened for coexistence with other automation plugins  
- Safe if other plugins auto-close or auto-open shared addons  
- All logic is addon-visibility driven, not plugin-specific  

---

## Status

- Feature-complete for automated selling and repricing  
- Stable across retainers, items, and shared-addon environments 
- No manual interaction is required at any stage of execution.