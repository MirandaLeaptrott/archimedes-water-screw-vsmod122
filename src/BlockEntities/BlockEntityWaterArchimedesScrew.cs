using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;
using Vintagestory.GameContent.Mechanics;

namespace ArchimedesScrew;

public sealed class BlockEntityWaterArchimedesScrew : BlockEntity
{
    private const string OwnedPositionsKey = "ownedPositions";
    private const string ControllerIdKey = "controllerId";
    private const string LastSeedKey = "lastSeed";
    private const string WasControllerKey = "wasController";

    private readonly Dictionary<string, BlockPos> ownedPositions = new(StringComparer.Ordinal);

    private ArchimedesScrewModSystem? modSystem;
    private ArchimedesWaterNetworkManager? waterManager;
    private ArchimedesScrewConfig.WaterConfig? waterConfig;

    private long tickListenerId;
    private int currentIntervalMs;
    private bool forceDrain;
    private bool wasController;
    private BlockPos? lastSeedPos;
    private bool? lastLoggedControllerState;
    private bool? lastLoggedPowerState;
    private string? lastLoggedSeedKey;

    public string ControllerId { get; private set; } = Guid.NewGuid().ToString("N");

    public override void Initialize(ICoreAPI api)
    {
        base.Initialize(api);

        if (api.Side != EnumAppSide.Server)
        {
            return;
        }

        modSystem = api.ModLoader.GetModSystem<ArchimedesScrewModSystem>();
        waterManager = modSystem.WaterManager;
        waterConfig = modSystem.Config.Water;

        waterManager?.RegisterScrewBlock(Pos);
        waterManager?.RegisterLoadedController(this);
        Log("Initialized controller {0} for block {1} at {2}", ControllerId, Block?.Code, Pos);

        if (ownedPositions.Count > 0)
        {
            waterManager?.RegisterRestoredOwnership(ControllerId, Pos, ownedPositions.Values.ToList());
            Log("Restored {0} owned managed-water positions from save", ownedPositions.Count);
        }

        EnsureTickListener(waterConfig?.FastTickMs ?? 250);
    }

    public override void OnBlockPlaced(ItemStack? byItemStack = null)
    {
        base.OnBlockPlaced(byItemStack);
        waterManager?.RegisterScrewBlock(Pos);
        waterManager?.RegisterLoadedController(this);
        Log("Block placed at {0}: {1}", Pos, Block?.Code);
    }

    public override void OnBlockRemoved()
    {
        Log("Block removed at {0}: {1}", Pos, Block?.Code);
        ReleaseAllManagedWater("block removed");
        waterManager?.UnregisterScrewBlock(Pos);
        waterManager?.RemoveControllerSnapshot(ControllerId);
        base.OnBlockRemoved();
    }

    public override void OnBlockUnloaded()
    {
        Log("Block unloaded at {0}", Pos);
        waterManager?.UnregisterLoadedController(ControllerId);
        base.OnBlockUnloaded();
    }

    public void NotifyManagedWaterRemoved(BlockPos pos)
    {
        ownedPositions.Remove(ArchimedesWaterNetworkManager.PosKey(pos));
        forceDrain = true;
        Log("Managed water removed externally at {0}; scheduling drain", pos);
    }

    public void ClearOwnedStateAfterPurge()
    {
        ownedPositions.Clear();
        forceDrain = false;
        wasController = false;
        lastSeedPos = null;
        lastLoggedSeedKey = null;
        MarkDirty();
        Log("Cleared owned water state after purge");
    }

    public void ReleaseAllManagedWater(string reason = "unspecified")
    {
        if (waterManager == null || ownedPositions.Count == 0)
        {
            ownedPositions.Clear();
            waterManager?.UpdateControllerSnapshot(ControllerId, Pos, Array.Empty<BlockPos>());
            Log("Release requested for reason '{0}', but no managed water was owned", reason);
            return;
        }

        int count = ownedPositions.Count;
        foreach (BlockPos ownedPos in ownedPositions.Values.ToArray())
        {
            waterManager.ReleaseOwner(ControllerId, ownedPos);
        }

        ownedPositions.Clear();
        waterManager.UpdateControllerSnapshot(ControllerId, Pos, Array.Empty<BlockPos>());
        MarkDirty();
        Log("Released {0} managed-water blocks because {1}", count, reason);
    }

    public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldAccessForResolve)
    {
        base.FromTreeAttributes(tree, worldAccessForResolve);

        ControllerId = tree.GetString(ControllerIdKey, ControllerId);
        wasController = tree.GetBool(WasControllerKey, false);

        byte[]? ownedBytes = tree.GetBytes(OwnedPositionsKey);
        ownedPositions.Clear();
        if (ownedBytes != null)
        {
            int[] flatPositions = SerializerUtil.Deserialize<int[]>(ownedBytes);
            for (int i = 0; i + 2 < flatPositions.Length; i += 3)
            {
                BlockPos pos = new(flatPositions[i], flatPositions[i + 1], flatPositions[i + 2]);
                ownedPositions[ArchimedesWaterNetworkManager.PosKey(pos)] = pos;
            }
        }

        byte[]? seedBytes = tree.GetBytes(LastSeedKey);
        lastSeedPos = seedBytes == null ? null : DecodeSinglePos(SerializerUtil.Deserialize<int[]>(seedBytes));
    }

    public override void ToTreeAttributes(ITreeAttribute tree)
    {
        base.ToTreeAttributes(tree);

        tree.SetString(ControllerIdKey, ControllerId);
        tree.SetBool(WasControllerKey, wasController);
        tree.SetBytes(OwnedPositionsKey, SerializerUtil.Serialize(EncodePositions(ownedPositions.Values)));

        if (lastSeedPos == null)
        {
            tree.RemoveAttribute(LastSeedKey);
        }
        else
        {
            tree.SetBytes(LastSeedKey, SerializerUtil.Serialize(new[] { lastSeedPos.X, lastSeedPos.Y, lastSeedPos.Z }));
        }
    }

    private void OnTick(float dt)
    {
        if (Api == null || Api.Side != EnumAppSide.Server || waterManager == null || waterConfig == null)
        {
            return;
        }

        BlockWaterArchimedesScrew? screwBlock = Block as BlockWaterArchimedesScrew;
        if (screwBlock == null)
        {
            return;
        }

        bool isController = screwBlock.IsIntakeBlock() && screwBlock.HasValidWaterIntake(Api.World, Pos);
        LogStateChange("controller validity", ref lastLoggedControllerState, isController);
        if (!isController)
        {
            if (ownedPositions.Count > 0)
            {
                ReleaseAllManagedWater("controller is no longer a valid intake");
            }

            wasController = false;
            lastSeedPos = null;
            lastLoggedSeedKey = null;
            EnsureTickListener(waterConfig.IdleTickMs);
            return;
        }

        wasController = true;

        if (forceDrain)
        {
            forceDrain = false;
            ReleaseAllManagedWater("forceDrain flag set");
            EnsureTickListener(waterConfig.FastTickMs);
            return;
        }

        if (HasLostManagedWater())
        {
            ReleaseAllManagedWater("owned managed water was lost");
            EnsureTickListener(waterConfig.FastTickMs);
            return;
        }

        bool isPowered = IsPowered();
        LogStateChange("powered state", ref lastLoggedPowerState, isPowered);
        if (!isPowered)
        {
            if (ownedPositions.Count > 0)
            {
                ReleaseAllManagedWater("mechanical power stopped");
            }

            EnsureTickListener(waterConfig.IdleTickMs);
            return;
        }

        BlockPos seedPos = GetSeedPosition();
        string seedKey = ArchimedesWaterNetworkManager.PosKey(seedPos);
        if (lastLoggedSeedKey != seedKey)
        {
            lastLoggedSeedKey = seedKey;
            Log("Seed/output position is now {0}", seedPos);
        }

        if (lastSeedPos != null && !lastSeedPos.Equals(seedPos) && ownedPositions.Count > 0)
        {
            ReleaseAllManagedWater("seed/output position changed");
        }

        lastSeedPos = seedPos.Copy();

        if (!CanUseSeedPosition(seedPos))
        {
            if (ownedPositions.Count > 0)
            {
                ReleaseAllManagedWater("seed/output position is blocked");
            }

            Log("Seed/output position {0} is blocked by solid or non-managed fluid", seedPos);
            EnsureTickListener(waterConfig.IdleTickMs);
            return;
        }

        if (!ownedPositions.ContainsKey(ArchimedesWaterNetworkManager.PosKey(seedPos)) && !waterManager.TryClaimWater(ControllerId, seedPos))
        {
            Log("Unable to claim seed/output position {0}", seedPos);
            EnsureTickListener(waterConfig.IdleTickMs);
            return;
        }

        ownedPositions[ArchimedesWaterNetworkManager.PosKey(seedPos)] = seedPos.Copy();

        List<BlockPos> growthCandidates = CollectGrowthCandidates(seedPos);
        if (growthCandidates.Count == 0)
        {
            waterManager.UpdateControllerSnapshot(ControllerId, Pos, ownedPositions.Values.ToList());
            EnsureTickListener(waterConfig.IdleTickMs);
            MarkDirty();
            Log("No growth candidates remain around seed/output position {0}; switching to idle tick", seedPos);
            return;
        }

        int grown = 0;
        foreach (BlockPos candidate in growthCandidates)
        {
            if (grown >= waterConfig.MaxBlocksPerStep)
            {
                break;
            }

            if (!waterManager.TryClaimWater(ControllerId, candidate))
            {
                continue;
            }

            ownedPositions[ArchimedesWaterNetworkManager.PosKey(candidate)] = candidate.Copy();
            grown++;
        }

        waterManager.UpdateControllerSnapshot(ControllerId, Pos, ownedPositions.Values.ToList());
        EnsureTickListener(grown > 0 ? waterConfig.FastTickMs : waterConfig.IdleTickMs);
        MarkDirty();
        Log(
            "Growth tick at {0}: candidates={1}, grown={2}, ownedTotal={3}, nextIntervalMs={4}",
            seedPos,
            growthCandidates.Count,
            grown,
            ownedPositions.Count,
            grown > 0 ? waterConfig.FastTickMs : waterConfig.IdleTickMs
        );
    }

    private bool HasLostManagedWater()
    {
        if (Api == null || waterManager == null)
        {
            return false;
        }

        foreach (BlockPos pos in ownedPositions.Values)
        {
            Block fluidBlock = Api.World.BlockAccessor.GetBlock(pos, BlockLayersAccess.Fluid);
            if (!waterManager.IsArchimedesWaterBlock(fluidBlock))
            {
                return true;
            }
        }

        return false;
    }

    private bool IsPowered()
    {
        BEBehaviorMPArchimedesScrew? behavior = GetBehavior<BEBehaviorMPArchimedesScrew>();
        if (behavior?.Network == null || waterConfig == null)
        {
            return false;
        }

        return Math.Abs(behavior.Network.Speed) >= waterConfig.MinimumNetworkSpeed;
    }

    private BlockPos FindTopScrewPos()
    {
        BlockPos top = Pos.Copy();
        int maxLength = waterConfig?.MaxScrewLength ?? 32;

        for (int i = 0; i < maxLength; i++)
        {
            BlockPos above = top.UpCopy();
            if (Api?.World.BlockAccessor.GetBlock(above) is not BlockWaterArchimedesScrew)
            {
                break;
            }

            top = above;
        }

        return top;
    }

    private BlockPos GetSeedPosition()
    {
        BlockPos topPos = FindTopScrewPos();
        if (Api?.World.BlockAccessor.GetBlock(topPos) is BlockWaterArchimedesScrew topScrew && topScrew.IsOutletBlock())
        {
            BlockFacing? facing = topScrew.GetPortFacing();
            if (facing != null)
            {
                return topPos.AddCopy(facing);
            }
        }

        return topPos.UpCopy();
    }

    private bool CanUseSeedPosition(BlockPos seedPos)
    {
        if (Api == null)
        {
            return false;
        }

        Block solidBlock = Api.World.BlockAccessor.GetBlock(seedPos);
        Block fluidBlock = Api.World.BlockAccessor.GetBlock(seedPos, BlockLayersAccess.Fluid);

        return solidBlock.Id == 0 && (fluidBlock.Id == 0 || waterManager?.IsArchimedesWaterBlock(fluidBlock) == true);
    }

    private List<BlockPos> CollectGrowthCandidates(BlockPos seedPos)
    {
        List<BlockPos> candidates = new();
        if (Api == null || waterConfig == null)
        {
            return candidates;
        }

        Queue<BlockPos> queue = new();
        HashSet<string> visited = new(StringComparer.Ordinal);
        HashSet<string> candidateKeys = new(StringComparer.Ordinal);

        queue.Enqueue(seedPos);
        visited.Add(ArchimedesWaterNetworkManager.PosKey(seedPos));

        while (queue.Count > 0)
        {
            BlockPos current = queue.Dequeue();
            foreach (BlockFacing face in BlockFacing.HORIZONTALS)
            {
                BlockPos next = current.AddCopy(face);
                string nextKey = ArchimedesWaterNetworkManager.PosKey(next);

                if (!IsWithinRadius(seedPos, next, waterConfig.MaxRadius))
                {
                    continue;
                }

                Block fluidBlock = Api.World.BlockAccessor.GetBlock(next, BlockLayersAccess.Fluid);
                if (waterManager!.IsArchimedesWaterBlock(fluidBlock))
                {
                    if (visited.Add(nextKey))
                    {
                        queue.Enqueue(next);
                    }

                    continue;
                }

                Block solidBlock = Api.World.BlockAccessor.GetBlock(next);
                if (solidBlock.Id == 0 && fluidBlock.Id == 0 && candidateKeys.Add(nextKey))
                {
                    candidates.Add(next);
                }
            }
        }

        return candidates;
    }

    private static bool IsWithinRadius(BlockPos center, BlockPos test, int radius)
    {
        int dx = test.X - center.X;
        int dz = test.Z - center.Z;
        return dx * dx + dz * dz <= radius * radius;
    }

    private void EnsureTickListener(int intervalMs)
    {
        if (Api == null)
        {
            return;
        }

        if (tickListenerId != 0 && currentIntervalMs == intervalMs)
        {
            return;
        }

        if (tickListenerId != 0)
        {
            UnregisterGameTickListener(tickListenerId);
        }

        currentIntervalMs = intervalMs;
        tickListenerId = RegisterGameTickListener(OnTick, intervalMs);
        Log("Registered tick listener at {0} ms", intervalMs);
    }

    private static int[] EncodePositions(IEnumerable<BlockPos> positions)
    {
        List<int> flat = new();
        foreach (BlockPos pos in positions)
        {
            flat.Add(pos.X);
            flat.Add(pos.Y);
            flat.Add(pos.Z);
        }

        return flat.ToArray();
    }

    private static BlockPos DecodeSinglePos(int[] values)
    {
        return new BlockPos(values[0], values[1], values[2]);
    }

    private void Log(string message, params object?[] args)
    {
        Api?.Logger.Notification($"{ArchimedesScrewModSystem.LogPrefix} [controller:{ControllerId}] {message}", args);
    }

    private void LogStateChange(string name, ref bool? lastValue, bool value)
    {
        if (lastValue == value)
        {
            return;
        }

        lastValue = value;
        Log("{0} changed to {1}", name, value);
    }
}
