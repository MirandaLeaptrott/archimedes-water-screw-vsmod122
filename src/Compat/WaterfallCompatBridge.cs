using System;
using HarmonyLib;
using Vintagestory.API.Server;

namespace ArchimedesScrew;

internal sealed class WaterfallCompatBridge : IDisposable
{
    private const string WaterfallModId = "waterfall";
    private readonly ICoreServerAPI api;
    private readonly Harmony harmony;
    private bool isPatched;

    public WaterfallCompatBridge(ICoreServerAPI api)
    {
        this.api = api;
        harmony = new Harmony($"{ArchimedesScrewModSystem.ModId}.compat.waterfall");
    }

    public void RefreshForConfig(ArchimedesScrewConfig.WaterConfig waterConfig)
    {
        WaterfallCompatPatch.Api = api;
        WaterfallCompatPatch.DebugLoggingEnabled = waterConfig.WaterfallCompatDebug;

        if (!waterConfig.EnableWaterfallCompat)
        {
            Unpatch();
            ArchimedesScrewModSystem.LogVerboseOrNotification(api.Logger, "{0} [compat/waterfall] Compatibility disabled by config", ArchimedesScrewModSystem.LogPrefix);
            return;
        }

        if (!api.ModLoader.IsModEnabled(WaterfallModId))
        {
            Unpatch();
            ArchimedesScrewModSystem.LogVerboseOrNotification(api.Logger, "{0} [compat/waterfall] Mod not installed; compat inactive", ArchimedesScrewModSystem.LogPrefix);
            return;
        }

        if (isPatched)
        {
            return;
        }

        harmony.PatchAll(typeof(WaterfallCompatPatch).Assembly);
        isPatched = true;
        ArchimedesScrewModSystem.LogVerboseOrNotification(api.Logger, "{0} [compat/waterfall] Compat patch set active", ArchimedesScrewModSystem.LogPrefix);
    }

    public void Dispose()
    {
        Unpatch();
    }

    private void Unpatch()
    {
        if (!isPatched)
        {
            return;
        }

        harmony.UnpatchAll(harmony.Id);
        isPatched = false;
        ArchimedesScrewModSystem.LogVerboseOrNotification(api.Logger, "{0} [compat/waterfall] Compat unpatched", ArchimedesScrewModSystem.LogPrefix);
    }
}
