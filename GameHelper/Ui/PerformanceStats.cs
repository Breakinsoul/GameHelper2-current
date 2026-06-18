// <copyright file="PerformanceStats.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace GameHelper.Ui
{
    using System.Collections.Generic;
    using System.Numerics;
    using Coroutine;
    using CoroutineEvents;
    using ImGuiNET;
    using Utils;

    /// <summary>
    ///     Visualize the co-routines stats.
    /// </summary>
    public static class PerformanceStats
    {
        private static readonly Dictionary<string, MovingAverage> MovingAverageValue = new();

        private static bool isPerformanceWindowHovered;

        /// <summary>
        ///     Initializes the co-routines.
        /// </summary>
        internal static void InitializeCoroutines()
        {
            CoroutineHandler.Start(PerformanceStatRenderCoRoutine());
        }

        /// <summary>
        ///     Draws the window to display the perf stats.
        /// </summary>
        /// <returns>co-routine IWait.</returns>
        private static IEnumerator<Wait> PerformanceStatRenderCoRoutine()
        {
            var pos = new Vector2(0, 100);
            while (true)
            {
                yield return new Wait(GameHelperEvents.OnRender);
                if (Core.GHSettings.ShowPerfStats)
                {
                    if (Core.GHSettings.HidePerfStatsWhenBg && !Core.Process.Foreground)
                    {
                        continue;
                    }

                    ImGui.SetNextWindowPos(pos);
                    if (isPerformanceWindowHovered)
                    {
                        ImGui.PushStyleColor(ImGuiCol.WindowBg, Vector4.Zero);
                        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
                    }

                    ImGui.Begin("Perf Stats Window", ImGuiHelper.TransparentWindowFlags);

                    if (isPerformanceWindowHovered)
                    {
                        ImGui.PopStyleVar();
                        ImGui.PopStyleColor();
                    }

                    var windowPos = ImGui.GetWindowPos();
                    isPerformanceWindowHovered = ImGui.IsMouseHoveringRect(windowPos, windowPos + ImGui.GetWindowSize());
                    if (isPerformanceWindowHovered)
                    {
                        ImGui.PushStyleColor(ImGuiCol.Text, Vector4.Zero);
                    }

                    if (!Core.GHSettings.MinimumPerfStats)
                    {
                        ImGui.Text("Performance Related Stats");
                        ImGui.Text($"Working Set Memory: {System.Environment.WorkingSet / (1024 * 1024)} (MB)");

                        ImGui.Text($"Total Event Coroutines: {CoroutineHandler.EventCount}");
                        ImGui.Text($"Total Tick Coroutines: {CoroutineHandler.TickingCount}");
                    }

                    var cAI = Core.States.InGameStateObject.CurrentAreaInstance;
                    ImGui.Text($"Total Entities: {cAI.AwakeEntities.Count}");
                    if (Core.GHSettings.DisableAllCounters == false)
                    {
                        ImGui.Text($"Awake Entities: {cAI.NetworkBubbleEntityCount}");
                        ImGui.Text($"Useless Awake:  {cAI.UselessAwakeEntities}");
                    }

                    var fps = ImGui.GetIO().Framerate;
                    ImGui.Text($"FPS: {fps}");

                    if (Core.GHSettings.ShowHitchDetector)
                    {
                        DrawHitchDetectorSummary();
                    }

                    if (!Core.GHSettings.MinimumPerfStats)
                    {
                        ImGui.NewLine();
                        ImGui.Text($"==Average of last {(int)(1440 / float.Max(fps, 1f))} seconds==");
                        for (var i = Core.CoroutinesRegistrar.Count - 1; i >= 0; i--)
                        {
                            var coroutine = Core.CoroutinesRegistrar[i];
                            if (coroutine.IsFinished)
                            {
                                Core.CoroutinesRegistrar.RemoveAt(i);
                                continue;
                            }

                            if (MovingAverageValue.TryGetValue(coroutine.Name, out var value))
                            {
                                value.ComputeAverage(
                                    coroutine.LastMoveNextTime.TotalMilliseconds,
                                    coroutine.MoveNextCount);
                                ImGui.Text($"{coroutine.Name}: {value.Average:0.00}(ms)");
                            }
                            else
                            {
                                MovingAverageValue[coroutine.Name] = new MovingAverage();
                            }
                        }
                    }

                    if (isPerformanceWindowHovered)
                    {
                        ImGui.PopStyleColor();
                    }

                    ImGui.End();
                }
            }
        }

        private static void DrawHitchDetectorSummary()
        {
            var snapshot = PerformanceHitchDetector.GetSnapshot();
            ImGui.Text($"Frame: {snapshot.LastFrameMs:0.00} ms | Worst: {snapshot.WorstFrameMs:0.00} ms | Threshold: {Core.GHSettings.HitchDetectorThresholdMs} ms");
            ImGui.TextWrapped($"Last hitch: {snapshot.LastHitchSummary}");
            if (!Core.GHSettings.MinimumPerfStats)
            {
                if (snapshot.LastHitchTime != default)
                {
                    ImGui.Text($"Last hitch time: {snapshot.LastHitchTime:T}");
                }

                DrawHitchRows("Slowest sections", snapshot.SlowestSections);
                DrawHitchRows("Slowest coroutines", snapshot.SlowestCoroutines);
            }
        }

        private static void DrawHitchRows(string title, HitchRow[] rows)
        {
            if (rows.Length == 0)
            {
                return;
            }

            ImGui.Text(title);
            if (ImGui.BeginTable(title, 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.SizingStretchProp))
            {
                ImGui.TableSetupColumn("Name");
                ImGui.TableSetupColumn("Last");
                ImGui.TableSetupColumn("Avg");
                ImGui.TableSetupColumn("Worst");
                ImGui.TableHeadersRow();
                foreach (var row in rows)
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.Text(row.Name);
                    ImGui.TableNextColumn();
                    ImGui.Text($"{row.LastMs:0.00}");
                    ImGui.TableNextColumn();
                    ImGui.Text($"{row.AverageMs:0.00}");
                    ImGui.TableNextColumn();
                    ImGui.Text($"{row.WorstMs:0.00}");
                }

                ImGui.EndTable();
            }
        }

        private class MovingAverage
        {
            private readonly Queue<double> samples = new();
            private readonly int windowSize = 144 * 10; // 10 seconds moving average @ 144 FPS.
            private int lastIterationNumber;
            private double sampleAccumulator;

            public double Average { get; private set; }

            /// <summary>
            ///     Computes a new windowed average each time a new sample arrives.
            /// </summary>
            /// <param name="newSample">new sample to add into the moving average.</param>
            /// <param name="iterationNumber">iteration number who's sample you are adding.</param>
            public void ComputeAverage(double newSample, int iterationNumber)
            {
                if (iterationNumber <= this.lastIterationNumber)
                {
                    return;
                }

                this.lastIterationNumber = iterationNumber;
                this.sampleAccumulator += newSample;
                this.samples.Enqueue(newSample);

                if (this.samples.Count > this.windowSize)
                {
                    this.sampleAccumulator -= this.samples.Dequeue();
                }

                this.Average = this.sampleAccumulator / this.samples.Count;
            }
        }
    }
}
