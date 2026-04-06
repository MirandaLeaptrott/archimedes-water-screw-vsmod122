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

    private readonly Dictionary<string, HashSet<string>> sourceOwnersByPos = new(StringComparer.Ordinal);
    private readonly HashSet<string> screwBlockKeys = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> controllerPosById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int[]> controllerOwnedById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, WeakReference<BlockEntityWaterArchimedesScrew>> loadedControllers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> suppressedRemovalNotifications = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Block> managedBlockCache = new(StringComparer.Ordinal);

    private readonly Dictionary<string, WeakReference<BlockEntityWaterArchimedesScrew>> centralWaterTickControllers = new(StringComparer.Ordinal);
    private readonly List<string> centralWaterTickOrder = new();
    private int centralWaterTickCursor;
    private long globalWaterTickListenerId;

    public ArchimedesWaterNetworkManager(ICoreServerAPI api, ArchimedesScrewConfig config)
    {
        this.api = api;
        this.config = config;
    }

    public void Dispose()
    {
        StopCentralWaterTick();
        GC.SuppressFinalize(this);
    }

    /// <summary>Registers the single server tick that runs intake water logic (staggered, budgeted).</summary>
    public void StartCentralWaterTick()
    {
        StopCentralWaterTick();
        int interval = Math.Max(5, config.Water.GlobalTickMs);
        globalWaterTickListenerId = api.Event.RegisterGameTickListener(OnGlobalWaterTick, interval);
    }

    public void StopCentralWaterTick()
    {
        if (globalWaterTickListenerId != 0)
        {
            api.Event.UnregisterGameTickListener(globalWaterTickListenerId);
            globalWaterTickListenerId = 0;
        }
    }

    public void RegisterForCentralWaterTick(BlockEntityWaterArchimedesScrew controller)
    {
        string id = controller.ControllerId;
        centralWaterTickControllers[id] = new WeakReference<BlockEntityWaterArchimedesScrew>(controller);
        if (!centralWaterTickOrder.Contains(id))
        {
            centralWaterTickOrder.Add(id);
        }
    }

    public void UnregisterFromCentralWaterTick(string controllerId)
    {
        centralWaterTickControllers.Remove(controllerId);
        centralWaterTickOrder.RemoveAll(s => string.Equals(s, controllerId, StringComparison.Ordinal));
    }

    private void OnGlobalWaterTick(float dt)
    {
        CompactCentralWaterTickList();

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

        centralWaterTickCursor = (centralWaterTickCursor + 1) % n;
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
        sourceOwnersByPos.Clear();

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
                GetOrCreateOwners(pos).Add(controllerId);
            }
        }

        api.Logger.Notification(
            "{0} Loaded water manager state: screws={1}, controllers={2}, trackedSources={3}",
            ArchimedesScrewModSystem.LogPrefix,
            screwBlockKeys.Count,
            controllerPosById.Count,
            sourceOwnersByPos.Count
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
            sourceOwnersByPos.Count
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
            GetOrCreateOwners(pos).Add(controllerId);
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

    public HashSet<string> CollectConnectedManagedWater(BlockPos startPos, out Dictionary<string, BlockPos> positionsByKey)
    {
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
        HashSet<string> owners = GetOrCreateOwners(pos);
        if (owners.Add(ownerId))
        {
            changed = true;
        }

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

        return GetOrCreateOwners(pos).Add(ownerId);
    }

    public void ReleaseSourceOwner(string ownerId, BlockPos pos)
    {
        string key = PosKey(pos);
        if (!sourceOwnersByPos.TryGetValue(key, out HashSet<string>? owners))
        {
            RemoveOrphanedManagedSource(pos, key);
            return;
        }

        owners.Remove(ownerId);
        if (owners.Count > 0)
        {
            return;
        }

        sourceOwnersByPos.Remove(key);
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

    public bool TryConvertVanillaSourceUsingAdjacentManagedFamily(BlockPos pos)
    {
        Block fluidBlock = api.World.BlockAccessor.GetBlock(pos, BlockLayersAccess.Fluid);
        if (!IsVanillaSourceBlock(fluidBlock))
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

            SetManagedSource(pos, familyId);
            return true;
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

        int adoptedByControllers = 0;
        foreach (WeakReference<BlockEntityWaterArchimedesScrew> reference in loadedControllers.Values)
        {
            if (!reference.TryGetTarget(out BlockEntityWaterArchimedesScrew? controller) ||
                !controller.TryGetActiveSeedState(out ArchimedesOutletState activeSeed))
            {
                continue;
            }

            CollectConnectedManagedWater(activeSeed.SeedPos, out Dictionary<string, BlockPos> connectedWater);
            if (!connectedWater.ContainsKey(key))
            {
                continue;
            }

            if (controller.TryAdoptSource(pos, reason))
            {
                adoptedByControllers++;
            }
        }

        return adoptedByControllers;
    }

    public void OnManagedWaterRemoved(BlockPos pos)
    {
        string key = PosKey(pos);
        if (suppressedRemovalNotifications.TryRemove(key, out _))
        {
            return;
        }

        if (!sourceOwnersByPos.Remove(key, out HashSet<string>? owners))
        {
            return;
        }

        foreach (string ownerId in owners)
        {
            if (loadedControllers.TryGetValue(ownerId, out WeakReference<BlockEntityWaterArchimedesScrew>? reference) &&
                reference.TryGetTarget(out BlockEntityWaterArchimedesScrew? controller))
            {
                controller.NotifyManagedWaterRemoved(pos);
            }
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
        HashSet<string> sourceKeys = new(StringComparer.Ordinal);

        foreach (string key in sourceOwnersByPos.Keys)
        {
            sourceKeys.Add(key);
        }

        foreach (int[] flatPositions in controllerOwnedById.Values)
        {
            foreach (BlockPos pos in DecodePositions(flatPositions))
            {
                sourceKeys.Add(PosKey(pos));
            }
        }

        foreach (WeakReference<BlockEntityWaterArchimedesScrew> pair in loadedControllers.Values)
        {
            if (pair.TryGetTarget(out BlockEntityWaterArchimedesScrew? controller))
            {
                controller.ClearOwnedStateAfterPurge();
            }
        }

        HashSet<string> allWaterKeys = new(StringComparer.Ordinal);
        foreach (string key in sourceKeys)
        {
            BlockPos pos = ParsePosKey(key);
            CollectConnectedManagedWater(pos, out Dictionary<string, BlockPos> connectedWater);
            foreach (string connectedKey in connectedWater.Keys)
            {
                allWaterKeys.Add(connectedKey);
            }
        }

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

            SuppressRemovalNotification(key);
            api.World.BlockAccessor.SetBlock(0, pos, BlockLayersAccess.Fluid);
            removedPositions.Add(pos);
            removed++;
        }

        foreach (BlockPos pos in removedPositions)
        {
            NotifyNeighboursOfFluidRemoval(pos);
        }

        sourceOwnersByPos.Clear();
        controllerOwnedById.Clear();

        api.Logger.Notification("{0} PurgeManagedWater removed {1} managed water blocks", ArchimedesScrewModSystem.LogPrefix, removed);
        return removed;
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

    public void SetManagedWaterVariant(BlockPos pos, string familyId, string flow, int height)
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
        TriggerLiquidUpdates(pos, desiredBlock);
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

    private HashSet<string> GetOrCreateOwners(BlockPos pos)
    {
        string key = PosKey(pos);
        if (!sourceOwnersByPos.TryGetValue(key, out HashSet<string>? owners))
        {
            owners = new HashSet<string>(StringComparer.Ordinal);
            sourceOwnersByPos[key] = owners;
        }

        return owners;
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
}
