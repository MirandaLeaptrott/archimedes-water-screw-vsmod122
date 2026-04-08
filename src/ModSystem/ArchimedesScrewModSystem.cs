using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Vintagestory.API.Client;
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
    private ICoreClientAPI? capi;
    private ICoreServerAPI? sapi;
    private IServerNetworkChannel? serverChannel;
    private IClientNetworkChannel? clientChannel;
    private EventBusListenerDelegate? configLibConfigSavedHandler;
    private EventBusListenerDelegate? configLibSettingChangedHandler;
    private ArchimedesScrewConfig.WaterConfig? pendingWaterConfig;
    private bool pendingRequiresCentralTickRestart;
    private WaterfallCompatBridge? waterfallCompatBridge;
    private ArchimedesWaterDebugOverlay? waterDebugOverlay;
    private long waterDebugTickListenerId;
    private bool waterDebugEnabled;

    private const string NetworkChannelName = ModId;
    private const int WaterDebugRadius = 32;
    public static bool VerboseDebugEnabled { get; private set; }

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
            VerboseDebugEnabled = Config.Water.VerboseDebug;
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
            "{0} Effective config: fastTickMs={1}, idleTickMs={2}, globalTickMs={3}, maxControllersPerGlobalTick={4}, assemblyAnalysisCacheMs={5}, maxBlocksPerStep={6}, maxScrewLength={7}, minNetworkSpeed={8}, maxVanillaConversionPasses={9}, enableRelaySources={10}, relayStrideBlocks={11}, maxRelayPromotionsPerTick={12}, maxRelaySourcesPerController={13}, requiredMechPowerForMaxRelay={14}, relayPowerHysteresisPct={15}, debugControllerStatsOnInteract={16}, enableWaterfallCompat={17}, waterfallCompatDebug={18}, verboseDebug={19}",
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
            w.DebugControllerStatsOnInteract,
            w.EnableWaterfallCompat,
            w.WaterfallCompatDebug,
            w.VerboseDebug
        );
    }

    public static void LogVerbose(ILogger? logger, string message, params object?[] args)
    {
        if (logger == null || !VerboseDebugEnabled)
        {
            return;
        }

        logger.Event("[VerboseDebug] " + message, args);
    }

    public static void LogVerboseOrNotification(ILogger? logger, string message, params object?[] args)
    {
        if (logger == null)
        {
            return;
        }

        if (VerboseDebugEnabled)
        {
            logger.Event("[VerboseDebug] " + message, args);
            return;
        }

        logger.Notification(message, args);
    }

    public override void StartServerSide(ICoreServerAPI api)
    {
        sapi = api;
        serverChannel = api.Network
            .RegisterChannel(NetworkChannelName)
            .RegisterMessageType<ArchimedesWaterDebugSnapshotPacket>();

        WaterManager = new ArchimedesWaterNetworkManager(api, Config);
        WaterManager.StartCentralWaterTick();
        waterfallCompatBridge = new WaterfallCompatBridge(api);
        waterfallCompatBridge.RefreshForConfig(Config.Water);
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

    public override void StartClientSide(ICoreClientAPI api)
    {
        capi = api;
        waterDebugOverlay = new ArchimedesWaterDebugOverlay(api);
        clientChannel = api.Network
            .RegisterChannel(NetworkChannelName)
            .RegisterMessageType<ArchimedesWaterDebugSnapshotPacket>();
        clientChannel.SetMessageHandler<ArchimedesWaterDebugSnapshotPacket>(packet =>
        {
            waterDebugOverlay?.ApplySnapshot(packet);
        });
    }

    public override void Dispose()
    {
        if (sapi != null)
        {
            sapi.Event.SaveGameLoaded -= OnSaveGameLoaded;
            sapi.Event.GameWorldSave -= OnGameWorldSave;
        }

        WaterManager?.Dispose();
        waterfallCompatBridge?.Dispose();
        if (sapi != null && waterDebugTickListenerId != 0)
        {
            sapi.Event.UnregisterGameTickListener(waterDebugTickListenerId);
            waterDebugTickListenerId = 0;
        }
        waterfallCompatBridge = null;
        WaterManager = null;
        base.Dispose();
    }

    private void OnSaveGameLoaded()
    {
        sapi?.Logger.Notification("{0} SaveGameLoaded: loading mod water state (then re-merge chunk ownership if controllers initialized early)", LogPrefix);
        WaterManager?.Load();
        WaterManager?.ReapplyOwnershipFromLoadedControllers();
        WaterManager?.BeginPostLoadReactivation();
        if (sapi != null)
        {
            // Retry compat resolution after world/mod systems are fully active.
            for (int i = 1; i <= 6; i++)
            {
                int delayMs = 250 * i;
                sapi.Event.RegisterCallback(_ => waterfallCompatBridge?.RefreshForConfig(Config.Water), delayMs);
            }
        }
    }

    private void OnGameWorldSave()
    {
        if (WaterManager != null)
        {
            sapi?.Logger.Notification(
                "{0} GameWorldSave: persisting water manager (weak controller refs={1}; chunk BE data saves with map chunks)",
                LogPrefix,
                WaterManager.LoadedControllerWeakReferenceCount);
        }
        else
        {
            sapi?.Logger.Notification("{0} GameWorldSave: water manager unavailable", LogPrefix);
        }

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
            .EndSubCommand()
            .BeginSubCommand("debugwater")
                .WithDescription("Visualize managed source ownership (green=consistent owned, orange=owned inconsistent, red=unowned).")
                .BeginSubCommand("on")
                    .WithDescription("Enable periodic water ownership overlay for all connected clients.")
                    .HandleWith(_ =>
                    {
                        waterDebugEnabled = true;
                        EnsureWaterDebugTickListener(api);
                        SendWaterDebugSnapshotToAllPlayers();
                        return TextCommandResult.Success("Water debug overlay enabled (green=consistent owned, orange=owned inconsistent, red=unowned).");
                    })
                .EndSubCommand()
                .BeginSubCommand("off")
                    .WithDescription("Disable ownership overlay and clear highlights.")
                    .HandleWith(_ =>
                    {
                        waterDebugEnabled = false;
                        if (waterDebugTickListenerId != 0)
                        {
                            api.Event.UnregisterGameTickListener(waterDebugTickListenerId);
                            waterDebugTickListenerId = 0;
                        }

                        if (sapi != null && serverChannel != null)
                        {
                            var clearPacket = new ArchimedesWaterDebugSnapshotPacket { Enabled = false };
                            foreach (IPlayer onlinePlayer in sapi.World.AllOnlinePlayers)
                            {
                                if (onlinePlayer is IServerPlayer serverPlayer)
                                {
                                    serverChannel.SendPacket(clearPacket, serverPlayer);
                                }
                            }
                        }
                        return TextCommandResult.Success("Water debug overlay disabled.");
                    })
                .EndSubCommand()
                .BeginSubCommand("scan")
                    .WithDescription("Print nearby managed sources and ownership in chat.")
                    .HandleWith(args =>
                    {
                        if (WaterManager == null || args.Caller.Player is not IServerPlayer player)
                        {
                            return TextCommandResult.Success("No active water manager or player context.");
                        }

                        IReadOnlyList<ManagedSourceDebugInfo> sources =
                            WaterManager.CollectManagedSourceDebug(player.Entity.Pos.AsBlockPos, WaterDebugRadius);
                        int owned = sources.Count(s => s.IsOwned);
                        int unowned = sources.Count - owned;
                        int inconsistentOwned = sources.Count(s => s.IsOwned && !s.IsOwnershipConsistent);
                        player.SendMessage(
                            GlobalConstants.InfoLogChatGroup,
                            $"Managed sources nearby (r={WaterDebugRadius}): total={sources.Count}, owned={owned}, ownedInconsistent={inconsistentOwned}, unowned={unowned}",
                            EnumChatType.Notification
                        );

                        foreach (ManagedSourceDebugInfo source in sources.Take(24))
                        {
                            string state = source.IsOwned
                                ? (source.IsOwnershipConsistent
                                    ? $"owned by {source.OwnerId} (consistent)"
                                    : $"owned by {source.OwnerId} (INCONSISTENT snapshot={source.OwnerSnapshotContainsPos}, loaded={source.OwnerControllerLoaded}, beTracks={source.OwnerLoadedControllerTracksPos})")
                                : "UNOWNED";
                            player.SendMessage(
                                GlobalConstants.InfoLogChatGroup,
                                $"{source.Pos}: {state}",
                                EnumChatType.Notification
                            );
                        }

                        if (sources.Count > 24)
                        {
                            player.SendMessage(
                                GlobalConstants.InfoLogChatGroup,
                                $"...and {sources.Count - 24} more.",
                                EnumChatType.Notification
                            );
                        }

                        return TextCommandResult.Success("Water ownership scan complete.");
                    })
                .EndSubCommand()
            .EndSubCommand();
    }

    private void EnsureWaterDebugTickListener(ICoreServerAPI api)
    {
        if (waterDebugTickListenerId != 0)
        {
            return;
        }

        waterDebugTickListenerId = api.Event.RegisterGameTickListener(_ => SendWaterDebugSnapshotToAllPlayers(), 500);
    }

    private void SendWaterDebugSnapshotToAllPlayers()
    {
        if (!waterDebugEnabled || sapi == null || WaterManager == null || serverChannel == null)
        {
            return;
        }

        foreach (IPlayer onlinePlayer in sapi.World.AllOnlinePlayers)
        {
            if (onlinePlayer is not IServerPlayer serverPlayer || serverPlayer.Entity == null)
            {
                continue;
            }

            IReadOnlyList<ManagedSourceDebugInfo> sources =
                WaterManager.CollectManagedSourceDebug(serverPlayer.Entity.Pos.AsBlockPos, WaterDebugRadius);
            var packet = new ArchimedesWaterDebugSnapshotPacket
            {
                Enabled = true
            };

            foreach (ManagedSourceDebugInfo source in sources)
            {
                packet.Sources.Add(new ArchimedesWaterDebugSourcePacket
                {
                    X = source.Pos.X,
                    Y = source.Pos.Y,
                    Z = source.Pos.Z,
                    IsOwned = source.IsOwned,
                    OwnerId = source.OwnerId,
                    IsOwnershipConsistent = source.IsOwnershipConsistent
                });
            }

            serverChannel.SendPacket(packet, serverPlayer);
        }
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
        VerboseDebugEnabled = Config.Water.VerboseDebug;
        pendingWaterConfig = null;

        if (pendingRequiresCentralTickRestart)
        {
            WaterManager?.RestartCentralWaterTickForCurrentConfig();
            pendingRequiresCentralTickRestart = false;
        }

        waterfallCompatBridge?.RefreshForConfig(Config.Water);

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
            case "ENABLE_WATERFALL_COMPAT":
                target.EnableWaterfallCompat = tree.GetBool("value");
                return true;
            case "WATERFALL_COMPAT_DEBUG":
                target.WaterfallCompatDebug = tree.GetBool("value");
                return true;
            case "VERBOSE_DEBUG":
                target.VerboseDebug = tree.GetBool("value");
                return true;
            default:
                return false;
        }
    }
}
