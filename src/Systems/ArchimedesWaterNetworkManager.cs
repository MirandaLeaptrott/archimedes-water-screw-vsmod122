using System.Collections.Concurrent;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Util;

namespace ArchimedesScrew;

public sealed class ArchimedesWaterNetworkManager
{
    private const string SaveKeyScrewBlocks = "archimedes_screw/screwblocks";
    private const string SaveKeyControllerPositions = "archimedes_screw/controllerpositions";
    private const string SaveKeyControllerOwned = "archimedes_screw/controllerowned";

    private readonly ICoreServerAPI api;
    private readonly ArchimedesScrewConfig config;

    private readonly Dictionary<string, HashSet<string>> ownersByWaterPos = new(StringComparer.Ordinal);
    private readonly HashSet<string> screwBlockKeys = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> controllerPosById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int[]> controllerOwnedById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, WeakReference<BlockEntityWaterArchimedesScrew>> loadedControllers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> suppressedRemovalNotifications = new(StringComparer.Ordinal);

    private Block? stillWaterBlock;

    public ArchimedesWaterNetworkManager(ICoreServerAPI api, ArchimedesScrewConfig config)
    {
        this.api = api;
        this.config = config;
    }

    public void Load()
    {
        screwBlockKeys.Clear();
        controllerPosById.Clear();
        controllerOwnedById.Clear();
        ownersByWaterPos.Clear();

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

        Dictionary<string, int[]>? ownedPositions = LoadSerialized<Dictionary<string, int[]>>(SaveKeyControllerOwned);
        if (ownedPositions == null)
        {
            return;
        }

        foreach ((string ownerId, int[] flatPositions) in ownedPositions)
        {
            controllerOwnedById[ownerId] = flatPositions;
            foreach (BlockPos pos in DecodePositions(flatPositions))
            {
                GetOrCreateOwners(pos).Add(ownerId);
            }
        }

        api.Logger.Notification(
            "{0} Loaded water manager state: screws={1}, controllers={2}, managedPositions={3}",
            ArchimedesScrewModSystem.LogPrefix,
            screwBlockKeys.Count,
            controllerPosById.Count,
            ownersByWaterPos.Count
        );
    }

    public void Save()
    {
        api.WorldManager.SaveGame.StoreData(SaveKeyScrewBlocks, SerializerUtil.Serialize(screwBlockKeys.ToArray()));
        api.WorldManager.SaveGame.StoreData(SaveKeyControllerPositions, SerializerUtil.Serialize(controllerPosById));
        api.WorldManager.SaveGame.StoreData(SaveKeyControllerOwned, SerializerUtil.Serialize(controllerOwnedById));
        api.Logger.Notification(
            "{0} Saved water manager state: screws={1}, controllers={2}, managedPositions={3}",
            ArchimedesScrewModSystem.LogPrefix,
            screwBlockKeys.Count,
            controllerPosById.Count,
            ownersByWaterPos.Count
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

    public void RegisterRestoredOwnership(string controllerId, BlockPos controllerPos, IReadOnlyCollection<BlockPos> ownedPositions)
    {
        controllerPosById[controllerId] = PosKey(controllerPos);
        controllerOwnedById[controllerId] = EncodePositions(ownedPositions);

        foreach (BlockPos pos in ownedPositions)
        {
            GetOrCreateOwners(pos).Add(controllerId);
        }
    }

    public void UpdateControllerSnapshot(string controllerId, BlockPos controllerPos, IReadOnlyCollection<BlockPos> ownedPositions)
    {
        controllerPosById[controllerId] = PosKey(controllerPos);
        controllerOwnedById[controllerId] = EncodePositions(ownedPositions);
    }

    public void RemoveControllerSnapshot(string controllerId)
    {
        controllerPosById.Remove(controllerId);
        controllerOwnedById.Remove(controllerId);
        loadedControllers.Remove(controllerId);
    }

    public void RegisterScrewBlock(BlockPos pos)
    {
        screwBlockKeys.Add(PosKey(pos));
    }

    public void UnregisterScrewBlock(BlockPos pos)
    {
        screwBlockKeys.Remove(PosKey(pos));
    }

    public bool IsManagedWater(BlockPos pos)
    {
        return ownersByWaterPos.ContainsKey(PosKey(pos));
    }

    public bool IsArchimedesWaterBlock(Block block)
    {
        return block.Code?.Domain == ArchimedesScrewModSystem.ModId &&
               block.Code.Path.StartsWith(ArchimedesScrewModSystem.ManagedWaterCode, StringComparison.Ordinal);
    }

    public bool TryClaimWater(string ownerId, BlockPos pos)
    {
        Block solidBlock = api.World.BlockAccessor.GetBlock(pos);
        Block fluidBlock = api.World.BlockAccessor.GetBlock(pos, BlockLayersAccess.Fluid);

        bool isAir = solidBlock.Id == 0;
        bool alreadyManaged = IsArchimedesWaterBlock(fluidBlock);
        if (!isAir && !alreadyManaged)
        {
            return false;
        }

        string key = PosKey(pos);
        HashSet<string> owners = GetOrCreateOwners(pos);
        bool added = owners.Add(ownerId);
        if (!added)
        {
            return true;
        }

        if (owners.Count == 1)
        {
            EnsureStillWaterBlock(pos);
        }

        return true;
    }

    public void ReleaseOwner(string ownerId, BlockPos pos)
    {
        string key = PosKey(pos);
        if (!ownersByWaterPos.TryGetValue(key, out HashSet<string>? owners))
        {
            return;
        }

        owners.Remove(ownerId);
        if (owners.Count > 0)
        {
            return;
        }

        ownersByWaterPos.Remove(key);
        SuppressRemovalNotification(key);
        api.World.BlockAccessor.SetBlock(0, pos, BlockLayersAccess.Fluid);
    }

    public void OnManagedWaterRemoved(BlockPos pos)
    {
        string key = PosKey(pos);
        if (suppressedRemovalNotifications.TryRemove(key, out _))
        {
            return;
        }

        if (!ownersByWaterPos.Remove(key, out HashSet<string>? owners))
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
        HashSet<string> allWaterKeys = new(StringComparer.Ordinal);

        foreach (string key in ownersByWaterPos.Keys)
        {
            allWaterKeys.Add(key);
        }

        foreach (int[] flatPositions in controllerOwnedById.Values)
        {
            foreach (BlockPos pos in DecodePositions(flatPositions))
            {
                allWaterKeys.Add(PosKey(pos));
            }
        }

        foreach (var pair in loadedControllers.Values)
        {
            if (pair.TryGetTarget(out BlockEntityWaterArchimedesScrew? controller))
            {
                controller.ClearOwnedStateAfterPurge();
            }
        }

        int removed = 0;
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
            removed++;
        }

        ownersByWaterPos.Clear();
        controllerOwnedById.Clear();

        api.Logger.Notification("{0} PurgeManagedWater removed {1} managed water blocks", ArchimedesScrewModSystem.LogPrefix, removed);

        return removed;
    }

    public int PurgeScrewsOnly()
    {
        foreach (var pair in loadedControllers.Values)
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

    public bool TryGetPersistedControllerPos(string controllerId, out BlockPos pos)
    {
        if (controllerPosById.TryGetValue(controllerId, out string? key))
        {
            pos = ParsePosKey(key);
            return true;
        }

        pos = new BlockPos(0);
        return false;
    }

    private T? LoadSerialized<T>(string key)
    {
        byte[] data = api.WorldManager.SaveGame.GetData(key);
        return data == null ? default : SerializerUtil.Deserialize<T>(data);
    }

    private HashSet<string> GetOrCreateOwners(BlockPos pos)
    {
        string key = PosKey(pos);
        if (!ownersByWaterPos.TryGetValue(key, out HashSet<string>? owners))
        {
            owners = new HashSet<string>(StringComparer.Ordinal);
            ownersByWaterPos[key] = owners;
        }

        return owners;
    }

    private void EnsureStillWaterBlock(BlockPos pos)
    {
        stillWaterBlock ??= api.World.GetBlock(new AssetLocation(ArchimedesScrewModSystem.ModId, $"{ArchimedesScrewModSystem.ManagedWaterCode}-still-7"));
        if (stillWaterBlock == null)
        {
            throw new InvalidOperationException("Managed Archimedes water block could not be resolved.");
        }

        Block currentFluid = api.World.BlockAccessor.GetBlock(pos, BlockLayersAccess.Fluid);
        if (currentFluid.Id == stillWaterBlock.Id)
        {
            return;
        }

        api.World.BlockAccessor.SetBlock(stillWaterBlock.Id, pos, BlockLayersAccess.Fluid);
    }

    private void SuppressRemovalNotification(string key)
    {
        suppressedRemovalNotifications[key] = 1;
    }

    public static string PosKey(BlockPos pos)
    {
        return $"{pos.X},{pos.Y},{pos.Z}";
    }

    private static BlockPos ParsePosKey(string key)
    {
        string[] parts = key.Split(',');
        return new BlockPos(int.Parse(parts[0]), int.Parse(parts[1]), int.Parse(parts[2]));
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

    private static IEnumerable<BlockPos> DecodePositions(int[] flatPositions)
    {
        for (int i = 0; i + 2 < flatPositions.Length; i += 3)
        {
            yield return new BlockPos(flatPositions[i], flatPositions[i + 1], flatPositions[i + 2]);
        }
    }
}
