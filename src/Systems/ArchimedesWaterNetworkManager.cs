using System;
using System.Collections.Concurrent;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Util;

namespace ArchimedesScrew;

public sealed class ArchimedesWaterNetworkManager : IDisposable
{
    private const string SaveKeyScrewBlocks = "archimedes_screw/screwblocks";
    private const string SaveKeyControllerPositions = "archimedes_screw/controllerpositions";
    private const string SaveKeyControllerOwned = "archimedes_screw/controllerowned";

    private const int MaxBfsVisited = 4096;

    private readonly ICoreServerAPI api;
    private readonly ArchimedesScrewConfig config;

    private readonly Dictionary<string, string> sourceOwnerByPos = new(StringComparer.Ordinal);
    private readonly HashSet<string> screwBlockKeys = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> controllerPosById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int[]> controllerOwnedById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, WeakReference<BlockEntityWaterArchimedesScrew>> loadedControllers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> suppressedRemovalNotifications = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Block> managedBlockCache = new(StringComparer.Ordinal);

    private readonly Dictionary<string, WeakReference<BlockEntityWaterArchimedesScrew>> centralWaterTickControllers = new(StringComparer.Ordinal);
    private readonly List<string> centralWaterTickOrder = new();
    private readonly HashSet<string> centralWaterTickSet = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ConnectedManagedComponentCacheEntry> connectedManagedComponentCache = new(StringComparer.Ordinal);
    private readonly List<ArchimedesOutletState> activeSeedStatesCache = new();
    private int centralWaterTickCursor;
    private int centralWaterTickCountDownToCompaction = 20;
    private int connectedManagedComponentCacheGeneration;
    private int activeSeedStatesCacheGeneration = -1;
    private bool isInGlobalWaterTickDispatch;
    private long globalWaterTickListenerId;
    private long postLoadReactivationListenerId;
    private int postLoadReactivationAttemptsRemaining;

    public ArchimedesWaterNetworkManager(ICoreServerAPI api, ArchimedesScrewConfig config)
    {
        this.api = api;
        this.config = config;
    }

    public void Dispose()
    {
        StopCentralWaterTick();
        StopPostLoadReactivation();
        GC.SuppressFinalize(this);
    }

    /// <summary>Registers the single server tick that runs intake water logic (staggered, budgeted).</summary>
    public void StartCentralWaterTick()
    {
        StopCentralWaterTick();
        int interval = Math.Max(5, config.Water.GlobalTickMs);
        globalWaterTickListenerId = api.Event.RegisterGameTickListener(OnGlobalWaterTick, interval);
    }

    /// <summary>Call after <see cref="ArchimedesScrewConfig.Water"/> fields change (e.g. Config Lib live reload).</summary>
    public void RestartCentralWaterTickForCurrentConfig()
    {
        StartCentralWaterTick();
    }

    public void StopCentralWaterTick()
    {
        if (globalWaterTickListenerId != 0)
        {
            api.Event.UnregisterGameTickListener(globalWaterTickListenerId);
            globalWaterTickListenerId = 0;
        }
    }

    public void BeginPostLoadReactivation(int initialDelayMs = 300, int retryIntervalMs = 700, int maxAttempts = 8)
    {
        StopPostLoadReactivation();
        postLoadReactivationAttemptsRemaining = Math.Max(1, maxAttempts);

        int initialDelay = Math.Max(50, initialDelayMs);
        int retryInterval = Math.Max(100, retryIntervalMs);
        postLoadReactivationListenerId = api.Event.RegisterGameTickListener(
            _ => OnPostLoadReactivationTick(retryInterval),
            initialDelay
        );
    }

    private void StopPostLoadReactivation()
    {
        if (postLoadReactivationListenerId != 0)
        {
            api.Event.UnregisterGameTickListener(postLoadReactivationListenerId);
            postLoadReactivationListenerId = 0;
        }
    }

    private void OnPostLoadReactivationTick(int retryIntervalMs)
    {
        if (postLoadReactivationAttemptsRemaining <= 0)
        {
            StopPostLoadReactivation();
            return;
        }

        postLoadReactivationAttemptsRemaining--;
        int touched = ReactivateManagedFluidsFromTrackedAnchors();

        if (postLoadReactivationAttemptsRemaining <= 0)
        {
            StopPostLoadReactivation();
            api.Logger.Notification(
                "{0} Post-load managed fluid reactivation finished; touched={1}",
                ArchimedesScrewModSystem.LogPrefix,
                touched
            );
            return;
        }

        StopPostLoadReactivation();
        postLoadReactivationListenerId = api.Event.RegisterGameTickListener(
            _ => OnPostLoadReactivationTick(retryIntervalMs),
            retryIntervalMs
        );
    }

    public int ReactivateManagedFluidsFromTrackedAnchors()
    {
        HashSet<string> anchors = BuildManagedWaterAnchorKeys();
        HashSet<string> allWaterKeys = new(StringComparer.Ordinal);
        foreach (string key in anchors)
        {
            BlockPos pos = ParsePosKey(key);
            CollectManagedComponentKeysAroundAnchor(pos, allWaterKeys);
        }

        int touched = 0;
        foreach (string key in allWaterKeys)
        {
            BlockPos pos = ParsePosKey(key);
            Block fluid = api.World.BlockAccessor.GetBlock(pos, BlockLayersAccess.Fluid);
            if (!IsArchimedesWaterBlock(fluid))
            {
                continue;
            }

            TriggerLiquidUpdates(pos, fluid);
            touched++;
        }

        api.Logger.Notification(
            "{0} Post-load managed fluid reactivation pass touched={1} (anchors={2})",
            ArchimedesScrewModSystem.LogPrefix,
            touched,
            anchors.Count
        );

        return touched;
    }

    public void RegisterForCentralWaterTick(BlockEntityWaterArchimedesScrew controller)
    {
        string id = controller.ControllerId;
        centralWaterTickControllers[id] = new WeakReference<BlockEntityWaterArchimedesScrew>(controller);
        if (centralWaterTickSet.Add(id))
        {
            centralWaterTickOrder.Add(id);
        }
    }

    public void UnregisterFromCentralWaterTick(string controllerId)
    {
        centralWaterTickControllers.Remove(controllerId);
        if (!centralWaterTickSet.Remove(controllerId))
        {
            return;
        }

        centralWaterTickOrder.RemoveAll(s => string.Equals(s, controllerId, StringComparison.Ordinal));
    }

    private void OnGlobalWaterTick(float dt)
    {
        using ArchimedesPerf.PerfScope _perf = ArchimedesPerf.Measure("water.globalTick");
        isInGlobalWaterTickDispatch = true;
        try
        {
            connectedManagedComponentCacheGeneration++;
            connectedManagedComponentCache.Clear();
            activeSeedStatesCacheGeneration = -1;
            activeSeedStatesCache.Clear();
            if (--centralWaterTickCountDownToCompaction <= 0)
            {
                CompactCentralWaterTickList();
                centralWaterTickCountDownToCompaction = 20;
            }

            long now = Environment.TickCount64;
            int budget = Math.Max(1, config.Water.MaxControllersPerGlobalTick);
            int n = centralWaterTickOrder.Count;
            if (n == 0)
            {
                return;
            }

            int processed = 0;
            for (int step = 0; step < n; step++)
            {
                if (processed >= budget)
                {
                    break;
                }

                int idx = (centralWaterTickCursor + step) % n;
                string id = centralWaterTickOrder[idx];

                if (!centralWaterTickControllers.TryGetValue(id, out WeakReference<BlockEntityWaterArchimedesScrew>? wr) ||
                    !wr.TryGetTarget(out BlockEntityWaterArchimedesScrew? be))
                {
                    continue;
                }

                if (!be.IsCentralWaterTickDue(now))
                {
                    continue;
                }

                be.RunCentralWaterTick();
                processed++;
            }

            ArchimedesPerf.AddCount("water.globalTick.processedControllers", processed);
            centralWaterTickCursor = (centralWaterTickCursor + 1) % n;
        }
        finally
        {
            isInGlobalWaterTickDispatch = false;
            ArchimedesPerf.MaybeFlush(api);
        }
    }

    private void CompactCentralWaterTickList()
    {
        for (int i = centralWaterTickOrder.Count - 1; i >= 0; i--)
        {
            string id = centralWaterTickOrder[i];
            if (!centralWaterTickControllers.TryGetValue(id, out WeakReference<BlockEntityWaterArchimedesScrew>? wr) ||
                !wr.TryGetTarget(out _))
            {
                centralWaterTickOrder.RemoveAt(i);
                centralWaterTickControllers.Remove(id);
                centralWaterTickSet.Remove(id);
            }
        }

        if (centralWaterTickCursor >= centralWaterTickOrder.Count && centralWaterTickOrder.Count > 0)
        {
            centralWaterTickCursor %= centralWaterTickOrder.Count;
        }
    }

    public void Load()
    {
        screwBlockKeys.Clear();
        controllerPosById.Clear();
        controllerOwnedById.Clear();
        sourceOwnerByPos.Clear();

        string[]? screwKeys = LoadSerialized<string[]>(SaveKeyScrewBlocks);
        if (screwKeys != null)
        {
            foreach (string key in screwKeys)
            {
                screwBlockKeys.Add(key);
            }
        }

        Dictionary<string, string>? controllerPositions = LoadSerialized<Dictionary<string, string>>(SaveKeyControllerPositions);
        if (controllerPositions != null)
        {
            foreach ((string id, string posKey) in controllerPositions)
            {
                controllerPosById[id] = posKey;
            }
        }

        Dictionary<string, int[]>? ownedSources = LoadSerialized<Dictionary<string, int[]>>(SaveKeyControllerOwned);
        if (ownedSources == null)
        {
            return;
        }

        foreach ((string controllerId, int[]? flatPositions) in ownedSources)
        {
            if (flatPositions == null || flatPositions.Length == 0)
            {
                controllerOwnedById[controllerId] = Array.Empty<int>();
                continue;
            }

            controllerOwnedById[controllerId] = flatPositions;
            foreach (BlockPos pos in DecodePositions(flatPositions))
            {
                string key = PosKey(pos);
                if (!sourceOwnerByPos.TryGetValue(key, out string? existing))
                {
                    sourceOwnerByPos[key] = controllerId;
                    continue;
                }

                // Deterministic conflict resolution for old saves that had multi-owner snapshots.
                if (string.CompareOrdinal(controllerId, existing) < 0)
                {
                    sourceOwnerByPos[key] = controllerId;
                }
            }
        }

        api.Logger.Notification(
            "{0} Loaded water manager state: screws={1}, controllers={2}, trackedSources={3}",
            ArchimedesScrewModSystem.LogPrefix,
            screwBlockKeys.Count,
            controllerPosById.Count,
            sourceOwnerByPos.Count
        );
    }

    public void Save()
    {
        api.WorldManager.SaveGame.StoreData(SaveKeyScrewBlocks, SerializerUtil.Serialize(screwBlockKeys.ToArray()));
        api.WorldManager.SaveGame.StoreData(SaveKeyControllerPositions, SerializerUtil.Serialize(controllerPosById));
        api.WorldManager.SaveGame.StoreData(SaveKeyControllerOwned, SerializerUtil.Serialize(controllerOwnedById));
        api.Logger.Notification(
            "{0} Saved water manager state: screws={1}, controllers={2}, trackedSources={3}",
            ArchimedesScrewModSystem.LogPrefix,
            screwBlockKeys.Count,
            controllerPosById.Count,
            sourceOwnerByPos.Count
        );
    }

    public void RegisterLoadedController(BlockEntityWaterArchimedesScrew controller)
    {
        loadedControllers[controller.ControllerId] = new WeakReference<BlockEntityWaterArchimedesScrew>(controller);
        controllerPosById[controller.ControllerId] = PosKey(controller.Pos);
    }

    public void UnregisterLoadedController(string controllerId)
    {
        loadedControllers.Remove(controllerId);
    }

    public void RegisterRestoredOwnership(string controllerId, BlockPos controllerPos, IReadOnlyCollection<BlockPos> sourcePositions)
    {
        controllerPosById[controllerId] = PosKey(controllerPos);
        controllerOwnedById[controllerId] = EncodePositions(sourcePositions);

        foreach (BlockPos pos in sourcePositions)
        {
            sourceOwnerByPos[PosKey(pos)] = controllerId;
        }
    }

    public void UpdateControllerSnapshot(string controllerId, BlockPos controllerPos, IReadOnlyCollection<BlockPos> sourcePositions)
    {
        controllerPosById[controllerId] = PosKey(controllerPos);
        controllerOwnedById[controllerId] = EncodePositions(sourcePositions);
    }

    public void RemoveControllerSnapshot(string controllerId)
    {
        controllerPosById.Remove(controllerId);
        controllerOwnedById.Remove(controllerId);
        loadedControllers.Remove(controllerId);
        UnregisterFromCentralWaterTick(controllerId);
    }

    public void RegisterScrewBlock(BlockPos pos)
    {
        screwBlockKeys.Add(PosKey(pos));
    }

    public void UnregisterScrewBlock(BlockPos pos)
    {
        screwBlockKeys.Remove(PosKey(pos));
    }

    public bool IsArchimedesWaterBlock(Block block)
    {
        return block.Code?.Domain == ArchimedesScrewModSystem.ModId &&
               ArchimedesWaterFamilies.IsManagedWater(block);
    }

    public bool IsArchimedesSourceBlock(Block block)
    {
        return IsArchimedesWaterBlock(block) &&
               string.Equals(block.Variant?["flow"], "still", StringComparison.Ordinal) &&
               string.Equals(block.Variant?["height"], "7", StringComparison.Ordinal);
    }

    public bool IsArchimedesLowestFlowingBlock(Block block)
    {
        return IsArchimedesWaterBlock(block) &&
               !string.Equals(block.Variant?["flow"], "still", StringComparison.Ordinal) &&
               string.Equals(block.Variant?["height"], "1", StringComparison.Ordinal);
    }

    public bool TryGetSourceOwner(BlockPos pos, out string ownerId)
    {
        return sourceOwnerByPos.TryGetValue(PosKey(pos), out ownerId!);
    }

    public bool TryResolveVanillaWaterFamily(Block block, out string familyId)
    {
        if (ArchimedesWaterFamilies.TryResolveVanillaFamily(block, out ArchimedesWaterFamily family))
        {
            familyId = family.Id;
            return true;
        }

        familyId = string.Empty;
        return false;
    }

    public bool TryResolveManagedWaterFamily(Block block, out string familyId)
    {
        if (ArchimedesWaterFamilies.TryResolveManagedFamily(block, out ArchimedesWaterFamily family))
        {
            familyId = family.Id;
            return true;
        }

        familyId = string.Empty;
        return false;
    }

    /// <summary>Vanilla or mod-managed Archimedes liquid at an intake cell (any flow/height).</summary>
    public bool TryResolveIntakeWaterFamily(Block block, out string familyId)
    {
        if (TryResolveVanillaWaterFamily(block, out familyId))
        {
            return true;
        }

        if (TryResolveManagedWaterFamily(block, out familyId))
        {
            return true;
        }

        familyId = string.Empty;
        return false;
    }

    public List<ArchimedesOutletState> GetActiveSeedStates()
    {
        List<ArchimedesOutletState> states = new();
        foreach (WeakReference<BlockEntityWaterArchimedesScrew> reference in loadedControllers.Values)
        {
            if (reference.TryGetTarget(out BlockEntityWaterArchimedesScrew? controller) &&
                controller.TryGetActiveSeedState(out ArchimedesOutletState state))
            {
                states.Add(state);
            }
        }

        return states;
    }

    public IReadOnlyList<ArchimedesOutletState> GetActiveSeedStatesCached()
    {
        if (!isInGlobalWaterTickDispatch)
        {
            return GetActiveSeedStates();
        }

        if (activeSeedStatesCacheGeneration == connectedManagedComponentCacheGeneration)
        {
            return activeSeedStatesCache;
        }

        activeSeedStatesCache.Clear();
        foreach (WeakReference<BlockEntityWaterArchimedesScrew> reference in loadedControllers.Values)
        {
            if (reference.TryGetTarget(out BlockEntityWaterArchimedesScrew? controller) &&
                controller.TryGetActiveSeedState(out ArchimedesOutletState state))
            {
                activeSeedStatesCache.Add(state);
            }
        }

        activeSeedStatesCacheGeneration = connectedManagedComponentCacheGeneration;
        return activeSeedStatesCache;
    }

    public HashSet<string> CollectConnectedManagedWater(BlockPos startPos, out Dictionary<string, BlockPos> positionsByKey)
    {
        using ArchimedesPerf.PerfScope _perf = ArchimedesPerf.Measure("water.collectConnectedManaged");
        positionsByKey = new Dictionary<string, BlockPos>(StringComparer.Ordinal);
        HashSet<string> visited = new(StringComparer.Ordinal);

        Block startFluid = api.World.BlockAccessor.GetBlock(startPos, BlockLayersAccess.Fluid);
        if (!IsArchimedesWaterBlock(startFluid))
        {
            return visited;
        }

        Queue<BlockPos> queue = new();
        queue.Enqueue(startPos.Copy());
        visited.Add(PosKey(startPos));

        while (queue.Count > 0)
        {
            if (visited.Count >= MaxBfsVisited)
            {
                api.Logger.Warning(
                    "{0} BFS in CollectConnectedManagedWater hit limit of {1} blocks starting at {2}",
                    ArchimedesScrewModSystem.LogPrefix,
                    MaxBfsVisited,
                    startPos
                );
                break;
            }

            BlockPos current = queue.Dequeue();
            string key = PosKey(current);
            positionsByKey[key] = current.Copy();

            foreach (BlockFacing face in BlockFacing.ALLFACES)
            {
                BlockPos next = current.AddCopy(face);
                string nextKey = PosKey(next);
                if (!visited.Add(nextKey))
                {
                    continue;
                }

                Block fluidBlock = api.World.BlockAccessor.GetBlock(next, BlockLayersAccess.Fluid);
                if (!IsArchimedesWaterBlock(fluidBlock))
                {
                    continue;
                }

                queue.Enqueue(next);
            }
        }

        ArchimedesPerf.AddCount("water.collectConnectedManaged.visited", visited.Count);
        return visited;
    }

    /// <summary>
    /// Returns connected managed-water component for <paramref name="startPos"/> using a per-global-tick cache.
    /// Consumers must treat returned collections as read-only.
    /// </summary>
    public HashSet<string> CollectConnectedManagedWaterCached(BlockPos startPos, out Dictionary<string, BlockPos> positionsByKey)
    {
        using ArchimedesPerf.PerfScope _perf = ArchimedesPerf.Measure("water.collectConnectedManagedCached");
        if (!isInGlobalWaterTickDispatch)
        {
            return CollectConnectedManagedWater(startPos, out positionsByKey);
        }

        string startKey = PosKey(startPos);
        if (connectedManagedComponentCache.TryGetValue(startKey, out ConnectedManagedComponentCacheEntry? cached) &&
            cached.Generation == connectedManagedComponentCacheGeneration)
        {
            positionsByKey = cached.PositionsByKey;
            ArchimedesPerf.AddCount("water.collectConnectedManagedCached.hit");
            return cached.Visited;
        }

        HashSet<string> visited = CollectConnectedManagedWater(startPos, out positionsByKey);
        ConnectedManagedComponentCacheEntry entry = new(
            connectedManagedComponentCacheGeneration,
            visited,
            positionsByKey
        );
        // Component-level fanout: any position in this connected region should hit cache this tick.
        foreach (string key in visited)
        {
            connectedManagedComponentCache[key] = entry;
        }
        ArchimedesPerf.AddCount("water.collectConnectedManagedCached.miss");
        ArchimedesPerf.AddCount("water.collectConnectedManagedCached.fanoutKeys", visited.Count);
        return visited;
    }

    public HashSet<string> CollectConnectedArchimedesSources(BlockPos startPos, out Dictionary<string, BlockPos> sourcePositionsByKey)
    {
        sourcePositionsByKey = new Dictionary<string, BlockPos>(StringComparer.Ordinal);
        HashSet<string> connectedWater = CollectConnectedManagedWater(startPos, out Dictionary<string, BlockPos> waterPositions);
        foreach ((string key, BlockPos pos) in waterPositions)
        {
            Block fluidBlock = api.World.BlockAccessor.GetBlock(pos, BlockLayersAccess.Fluid);
            if (!IsArchimedesSourceBlock(fluidBlock))
            {
                continue;
            }

            sourcePositionsByKey[key] = pos.Copy();
        }

        return new HashSet<string>(sourcePositionsByKey.Keys, StringComparer.Ordinal);
    }

    public bool EnsureSourceOwned(string ownerId, BlockPos pos, string familyId)
    {
        Block solidBlock = api.World.BlockAccessor.GetBlock(pos);
        Block fluidBlock = api.World.BlockAccessor.GetBlock(pos, BlockLayersAccess.Fluid);
        bool solidClear = solidBlock.Id == 0 || solidBlock.ForFluidsLayer;
        if (!solidClear)
        {
            return false;
        }

        bool fluidClear = fluidBlock.Id == 0 ||
                          IsArchimedesWaterBlock(fluidBlock) ||
                          IsVanillaSourceBlock(fluidBlock);
        if (!fluidClear)
        {
            return false;
        }

        bool changed = false;
        string key = PosKey(pos);
        if (!sourceOwnerByPos.TryGetValue(key, out string? existingOwner) ||
            !string.Equals(existingOwner, ownerId, StringComparison.Ordinal))
        {
            sourceOwnerByPos[key] = ownerId;
            changed = true;
        }

        AddOwnedPosToSnapshot(ownerId, pos);

        Block currentFluid = api.World.BlockAccessor.GetBlock(pos, BlockLayersAccess.Fluid);
        if (!IsArchimedesSourceBlock(currentFluid) ||
            !TryResolveManagedWaterFamily(currentFluid, out string currentFamilyId) ||
            !string.Equals(currentFamilyId, familyId, StringComparison.Ordinal))
        {
            SetManagedSource(pos, familyId);
            changed = true;
        }

        return changed;
    }

    public bool EnsureSourceOwnership(string ownerId, BlockPos pos)
    {
        Block fluidBlock = api.World.BlockAccessor.GetBlock(pos, BlockLayersAccess.Fluid);
        if (!IsArchimedesSourceBlock(fluidBlock))
        {
            return false;
        }

        string key = PosKey(pos);
        if (sourceOwnerByPos.TryGetValue(key, out _))
        {
            return false;
        }

        sourceOwnerByPos[key] = ownerId;
        AddOwnedPosToSnapshot(ownerId, pos);
        return true;
    }

    public void ReleaseSourceOwner(string ownerId, BlockPos pos)
    {
        string key = PosKey(pos);
        if (!sourceOwnerByPos.TryGetValue(key, out string? owner))
        {
            RemoveOrphanedManagedSource(pos, key);
            return;
        }

        if (!string.Equals(owner, ownerId, StringComparison.Ordinal))
        {
            return;
        }

        sourceOwnerByPos.Remove(key);
        RemoveOwnedPosFromSnapshot(ownerId, pos);
        Block fluidBlock = api.World.BlockAccessor.GetBlock(pos, BlockLayersAccess.Fluid);
        if (!IsArchimedesWaterBlock(fluidBlock))
        {
            return;
        }

        SuppressRemovalNotification(key);
        RemoveFluidAndNotifyNeighbours(pos);
    }

    public int ConvertAdjacentVanillaSources(BlockPos startPos)
    {
        int converted = 0;
        HashSet<string> convertedKeys = new(StringComparer.Ordinal);
        CollectConnectedManagedWater(startPos, out Dictionary<string, BlockPos> connectedWater);
        foreach (BlockPos pos in connectedWater.Values)
        {
            Block currentFluid = api.World.BlockAccessor.GetBlock(pos, BlockLayersAccess.Fluid);
            if (!TryResolveManagedWaterFamily(currentFluid, out string familyId))
            {
                continue;
            }

            foreach (BlockFacing face in BlockFacing.ALLFACES)
            {
                BlockPos adjacentPos = pos.AddCopy(face);
                string adjacentKey = PosKey(adjacentPos);
                if (!convertedKeys.Add(adjacentKey))
                {
                    continue;
                }

                if (!TryConvertVanillaSource(adjacentPos, familyId))
                {
                    continue;
                }

                converted++;
            }
        }

        return converted;
    }

    /// <summary>
    /// Repeats <see cref="ConvertAdjacentVanillaSources"/> until no vanilla sources remain adjacent to the
    /// growing managed-water component, or <paramref name="maxPasses"/> is reached. A single pass only converts
    /// neighbours of the current BFS set, so a chain of bucket-placed sources needs multiple passes.
    /// </summary>
    public int ConvertAdjacentVanillaSourcesIteratively(BlockPos startPos, int maxPasses)
    {
        int capped = Math.Clamp(maxPasses, 1, 256);
        int total = 0;
        for (int pass = 0; pass < capped; pass++)
        {
            int batch = ConvertAdjacentVanillaSources(startPos);
            if (batch == 0)
            {
                break;
            }

            total += batch;
        }

        return total;
    }

    public bool TryConvertVanillaSource(BlockPos pos, string familyId)
    {
        Block fluidBlock = api.World.BlockAccessor.GetBlock(pos, BlockLayersAccess.Fluid);
        if (!IsVanillaSourceBlock(fluidBlock))
        {
            return false;
        }

        SetManagedSource(pos, familyId);
        return true;
    }

    /// <summary>
    /// Player-placement path: convert to managed source and assign ownership before neighbour updates
    /// can immediately turn the cell into flowing water.
    /// </summary>
    public bool TryConvertVanillaSourceForPlayer(BlockPos pos, string familyId, string reason)
    {
        Block fluidBlock = api.World.BlockAccessor.GetBlock(pos, BlockLayersAccess.Fluid);
        if (!TryResolveVanillaWaterFamily(fluidBlock, out string currentFamilyId) ||
            !string.Equals(currentFamilyId, familyId, StringComparison.Ordinal))
        {
            return false;
        }

        SetManagedWaterVariant(pos, familyId, "still", 7, triggerUpdates: false);
        AssignConnectedSourceToActiveControllers(pos, reason);

        // Apply fluid reactions after assignment to avoid the source->flowing race.
        Block placed = api.World.BlockAccessor.GetBlock(pos, BlockLayersAccess.Fluid);
        if (placed.Id != 0)
        {
            TriggerLiquidUpdates(pos, placed);
        }

        return true;
    }

    public bool TryConvertVanillaSourceUsingAdjacentManagedFamilyForPlayer(BlockPos pos, string reason)
    {
        Block fluidBlock = api.World.BlockAccessor.GetBlock(pos, BlockLayersAccess.Fluid);
        if (!TryResolveVanillaWaterFamily(fluidBlock, out _))
        {
            return false;
        }

        foreach (BlockFacing face in BlockFacing.ALLFACES)
        {
            Block neighbourFluid = api.World.BlockAccessor.GetBlock(pos.AddCopy(face), BlockLayersAccess.Fluid);
            if (!TryResolveManagedWaterFamily(neighbourFluid, out string familyId))
            {
                continue;
            }

            return TryConvertVanillaSourceForPlayer(pos, familyId, reason);
        }

        return false;
    }

    public int AssignConnectedSourceToActiveControllers(BlockPos pos, string reason)
    {
        string key = PosKey(pos);
        Block fluidBlock = api.World.BlockAccessor.GetBlock(pos, BlockLayersAccess.Fluid);
        if (!IsArchimedesSourceBlock(fluidBlock))
        {
            return 0;
        }

        if (!TryResolveManagedWaterFamily(fluidBlock, out string familyId))
        {
            return 0;
        }

        return AssignNearestActiveControllerForNewSource(pos, familyId, reason) ? 1 : 0;
    }

    public void OnManagedWaterRemoved(BlockPos pos)
    {
        string key = PosKey(pos);
        if (suppressedRemovalNotifications.TryRemove(key, out _))
        {
            return;
        }

        if (!sourceOwnerByPos.Remove(key, out string? ownerId))
        {
            return;
        }

        RemoveOwnedPosFromSnapshot(ownerId, pos);
        if (loadedControllers.TryGetValue(ownerId, out WeakReference<BlockEntityWaterArchimedesScrew>? reference) &&
            reference.TryGetTarget(out BlockEntityWaterArchimedesScrew? controller))
        {
            controller.NotifyManagedWaterRemoved(pos);
        }
    }

    public int PurgeAll()
    {
        int removed = PurgeManagedWater();
        removed += PurgeScrewsOnly();
        api.Logger.Notification("{0} PurgeAll removed {1} blocks", ArchimedesScrewModSystem.LogPrefix, removed);
        return removed;
    }

    public int PurgeManagedWater()
    {
        HashSet<string> anchorKeys = BuildManagedWaterAnchorKeys();

        foreach (WeakReference<BlockEntityWaterArchimedesScrew> pair in loadedControllers.Values)
        {
            if (pair.TryGetTarget(out BlockEntityWaterArchimedesScrew? controller))
            {
                controller.ClearOwnedStateAfterPurge();
            }
        }

        HashSet<string> allWaterKeys = new(StringComparer.Ordinal);
        foreach (string key in anchorKeys)
        {
            BlockPos pos = ParsePosKey(key);
            CollectManagedComponentKeysAroundAnchor(pos, allWaterKeys);
        }

        int converted = 0;
        int removed = 0;
        List<BlockPos> removedPositions = new();
        foreach (string key in allWaterKeys)
        {
            BlockPos pos = ParsePosKey(key);
            Block block = api.World.BlockAccessor.GetBlock(pos, BlockLayersAccess.Fluid);
            if (!IsArchimedesWaterBlock(block))
            {
                continue;
            }

            if (TryGetVanillaEquivalent(block, out Block? vanillaEquivalent))
            {
                SuppressRemovalNotification(key);
                api.World.BlockAccessor.SetBlock(vanillaEquivalent.Id, pos, BlockLayersAccess.Fluid);
                TriggerLiquidUpdates(pos, vanillaEquivalent);
                converted++;
            }
            else
            {
                SuppressRemovalNotification(key);
                api.World.BlockAccessor.SetBlock(0, pos, BlockLayersAccess.Fluid);
                removedPositions.Add(pos);
                removed++;
            }
        }

        foreach (BlockPos pos in removedPositions)
        {
            NotifyNeighboursOfFluidRemoval(pos);
        }

        sourceOwnerByPos.Clear();
        controllerOwnedById.Clear();

        int total = converted + removed;
        api.Logger.Notification(
            "{0} PurgeManagedWater replaced {1} managed blocks (convertedToVanilla={2}, removed={3})",
            ArchimedesScrewModSystem.LogPrefix,
            total,
            converted,
            removed
        );
        return total;
    }

    public int PurgeScrewsOnly()
    {
        foreach (WeakReference<BlockEntityWaterArchimedesScrew> pair in loadedControllers.Values)
        {
            if (pair.TryGetTarget(out BlockEntityWaterArchimedesScrew? controller))
            {
                controller.ReleaseAllManagedWater();
            }
        }

        int removed = 0;
        foreach (string key in screwBlockKeys.ToArray())
        {
            BlockPos pos = ParsePosKey(key);
            Block block = api.World.BlockAccessor.GetBlock(pos);
            if (block is not BlockWaterArchimedesScrew)
            {
                continue;
            }

            api.World.BlockAccessor.SetBlock(0, pos);
            removed++;
        }

        screwBlockKeys.Clear();
        controllerPosById.Clear();

        api.Logger.Notification("{0} PurgeScrewsOnly removed {1} screw blocks", ArchimedesScrewModSystem.LogPrefix, removed);
        return removed;
    }

    public Block GetManagedBlock(string familyId, string flow, int height)
    {
        string cacheKey = $"{familyId}:{flow}:{height}";
        if (managedBlockCache.TryGetValue(cacheKey, out Block? cached))
        {
            return cached;
        }

        AssetLocation code = ArchimedesWaterFamilies.GetManagedBlockCode(familyId, flow, height);
        Block? block = api.World.GetBlock(code);
        if (block == null)
        {
            throw new InvalidOperationException($"Managed Archimedes water block could not be resolved for {code}.");
        }

        managedBlockCache[cacheKey] = block;
        return block;
    }

    public void SetManagedSource(BlockPos pos, string familyId)
    {
        SetManagedWaterVariant(pos, familyId, "still", 7);
    }

    public void SetManagedWaterVariant(BlockPos pos, string familyId, string flow, int height, bool triggerUpdates = true)
    {
        Block desiredBlock = GetManagedBlock(familyId, flow, height);
        Block currentFluid = api.World.BlockAccessor.GetBlock(pos, BlockLayersAccess.Fluid);
        if (currentFluid.Id == desiredBlock.Id)
        {
            return;
        }

        if (IsArchimedesWaterBlock(currentFluid))
        {
            SuppressRemovalNotification(PosKey(pos));
        }

        api.World.BlockAccessor.SetBlock(desiredBlock.Id, pos, BlockLayersAccess.Fluid);
        if (triggerUpdates)
        {
            TriggerLiquidUpdates(pos, desiredBlock);
        }
    }

    public static string PosKey(BlockPos pos)
    {
        return $"{pos.X},{pos.Y},{pos.Z}";
    }

    private bool IsVanillaSourceBlock(Block block)
    {
        return block.IsLiquid() &&
               ArchimedesWaterFamilies.TryResolveVanillaFamily(block, out _) &&
               string.Equals(block.Variant?["flow"], "still", StringComparison.Ordinal) &&
               string.Equals(block.Variant?["height"], "7", StringComparison.Ordinal);
    }

    private void RemoveOrphanedManagedSource(BlockPos pos, string key)
    {
        Block fluidBlock = api.World.BlockAccessor.GetBlock(pos, BlockLayersAccess.Fluid);
        if (!IsArchimedesSourceBlock(fluidBlock))
        {
            return;
        }

        SuppressRemovalNotification(key);
        RemoveFluidAndNotifyNeighbours(pos);
    }

    private T? LoadSerialized<T>(string key)
    {
        byte[] data = api.WorldManager.SaveGame.GetData(key);
        return data == null ? default : SerializerUtil.Deserialize<T>(data);
    }

    private bool AssignNearestActiveControllerForNewSource(BlockPos sourcePos, string familyId, string reason = "new source assignment")
    {
        CollectConnectedManagedWaterCached(sourcePos, out Dictionary<string, BlockPos> connectedWater);
        string? nearest = FindNearestActiveControllerId(
            sourcePos,
            connectedWater.Keys,
            familyId,
            excludedControllerId: null,
            requireConnected: true
        );
        if (nearest == null)
        {
            return false;
        }

        string key = PosKey(sourcePos);
        sourceOwnerByPos[key] = nearest;
        AddOwnedPosToSnapshot(nearest, sourcePos);
        NotifyControllerSourceAssigned(nearest, sourcePos, reason);
        return true;
    }

    private string? FindNearestActiveControllerId(
        BlockPos sourcePos,
        IEnumerable<string> connectedWaterKeys,
        string familyId,
        string? excludedControllerId,
        bool requireConnected
    )
    {
        using ArchimedesPerf.PerfScope _perf = ArchimedesPerf.Measure("water.findNearestActiveController");
        HashSet<string>? connected = requireConnected
            ? new HashSet<string>(connectedWaterKeys, StringComparer.Ordinal)
            : null;

        List<ArchimedesOutletState> candidates = new();
        foreach (ArchimedesOutletState seed in GetActiveSeedStatesCached())
        {
            if (excludedControllerId != null &&
                string.Equals(seed.ControllerId, excludedControllerId, StringComparison.Ordinal))
            {
                continue;
            }

            if (!string.Equals(seed.FamilyId, familyId, StringComparison.Ordinal))
            {
                continue;
            }

            if (requireConnected)
            {
                if (connected == null || connected.Count == 0)
                {
                    continue;
                }

                // Source component has already been collected by the caller.
                // If a candidate seed's position is part of that same component, it is connected.
                if (!connected.Contains(PosKey(seed.SeedPos)))
                {
                    continue;
                }
            }

            candidates.Add(seed);
        }

        string? resolved = candidates
            .OrderBy(seed => DistanceSquared(sourcePos, seed.SeedPos))
            .ThenBy(seed => seed.SeedPos.Y)
            .ThenBy(seed => seed.SeedPos.X)
            .ThenBy(seed => seed.SeedPos.Z)
            .ThenBy(seed => seed.ControllerId, StringComparer.Ordinal)
            .Select(seed => seed.ControllerId)
            .FirstOrDefault();
        ArchimedesPerf.AddCount("water.findNearestActiveController.candidates", candidates.Count);
        return resolved;
    }

    private void NotifyControllerSourceAssigned(string controllerId, BlockPos sourcePos, string reason)
    {
        if (loadedControllers.TryGetValue(controllerId, out WeakReference<BlockEntityWaterArchimedesScrew>? reference) &&
            reference.TryGetTarget(out BlockEntityWaterArchimedesScrew? controller))
        {
            controller.TrackAssignedSourceFromManager(sourcePos, reason);
        }
    }

    private void AddOwnedPosToSnapshot(string controllerId, BlockPos pos)
    {
        HashSet<string> keys = new(StringComparer.Ordinal);
        List<BlockPos> list = new();
        if (controllerOwnedById.TryGetValue(controllerId, out int[]? existing))
        {
            foreach (BlockPos ex in DecodePositions(existing))
            {
                if (keys.Add(PosKey(ex)))
                {
                    list.Add(ex);
                }
            }
        }

        if (keys.Add(PosKey(pos)))
        {
            list.Add(pos.Copy());
        }

        controllerOwnedById[controllerId] = EncodePositions(list);
    }

    private void RemoveOwnedPosFromSnapshot(string controllerId, BlockPos pos)
    {
        if (!controllerOwnedById.TryGetValue(controllerId, out int[]? existing))
        {
            return;
        }

        string removeKey = PosKey(pos);
        List<BlockPos> kept = DecodePositions(existing)
            .Where(p => !string.Equals(PosKey(p), removeKey, StringComparison.Ordinal))
            .Select(p => p.Copy())
            .ToList();
        controllerOwnedById[controllerId] = EncodePositions(kept);
    }

    private void SuppressRemovalNotification(string key)
    {
        suppressedRemovalNotifications[key] = 1;
    }

    private void RemoveFluidAndNotifyNeighbours(BlockPos pos)
    {
        api.World.BlockAccessor.SetBlock(0, pos, BlockLayersAccess.Fluid);
        NotifyNeighboursOfFluidRemoval(pos);
    }

    private void NotifyNeighboursOfFluidRemoval(BlockPos pos)
    {
        api.World.BlockAccessor.TriggerNeighbourBlockUpdate(pos);
        api.World.BlockAccessor.MarkBlockDirty(pos);

        foreach (BlockFacing face in BlockFacing.ALLFACES)
        {
            BlockPos neighbourPos = pos.AddCopy(face);

            Block neighbourSolid = api.World.BlockAccessor.GetBlock(neighbourPos);
            if (neighbourSolid.Id != 0)
            {
                neighbourSolid.OnNeighbourBlockChange(api.World, neighbourPos, pos);
            }

            Block neighbourFluid = api.World.BlockAccessor.GetBlock(neighbourPos, BlockLayersAccess.Fluid);
            if (neighbourFluid.Id != 0)
            {
                neighbourFluid.OnNeighbourBlockChange(api.World, neighbourPos, pos);
            }
        }
    }

    private void TriggerLiquidUpdates(BlockPos pos, Block placedFluid)
    {
        using ArchimedesPerf.PerfScope _perf = ArchimedesPerf.Measure("water.triggerLiquidUpdates");
        api.World.BlockAccessor.TriggerNeighbourBlockUpdate(pos);
        api.World.BlockAccessor.MarkBlockDirty(pos);

        placedFluid.OnNeighbourBlockChange(api.World, pos, pos);

        foreach (BlockFacing face in BlockFacing.ALLFACES)
        {
            BlockPos neighbourPos = pos.AddCopy(face);

            Block neighbourSolid = api.World.BlockAccessor.GetBlock(neighbourPos);
            if (neighbourSolid.Id != 0)
            {
                neighbourSolid.OnNeighbourBlockChange(api.World, neighbourPos, pos);
            }

            Block neighbourFluid = api.World.BlockAccessor.GetBlock(neighbourPos, BlockLayersAccess.Fluid);
            if (neighbourFluid.Id != 0)
            {
                neighbourFluid.OnNeighbourBlockChange(api.World, neighbourPos, pos);
            }
        }
    }

    private void CollectManagedComponentKeysAroundAnchor(BlockPos anchor, HashSet<string> allWaterKeys)
    {
        TryCollectManagedComponent(anchor, allWaterKeys);
        foreach (BlockFacing face in BlockFacing.ALLFACES)
        {
            TryCollectManagedComponent(anchor.AddCopy(face), allWaterKeys);
        }
    }

    private HashSet<string> BuildManagedWaterAnchorKeys()
    {
        HashSet<string> anchorKeys = new(StringComparer.Ordinal);

        foreach (string key in sourceOwnerByPos.Keys)
        {
            anchorKeys.Add(key);
        }

        foreach (int[] flatPositions in controllerOwnedById.Values)
        {
            foreach (BlockPos pos in DecodePositions(flatPositions))
            {
                anchorKeys.Add(PosKey(pos));
            }
        }

        foreach (string screwKey in screwBlockKeys)
        {
            anchorKeys.Add(screwKey);
        }

        foreach (string controllerPosKey in controllerPosById.Values)
        {
            anchorKeys.Add(controllerPosKey);
        }

        foreach (WeakReference<BlockEntityWaterArchimedesScrew> wr in loadedControllers.Values)
        {
            if (wr.TryGetTarget(out BlockEntityWaterArchimedesScrew? controller))
            {
                anchorKeys.Add(PosKey(controller.Pos));
            }
        }

        return anchorKeys;
    }

    private void TryCollectManagedComponent(BlockPos pos, HashSet<string> allWaterKeys)
    {
        string posKey = PosKey(pos);
        if (allWaterKeys.Contains(posKey))
        {
            return;
        }

        Block fluid = api.World.BlockAccessor.GetBlock(pos, BlockLayersAccess.Fluid);
        if (!IsArchimedesWaterBlock(fluid))
        {
            return;
        }

        CollectConnectedManagedWater(pos, out Dictionary<string, BlockPos> connectedWater);
        foreach (string key in connectedWater.Keys)
        {
            allWaterKeys.Add(key);
        }
    }

    private bool TryGetVanillaEquivalent(Block managedBlock, out Block vanillaBlock)
    {
        vanillaBlock = null!;
        if (!TryResolveManagedWaterFamily(managedBlock, out string familyId))
        {
            return false;
        }

        ArchimedesWaterFamily family = ArchimedesWaterFamilies.GetById(familyId);
        string flow = managedBlock.Variant?["flow"] ?? "still";
        string heightText = managedBlock.Variant?["height"] ?? "7";
        if (!int.TryParse(heightText, out int height))
        {
            return false;
        }

        Block? resolved = api.World.GetBlock(new AssetLocation("game", $"{family.VanillaCode}-{flow}-{height}"));
        if (resolved == null)
        {
            return false;
        }

        vanillaBlock = resolved;
        return true;
    }

    private static BlockPos ParsePosKey(string key)
    {
        string[] parts = key.Split(',');
        if (parts.Length < 3 ||
            !int.TryParse(parts[0], out int x) ||
            !int.TryParse(parts[1], out int y) ||
            !int.TryParse(parts[2], out int z))
        {
            throw new FormatException($"Invalid position key format: '{key}'");
        }

        return new BlockPos(x, y, z);
    }

    private static int[] EncodePositions(IReadOnlyCollection<BlockPos> positions)
    {
        int[] flat = new int[positions.Count * 3];
        int index = 0;
        foreach (BlockPos pos in positions)
        {
            flat[index++] = pos.X;
            flat[index++] = pos.Y;
            flat[index++] = pos.Z;
        }

        return flat;
    }

    private static IEnumerable<BlockPos> DecodePositions(int[]? flatPositions)
    {
        if (flatPositions == null || flatPositions.Length < 3)
        {
            yield break;
        }

        for (int i = 0; i + 2 < flatPositions.Length; i += 3)
        {
            yield return new BlockPos(flatPositions[i], flatPositions[i + 1], flatPositions[i + 2]);
        }
    }

    private static int DistanceSquared(BlockPos a, BlockPos b)
    {
        int dx = a.X - b.X;
        int dy = a.Y - b.Y;
        int dz = a.Z - b.Z;
        return dx * dx + dy * dy + dz * dz;
    }

    private sealed record ConnectedManagedComponentCacheEntry(
        int Generation,
        HashSet<string> Visited,
        Dictionary<string, BlockPos> PositionsByKey
    );
}
