using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace ArchimedesScrew;

internal static class ArchimedesWaterBlockHelper
{
    public static void NotifyManagerOnRemoval(IWorldAccessor world, BlockPos pos, Block removedBlock)
    {
        if (world.Side != EnumAppSide.Server)
        {
            return;
        }

        ArchimedesWaterNetworkManager? manager = world.Api.ModLoader.GetModSystem<ArchimedesScrewModSystem>().WaterManager;
        if (manager == null)
        {
            return;
        }

        Block replacementFluid = world.BlockAccessor.GetBlock(pos, BlockLayersAccess.Fluid);
        // Keep ownership stable across managed variant transitions (e.g. still-7 -> still-6).
        // The old block is "removed" by fluid simulation, but this is not true source loss.
        if (manager.IsArchimedesWaterBlock(replacementFluid) &&
            manager.TryResolveManagedWaterFamily(removedBlock, out string removedFamilyId) &&
            manager.TryResolveManagedWaterFamily(replacementFluid, out string replacementFamilyId) &&
            string.Equals(removedFamilyId, replacementFamilyId, StringComparison.Ordinal))
        {
            return;
        }

        manager.OnManagedWaterRemoved(pos);
        manager.TryConvertVanillaSourceUsingAdjacentManagedFamilyForPlayer(
            pos,
            "player-placed source replaced managed water"
        );
    }

    public static void TryConvertNeighbourSource(IWorldAccessor world, BlockPos neibpos, BlockPos referencePos)
    {
        if (world.Side != EnumAppSide.Server)
        {
            return;
        }

        ArchimedesWaterNetworkManager? manager = world.Api.ModLoader.GetModSystem<ArchimedesScrewModSystem>().WaterManager;
        if (manager == null)
        {
            return;
        }

        Block referenceFluid = world.BlockAccessor.GetBlock(referencePos, BlockLayersAccess.Fluid);
        if (!manager.TryResolveManagedWaterFamily(referenceFluid, out string familyId))
        {
            return;
        }

        manager.TryConvertVanillaSourceForPlayer(
            neibpos,
            familyId,
            "player-placed source adjacent to managed water"
        );
    }
}

public sealed class BlockArchimedesWaterStill : BlockWater
{
    public override void OnNeighbourBlockChange(IWorldAccessor world, BlockPos pos, BlockPos neibpos)
    {
        base.OnNeighbourBlockChange(world, pos, neibpos);
        ArchimedesWaterBlockHelper.TryConvertNeighbourSource(world, neibpos, pos);
    }

    public override void OnBlockRemoved(IWorldAccessor world, BlockPos pos)
    {
        base.OnBlockRemoved(world, pos);
        ArchimedesWaterBlockHelper.NotifyManagerOnRemoval(world, pos, this);
    }
}

public sealed class BlockArchimedesWaterFlowing : BlockWaterflowing
{
    public override void OnNeighbourBlockChange(IWorldAccessor world, BlockPos pos, BlockPos neibpos)
    {
        base.OnNeighbourBlockChange(world, pos, neibpos);
        ArchimedesWaterBlockHelper.TryConvertNeighbourSource(world, neibpos, pos);
    }

    public override void OnBlockRemoved(IWorldAccessor world, BlockPos pos)
    {
        base.OnBlockRemoved(world, pos);
        ArchimedesWaterBlockHelper.NotifyManagerOnRemoval(world, pos, this);
    }
}

public sealed class BlockArchimedesWaterfall : BlockWaterfall
{
    public override void OnNeighbourBlockChange(IWorldAccessor world, BlockPos pos, BlockPos neibpos)
    {
        base.OnNeighbourBlockChange(world, pos, neibpos);
        ArchimedesWaterBlockHelper.TryConvertNeighbourSource(world, neibpos, pos);
    }

    public override void OnBlockRemoved(IWorldAccessor world, BlockPos pos)
    {
        base.OnBlockRemoved(world, pos);
        ArchimedesWaterBlockHelper.NotifyManagerOnRemoval(world, pos, this);
    }
}
