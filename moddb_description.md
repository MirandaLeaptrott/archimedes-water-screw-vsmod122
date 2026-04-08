# Archimedes Screw

A mechanically powered water-lifting mod for Vintage Story.

This mod adds a vertical Archimedes screw multiblock that draws water at an intake, lifts it upward, and maintains managed water at the outlet while powered. It is intended for practical pumping, aqueduct routing, and stable long-distance flow setups.

## Highlights

- Vertical Archimedes screw multiblock with intake, straight segments, and outlet variants
- Powered pumping behavior with managed outlet water
- Relay-source system for long-distance aqueduct support
- Controller ownership tracking and cleanup of managed sources on invalidation
- Save/load-safe ownership restore and relay stabilization
- Truncation-safe behavior for very large connected managed-water bodies
- Optional Waterfall compatibility hooks (when Waterfall is installed)
- Runtime tuning via config asset and Config Lib integration

## Current Feature Summary

- Supports managed fresh water, salt water, and boiling water families
- Converts nearby vanilla source placements into managed flow where appropriate
- Uses controller ownership to keep cleanup deterministic when assemblies stop being valid
- Includes admin purge commands for maintenance/testing
- Supports optional verbose debug logging through config

## Compatibility

- Game version: **1.21.6**
- Dependency: `survival`
- Optional: `configlib` (for in-game config UI and save-apply workflow)
- Optional: `waterfall` (compatibility hooks when enabled)

## Configuration

Core settings are in:

- `assets/archimedes_screw/config/settings.json`

If Config Lib is installed, settings are exposed in-game and can be edited in:

- `ModConfig/archimedes_screw.yaml`

Notable settings include:

- Tick and dispatcher tuning (`fastTickMs`, `idleTickMs`, `globalTickMs`)
- Source conversion budget (`maxVanillaConversionPasses`)
- Relay behavior (`enableRelaySources`, `relayStrideBlocks`, relay caps/power thresholds)
- Debug toggles (`debugControllerStatsOnInteract`, `verboseDebug`, compat debug flags)

## Admin Commands

- `/archscrew purge` - Remove all managed water and screw blocks from this mod
- `/archscrew purgewater` - Remove managed Archimedes water only
- `/archscrew purgescrews` - Remove Archimedes screw blocks only

## Notes

- Current build is focused on creative/admin placement workflows.
- Mechanical connections are vertical (top/bottom).
- For large networks, truncation-safe logic pauses unsafe automation instead of creating unmanaged leftovers.

## Known Limitations

- No full survival crafting progression is included yet.
- Extremely large connected managed-water systems may reduce relay automation opportunities while safety guards are active.

## Installation

1. Download the mod zip.
2. Place it in your Vintage Story `Mods` folder.
3. Start the game/server.

## License

See the included `LICENSE` file in this repository/package.
