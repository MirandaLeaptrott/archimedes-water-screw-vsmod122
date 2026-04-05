namespace ArchimedesScrew;

public sealed class ArchimedesScrewConfig
{
    public WaterConfig Water { get; set; } = new();

    public sealed class WaterConfig
    {
        public int MaxRadius { get; set; } = 12;

        public int FastTickMs { get; set; } = 250;

        public int IdleTickMs { get; set; } = 2000;

        public int MaxBlocksPerStep { get; set; } = 16;

        public int MaxScrewLength { get; set; } = 32;

        public float MinimumNetworkSpeed { get; set; } = 0.001f;
    }
}
