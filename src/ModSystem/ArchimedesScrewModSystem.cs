using System;
using Newtonsoft.Json;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;

namespace ArchimedesScrew;

public sealed class ArchimedesScrewModSystem : ModSystem
{
    public const string LogPrefix = "[archimedes_screw]";
    public const string ModId = "archimedes_screw";
    public const string ScrewBlockCode = "water-archimedesscrew";

    /// <summary>
    /// Config asset patched at load time by Config Lib (from ModConfig YAML) when Config Lib is installed.
    /// </summary>
    public const string ConfigAssetPath = "config/settings.json";

    /// <summary>Fired by Config Lib after writing <c>ModConfig/{ModId}.yaml</c> (see vsmod_configlib).</summary>
    public static string ConfigLibSavedEventName => $"configlib:{ModId}:config-saved";
    public static string ConfigLibSettingChangedEventName => $"configlib:{ModId}:setting-changed";
    public static string ConfigLibReloadEventName => "configlib:config-reload";

    private ICoreAPI? api;
    private ICoreServerAPI? sapi;
    private EventBusListenerDelegate? configLibConfigSavedHandler;
    private EventBusListenerDelegate? configLibSettingChangedHandler;
    private ArchimedesScrewConfig.WaterConfig? pendingWaterConfig;
    private bool pendingRequiresCentralTickRestart;

    public ArchimedesScrewConfig Config { get; private set; } = new();

    public ArchimedesWaterNetworkManager? WaterManager { get; private set; }

    /// <summary>
    /// Runs after Config Lib (0.01) so Start/early hooks see consistent ordering when relevant.
    /// </summary>
    public override double ExecuteOrder() => 0.2;

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
        var asset = api.Assets.TryGet(new AssetLocation(ModId, ConfigAssetPath));
        if (asset == null)
        {
            api.Logger.Warning("{0} Missing config asset {1}, using defaults.", LogPrefix, ConfigAssetPath);
            Config = new ArchimedesScrewConfig();
            return;
        }

        try
        {
            Config = JsonConvert.DeserializeObject<ArchimedesScrewConfig>(asset.ToText()) ?? new ArchimedesScrewConfig();
            LogEffectiveConfig(api, Config);
            api.Logger.Notification(
                "{0} Loaded {1} (with Config Lib: edit ModConfig/{2}.yaml or in-game; without Config Lib: edit mod asset defaults only)",
                LogPrefix,
                ConfigAssetPath,
                ModId
            );
        }
        catch (JsonException ex)
        {
            api.Logger.Error("{0} Failed to parse config asset {1}: {2}", LogPrefix, ConfigAssetPath, ex);
            Config = new ArchimedesScrewConfig();
        }
    }

    private static void LogEffectiveConfig(ICoreAPI api, ArchimedesScrewConfig config)
    {
        ArchimedesScrewConfig.WaterConfig w = config.Water;
        api.Logger.Notification(
            "{0} Effective config: fastTickMs={1}, idleTickMs={2}, globalTickMs={3}, maxControllersPerGlobalTick={4}, assemblyAnalysisCacheMs={5}, maxBlocksPerStep={6}, maxScrewLength={7}, minNetworkSpeed={8}, maxVanillaConversionPasses={9}, enableRelaySources={10}, relayStrideBlocks={11}, maxRelayPromotionsPerTick={12}, maxRelaySourcesPerController={13}, requiredMechPowerForMaxRelay={14}, relayPowerHysteresisPct={15}, debugControllerStatsOnInteract={16}",
            LogPrefix,
            w.FastTickMs,
            w.IdleTickMs,
            w.GlobalTickMs,
            w.MaxControllersPerGlobalTick,
            w.AssemblyAnalysisCacheMs,
            w.MaxBlocksPerStep,
            w.MaxScrewLength,
            w.MinimumNetworkSpeed,
            w.MaxVanillaConversionPasses,
            w.EnableRelaySources,
            w.RelayStrideBlocks,
            w.MaxRelayPromotionsPerTick,
            w.MaxRelaySourcesPerController,
            w.RequiredMechPowerForMaxRelay,
            w.RelayPowerHysteresisPct,
            w.DebugControllerStatsOnInteract
        );
    }

    public override void StartServerSide(ICoreServerAPI api)
    {
        sapi = api;

        WaterManager = new ArchimedesWaterNetworkManager(api, Config);
        WaterManager.StartCentralWaterTick();
        api.Logger.Notification("{0} Server side initialized (central water tick)", LogPrefix);

        api.Event.SaveGameLoaded += OnSaveGameLoaded;
        api.Event.GameWorldSave += OnGameWorldSave;

        // vsmod_configlib pushes this after writing YAML; game API may not expose UnregisterEventBusListener on all builds.
        configLibConfigSavedHandler = OnConfigLibConfigSaved;
        api.Event.RegisterEventBusListener(configLibConfigSavedHandler, filterByEventName: ConfigLibSavedEventName);
        configLibSettingChangedHandler = OnConfigLibSettingChanged;
        api.Event.RegisterEventBusListener(configLibSettingChangedHandler, filterByEventName: ConfigLibSettingChangedEventName);

        RegisterCommands(api);
    }

    public override void Dispose()
    {
        if (sapi != null)
        {
            sapi.Event.SaveGameLoaded -= OnSaveGameLoaded;
            sapi.Event.GameWorldSave -= OnGameWorldSave;
        }

        WaterManager?.Dispose();
        WaterManager = null;
        base.Dispose();
    }

    private void OnSaveGameLoaded()
    {
        sapi?.Logger.Notification("{0} Save game loaded, restoring water manager state", LogPrefix);
        WaterManager?.Load();
        WaterManager?.BeginPostLoadReactivation();
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
            .EndSubCommand()
            .BeginSubCommand("perf")
                .WithDescription("Control Archimedes profiling logs (on/off/flush/status).")
                .BeginSubCommand("on")
                    .WithDescription("Enable periodic profiling logs.")
                    .HandleWith(_ =>
                    {
                        ArchimedesPerf.SetEnabled(true);
                        api.Logger.Notification("{0} Profiling enabled (interval={1}ms)", LogPrefix, ArchimedesPerf.FlushIntervalMs);
                        return TextCommandResult.Success($"Profiling enabled (interval={ArchimedesPerf.FlushIntervalMs}ms).");
                    })
                .EndSubCommand()
                .BeginSubCommand("off")
                    .WithDescription("Disable profiling logs.")
                    .HandleWith(_ =>
                    {
                        ArchimedesPerf.SetEnabled(false);
                        api.Logger.Notification("{0} Profiling disabled", LogPrefix);
                        return TextCommandResult.Success("Profiling disabled.");
                    })
                .EndSubCommand()
                .BeginSubCommand("flush")
                    .WithDescription("Immediately flush current profiling aggregates to the server log.")
                    .HandleWith(_ =>
                    {
                        if (!ArchimedesPerf.IsEnabled)
                        {
                            return TextCommandResult.Success("Profiling is disabled.");
                        }

                        ArchimedesPerf.FlushNow(api);
                        return TextCommandResult.Success("Profiling stats flushed to server log.");
                    })
                .EndSubCommand()
                .BeginSubCommand("status")
                    .WithDescription("Show profiler status and current interval.")
                    .HandleWith(_ =>
                    {
                        string state = ArchimedesPerf.IsEnabled ? "enabled" : "disabled";
                        return TextCommandResult.Success($"Profiling is {state} (interval={ArchimedesPerf.FlushIntervalMs}ms).");
                    })
                .EndSubCommand()
            .EndSubCommand();
    }

    private void OnConfigLibConfigSaved(string eventName, ref EnumHandling handling, IAttribute data)
    {
        if (sapi == null)
        {
            return;
        }

        if (pendingWaterConfig == null)
        {
            sapi.Logger.Notification("{0} Config Lib save detected; no pending runtime changes.", LogPrefix);
            return;
        }

        Config.Water.CopyValuesFrom(pendingWaterConfig);
        pendingWaterConfig = null;

        if (pendingRequiresCentralTickRestart)
        {
            WaterManager?.RestartCentralWaterTickForCurrentConfig();
            pendingRequiresCentralTickRestart = false;
        }

        sapi.Logger.Notification("{0} Applied pending Config Lib settings on save", LogPrefix);
        LogEffectiveConfig(sapi, Config);
    }

    private void OnConfigLibSettingChanged(string eventName, ref EnumHandling handling, IAttribute data)
    {
        if (sapi == null || data is not TreeAttribute tree)
        {
            return;
        }

        string settingCode = tree.GetAsString("setting");
        pendingWaterConfig ??= CloneWaterConfig(Config.Water);
        bool changed = TryApplySetting(pendingWaterConfig, settingCode, tree, out bool requiresCentralTickRestart);

        if (!changed)
        {
            return;
        }

        if (requiresCentralTickRestart)
        {
            pendingRequiresCentralTickRestart = true;
        }

        sapi.Logger.Notification("{0} Queued Config Lib setting (applies on save): {1}", LogPrefix, settingCode);
    }

    private static ArchimedesScrewConfig.WaterConfig CloneWaterConfig(ArchimedesScrewConfig.WaterConfig source)
    {
        var clone = new ArchimedesScrewConfig.WaterConfig();
        clone.CopyValuesFrom(source);
        return clone;
    }

    private static bool TryApplySetting(
        ArchimedesScrewConfig.WaterConfig target,
        string settingCode,
        TreeAttribute tree,
        out bool requiresCentralTickRestart)
    {
        requiresCentralTickRestart = false;
        switch (settingCode)
        {
            case "FAST_TICK_MS":
                target.FastTickMs = tree.GetInt("value");
                return true;
            case "IDLE_TICK_MS":
                target.IdleTickMs = tree.GetInt("value");
                return true;
            case "GLOBAL_TICK_MS":
                target.GlobalTickMs = tree.GetInt("value");
                requiresCentralTickRestart = true;
                return true;
            case "MAX_CONTROLLERS_PER_GLOBAL_TICK":
                target.MaxControllersPerGlobalTick = tree.GetInt("value");
                requiresCentralTickRestart = true;
                return true;
            case "ASSEMBLY_ANALYSIS_CACHE_MS":
                target.AssemblyAnalysisCacheMs = tree.GetInt("value");
                return true;
            case "MAX_BLOCKS_PER_STEP":
                target.MaxBlocksPerStep = tree.GetInt("value");
                return true;
            case "MAX_SCREW_LENGTH":
                target.MaxScrewLength = tree.GetInt("value");
                return true;
            case "MAX_VANILLA_CONVERSION_PASSES":
                target.MaxVanillaConversionPasses = tree.GetInt("value");
                return true;
            case "ENABLE_RELAY_SOURCES":
                target.EnableRelaySources = tree.GetBool("value");
                return true;
            case "RELAY_STRIDE_BLOCKS":
                target.RelayStrideBlocks = tree.GetInt("value");
                return true;
            case "MAX_RELAY_PROMOTIONS_PER_TICK":
                target.MaxRelayPromotionsPerTick = tree.GetInt("value");
                return true;
            case "MAX_RELAY_SOURCES_PER_CONTROLLER":
                target.MaxRelaySourcesPerController = tree.GetInt("value");
                return true;
            case "REQUIRED_MECH_POWER_FOR_MAX_RELAY":
                target.RequiredMechPowerForMaxRelay = tree.GetFloat("value");
                return true;
            case "RELAY_POWER_HYSTERESIS_PCT":
                target.RelayPowerHysteresisPct = tree.GetFloat("value");
                return true;
            case "MINIMUM_NETWORK_SPEED":
                target.MinimumNetworkSpeed = tree.GetFloat("value");
                return true;
            case "DEBUG_CONTROLLER_STATS_ON_INTERACT":
                target.DebugControllerStatsOnInteract = tree.GetBool("value");
                return true;
            default:
                return false;
        }
    }
}
