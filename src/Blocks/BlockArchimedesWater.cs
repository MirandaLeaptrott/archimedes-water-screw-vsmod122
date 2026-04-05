using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace ArchimedesScrew;

public sealed class BlockArchimedesWaterStill : BlockWater
{
    public override void OnBlockRemoved(IWorldAccessor world, BlockPos pos)
    {
        base.OnBlockRemoved(world, pos);
        NotifyManager(world, pos);
    }

    private static void NotifyManager(IWorldAccessor world, BlockPos pos)
    {
        if (world.Side != EnumAppSide.Server)
        {
            return;
        }

        world.Api.ModLoader.GetModSystem<ArchimedesScrewModSystem>().WaterManager?.OnManagedWaterRemoved(pos);
    }
}

public sealed class BlockArchimedesWaterFlowing : BlockWaterflowing
{
    public override void OnBlockRemoved(IWorldAccessor world, BlockPos pos)
    {
        base.OnBlockRemoved(world, pos);
        if (world.Side != EnumAppSide.Server)
        {
            return;
        }

        world.Api.ModLoader.GetModSystem<ArchimedesScrewModSystem>().WaterManager?.OnManagedWaterRemoved(pos);
    }
}

public sealed class BlockArchimedesWaterfall : BlockWaterfall
{
    public override void OnBlockRemoved(IWorldAccessor world, BlockPos pos)
    {
        base.OnBlockRemoved(world, pos);
        if (world.Side != EnumAppSide.Server)
        {
            return;
        }

        world.Api.ModLoader.GetModSystem<ArchimedesScrewModSystem>().WaterManager?.OnManagedWaterRemoved(pos);
    }
}
