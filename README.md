# Archimedes Screw Mod

This mod adds a mechanically powered water Archimedes screw for Vintage Story. An intake screw is placed inside a still source liquid block, stacked upward with additional screw blocks, and when powered it creates and maintains custom managed source water at the output end of the assembly.

## Project Layout

Source code now lives under `src/`:

- `src/ModSystem/ArchimedesScrewModSystem.cs`
  Registers blocks and block entities, loads config, creates the server-side water manager, and registers the `/archscrew` admin commands.
- `src/Config/ArchimedesScrewConfig.cs`
  Defines the runtime config model loaded from `assets/archimedes_screw/config/settings.json`.
- `src/Systems/ArchimedesWaterNetworkManager.cs`
  Tracks owned water blocks, supports overlapping screw ownership, persists water/controller state, and implements purge operations.
- `src/Blocks/BlockWaterArchimedesScrew.cs`
  Implements screw placement rules, mechanical connectors, and intake validation for the custom water screw block.
- `src/Blocks/BlockArchimedesWater.cs`
  Defines the custom water block classes and notifies the manager when managed water is removed.
- `src/BlockEntities/BlockEntityWaterArchimedesScrew.cs`
  Runs the powered screw logic: finds the outlet, grows managed water, slows checks when idle, and drains owned water when unpowered or broken.

Asset and config files live under `assets/archimedes_screw/`:

- `blocktypes/metal/waterarchimedesscrew.json`: intake and straight screw block asset using vanilla screw visuals.
- `blocktypes/metal/waterarchimedesscrew-outlet.json`: upside-down outlet block asset for the top of the screw stack.
- `blocktypes/liquid/archimedes-water.json`: custom managed water block definition.
- `config/settings.json`: default runtime settings used by the code.
- `config/configlib-patches.json`: Config Lib integration for editing settings in-game.
- `lang/en.json`: English names and config labels.

## Build

Requirements:

- .NET 8 SDK
- Vintage Story 1.21.6 installed

This project defaults to using `/home/dewet/Games/vintagestory` as the game path. If your game is elsewhere, set `VINTAGE_STORY` before building.

Build command:

```bash
dotnet build
```

Build output:

- `bin/Debug/Mods/mod/archimedes_screw.dll`
- `bin/Debug/Mods/mod/modinfo.json`
- `bin/Debug/Mods/mod/assets/...`

To install the build into the game, copy the contents of `bin/Debug/Mods/mod/` into your Vintage Story mods folder, or package that folder as a zip mod.

## In-Game Test Plan

Use this checklist for the current build.

### Basic Setup

1. Install the built mod into your Vintage Story mods folder.
2. Start a test world with creative mode enabled.
3. Open creative inventory and confirm there is an `Archimedes Screw` tab.
4. Confirm the custom screw blocks render with the Archimedes screw model in inventory and when held.

### Placement Tests

1. Put a full still liquid source block in place.
2. Try placing the `ported` intake with no water nearby.
   Expected: placement fails with the water requirement message.
3. Try placing the `ported` intake directly inside the still source block.
   Expected: placement succeeds.
4. Try placing it against flowing water or a non-full water block.
   Expected: placement fails.
5. Try the same placement using different still source liquids, not just normal fresh water.
   Expected: placement succeeds as long as the liquid block is non-flowing and full.
6. Stand on different sides of the target block and place the intake several times.
   Expected: the intake port rotates to face away from the player.
7. Place the new `outlet` block at the top of a screw stack from different sides.
   Expected: the outlet port rotates to face away from the player and the model is upside down.

### Power Tests

1. Stack one or more `straight` screw blocks above the intake.
2. Test one assembly without an outlet block at the top.
3. Test a second assembly with the new `outlet` block at the top.
4. Connect the screw line to mechanical power from above or below.
   Note: this build only connects on the vertical axis.
5. Power the mechanism with a known-good vanilla setup such as axles plus a windmill or water wheel.
6. Right-click any block in the assembly with an empty hand.
   Expected: an on-screen status message explains whether the assembly is valid and, if not, what is wrong.
7. Confirm the screw does not produce water while unpowered.
8. Confirm the screw starts producing water once the mechanical network is rotating.

### Water Behavior Tests

1. In the assembly without an outlet, verify a managed water source appears one block above the top screw.
2. In the assembly with an outlet, verify a managed water source appears in front of the outlet port.
3. Verify the managed water spreads horizontally into nearby air.
4. Let it fill an enclosed area and confirm it stops aggressively growing once no more air is available.
5. Break power and confirm the managed water drains away.
6. Break one of the screw blocks and confirm the managed water drains away.
7. Remove the initial generated water block and confirm the rest of the managed water collapses.

### Multi-Screw Tests

1. Build two powered screws feeding the same water area.
2. Confirm both can support the same managed water region.
3. Break or depower only one screw.
   Expected: the water remains if the second screw still supports it.
4. Break or depower the second screw as well.
   Expected: the managed water drains away.

### Stability Checks

1. Let the powered screw run for a while after water has spread.
2. Move around the generated water and listen for any abnormal sound behavior.
3. Watch for crashes related to ambient sound, block ticking, or mechanical rendering.
4. If there is a crash, save the latest stack trace from `client-main.log`.

## Admin Commands

- `/archscrew purge`: remove all mod water and screw blocks
- `/archscrew purgewater`: remove all managed Archimedes water
- `/archscrew purgescrews`: remove all custom screw blocks

## Current Notes

- Crafting recipes are not added yet; this build is creative/admin placement only.
- The screw currently expects vertical mechanical connections only.
- The intake currently requires a full still source liquid at the intake block position.
- The mod compiles cleanly, but it still needs real in-game behavior validation and tuning.
