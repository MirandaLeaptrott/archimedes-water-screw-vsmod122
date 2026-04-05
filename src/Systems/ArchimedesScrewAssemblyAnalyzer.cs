using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent.Mechanics;

namespace ArchimedesScrew;

public static class ArchimedesScrewAssemblyAnalyzer
{
    public sealed class AssemblyStatus
    {
        public bool IsAssemblyValid { get; init; }
        public bool IsFunctional { get; init; }
        public string Message { get; init; } = string.Empty;
        public BlockPos? IntakePos { get; init; }
        public BlockPos? TopPos { get; init; }
        public BlockPos? OutputPos { get; init; }
        public bool IsPowered { get; init; }
        public bool HasOutlet { get; init; }
    }

    public static AssemblyStatus Analyze(IWorldAccessor world, BlockPos anyPos, float minimumNetworkSpeed = 0.001f)
    {
        if (world.BlockAccessor.GetBlock(anyPos) is not BlockWaterArchimedesScrew start)
        {
            return new AssemblyStatus { IsAssemblyValid = false, IsFunctional = false, Message = "Target block is not part of a water Archimedes screw assembly." };
        }

        BlockPos bottom = anyPos.Copy();
        while (world.BlockAccessor.GetBlock(bottom.DownCopy()) is BlockWaterArchimedesScrew)
        {
            bottom.Down();
        }

        var stack = new List<(BlockPos Pos, BlockWaterArchimedesScrew Block)>();
        BlockPos cursor = bottom.Copy();
        while (world.BlockAccessor.GetBlock(cursor) is BlockWaterArchimedesScrew screw)
        {
            stack.Add((cursor.Copy(), screw));
            cursor.Up();
        }

        if (stack.Count == 0)
        {
            return new AssemblyStatus { IsAssemblyValid = false, IsFunctional = false, Message = "Could not resolve the screw stack." };
        }

        var intakeEntries = stack.Where(entry => entry.Block.IsIntakeBlock()).ToList();
        var outletEntries = stack.Where(entry => entry.Block.IsOutletBlock()).ToList();
        BlockPos topPos = stack[^1].Pos.Copy();

        if (intakeEntries.Count == 0)
        {
            return new AssemblyStatus { IsAssemblyValid = false, IsFunctional = false, Message = "Assembly has no intake block.", TopPos = topPos };
        }

        if (intakeEntries.Count > 1)
        {
            return new AssemblyStatus { IsAssemblyValid = false, IsFunctional = false, Message = "Assembly has more than one intake block.", TopPos = topPos };
        }

        var intakeEntry = intakeEntries[0];
        if (!intakeEntry.Pos.Equals(stack[0].Pos))
        {
            return new AssemblyStatus
            {
                IsFunctional = false,
                IsAssemblyValid = false,
                Message = "Intake must be the bottom-most block in the assembly.",
                IntakePos = intakeEntry.Pos,
                TopPos = topPos
            };
        }

        if (!intakeEntry.Block.HasValidWaterIntake(world, intakeEntry.Pos))
        {
            return new AssemblyStatus
            {
                IsFunctional = false,
                IsAssemblyValid = false,
                Message = "Intake is not placed inside a full non-flowing source liquid block.",
                IntakePos = intakeEntry.Pos,
                TopPos = topPos
            };
        }

        if (outletEntries.Count == 0)
        {
            return new AssemblyStatus
            {
                IsAssemblyValid = false,
                IsFunctional = false,
                Message = "Assembly requires an outlet block at the top.",
                IntakePos = intakeEntry.Pos,
                TopPos = topPos,
                HasOutlet = false
            };
        }

        if (outletEntries.Count > 1)
        {
            return new AssemblyStatus
            {
                IsAssemblyValid = false,
                IsFunctional = false,
                Message = "Assembly has more than one outlet block.",
                IntakePos = intakeEntry.Pos,
                TopPos = topPos
            };
        }

        if (outletEntries.Count == 1 && !outletEntries[0].Pos.Equals(topPos))
        {
            return new AssemblyStatus
            {
                IsAssemblyValid = false,
                IsFunctional = false,
                Message = "Outlet must be the top-most block in the assembly.",
                IntakePos = intakeEntry.Pos,
                TopPos = topPos,
                HasOutlet = true
            };
        }

        foreach (var entry in stack.Skip(1).SkipLast(outletEntries.Count == 1 ? 1 : 0))
        {
            if (entry.Block.IsDirectionalEndBlock())
            {
                return new AssemblyStatus
                {
                    IsAssemblyValid = false,
                    IsFunctional = false,
                    Message = "Only straight screw blocks may appear between the intake and the top/output block.",
                    IntakePos = intakeEntry.Pos,
                    TopPos = topPos,
                    HasOutlet = outletEntries.Count == 1
                };
            }
        }

        BlockPos outputPos = topPos.UpCopy();
        if (outletEntries.Count == 1)
        {
            BlockFacing? facing = outletEntries[0].Block.GetPortFacing();
            if (facing == null)
            {
                return new AssemblyStatus
                {
                    IsAssemblyValid = false,
                    IsFunctional = false,
                    Message = "Outlet orientation could not be resolved.",
                    IntakePos = intakeEntry.Pos,
                    TopPos = topPos,
                    HasOutlet = true
                };
            }

            outputPos = outletEntries[0].Pos.AddCopy(facing);
        }

        Block solidBlock = world.BlockAccessor.GetBlock(outputPos);
        Block fluidBlock = world.BlockAccessor.GetBlock(outputPos, BlockLayersAccess.Fluid);
        bool outputClear = IsOutputPositionClear(solidBlock, fluidBlock);
        if (!outputClear)
        {
            return new AssemblyStatus
            {
                IsAssemblyValid = false,
                IsFunctional = false,
                Message = $"Output position {outputPos} is blocked by {solidBlock.Code ?? fluidBlock.Code}.",
                IntakePos = intakeEntry.Pos,
                TopPos = topPos,
                OutputPos = outputPos,
                HasOutlet = outletEntries.Count == 1
            };
        }

        bool isPowered = false;
        var intakeBe = world.BlockAccessor.GetBlockEntity(intakeEntry.Pos);
        var behavior = intakeBe?.GetBehavior<BEBehaviorMPArchimedesScrew>();
        if (behavior?.Network != null)
        {
            isPowered = System.Math.Abs(behavior.Network.Speed) >= minimumNetworkSpeed;
        }

        if (!isPowered)
        {
            return new AssemblyStatus
            {
                IsAssemblyValid = true,
                IsFunctional = false,
                Message = $"Assembly structure is valid, but it is not mechanically powered. Output would be at {outputPos}.",
                IntakePos = intakeEntry.Pos,
                TopPos = topPos,
                OutputPos = outputPos,
                IsPowered = false,
                HasOutlet = outletEntries.Count == 1
            };
        }

        StringBuilder sb = new();
        sb.Append($"intake at {intakeEntry.Pos}, top at {topPos}, output at {outputPos}");
        if (outletEntries.Count == 1)
        {
            sb.Append(", using outlet");
        }
        else
        {
            sb.Append(", no outlet");
        }

        return new AssemblyStatus
        {
            IsAssemblyValid = true,
            IsFunctional = true,
            Message = sb.ToString(),
            IntakePos = intakeEntry.Pos,
            TopPos = topPos,
            OutputPos = outputPos,
            IsPowered = true,
            HasOutlet = outletEntries.Count == 1
        };
    }

    private static bool IsOutputPositionClear(Block solidBlock, Block fluidBlock)
    {
        bool solidClear = solidBlock.Id == 0 || solidBlock.ForFluidsLayer;
        bool fluidClear = fluidBlock.Id == 0 ||
                          (fluidBlock.Code?.Domain == ArchimedesScrewModSystem.ModId &&
                           fluidBlock.Code.Path.StartsWith(ArchimedesScrewModSystem.ManagedWaterCode, System.StringComparison.Ordinal));

        return solidClear && fluidClear;
    }
}
