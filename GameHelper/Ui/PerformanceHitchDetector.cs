// <copyright file="PerformanceHitchDetector.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace GameHelper.Ui;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Coroutine;

/// <summary>
///     Lightweight hitch detector that runs independently from the full profiler.
/// </summary>
internal static class PerformanceHitchDetector
{
    private const int MaxRows = 12;
    private const int WindowSize = 180;
    private static readonly object Sync = new();
    private static readonly Dictionary<string, TimedSectionStats> Sections = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, CoroutineStats> Coroutines = new(StringComparer.Ordinal);

    private static long frameStartTicks;
    private static double lastFrameMs;
    private static double worstFrameMs;
    private static string lastHitchSummary = "No hitches recorded";
    private static DateTime lastHitchTime = DateTime.MinValue;
    private static DateTime lastMonitorWriteTime = DateTime.MinValue;

    public static void StartFrame()
    {
        if (!Core.GHSettings.ShowHitchDetector)
        {
            return;
        }

        frameStartTicks = Stopwatch.GetTimestamp();
    }

    public static void EndFrame(IReadOnlyList<ActiveCoroutine> coroutines)
    {
        if (!Core.GHSettings.ShowHitchDetector || frameStartTicks == 0)
        {
            return;
        }

        var elapsedMs = TicksToMs(Stopwatch.GetTimestamp() - frameStartTicks);
        TimedSectionStats[] sectionSnapshot;
        lock (Sync)
        {
            lastFrameMs = elapsedMs;
            worstFrameMs = Math.Max(worstFrameMs, elapsedMs);
            sectionSnapshot = Sections.Values.ToArray();
        }

        ObserveCoroutines(coroutines);

        if (elapsedMs >= Core.GHSettings.HitchDetectorThresholdMs)
        {
            var slowestPhase = sectionSnapshot
                .Where(IsAggregateSection)
                .OrderByDescending(x => x.LastMs)
                .FirstOrDefault();
            var slowestDetail = sectionSnapshot
                .Where(x => !IsAggregateSection(x) && !IsIgnoredSection(x))
                .OrderByDescending(x => x.LastMs)
                .FirstOrDefault();
            var sectionText = FormatHitchSection(slowestPhase, slowestDetail);
            lock (Sync)
            {
                lastHitchTime = DateTime.Now;
                lastHitchSummary = $"Frame {elapsedMs:0.00} ms, slowest: {sectionText}";
            }
        }

        WriteMonitorFileIfNeeded();
    }

    public static void RecordSection(string name, long elapsedTicks)
    {
        if (!Core.GHSettings.ShowHitchDetector)
        {
            return;
        }

        RecordSectionMs(name, TicksToMs(elapsedTicks));
    }

    public static void RecordSectionMs(string name, double elapsedMs)
    {
        if (!Core.GHSettings.ShowHitchDetector)
        {
            return;
        }

        lock (Sync)
        {
            if (!Sections.TryGetValue(name, out var stats))
            {
                stats = new TimedSectionStats(name);
                Sections[name] = stats;
            }

            stats.Add(elapsedMs);
        }
    }

    public static HitchSnapshot GetSnapshot()
    {
        lock (Sync)
        {
            return new HitchSnapshot(
                lastFrameMs,
                worstFrameMs,
                lastHitchSummary,
                lastHitchTime,
                Sections.Values
                    .Where(x => !IsIgnoredSection(x))
                    .OrderByDescending(x => x.WorstMs)
                    .Take(MaxRows)
                    .Select(x => new HitchRow(x.Name, x.LastMs, x.AverageMs, x.WorstMs))
                    .ToArray(),
                Coroutines.Values
                    .OrderByDescending(x => x.WorstMs)
                    .Take(MaxRows)
                    .Select(x => new HitchRow(x.Name, x.LastMs, x.AverageMs, x.WorstMs))
                    .ToArray());
        }
    }

    public static void Reset()
    {
        lock (Sync)
        {
            Sections.Clear();
            Coroutines.Clear();
            lastFrameMs = 0;
            worstFrameMs = 0;
            lastHitchSummary = "No hitches recorded";
            lastHitchTime = DateTime.MinValue;
        }
    }

    private static void ObserveCoroutines(IReadOnlyList<ActiveCoroutine> coroutines)
    {
        lock (Sync)
        {
            for (var i = 0; i < coroutines.Count; i++)
            {
                var coroutine = coroutines[i];
                if (coroutine.IsFinished)
                {
                    continue;
                }

                if (!Coroutines.TryGetValue(coroutine.Name, out var stats))
                {
                    stats = new CoroutineStats(coroutine.Name);
                    Coroutines[coroutine.Name] = stats;
                }

                if (stats.LastMoveNextCount == coroutine.MoveNextCount)
                {
                    continue;
                }

                stats.LastMoveNextCount = coroutine.MoveNextCount;
                stats.Add(coroutine.LastMoveNextTime.TotalMilliseconds);
            }
        }
    }

    private static void WriteMonitorFileIfNeeded()
    {
        var now = DateTime.Now;
        if ((now - lastMonitorWriteTime).TotalSeconds < 1)
        {
            return;
        }

        lastMonitorWriteTime = now;

        try
        {
            var snapshot = GetSnapshot();
            var payload = new HitchMonitorEntry(
                now,
                snapshot.LastFrameMs,
                snapshot.WorstFrameMs,
                Core.GHSettings.HitchDetectorThresholdMs,
                snapshot.LastHitchSummary,
                snapshot.LastHitchTime == default ? null : snapshot.LastHitchTime,
                snapshot.SlowestSections,
                snapshot.SlowestCoroutines);

            File.AppendAllText(
                "hitch_monitor.jsonl",
                JsonSerializer.Serialize(payload, HitchMonitorJson.Options) + Environment.NewLine);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PerformanceHitchDetector.WriteMonitorFileIfNeeded] {ex.Message}");
        }
    }

    private static double TicksToMs(long ticks)
    {
        return ticks * 1000.0 / Stopwatch.Frequency;
    }

    private static bool IsAggregateSection(TimedSectionStats stats)
    {
        return stats.Name is "Coroutine Tick" or
            "PerFrameDataUpdate" or
            "PostPerFrameDataUpdate" or
            "OnRender" or
            "OnPostRender";
    }

    private static bool IsIgnoredSection(TimedSectionStats stats)
    {
        return stats.Name == "AreaInstance: Area changed rebuild" ||
            stats.Name.StartsWith("AreaInstance rebuild:", StringComparison.Ordinal);
    }

    private static string FormatHitchSection(TimedSectionStats? phase, TimedSectionStats? detail)
    {
        if (detail == null)
        {
            return phase == null ? "unknown section" : $"{phase.Name} {phase.LastMs:0.00} ms";
        }

        if (phase == null)
        {
            return $"{detail.Name} {detail.LastMs:0.00} ms";
        }

        return $"{detail.Name} {detail.LastMs:0.00} ms ({phase.Name} {phase.LastMs:0.00} ms)";
    }

    private class TimedSectionStats(string name)
    {
        private readonly Queue<double> samples = new(WindowSize);
        private double sum;

        public string Name { get; } = name;

        public double LastMs { get; private set; }

        public double AverageMs => this.samples.Count == 0 ? 0 : this.sum / this.samples.Count;

        public double WorstMs { get; private set; }

        public void Add(double value)
        {
            this.LastMs = value;
            this.WorstMs = Math.Max(this.WorstMs, value);
            this.samples.Enqueue(value);
            this.sum += value;
            if (this.samples.Count > WindowSize)
            {
                this.sum -= this.samples.Dequeue();
            }
        }
    }

    private sealed class CoroutineStats(string name) : TimedSectionStats(name)
    {
        public int LastMoveNextCount { get; set; }
    }
}

internal readonly record struct HitchSnapshot(
    double LastFrameMs,
    double WorstFrameMs,
    string LastHitchSummary,
    DateTime LastHitchTime,
    HitchRow[] SlowestSections,
    HitchRow[] SlowestCoroutines);

internal readonly record struct HitchRow(string Name, double LastMs, double AverageMs, double WorstMs);

internal readonly record struct HitchMonitorEntry(
    DateTime Time,
    double LastFrameMs,
    double WorstFrameMs,
    int ThresholdMs,
    string LastHitchSummary,
    DateTime? LastHitchTime,
    HitchRow[] SlowestSections,
    HitchRow[] SlowestCoroutines);

internal static class HitchMonitorJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
