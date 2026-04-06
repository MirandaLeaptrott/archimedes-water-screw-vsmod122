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
    }
}
