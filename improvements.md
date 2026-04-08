# Archimedes Screw Repo Deep Dive: Improvements

This document lists concrete improvements found during a deep review of `src/`, config assets, and docs.

## High-priority bug fixes

1. **Harden position-key parsing to avoid server crashes on corrupt save data**
   - **Why it matters:** `ParsePosKey` throws `FormatException`; if stored key data is malformed, load/purge/tick paths could throw and destabilize server startup.
   - **Evidence:** Throwing parse methods in `ArchimedesWaterNetworkManager` and `BlockEntityWaterArchimedesScrew`.
   - **Suggested fix:** Add `TryParsePosKey(string, out BlockPos)` and skip/log bad entries instead of throwing.

2. **Avoid heavy reflection scan every compat patch attempt**
   - **Why it matters:** Waterfall compat fallback scans all loaded assemblies/types searching for `SpillContents`, which can be expensive and brittle.
   - **Evidence:** `ResolveWaterfallSpillMethod()` loops every assembly + `GetTypes()`.
   - **Suggested fix:** Cache successful target by full type name, constrain scan scope (known namespaces/assembly names), and degrade to once-per-session warning on failure.

## Medium-priority reliability and correctness improvements

3. **Validate/clamp config values centrally**
   - **Why it matters:** Config Lib ranges help in UI, but runtime should still defend against invalid/malformed values from file edits or other mods.
   - **Suggested fix:** Add `Normalize()` in `WaterConfig` or in mod system load path to clamp all values (tick rates, caps, hysteresis, speed thresholds) with warning logs on correction.

4. **Use unload-safe event listener cleanup for Config Lib handlers**
   - **Why it matters:** `RegisterEventBusListener` handlers are added, but there is no explicit unregister path; on long sessions/reload scenarios this can risk duplicate handling.
   - **Suggested fix:** If API supports unregister in target game version, store IDs and unregister in `Dispose`; otherwise gate handlers with a disposed flag.

5. **Stabilize central tick cursor advancement fairness**
   - **Why it matters:** Cursor increments by one each global tick regardless of processed count. Under heavy due-load + low budget this can skew fairness or increase latency variance.
   - **Suggested fix:** Advance cursor by number of inspected or processed entries with wrap-around and benchmark with 100+ controllers.

6. **Avoid repeated string key generation in hot loops**
   - **Why it matters:** `PosKey(...)` string creation is frequent in BFS, relay creation, ownership checks, and drain routines.
   - **Suggested fix:** Consider lightweight struct key (`(int x,int y,int z)` or custom comparer) for internal dictionaries; keep string serialization only for persistence/logging.

7. **Reduce temporary allocations in controller hot paths**
   - **Why it matters:** Frequent `.ToList()`, `.ToArray()`, LINQ sorting, and copied `BlockPos` objects may increase GC pressure in busy servers.
   - **Suggested fix:** Replace hot-path LINQ with pooled lists/manual loops where practical; batch snapshot updates only when changed.

8. **Guard against stale `WeakReference` buildup outside compaction windows**
    - **Why it matters:** Central tick list compacts every 20 cycles; under high churn this can still accumulate stale entries temporarily.
    - **Suggested fix:** Opportunistic cleanup when stale ratio exceeds threshold during dispatch.

## Performance and scalability opportunities

9. **Cap/partition BFS work per tick for extreme water networks**
    - **Why it matters:** `CollectConnectedManagedWater` has `MaxBfsVisited=4096`, but large connected networks can still consume substantial CPU bursts.
    - **Suggested fix:** Add optional per-tick BFS budget or progressive scan mode for very large components, with debug counters to track truncation impact.

10. **Improve relay candidate selection complexity**
    - **Why it matters:** Relay creation sorts full distance map and performs multiple ownership checks; complexity grows with network size.
    - **Suggested fix:** Introduce bounded candidate heap or early filter pipeline to avoid sorting all nodes every attempt.

11. **Add perf metric aggregation by controller count tiers**
    - **Why it matters:** Existing profiler is useful but mostly operation-centric. Capacity planning benefits from metrics normalized by active controller counts.
    - **Suggested fix:** Log active controllers, due controllers, processed controllers, avg queue latency, and skipped scans per interval.

## Code cleanup and maintainability

12. **Consolidate duplicate position encode/decode helpers**
    - **Why it matters:** Similar encode/decode/parse logic exists in multiple classes, increasing drift risk.
    - **Suggested fix:** Move to a shared utility class (`ArchimedesPosCodec`) with tested parse and serialization methods.

13. **Consolidate managed water checks into one policy helper**
    - **Why it matters:** Validity checks for solid/fluid output/intake clear conditions are duplicated between analyzer/controller/manager.
    - **Suggested fix:** Extract canonical predicates for “output cell usable”, “intake fluid valid”, “source replacable”.

14. **Review logging verbosity in production paths**
    - **Why it matters:** `Notification` level is used frequently in placement/status/control flows; this can flood logs on active servers.
    - **Suggested fix:** Move repetitive logs to debug-gated path (`DebugControllerStatsOnInteract`-style or dedicated `water.debugLogs` setting), keep key lifecycle logs at notification.

15. **Prefer explicit naming around “source ownership” semantics**
    - **Why it matters:** Some methods (“EnsureSourceOwned”, “AssignConnectedSource...”, “TrackAssignedSource...”) are close in purpose but semantically distinct.
    - **Suggested fix:** Tighten naming/docs around “ownership assignment”, “state snapshot”, and “fluid placement” phases.

16. **Clean minor dead/placeholder code**
    - **Example:** Empty override `DidConnectAt(...)` can be removed (if not required) or commented with intent.

## Test coverage improvements

17. **Add automated tests around ownership determinism and reassignment**
    - **Why it matters:** These behaviors are central and easy to regress with performance changes.
    - **Suggested scenarios:** symmetric tie-breaks, invalidation handoff, merge/split network ownership transitions.

18. **Add tests for config reload semantics**
    - **Why it matters:** Deferred apply-on-save behavior is intentional; regressions here are subtle.
    - **Suggested scenarios:** setting changed event queueing, save event application, central tick restart-required values.

19. **Add persistence corruption-resilience tests**
    - **Why it matters:** Save data is long-lived in multiplayer worlds.
    - **Suggested scenarios:** malformed keys, partial owned arrays, duplicate source owner conflicts, stale controller IDs.

20. **Add compatibility contract tests for Waterfall hook resolution**
    - **Why it matters:** Reflection-based targeting is fragile across mod updates.
    - **Suggested scenarios:** method found directly, fallback found, not found (graceful no-op), debug logging path.

## Documentation and ops improvements

21. **Document config source-of-truth and precedence**
    - **Why it matters:** Current setup uses defaults + config asset + Config Lib patching + save-apply behavior.
    - **Suggested fix:** Add a short section in `README.md` that explains precedence and when values take effect.

22. **Versioned migration notes for saved-state format changes**
    - **Why it matters:** Future changes to ownership/snapshots/parsing benefit from explicit migration handling.
    - **Suggested fix:** Add save schema version key and migration hooks in load path.

23. **Expand troubleshooting guide**
    - **Why it matters:** Admin users need rapid diagnosis for “assembly valid but dry”, “relay not creating”, “compat inactive”.
    - **Suggested fix:** Add symptom -> probable cause -> command/check matrix in docs.

## Suggested implementation order

1. Fix config/behavior correctness: items **1-2** (parse hardening, compat reflection).
2. Reliability hardening: items **3-5**.
3. Performance improvements: items **6-11**.
4. Cleanup and test expansion: items **12-20**.
5. Documentation/ops polish: items **21-23**.

## Quick wins (low effort, high value)

- Replace throwing `ParsePosKey` with `TryParse` + warning logs.
- Add config value normalization/clamping at startup and on config save.
