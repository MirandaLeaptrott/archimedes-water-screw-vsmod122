using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Vintagestory.API.Common;

namespace ArchimedesScrew;

/// <summary>
/// Lightweight opt-in profiler for server hot paths.
/// Toggle <see cref="Enabled"/> in code when needed.
/// </summary>
public static class ArchimedesPerf
{
    // Set true while profiling.
    public static bool Enabled = false;

    public static int FlushIntervalMs = 5000;
    public static int MaxLoggedMetrics = 48;

    private static readonly object Sync = new();
    private static readonly Dictionary<string, Metric> Metrics = new(StringComparer.Ordinal);
    private static long nextFlushAtMs;

    public static bool IsEnabled => Enabled;

    public static void SetEnabled(bool enabled)
    {
        Enabled = enabled;
        lock (Sync)
        {
            Metrics.Clear();
            nextFlushAtMs = Environment.TickCount64 + Math.Max(1000, FlushIntervalMs);
        }
    }

    public static PerfScope Measure(string name)
    {
        if (!Enabled)
        {
            return default;
        }

        return new PerfScope(name, Stopwatch.GetTimestamp(), true);
    }

    public static void AddCount(string name, long value = 1)
    {
        if (!Enabled)
        {
            return;
        }

        lock (Sync)
        {
            Metrics.TryGetValue(name, out Metric metric);
            metric.Count += value;
            Metrics[name] = metric;
        }
    }

    public static void MaybeFlush(ICoreAPI? api)
    {
        if (!Enabled || api == null)
        {
            return;
        }

        long now = Environment.TickCount64;
        if (now < nextFlushAtMs)
        {
            return;
        }

        List<KeyValuePair<string, Metric>> snapshot;
        lock (Sync)
        {
            if (now < nextFlushAtMs)
            {
                return;
            }

            nextFlushAtMs = now + Math.Max(1000, FlushIntervalMs);
            if (Metrics.Count == 0)
            {
                return;
            }

            snapshot = Metrics.ToList();
            Metrics.Clear();
        }

        double tickToMs = 1000d / Stopwatch.Frequency;
        foreach ((string name, Metric metric) in snapshot
                     .OrderByDescending(pair => pair.Value.TotalTicks)
                     .Take(Math.Max(1, MaxLoggedMetrics)))
        {
            double totalMs = metric.TotalTicks * tickToMs;
            double avgMs = metric.Calls > 0 ? totalMs / metric.Calls : 0;
            double maxMs = metric.MaxTicks * tickToMs;
            ArchimedesScrewModSystem.LogVerboseOrNotification(api.Logger,
                "{0} [perf/{1}s] {2}: calls={3}, totalMs={4:0.###}, avgMs={5:0.###}, maxMs={6:0.###}, count={7}",
                ArchimedesScrewModSystem.LogPrefix,
                Math.Max(1, FlushIntervalMs / 1000),
                name,
                metric.Calls,
                totalMs,
                avgMs,
                maxMs,
                metric.Count
            );
        }

        long cacheHits = GetCount(snapshot, "water.collectConnectedManagedCached.hit");
        long cacheMisses = GetCount(snapshot, "water.collectConnectedManagedCached.miss");
        long cacheTotal = cacheHits + cacheMisses;
        if (cacheTotal > 0)
        {
            double hitRate = 100.0 * cacheHits / cacheTotal;
            ArchimedesScrewModSystem.LogVerboseOrNotification(api.Logger,
                "{0} [perf/{1}s] water.collectConnectedManagedCached.hitRate: hits={2}, misses={3}, hitRate={4:0.##}%",
                ArchimedesScrewModSystem.LogPrefix,
                Math.Max(1, FlushIntervalMs / 1000),
                cacheHits,
                cacheMisses,
                hitRate
            );
        }
    }

    public static void FlushNow(ICoreAPI? api)
    {
        if (!Enabled || api == null)
        {
            return;
        }

        nextFlushAtMs = Environment.TickCount64;
        MaybeFlush(api);
    }

    internal static void EndMeasure(string name, long elapsedTicks)
    {
        if (!Enabled)
        {
            return;
        }

        lock (Sync)
        {
            Metrics.TryGetValue(name, out Metric metric);
            metric.Calls++;
            metric.TotalTicks += elapsedTicks;
            if (elapsedTicks > metric.MaxTicks)
            {
                metric.MaxTicks = elapsedTicks;
            }
            Metrics[name] = metric;
        }
    }

    public readonly struct PerfScope : IDisposable
    {
        private readonly string? name;
        private readonly long startTicks;
        private readonly bool active;

        internal PerfScope(string name, long startTicks, bool active)
        {
            this.name = name;
            this.startTicks = startTicks;
            this.active = active;
        }

        public void Dispose()
        {
            if (!active || name == null)
            {
                return;
            }

            long elapsed = Stopwatch.GetTimestamp() - startTicks;
            EndMeasure(name, elapsed);
        }
    }

    private struct Metric
    {
        public long Calls;
        public long TotalTicks;
        public long MaxTicks;
        public long Count;
    }

    private static long GetCount(List<KeyValuePair<string, Metric>> snapshot, string key)
    {
        foreach ((string name, Metric metric) in snapshot)
        {
            if (string.Equals(name, key, StringComparison.Ordinal))
            {
                return metric.Count;
            }
        }

        return 0;
    }
}
