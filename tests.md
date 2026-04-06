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
