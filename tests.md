# Archimedes Screw — In-game checks

Run in a creative test world with visible server log (`[archimedes_screw]`). Use `/archscrew purge` between unrelated cases if needed.

| # | What to do | What you should see |
|---|------------|---------------------|
| **1** | Hold the **end cap** (ported creative item). Place it in **still vanilla water** with correct orientation. | Block places as **intake**; no `archimedes-screw-endcap-context` / place failure. |
| **2** | Place **straight** segments upward from that intake, then place the **end cap** on top of the column (dry cell, no water). | Top block places as **outlet** (full collision / upside-down port visual). Assembly works when powered. |
| **3** | Try to place the **end cap** in **empty air** with **no** screw below and **no** water at the target cell. | Placement **fails** with message tied to **end cap context** (needs water or valid screw below). |
| **4** | Build a **working** screw that outputs Archimedes water. In that pool, place a **new** end cap in the **managed water** (same family). Stack straight segments and an outlet; apply power. | Intake accepts **Archimedes** water; screw runs and maintains output source (no permanent `unsupported intake fluid` in log). |
| **5** | Complete a valid assembly (intake in water, straight stack, outlet, mechanical power). **Empty hand**, **right-click** any screw in the stack. | Chat/notification: assembly **functional** (not “outlet orientation could not be resolved” or missing outlet). |
| **6** | Same functional assembly: **remove power**, wait a few seconds, **restore power**. | Water/source behavior recovers; no stuck state requiring break/replace. |
| **7** | Place **two** separate functional screws whose **output pools later connect** (same managed water body). | No immediate mass-delete of sources; both can stay plausible (co-support). If something breaks, note it in **Result**. |
| **8** | Load or build **many** intakes (e.g. 20+) spread in one chunk; all powered and valid. Observe TPS / log spam for ~1 minute. | Game stays usable; no huge burst of per-block tick spam compared to a single screw (central dispatcher). |
| **9** | Break a **middle** straight segment of a tall stack (intake still in water, outlet above gap). **Right-click** remaining part or check log. | Assembly reports **invalid** until repaired; after repairing stack, status **functional** again. |
| **10** | Change config **maxControllersPerGlobalTick** to **1**, restart world, run **8** screws simultaneously. | All still **eventually** update (slower reaction); none permanently ignored. Restore default when done. |

## Result column (optional)

Copy the table and add a **Result** column, or note pass/fail and build version per row.

## Additional ownership tests (single-owner model)

| ID | What to do | What you should see |
|----|------------|---------------------|
| **A1** | Build 2 active screws with same water family, close together, with connected output water. Place a **new vanilla source** adjacent to that connected managed water so it gets converted. | Converted source is owned by **one** controller only (no shared ownership behavior / no tug-of-war logs). |
| **A2** | Determinism check: repeat **A1** twice with identical layout and placement order (after `/archscrew purge`). | The **same controller** wins assignment both times for the same source location. |
| **A3** | Tie-break check: place two candidate outlets as symmetrically as possible around one new converted source. Repeat a few times. | Assignment remains stable and repeatable (no random flipping between controllers). |
| **A4** | Create a connected network where controller X owns multiple sources, then invalidate X (break intake or remove power long enough). | X-owned source nodes are reassigned to the nearest **active valid** controller where possible; if none exists, those sources are drained by cleanup. |

## Relay behavior (compact)

Use one powered intake/outlet stack and one long, mostly horizontal aqueduct line.

| ID | What to do | What you should see |
|----|------------|---------------------|
| **R1** | Enable relays. Set `relayStrideBlocks=14`, `maxRelaySourcesPerController=8`, `requiredMechPowerForMaxRelay=0.02`. Run at **minimum functional power** (just above `minimumNetworkSpeed`). | **0 relays** are created; only the normal seed source exists. |
| **R2** | Increase power gradually from minimum to high. Observe relay count over ~30-60s. | Relay count increases stepwise with power, never exceeding `maxRelaySourcesPerController`. |
| **R3** | Hover power around a threshold (small up/down changes) with non-zero `relayPowerHysteresisPct` (e.g. 0.05). | Relay count does **not** rapidly oscillate every tick (hysteresis effect). |
| **R4** | Build candidate cells that violate one rule at a time: not lowest flowing, liquid below, air below, no cardinal adjacent air. | No relay is created on invalid candidates; creation only occurs when **all** rules are satisfied. |
| **R5** | Force over-cap condition by reducing `maxRelaySourcesPerController` below current relay count. | Relays are trimmed down to cap over subsequent ticks; seed source remains intact. |
| **R6** | Disable controller power or invalidate assembly, then re-enable. | Relay-owned sources drain/cleanup when invalid, and can be recreated when valid again; no orphan relay ownership remains. |
| **R7** | Two nearby active controllers with connected managed water; trigger relay opportunities near boundary. | A relay source is never captured by the wrong owner once owned; no cross-controller thrash. |
