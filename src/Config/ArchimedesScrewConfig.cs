namespace ArchimedesScrew;

public sealed class ArchimedesScrewConfig
{
    public WaterConfig Water { get; set; } = new();

    public sealed class WaterConfig
    {
        public int FastTickMs { get; set; } = 250;

        public int IdleTickMs { get; set; } = 2000;

        /// <summary>
        /// Server-wide game tick interval (ms) that dispatches intake controller work.
        /// </summary>
        public int GlobalTickMs { get; set; } = 50;

        /// <summary>
        /// Max intake controllers processed per global tick (round-robin when many are due).
        /// </summary>
        public int MaxControllersPerGlobalTick { get; set; } = 48;

        /// <summary>
        /// Reuse assembly analysis within this window (ms) to avoid duplicate scans.
        /// </summary>
        public int AssemblyAnalysisCacheMs { get; set; } = 120;

        /// <summary>
        /// How many owned Archimedes sources to remove per fast tick when draining (farthest from reference first).
        /// </summary>
        public int MaxBlocksPerStep { get; set; } = 1;

        public int MaxScrewLength { get; set; } = 32;

        public float MinimumNetworkSpeed { get; set; } = 0.001f;

        /// <summary>
        /// Max rounds of vanilla-source conversion per controller tick (each round expands the managed-water BFS).
        /// </summary>
        public int MaxVanillaConversionPasses { get; set; } = 32;

        /// <summary>
        /// Enables/disables relay source creation for long-distance aqueduct support.
        /// </summary>
        public bool EnableRelaySources { get; set; } = true;

        /// <summary>
        /// Desired spacing in blocks between relay source opportunities along connected flow.
        /// </summary>
        public int RelayStrideBlocks { get; set; } = 6;

        /// <summary>
        /// Max relay source promotions (and trims) per controller tick.
        /// </summary>
        public int MaxRelayPromotionsPerTick { get; set; } = 1;

        /// <summary>
        /// Absolute max relay-created sources one controller may own at full power.
        /// </summary>
        public int MaxRelaySourcesPerController { get; set; } = 12;

        /// <summary>
        /// Mechanical power needed to unlock full relay cap.
        /// </summary>
        public float RequiredMechPowerForMaxRelay { get; set; } = 0.5f;

        /// <summary>
        /// Fractional hysteresis around relay-cap transitions to avoid add/remove thrash.
        /// </summary>
        public float RelayPowerHysteresisPct { get; set; } = 0.05f;

        /// <summary>
        /// Enables verbose per-controller diagnostics on right-click status checks.
        /// </summary>
        public bool DebugControllerStatsOnInteract { get; set; } = false;

        /// <summary>
        /// Enables optional compatibility hooks for the Waterfall mod when installed.
        /// </summary>
        public bool EnableWaterfallCompat { get; set; } = true;

        /// <summary>
        /// Emits verbose logging for Waterfall compatibility hook decisions.
        /// </summary>
        public bool WaterfallCompatDebug { get; set; } = false;

        /// <summary>
        /// Routes non-essential mod diagnostics to verbose debug log entries.
        /// </summary>
        public bool VerboseDebug { get; set; } = false;

        /// <summary>
        /// Copies tunable fields onto this instance so existing references (e.g. block entities) stay valid.
        /// </summary>
        public void CopyValuesFrom(WaterConfig source)
        {
            FastTickMs = source.FastTickMs;
            IdleTickMs = source.IdleTickMs;
            GlobalTickMs = source.GlobalTickMs;
            MaxControllersPerGlobalTick = source.MaxControllersPerGlobalTick;
            AssemblyAnalysisCacheMs = source.AssemblyAnalysisCacheMs;
            MaxBlocksPerStep = source.MaxBlocksPerStep;
            MaxScrewLength = source.MaxScrewLength;
            MinimumNetworkSpeed = source.MinimumNetworkSpeed;
            MaxVanillaConversionPasses = source.MaxVanillaConversionPasses;
            EnableRelaySources = source.EnableRelaySources;
            RelayStrideBlocks = source.RelayStrideBlocks;
            MaxRelayPromotionsPerTick = source.MaxRelayPromotionsPerTick;
            MaxRelaySourcesPerController = source.MaxRelaySourcesPerController;
            RequiredMechPowerForMaxRelay = source.RequiredMechPowerForMaxRelay;
            RelayPowerHysteresisPct = source.RelayPowerHysteresisPct;
            DebugControllerStatsOnInteract = source.DebugControllerStatsOnInteract;
            EnableWaterfallCompat = source.EnableWaterfallCompat;
            WaterfallCompatDebug = source.WaterfallCompatDebug;
            VerboseDebug = source.VerboseDebug;
        }
    }
}
