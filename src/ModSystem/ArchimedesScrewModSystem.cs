using Newtonsoft.Json;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace ArchimedesScrew;

public sealed class ArchimedesScrewModSystem : ModSystem
{
    public const string LogPrefix = "[archimedes_screw]";
    public const string ModId = "archimedes_screw";
    public const string ScrewBlockCode = "water-archimedesscrew";
    public const string OutletBlockCode = "water-archimedesscrew-outlet";
    public const string ManagedWaterCode = "archimedes-water";

    private ICoreAPI? api;
    private ICoreServerAPI? sapi;

    public ArchimedesScrewConfig Config { get; private set; } = new();

    public ArchimedesWaterNetworkManager? WaterManager { get; private set; }

    public override void Start(ICoreAPI api)
    {
        this.api = api;

        api.RegisterBlockClass(nameof(BlockWaterArchimedesScrew), typeof(BlockWaterArchimedesScrew));
        api.RegisterBlockClass(nameof(BlockArchimedesWaterStill), typeof(BlockArchimedesWaterStill));
        api.RegisterBlockClass(nameof(BlockArchimedesWaterFlowing), typeof(BlockArchimedesWaterFlowing));
        api.RegisterBlockClass(nameof(BlockArchimedesWaterfall), typeof(BlockArchimedesWaterfall));
        api.RegisterBlockEntityClass(nameof(BlockEntityWaterArchimedesScrew), typeof(BlockEntityWaterArchimedesScrew));
    }

    public override void AssetsFinalize(ICoreAPI api)
    {
        var asset = api.Assets.TryGet(new AssetLocation(ModId, "config/settings.json"));
        if (asset == null)
        {
            api.Logger.Warning("[archimedes_screw] Missing config asset, using defaults.");
            Config = new ArchimedesScrewConfig();
            return;
        }

        try
        {
            Config = JsonConvert.DeserializeObject<ArchimedesScrewConfig>(asset.ToText()) ?? new ArchimedesScrewConfig();
            api.Logger.Notification(
                "{0} Loaded config: radius={1}, fastTickMs={2}, idleTickMs={3}, maxBlocksPerStep={4}, maxScrewLength={5}, minNetworkSpeed={6}",
                LogPrefix,
                Config.Water.MaxRadius,
                Config.Water.FastTickMs,
                Config.Water.IdleTickMs,
                Config.Water.MaxBlocksPerStep,
                Config.Water.MaxScrewLength,
                Config.Water.MinimumNetworkSpeed
            );
        }
        catch (JsonException ex)
        {
            api.Logger.Error("[archimedes_screw] Failed to parse config asset: {0}", ex);
            Config = new ArchimedesScrewConfig();
        }
    }

    public override void StartServerSide(ICoreServerAPI api)
    {
        sapi = api;
        WaterManager = new ArchimedesWaterNetworkManager(api, Config);
        api.Logger.Notification("{0} Server side initialized", LogPrefix);

        api.Event.SaveGameLoaded += OnSaveGameLoaded;
        api.Event.GameWorldSave += OnGameWorldSave;

        RegisterCommands(api);
    }

    private void OnSaveGameLoaded()
    {
        sapi?.Logger.Notification("{0} Save game loaded, restoring water manager state", LogPrefix);
        WaterManager?.Load();
    }

    private void OnGameWorldSave()
    {
        sapi?.Logger.Notification("{0} Saving water manager state", LogPrefix);
        WaterManager?.Save();
    }

    private void RegisterCommands(ICoreServerAPI api)
    {
        api.ChatCommands
            .Create("archscrew")
            .WithDescription("Administrative commands for the Archimedes Screw mod.")
            .RequiresPrivilege(Privilege.controlserver)
            .BeginSubCommand("purge")
                .WithDescription("Remove all Archimedes screw blocks and managed water.")
                .HandleWith(_ =>
                {
                    int removed = WaterManager?.PurgeAll() ?? 0;
                    api.Logger.Notification("{0} Command purge removed {1} mod blocks", LogPrefix, removed);
                    return TextCommandResult.Success($"Removed {removed} mod blocks.");
                })
            .EndSubCommand()
            .BeginSubCommand("purgewater")
                .WithDescription("Remove all managed Archimedes water blocks.")
                .HandleWith(_ =>
                {
                    int removed = WaterManager?.PurgeManagedWater() ?? 0;
                    api.Logger.Notification("{0} Command purgewater removed {1} managed water blocks", LogPrefix, removed);
                    return TextCommandResult.Success($"Removed {removed} managed water blocks.");
                })
            .EndSubCommand()
            .BeginSubCommand("purgescrews")
                .WithDescription("Remove all Archimedes screw blocks.")
                .HandleWith(_ =>
                {
                    int removed = WaterManager?.PurgeScrewsOnly() ?? 0;
                    api.Logger.Notification("{0} Command purgescrews removed {1} screw blocks", LogPrefix, removed);
                    return TextCommandResult.Success($"Removed {removed} screw blocks.");
                })
            .EndSubCommand();
    }
}
