using System;
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

        BlockWaterArchimedesScrew? blockToPlace = ResolveBlockToPlace(world, byPlayer, blockSel, ref failureCode);
        if (blockToPlace == null)
        {
            return false;
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

    private BlockWaterArchimedesScrew? ResolveBlockToPlace(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel, ref string failureCode)
    {
        if (IsStraightSegment())
        {
            return this;
        }

        if (!IsUnifiedEndCapInHand())
        {
            return this;
        }

        BlockFacing facing = SuggestedHVOrientation(byPlayer, blockSel)[0].Opposite;
        BlockPos placePos = blockSel.Position;

        if (TryResolveOutletPlacement(world, placePos, out _))
        {
            string outletVariant = "end-outlet-" + facing.Code;
            BlockWaterArchimedesScrew? outlet = ResolveMainScrewVariantBlock(outletVariant);
            api.Logger.Notification(
                "{0} End-cap as outlet: variant {1} at {2}",
                ArchimedesScrewModSystem.LogPrefix,
                outletVariant,
                placePos
            );
            return outlet ?? this;
        }

        if (HasValidWaterIntake(world, placePos))
        {
            string intakeVariant = "ported-" + facing.Code;
            BlockWaterArchimedesScrew? intake = ResolveMainScrewVariantBlock(intakeVariant);
            api.Logger.Notification(
                "{0} End-cap as intake: variant {1} at {2}",
                ArchimedesScrewModSystem.LogPrefix,
                intakeVariant,
                placePos
            );
            return intake ?? this;
        }

        failureCode = "archimedes-screw-endcap-context";
        api.Logger.Notification(
            "{0} End-cap placement rejected at {1}: need vanilla water here, or place on a straight screw or intake below",
            ArchimedesScrewModSystem.LogPrefix,
            placePos
        );
        return null;
    }

    /// <summary>True when the block below supports placing an outlet (straight segment or intake, not another outlet).</summary>
    public static bool TryResolveOutletPlacement(IWorldAccessor world, BlockPos placePos, out BlockWaterArchimedesScrew? belowScrew)
    {
        Block below = world.BlockAccessor.GetBlock(placePos.DownCopy());
        belowScrew = below as BlockWaterArchimedesScrew;
        if (belowScrew == null)
        {
            return false;
        }

        if (belowScrew.IsOutletBlock())
        {
            return false;
        }

        return belowScrew.IsStraightSegment() || belowScrew.IsIntakeBlock();
    }

    public bool IsStraightSegment()
    {
        return Code.Path.StartsWith(ArchimedesScrewModSystem.ScrewBlockCode, StringComparison.Ordinal) &&
               string.Equals(Variant["type"], "straight", StringComparison.Ordinal);
    }

    private bool IsUnifiedEndCapInHand()
    {
        if (!Code.Path.StartsWith(ArchimedesScrewModSystem.ScrewBlockCode, StringComparison.Ordinal))
        {
            return false;
        }

        string type = Variant["type"];
        return type.StartsWith("ported-", StringComparison.Ordinal) ||
               type.StartsWith("end-outlet-", StringComparison.Ordinal);
    }

    private BlockWaterArchimedesScrew? ResolveMainScrewVariantBlock(string typeVariant)
    {
        return api.World.GetBlock(
            new AssetLocation(ArchimedesScrewModSystem.ModId, $"{ArchimedesScrewModSystem.ScrewBlockCode}-{typeVariant}")
        ) as BlockWaterArchimedesScrew;
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

            if (api.ModLoader.GetModSystem<ArchimedesScrewModSystem>().Config.Water.DebugControllerStatsOnInteract &&
                world.BlockAccessor.GetBlock(blockSel.Position) is BlockWaterArchimedesScrew clickedScrew &&
                clickedScrew.IsIntakeBlock() &&
                world.BlockAccessor.GetBlockEntity(blockSel.Position) is BlockEntityWaterArchimedesScrew controllerBe)
            {
                controllerBe.LogDebugControllerStats();
            }
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
        return Code.Path.StartsWith(ArchimedesScrewModSystem.ScrewBlockCode + "-", StringComparison.Ordinal) &&
               Variant["type"].StartsWith("end-outlet-", StringComparison.Ordinal);
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
                "end-outlet-north" => BlockFacing.NORTH,
                "end-outlet-east" => BlockFacing.EAST,
                "end-outlet-south" => BlockFacing.SOUTH,
                "end-outlet-west" => BlockFacing.WEST,
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
        return IsValidIntakeFluidBlock(currentFluid);
    }

    /// <summary>Vanilla or mod-managed liquid suitable for an intake (family matches screw output).</summary>
    private static bool IsValidIntakeFluidBlock(Block block)
    {
        return block.IsLiquid() &&
               (ArchimedesWaterFamilies.TryResolveVanillaFamily(block, out _) ||
                ArchimedesWaterFamilies.TryResolveManagedFamily(block, out _));
    }
}
