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
    private const string RelayPositionsKey = "relayPositions";
    private const string ControllerIdKey = "controllerId";
    private const string LastSeedKey = "lastSeed";
    private const string WasControllerKey = "wasController";
    private const int LowCadenceConnectivityScanStride = 3;

    private readonly Dictionary<string, BlockPos> ownedPositions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BlockPos> relayOwnedPositions = new(StringComparer.Ordinal);
    private static readonly HashSet<string> EmptyKeySet = new(StringComparer.Ordinal);

    private ArchimedesWaterNetworkManager? waterManager;
    private ArchimedesScrewConfig.WaterConfig? waterConfig;

    private long nextCentralWaterTickDueMs;
    private long nextRelayCreationDueMs;
    private ArchimedesScrewControllerSchedule lastScheduledCadence = ArchimedesScrewControllerSchedule.HighCadence;
    private int lowCadenceScanSkipsRemaining;
    private int lastEffectiveRelayCap;

    private long assemblyAnalysisCachedAtMs = long.MinValue;
    private ArchimedesScrewAssemblyAnalyzer.AssemblyStatus? cachedAssemblyAnalysis;

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

        ArchimedesScrewModSystem modSystem = api.ModLoader.GetModSystem<ArchimedesScrewModSystem>();
        waterManager = modSystem.WaterManager;
        waterConfig = modSystem.Config.Water;

        waterManager?.RegisterScrewBlock(Pos);
        waterManager?.RegisterLoadedController(this);
        Log("Initialized controller {0} for block {1} at {2}", ControllerId, Block?.Code, Pos);

        if (ownedPositions.Count > 0)
        {
            waterManager?.RegisterRestoredOwnership(ControllerId, Pos, ownedPositions.Values.ToList());
            Log("Restored {0} owned Archimedes source positions from save", ownedPositions.Count);
        }
        if (relayOwnedPositions.Count > 0)
        {
            Log("Restored {0} relay-owned source positions from save", relayOwnedPositions.Count);
        }

        nextCentralWaterTickDueMs = 0;
        nextRelayCreationDueMs = 0;
        UpdateCentralTickRegistration();
    }

    public override void OnBlockPlaced(ItemStack? byItemStack = null)
    {
        base.OnBlockPlaced(byItemStack);
        waterManager?.RegisterScrewBlock(Pos);
        waterManager?.RegisterLoadedController(this);
        InvalidateAssemblyAnalysisCache();
        nextCentralWaterTickDueMs = 0;
        nextRelayCreationDueMs = 0;
        UpdateCentralTickRegistration();
        Log("Block placed at {0}: {1}", Pos, Block?.Code);
    }

    public override void OnBlockRemoved()
    {
        Log("Block removed at {0}: {1}", Pos, Block?.Code);
        waterManager?.UnregisterFromCentralWaterTick(ControllerId);
        ReleaseAllManagedWater("block removed");
        waterManager?.UnregisterScrewBlock(Pos);
        waterManager?.RemoveControllerSnapshot(ControllerId);
        base.OnBlockRemoved();
    }

    public override void OnBlockUnloaded()
    {
        Log("Block unloaded at {0}", Pos);
        waterManager?.UnregisterFromCentralWaterTick(ControllerId);
        waterManager?.UnregisterLoadedController(ControllerId);
        base.OnBlockUnloaded();
    }

    public void LogDebugControllerStats()
    {
        if (Api == null || Api.Side != EnumAppSide.Server || waterConfig == null)
        {
            return;
        }

        int relayCount = relayOwnedPositions.Count;
        int managedCount = ownedPositions.Count;
        int seedOwnedCount = Math.Max(0, managedCount - relayCount);
        int relayCap = Math.Max(0, waterConfig.MaxRelaySourcesPerController);
        float power = GetCurrentMechanicalPower();
        bool relayDue = IsRelayCreationDue();

        Log(
            "Debug controller stats: pos={0}, managedSources={1}, relaySources={2}, nonRelaySources={3}, relayCapConfigured={4}, relayCapEffective={5}, relayCreateDue={6}, nextRelayCreationDueInMs={7}, cadence={8}, nextControllerTickDueInMs={9}, lowCadenceSkipsRemaining={10}, mechPower={11:0.#####}",
            Pos,
            managedCount,
            relayCount,
            seedOwnedCount,
            relayCap,
            lastEffectiveRelayCap,
            relayDue,
            Math.Max(0, nextRelayCreationDueMs - Environment.TickCount64),
            lastScheduledCadence,
            Math.Max(0, nextCentralWaterTickDueMs - Environment.TickCount64),
            lowCadenceScanSkipsRemaining,
            power
        );
    }

    public void InvalidateAssemblyAnalysisCache()
    {
        cachedAssemblyAnalysis = null;
        assemblyAnalysisCachedAtMs = long.MinValue;
    }

    internal bool IsCentralWaterTickDue(long nowMs)
    {
        return nowMs >= nextCentralWaterTickDueMs;
    }

    internal void RunCentralWaterTick()
    {
        OnWaterControllerTick();
    }

    private void UpdateCentralTickRegistration()
    {
        if (Api?.Side != EnumAppSide.Server || waterManager == null)
        {
            return;
        }

        if (Block is BlockWaterArchimedesScrew s && s.IsIntakeBlock())
        {
            waterManager.RegisterForCentralWaterTick(this);
        }
        else
        {
            waterManager.UnregisterFromCentralWaterTick(ControllerId);
        }
    }

    private void ScheduleNextWaterTick(ArchimedesScrewControllerSchedule schedule, int fastMs, int idleMs)
    {
        int interval = schedule == ArchimedesScrewControllerSchedule.HighCadence ? fastMs : idleMs;
        nextCentralWaterTickDueMs = Environment.TickCount64 + Math.Max(1, interval);
        lastScheduledCadence = schedule;
    }

    private ArchimedesScrewAssemblyAnalyzer.AssemblyStatus GetOrRefreshAssemblyAnalysis()
    {
        if (Api == null || waterConfig == null)
        {
            return new ArchimedesScrewAssemblyAnalyzer.AssemblyStatus
            {
                IsAssemblyValid = false,
                IsFunctional = false,
                Message = "controller not initialized"
            };
        }

        long now = Environment.TickCount64;
        int ttl = Math.Max(0, waterConfig.AssemblyAnalysisCacheMs);
        if (cachedAssemblyAnalysis != null && now - assemblyAnalysisCachedAtMs < ttl)
        {
            return cachedAssemblyAnalysis;
        }

        ArchimedesScrewAssemblyAnalyzer.AssemblyStatus fresh =
            ArchimedesScrewAssemblyAnalyzer.Analyze(Api.World, Pos, waterConfig.MinimumNetworkSpeed);
        cachedAssemblyAnalysis = fresh;
        assemblyAnalysisCachedAtMs = now;
        return fresh;
    }

    public void NotifyManagedWaterRemoved(BlockPos pos)
    {
        string key = ArchimedesWaterNetworkManager.PosKey(pos);
        ownedPositions.Remove(key);
        relayOwnedPositions.Remove(key);
        MarkDirty();
        Log("Tracked Archimedes source removed externally at {0}; ownership updated", pos);
    }

    internal void TrackAssignedSourceFromManager(BlockPos pos, string reason)
    {
        string key = ArchimedesWaterNetworkManager.PosKey(pos);
        if (ownedPositions.ContainsKey(key))
        {
            return;
        }

        ownedPositions[key] = pos.Copy();
        UpdateSnapshot();
        Log("Assigned source at {0} ({1})", pos, reason);
    }

    public void ClearOwnedStateAfterPurge()
    {
        ownedPositions.Clear();
        relayOwnedPositions.Clear();
        wasController = false;
        lastSeedPos = null;
        lastLoggedSeedKey = null;
        InvalidateAssemblyAnalysisCache();
        MarkDirty();
        Log("Cleared owned source state after purge");
    }

    public void ReleaseAllManagedWater(string reason = "unspecified")
    {
        if (waterManager == null)
        {
            ownedPositions.Clear();
            relayOwnedPositions.Clear();
            Log("Release requested for reason '{0}', but water manager is null", reason);
            return;
        }

        if (ownedPositions.Count == 0)
        {
            waterManager.UpdateControllerSnapshot(ControllerId, Pos, Array.Empty<BlockPos>());
            Log("Release requested for reason '{0}', but no Archimedes sources were owned", reason);
            return;
        }

        int count = ownedPositions.Count;
        foreach (BlockPos ownedPos in ownedPositions.Values.ToArray())
        {
            waterManager.ReleaseSourceOwner(ControllerId, ownedPos);
        }

        ownedPositions.Clear();
        relayOwnedPositions.Clear();
        waterManager.UpdateControllerSnapshot(ControllerId, Pos, Array.Empty<BlockPos>());
        MarkDirty();
        Log("Released {0} Archimedes source blocks because {1}", count, reason);
    }

    public bool TryGetActiveSeedState(out ArchimedesOutletState state)
    {
        state = default;
        ControllerEvaluation evaluation = EvaluateController();
        if (!evaluation.IsController ||
            !evaluation.IsPowered ||
            evaluation.FamilyId == null ||
            evaluation.SeedPos == null)
        {
            return false;
        }

        state = new ArchimedesOutletState(ControllerId, evaluation.SeedPos.Copy(), evaluation.FamilyId);
        return true;
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

        byte[]? relayBytes = tree.GetBytes(RelayPositionsKey);
        relayOwnedPositions.Clear();
        if (relayBytes != null)
        {
            int[] flatRelayPositions = SerializerUtil.Deserialize<int[]>(relayBytes);
            for (int i = 0; i + 2 < flatRelayPositions.Length; i += 3)
            {
                BlockPos pos = new(flatRelayPositions[i], flatRelayPositions[i + 1], flatRelayPositions[i + 2]);
                relayOwnedPositions[ArchimedesWaterNetworkManager.PosKey(pos)] = pos;
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
        tree.SetBytes(RelayPositionsKey, SerializerUtil.Serialize(EncodePositions(relayOwnedPositions.Values)));

        if (lastSeedPos == null)
        {
            tree.RemoveAttribute(LastSeedKey);
        }
        else
        {
            tree.SetBytes(LastSeedKey, SerializerUtil.Serialize(new[] { lastSeedPos.X, lastSeedPos.Y, lastSeedPos.Z }));
        }
    }

    private void OnWaterControllerTick()
    {
        using ArchimedesPerf.PerfScope _perf = ArchimedesPerf.Measure("controller.tick");
        if (Api == null || Api.Side != EnumAppSide.Server || waterManager == null || waterConfig == null)
        {
            return;
        }

        int fastMs = waterConfig.FastTickMs;
        int idleMs = waterConfig.IdleTickMs;

        ControllerEvaluation evaluation = EvaluateController();
        LogStateChange("controller validity", ref lastLoggedControllerState, evaluation.IsController);
        LogStateChange("powered state", ref lastLoggedPowerState, evaluation.IsPowered);

        if (!evaluation.IsController)
        {
            wasController = false;
            int removed = DrainUnsupportedSources(
                Array.Empty<ArchimedesOutletState>(),
                EmptyKeySet,
                evaluation.FailureReason);
            ArchimedesScrewControllerSchedule schedule = ownedPositions.Count > 0 || removed > 0
                ? ArchimedesScrewControllerSchedule.HighCadence
                : ArchimedesScrewControllerSchedule.LowCadence;
            ScheduleNextWaterTick(schedule, fastMs, idleMs);
            return;
        }

        wasController = true;

        if (!evaluation.IsPowered || evaluation.FamilyId == null || evaluation.SeedPos == null)
        {
            int removed = DrainUnsupportedSources(
                Array.Empty<ArchimedesOutletState>(),
                EmptyKeySet,
                evaluation.FailureReason);
            ArchimedesScrewControllerSchedule schedule = ownedPositions.Count > 0 || removed > 0
                ? ArchimedesScrewControllerSchedule.HighCadence
                : ArchimedesScrewControllerSchedule.LowCadence;
            ScheduleNextWaterTick(schedule, fastMs, idleMs);
            return;
        }

        BlockPos seedPos = evaluation.SeedPos.Copy();
        string familyId = evaluation.FamilyId;
        string seedKey = ArchimedesWaterNetworkManager.PosKey(seedPos);
        if (lastLoggedSeedKey != seedKey)
        {
            lastLoggedSeedKey = seedKey;
            Log("Seed/output position is now {0}", seedPos);
        }

        lastSeedPos = seedPos.Copy();

        bool ensuredSeed = EnsureSeedSource(seedPos, familyId);

        bool canSkipConnectivityScan =
            lastScheduledCadence == ArchimedesScrewControllerSchedule.LowCadence &&
            !ensuredSeed &&
            lowCadenceScanSkipsRemaining > 0;

        if (canSkipConnectivityScan)
        {
            lowCadenceScanSkipsRemaining--;
            ArchimedesPerf.AddCount("controller.connectivityScan.skipped");
            ScheduleNextWaterTick(ArchimedesScrewControllerSchedule.LowCadence, fastMs, idleMs);
            ArchimedesPerf.MaybeFlush(Api);
            return;
        }

        HashSet<string> connectedWaterKeys = waterManager.CollectConnectedManagedWaterCached(seedPos, out Dictionary<string, BlockPos> connectedWater);
        ReconcileRelayOwnedPositions();
        int relayCap = ComputeEffectiveRelayCap(evaluation.CurrentPower);
        int relayWorkBudget = Math.Max(1, waterConfig.MaxRelayPromotionsPerTick);
        int relayCreated = 0;
        int relayTrimmed = 0;
        if (waterConfig.EnableRelaySources)
        {
            if (IsRelayCreationDue())
            {
                relayCreated = CreateRelaySources(seedPos, familyId, connectedWaterKeys, connectedWater, relayCap, relayWorkBudget);
                ScheduleNextRelayCreationTick(idleMs);
            }
            relayTrimmed = TrimRelaySourcesToCap(seedPos, relayCap, relayWorkBudget);
        }

        List<ArchimedesOutletState> supportingSeeds = ResolveSupportingSeeds(seedPos, connectedWaterKeys);
        int removedDisconnected = DrainUnsupportedSources(supportingSeeds, connectedWaterKeys, string.Empty);

        if (ensuredSeed)
        {
            UpdateSnapshot();
        }

        bool busyWork = ensuredSeed || removedDisconnected > 0 || relayCreated > 0 || relayTrimmed > 0 || ownedPositions.Count == 0;
        ArchimedesScrewControllerSchedule nextSchedule = busyWork
            ? ArchimedesScrewControllerSchedule.HighCadence
            : ArchimedesScrewControllerSchedule.LowCadence;
        lowCadenceScanSkipsRemaining = nextSchedule == ArchimedesScrewControllerSchedule.LowCadence
            ? LowCadenceConnectivityScanStride - 1
            : 0;
        ScheduleNextWaterTick(nextSchedule, fastMs, idleMs);

        ArchimedesPerf.MaybeFlush(Api);
    }

    private ControllerEvaluation EvaluateController()
    {
        if (Api == null || waterManager == null || waterConfig == null)
        {
            return new ControllerEvaluation(false, false, 0f, "controller not initialized", null, null);
        }

        BlockWaterArchimedesScrew? screwBlock = Block as BlockWaterArchimedesScrew;
        if (screwBlock == null || !screwBlock.IsIntakeBlock())
        {
            return new ControllerEvaluation(false, false, 0f, "block is not an intake controller", null, null);
        }

        ArchimedesScrewAssemblyAnalyzer.AssemblyStatus assemblyStatus = GetOrRefreshAssemblyAnalysis();
        if (!assemblyStatus.IsAssemblyValid)
        {
            return new ControllerEvaluation(false, false, 0f, $"assembly invalid: {assemblyStatus.Message}", null, assemblyStatus.OutputPos?.Copy());
        }

        Block intakeFluid = Api.World.BlockAccessor.GetBlock(Pos, BlockLayersAccess.Fluid);
        if (!waterManager.TryResolveIntakeWaterFamily(intakeFluid, out string familyId))
        {
            return new ControllerEvaluation(false, assemblyStatus.IsPowered, 0f, $"unsupported intake fluid: {intakeFluid.Code}", null, assemblyStatus.OutputPos?.Copy());
        }

        BlockPos seedPos = assemblyStatus.OutputPos?.Copy() ?? GetSeedPosition();
        if (!CanUseSeedPosition(seedPos))
        {
            return new ControllerEvaluation(false, assemblyStatus.IsPowered, 0f, $"seed/output position {seedPos} is blocked", familyId, seedPos);
        }

        float currentPower = GetCurrentMechanicalPower();
        return new ControllerEvaluation(true, assemblyStatus.IsPowered, currentPower, string.Empty, familyId, seedPos);
    }

    private List<ArchimedesOutletState> ResolveSupportingSeeds(BlockPos seedPos, HashSet<string> connectedKeySet)
    {
        List<ArchimedesOutletState> supporting = new();
        HashSet<string> seenControllerIds = new(StringComparer.Ordinal);

        foreach (ArchimedesOutletState activeSeed in waterManager!.GetActiveSeedStatesCached())
        {
            if (!seenControllerIds.Add(activeSeed.ControllerId))
            {
                continue;
            }

            if (activeSeed.ControllerId == ControllerId ||
                activeSeed.SeedPos.Equals(seedPos) ||
                connectedKeySet.Contains(ArchimedesWaterNetworkManager.PosKey(activeSeed.SeedPos)))
            {
                supporting.Add(new ArchimedesOutletState(activeSeed.ControllerId, activeSeed.SeedPos.Copy(), activeSeed.FamilyId));
            }
        }

        if (supporting.Count == 0)
        {
            supporting.Add(new ArchimedesOutletState(ControllerId, seedPos.Copy(), string.Empty));
        }

        return supporting;
    }

    private bool EnsureSeedSource(BlockPos seedPos, string familyId)
    {
        if (waterManager == null)
        {
            return false;
        }

        bool changed = waterManager.EnsureSourceOwned(ControllerId, seedPos, familyId);
        ownedPositions[ArchimedesWaterNetworkManager.PosKey(seedPos)] = seedPos.Copy();
        return changed;
    }

    private int ComputeEffectiveRelayCap(float currentPower)
    {
        if (waterConfig == null || !waterConfig.EnableRelaySources)
        {
            lastEffectiveRelayCap = 0;
            return 0;
        }

        int configuredMax = Math.Max(0, waterConfig.MaxRelaySourcesPerController);
        if (configuredMax == 0)
        {
            lastEffectiveRelayCap = 0;
            return 0;
        }

        float minPower = Math.Max(0f, waterConfig.MinimumNetworkSpeed);
        float maxPower = Math.Max(minPower + 0.000001f, waterConfig.RequiredMechPowerForMaxRelay);
        if (currentPower <= minPower)
        {
            lastEffectiveRelayCap = 0;
            return 0;
        }

        float normalized = Math.Clamp((currentPower - minPower) / (maxPower - minPower), 0f, 1f);
        int targetCap = (int)MathF.Floor(configuredMax * normalized);
        targetCap = Math.Clamp(targetCap, 0, configuredMax);

        if (targetCap > lastEffectiveRelayCap)
        {
            float upThreshold = Math.Clamp(((lastEffectiveRelayCap + 1f) / configuredMax) + Math.Max(0f, waterConfig.RelayPowerHysteresisPct), 0f, 1f);
            if (normalized >= upThreshold)
            {
                lastEffectiveRelayCap = Math.Min(configuredMax, lastEffectiveRelayCap + 1);
            }
        }
        else if (targetCap < lastEffectiveRelayCap)
        {
            float downThreshold = Math.Clamp((lastEffectiveRelayCap / (float)configuredMax) - Math.Max(0f, waterConfig.RelayPowerHysteresisPct), 0f, 1f);
            if (normalized <= downThreshold)
            {
                lastEffectiveRelayCap = Math.Max(0, lastEffectiveRelayCap - 1);
            }
        }

        return lastEffectiveRelayCap;
    }

    private int CreateRelaySources(
        BlockPos seedPos,
        string familyId,
        HashSet<string> connectedWaterKeys,
        Dictionary<string, BlockPos> connectedWater,
        int relayCap,
        int perTickBudget)
    {
        using ArchimedesPerf.PerfScope _perf = ArchimedesPerf.Measure("controller.relayPass");
        if (Api == null || waterManager == null || waterConfig == null || relayCap <= relayOwnedPositions.Count)
        {
            return 0;
        }

        int stride = Math.Max(2, waterConfig.RelayStrideBlocks);
        Dictionary<string, int> distanceByKey = BuildDistanceMap(seedPos, connectedWaterKeys);
        int candidatesExamined = 0;
        int created = 0;
        int budget = Math.Max(1, perTickBudget);
        int maxCreateAllowed = Math.Min(budget, relayCap - relayOwnedPositions.Count);
        foreach ((string key, int distance) in distanceByKey.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal))
        {
            if (created >= maxCreateAllowed)
            {
                break;
            }

            if (distance <= 0)
            {
                continue;
            }

            if (!connectedWater.TryGetValue(key, out BlockPos? pos))
            {
                continue;
            }

            candidatesExamined++;
            if (!IsRelayCreationCandidate(pos))
            {
                continue;
            }

            if (waterManager.TryGetSourceOwner(pos, out string ownerId) &&
                !string.Equals(ownerId, ControllerId, StringComparison.Ordinal))
            {
                continue;
            }

            if (relayOwnedPositions.ContainsKey(key))
            {
                continue;
            }

            if (!IsRelayFarEnoughFromOwnedRelays(pos, stride))
            {
                continue;
            }

            if (!waterManager.EnsureSourceOwned(ControllerId, pos, familyId))
            {
                continue;
            }

            ownedPositions[key] = pos.Copy();
            relayOwnedPositions[key] = pos.Copy();
            created++;
        }

        ArchimedesPerf.AddCount("controller.relayCandidates", candidatesExamined);
        ArchimedesPerf.AddCount("controller.relayPromotions", created);
        if (created > 0)
        {
            UpdateSnapshot();
        }

        return created;
    }

    private int TrimRelaySourcesToCap(BlockPos seedPos, int relayCap, int perTickBudget)
    {
        if (waterManager == null || relayOwnedPositions.Count <= relayCap)
        {
            return 0;
        }

        int overflow = relayOwnedPositions.Count - relayCap;
        int removeCount = Math.Min(Math.Max(1, perTickBudget), overflow);
        List<BlockPos> ordered = relayOwnedPositions.Values
            .OrderByDescending(pos => DistanceSquared(pos, seedPos))
            .ThenByDescending(pos => pos.Y)
            .Take(removeCount)
            .ToList();
        foreach (BlockPos pos in ordered)
        {
            string key = ArchimedesWaterNetworkManager.PosKey(pos);
            relayOwnedPositions.Remove(key);
            ownedPositions.Remove(key);
            waterManager.ReleaseSourceOwner(ControllerId, pos);
        }

        ArchimedesPerf.AddCount("controller.relayTrimmed", ordered.Count);
        if (ordered.Count > 0)
        {
            UpdateSnapshot();
        }

        return ordered.Count;
    }

    private bool IsRelayCreationCandidate(BlockPos pos)
    {
        if (Api == null || waterManager == null)
        {
            return false;
        }

        Block fluid = Api.World.BlockAccessor.GetBlock(pos, BlockLayersAccess.Fluid);
        if (!waterManager.IsArchimedesLowestFlowingBlock(fluid))
        {
            return false;
        }

        BlockPos belowPos = pos.DownCopy();
        Block belowFluid = Api.World.BlockAccessor.GetBlock(belowPos, BlockLayersAccess.Fluid);
        if (belowFluid.IsLiquid())
        {
            return false;
        }

        Block belowSolid = Api.World.BlockAccessor.GetBlock(belowPos);
        if (belowSolid.Id == 0)
        {
            return false;
        }

        foreach (BlockFacing face in BlockFacing.HORIZONTALS)
        {
            BlockPos adjacentPos = pos.AddCopy(face);
            Block adjacentSolid = Api.World.BlockAccessor.GetBlock(adjacentPos);
            Block adjacentFluid = Api.World.BlockAccessor.GetBlock(adjacentPos, BlockLayersAccess.Fluid);
            if (adjacentSolid.Id == 0 && adjacentFluid.Id == 0)
            {
                return true;
            }
        }

        return false;
    }

    private Dictionary<string, int> BuildDistanceMap(BlockPos seedPos, HashSet<string> connectedWaterKeys)
    {
        Dictionary<string, int> distanceByKey = new(StringComparer.Ordinal);
        string seedKey = ArchimedesWaterNetworkManager.PosKey(seedPos);
        if (!connectedWaterKeys.Contains(seedKey))
        {
            return distanceByKey;
        }

        Queue<BlockPos> queue = new();
        distanceByKey[seedKey] = 0;
        queue.Enqueue(seedPos.Copy());
        while (queue.Count > 0)
        {
            BlockPos current = queue.Dequeue();
            string currentKey = ArchimedesWaterNetworkManager.PosKey(current);
            int currentDistance = distanceByKey[currentKey];
            foreach (BlockFacing face in BlockFacing.ALLFACES)
            {
                BlockPos next = current.AddCopy(face);
                string nextKey = ArchimedesWaterNetworkManager.PosKey(next);
                if (!connectedWaterKeys.Contains(nextKey) || distanceByKey.ContainsKey(nextKey))
                {
                    continue;
                }

                distanceByKey[nextKey] = currentDistance + 1;
                queue.Enqueue(next);
            }
        }

        return distanceByKey;
    }

    private bool IsRelayCreationDue()
    {
        return Environment.TickCount64 >= nextRelayCreationDueMs;
    }

    private void ScheduleNextRelayCreationTick(int idleMs)
    {
        nextRelayCreationDueMs = Environment.TickCount64 + Math.Max(1, idleMs);
    }

    private bool IsRelayFarEnoughFromOwnedRelays(BlockPos candidatePos, int minManhattanDistance)
    {
        if (relayOwnedPositions.Count == 0)
        {
            return true;
        }

        foreach (BlockPos relayPos in relayOwnedPositions.Values)
        {
            if (ManhattanDistance(candidatePos, relayPos) < minManhattanDistance)
            {
                return false;
            }
        }

        return true;
    }

    private void ReconcileRelayOwnedPositions()
    {
        if (Api == null || waterManager == null || relayOwnedPositions.Count == 0)
        {
            return;
        }

        foreach (string key in relayOwnedPositions.Keys.ToList())
        {
            BlockPos pos = ParsePosKey(key);
            Block fluid = Api.World.BlockAccessor.GetBlock(pos, BlockLayersAccess.Fluid);
            // Keep relay markers stable across temporary source/flow transitions.
            // Remove only when the position is no longer owned by this controller
            // or no longer an Archimedes-managed water block.
            if (!ownedPositions.ContainsKey(key) || !waterManager.IsArchimedesWaterBlock(fluid))
            {
                relayOwnedPositions.Remove(key);
            }
        }
    }

    private float GetCurrentMechanicalPower()
    {
        if (Api == null)
        {
            return 0f;
        }

        BlockEntity? intakeBe = Api.World.BlockAccessor.GetBlockEntity(Pos);
        BEBehaviorMPArchimedesScrew? behavior = intakeBe?.GetBehavior<BEBehaviorMPArchimedesScrew>();
        return behavior?.Network == null ? 0f : Math.Abs(behavior.Network.Speed);
    }

    private int DrainUnsupportedSources(IReadOnlyCollection<ArchimedesOutletState> referenceSeeds, HashSet<string> supportedKeySet, string reason)
    {
        using ArchimedesPerf.PerfScope _perf = ArchimedesPerf.Measure("controller.drainUnsupported");
        if (waterManager == null || waterConfig == null)
        {
            return 0;
        }

        List<BlockPos> toRelease = new();
        foreach (BlockPos pos in ownedPositions.Values)
        {
            if (!supportedKeySet.Contains(ArchimedesWaterNetworkManager.PosKey(pos)))
            {
                toRelease.Add(pos);
            }
        }

        if (toRelease.Count == 0)
        {
            return 0;
        }
        ArchimedesPerf.AddCount("controller.drainUnsupported.candidates", toRelease.Count);

        List<BlockPos> origins = new(referenceSeeds.Count);
        foreach (ArchimedesOutletState seed in referenceSeeds)
        {
            origins.Add(seed.SeedPos.Copy());
        }

        if (origins.Count == 0)
        {
            origins.Add(lastSeedPos?.Copy() ?? Pos.Copy());
        }

        int perTick = Math.Max(1, waterConfig.MaxBlocksPerStep);
        toRelease.Sort((left, right) =>
        {
            int distCmp = MinDistanceSquared(right, origins).CompareTo(MinDistanceSquared(left, origins));
            if (distCmp != 0)
            {
                return distCmp;
            }

            return right.Y.CompareTo(left.Y);
        });
        int releaseCount = Math.Min(perTick, toRelease.Count);
        ArchimedesPerf.AddCount("controller.drainUnsupported.releaseCount", releaseCount);

        for (int i = 0; i < releaseCount; i++)
        {
            BlockPos pos = toRelease[i];
            ownedPositions.Remove(ArchimedesWaterNetworkManager.PosKey(pos));
            waterManager.ReleaseSourceOwner(ControllerId, pos);
        }

        UpdateSnapshot();
        Log(
            "Drain tick toward {0}: removedSources={1}, remainingSources={2}, reason={3}",
            origins[0],
            releaseCount,
            ownedPositions.Count,
            reason
        );

        return releaseCount;
    }

    private void UpdateSnapshot()
    {
        waterManager?.UpdateControllerSnapshot(ControllerId, Pos, ownedPositions.Values.ToList());
        MarkDirty();
    }

    private bool CanUseSeedPosition(BlockPos seedPos)
    {
        if (Api == null)
        {
            return false;
        }

        Block solidBlock = Api.World.BlockAccessor.GetBlock(seedPos);
        Block fluidBlock = Api.World.BlockAccessor.GetBlock(seedPos, BlockLayersAccess.Fluid);

        bool solidClear = solidBlock.Id == 0 || solidBlock.ForFluidsLayer || waterManager?.IsArchimedesWaterBlock(solidBlock) == true;
        bool fluidClear = fluidBlock.Id == 0 ||
                          waterManager?.IsArchimedesWaterBlock(fluidBlock) == true ||
                          (fluidBlock.IsLiquid() && ArchimedesWaterFamilies.TryResolveVanillaFamily(fluidBlock, out _));

        return solidClear && fluidClear;
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

    private static int MinDistanceSquared(BlockPos pos, IEnumerable<BlockPos> origins)
    {
        return origins.Min(origin => DistanceSquared(pos, origin));
    }

    private static int DistanceSquared(BlockPos a, BlockPos b)
    {
        int dx = a.X - b.X;
        int dy = a.Y - b.Y;
        int dz = a.Z - b.Z;
        return dx * dx + dy * dy + dz * dz;
    }

    private static int ManhattanDistance(BlockPos a, BlockPos b)
    {
        return Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y) + Math.Abs(a.Z - b.Z);
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

    private static BlockPos? DecodeSinglePos(int[]? values)
    {
        if (values == null || values.Length < 3)
        {
            return null;
        }

        return new BlockPos(values[0], values[1], values[2]);
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

    private readonly record struct ControllerEvaluation(
        bool IsController,
        bool IsPowered,
        float CurrentPower,
        string FailureReason,
        string? FamilyId,
        BlockPos? SeedPos
    );
}
