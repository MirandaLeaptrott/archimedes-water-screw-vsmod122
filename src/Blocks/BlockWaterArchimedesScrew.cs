using System;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;
using Vintagestory.GameContent.Mechanics;

namespace ArchimedesScrew;

public sealed class BlockWaterArchimedesScrew : BlockMPBase
{
    public bool IsOrientedTo(BlockFacing facing)
    {
        return facing.Axis == EnumAxis.Y;
    }

    public override bool HasMechPowerConnectorAt(IWorldAccessor world, BlockPos pos, BlockFacing face)
    {
        return IsOrientedTo(face);
    }

    public override bool TryPlaceBlock(IWorldAccessor world, IPlayer byPlayer, ItemStack itemstack, BlockSelection blockSel, ref string failureCode)
    {
        if (!CanPlaceBlock(world, byPlayer, blockSel, ref failureCode))
        {
            api.Logger.Notification(
                "{0} Placement blocked for {1} at {2}: CanPlaceBlock failed with code '{3}'",
                ArchimedesScrewModSystem.LogPrefix,
                Code,
                blockSel.Position,
                failureCode
            );
            return false;
        }

        BlockWaterArchimedesScrew blockToPlace = this;
        if (IsDirectionalEndBlock())
        {
            BlockFacing playerFacing = GetFacingAwayFromPlayer(byPlayer, blockSel.Position);
            string variant = GetPlacedVariantForFacing(playerFacing);
            blockToPlace = api.World.GetBlock(CodeWithVariant("type", variant)) as BlockWaterArchimedesScrew
                ?? this;
            api.Logger.Notification(
                "{0} Placement chose facing {1} and variant '{2}' for {3} at {4}",
                ArchimedesScrewModSystem.LogPrefix,
                playerFacing.Code,
                variant,
                Code,
                blockSel.Position
            );
        }

        if (blockToPlace.IsIntakeBlock() && !blockToPlace.HasValidWaterIntake(world, blockSel.Position))
        {
            failureCode = "archimedes-screw-requires-water";
            Block currentFluid = world.BlockAccessor.GetBlock(blockSel.Position, BlockLayersAccess.Fluid);
            api.Logger.Notification(
                "{0} Intake placement rejected at {1}: fluid={2}, flow={3}, height={4}",
                ArchimedesScrewModSystem.LogPrefix,
                blockSel.Position,
                currentFluid.Code,
                currentFluid.Variant?["flow"] ?? "<null>",
                currentFluid.Variant?["height"] ?? "<null>"
            );
            return false;
        }

        foreach (BlockFacing face in BlockFacing.VERTICALS)
        {
            BlockPos pos = blockSel.Position.AddCopy(face);
            IMechanicalPowerBlock? block = world.BlockAccessor.GetBlock(pos) as IMechanicalPowerBlock;
            if (block == null || !block.HasMechPowerConnectorAt(world, pos, face.Opposite))
            {
                continue;
            }

            if (!blockToPlace.DoPlaceBlock(world, byPlayer, blockSel, itemstack))
            {
                continue;
            }

            api.Logger.Notification(
                "{0} Placed {1} at {2} and connected to mechanical block at {3} via {4}",
                ArchimedesScrewModSystem.LogPrefix,
                blockToPlace.Code,
                blockSel.Position,
                pos,
                face.Opposite.Code
            );
            block.DidConnectAt(world, pos, face.Opposite);
            WasPlaced(world, blockSel.Position, face);
            return true;
        }

        if (blockToPlace.CanPlaceBlock(world, byPlayer, blockSel, ref failureCode) &&
            blockToPlace.DoPlaceBlock(world, byPlayer, blockSel, itemstack))
        {
            api.Logger.Notification(
                "{0} Placed {1} at {2} without an immediate mechanical connection",
                ArchimedesScrewModSystem.LogPrefix,
                blockToPlace.Code,
                blockSel.Position
            );
            blockToPlace.WasPlaced(world, blockSel.Position, null);
            return true;
        }

        api.Logger.Notification(
            "{0} Placement ultimately failed for {1} at {2} with code '{3}'",
            ArchimedesScrewModSystem.LogPrefix,
            Code,
            blockSel.Position,
            failureCode
        );
        return false;
    }

    public override void OnNeighbourBlockChange(IWorldAccessor world, BlockPos pos, BlockPos neibpos)
    {
        base.OnNeighbourBlockChange(world, pos, neibpos);
    }

    public override void DidConnectAt(IWorldAccessor world, BlockPos pos, BlockFacing face)
    {
    }

    public override bool OnBlockInteractStart(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel)
    {
        ItemSlot slot = byPlayer.InventoryManager.ActiveHotbarSlot;
        if (!slot.Empty)
        {
            return base.OnBlockInteractStart(world, byPlayer, blockSel);
        }

        var status = ArchimedesScrewAssemblyAnalyzer.Analyze(world, blockSel.Position);
        string message = status.IsFunctional
            ? $"Assembly functional: {status.Message}"
            : $"Assembly not functional: {status.Message}";

        if (world.Side == EnumAppSide.Client)
        {
            (api as ICoreClientAPI)?.TriggerIngameError(this, "archscrewstatus", message);
        }

        if (world.Side == EnumAppSide.Server && byPlayer is IServerPlayer splr)
        {
            splr.SendMessage(GlobalConstants.InfoLogChatGroup, message, EnumChatType.Notification);
        }

        api.Logger.Notification("{0} Status check at {1}: {2}", ArchimedesScrewModSystem.LogPrefix, blockSel.Position, message);
        return true;
    }

    public bool IsIntakeBlock()
    {
        return Code.Path.StartsWith(ArchimedesScrewModSystem.ScrewBlockCode, StringComparison.Ordinal) &&
               Variant["type"].StartsWith("ported-", StringComparison.Ordinal);
    }

    public bool IsOutletBlock()
    {
        return Code.Path.StartsWith(ArchimedesScrewModSystem.OutletBlockCode, StringComparison.Ordinal);
    }

    public bool IsDirectionalEndBlock()
    {
        return IsIntakeBlock() || IsOutletBlock();
    }

    public BlockFacing? GetPortFacing()
    {
        if (IsOutletBlock())
        {
            return Variant["type"] switch
            {
                "north" => BlockFacing.NORTH,
                "east" => BlockFacing.EAST,
                "south" => BlockFacing.SOUTH,
                "west" => BlockFacing.WEST,
                _ => null
            };
        }

        return Variant["type"] switch
        {
            "ported-north" => BlockFacing.NORTH,
            "ported-east" => BlockFacing.EAST,
            "ported-south" => BlockFacing.SOUTH,
            "ported-west" => BlockFacing.WEST,
            _ => null
        };
    }

    public bool HasValidWaterIntake(IWorldAccessor world, BlockPos pos)
    {
        Block currentFluid = world.BlockAccessor.GetBlock(pos, BlockLayersAccess.Fluid);
        return IsWaterSourceBlock(currentFluid);
    }

    private static bool IsWaterSourceBlock(Block block)
    {
        return block.IsLiquid() &&
               string.Equals(block.Variant?["flow"], "still", StringComparison.Ordinal) &&
               string.Equals(block.Variant?["height"], "7", StringComparison.Ordinal);
    }

    private string GetPlacedVariantForFacing(BlockFacing facing)
    {
        if (IsOutletBlock())
        {
            return facing.Code;
        }

        return "ported-" + facing.Code;
    }

    private static BlockFacing GetFacingAwayFromPlayer(IPlayer byPlayer, BlockPos targetPos)
    {
        if (byPlayer.Entity != null)
        {
            double dx = byPlayer.Entity.Pos.X - (targetPos.X + 0.5);
            double dz = byPlayer.Entity.Pos.Z - (targetPos.Z + 0.5);
            if (Math.Abs(dx) > 0.001 || Math.Abs(dz) > 0.001)
            {
                float angle = (float)Math.Atan2(dz, dx);
                return BlockFacing.HorizontalFromAngle(angle).Opposite;
            }
        }

        float yaw = byPlayer.Entity?.Pos.Yaw ?? 0;
        return BlockFacing.HorizontalFromYaw(yaw);
    }
}
