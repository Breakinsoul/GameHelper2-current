namespace PerformanceMonitor
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Numerics;
    using Coroutine;
    using GameHelper;
    using GameHelper.Plugin;
    using GameHelper.RemoteEnums;
    using ImGuiNET;
    using Newtonsoft.Json;

    public sealed class PerformanceMonitor : PCore<PerformanceMonitorSettings>
    {
        private readonly Queue<float> fpsSamples = new();
        private readonly Queue<long> memorySamples = new();
        private float fpsSum;
        private long memorySum;
        private string exportStatus = string.Empty;

        private string SettingPathname => Path.Join(this.DllDirectory, "config", "settings.txt");

        public override void OnEnable(bool isGameOpened)
        {
            if (!File.Exists(this.SettingPathname))
            {
                return;
            }

            var content = File.ReadAllText(this.SettingPathname);
            this.Settings = JsonConvert.DeserializeObject<PerformanceMonitorSettings>(content) ?? new PerformanceMonitorSettings();
        }

        public override void OnDisable()
        {
        }

        public override void SaveSettings()
        {
            GameHelper.Utils.JsonHelper.SafeToFile(this.Settings, new FileInfo(this.SettingPathname));
        }

        public override void DrawSettings()
        {
            var show = this.Settings.ShowWindow;
            if (ImGui.Checkbox("Show monitor", ref show))
            {
                this.Settings.ShowWindow = show;
            }

            var compact = this.Settings.CompactMode;
            if (ImGui.Checkbox("Compact mode", ref compact))
            {
                this.Settings.CompactMode = compact;
            }

            var hide = this.Settings.HideWhenGameInBackground;
            if (ImGui.Checkbox("Hide when game is in background", ref hide))
            {
                this.Settings.HideWhenGameInBackground = hide;
            }

            var coroutine = this.Settings.ShowCoroutineStats;
            if (ImGui.Checkbox("Coroutine stats", ref coroutine))
            {
                this.Settings.ShowCoroutineStats = coroutine;
            }

            var entities = this.Settings.ShowEntityStats;
            if (ImGui.Checkbox("Entity stats", ref entities))
            {
                this.Settings.ShowEntityStats = entities;
            }

            var seconds = this.Settings.SampleWindowSeconds;
            if (ImGui.SliderInt("Average window seconds", ref seconds, 3, 120))
            {
                this.Settings.SampleWindowSeconds = seconds;
                this.TrimSamples();
            }
        }

        public override void DrawUI()
        {
            this.AddSamples();

            if (!this.Settings.ShowWindow)
            {
                return;
            }

            if (this.Settings.HideWhenGameInBackground && !Core.Process.Foreground)
            {
                return;
            }

            ImGui.SetNextWindowSize(this.Settings.CompactMode ? new Vector2(260f, 120f) : new Vector2(420f, 330f), ImGuiCond.FirstUseEver);
            var show = this.Settings.ShowWindow;
            if (!ImGui.Begin("Performance Monitor", ref show))
            {
                this.Settings.ShowWindow = show;
                ImGui.End();
                return;
            }

            this.Settings.ShowWindow = show;
            this.DrawMetrics();
            ImGui.End();
        }

        private void AddSamples()
        {
            var fps = MathF.Max(0f, ImGui.GetIO().Framerate);
            var memory = GC.GetTotalMemory(false);
            this.fpsSamples.Enqueue(fps);
            this.memorySamples.Enqueue(memory);
            this.fpsSum += fps;
            this.memorySum += memory;
            this.TrimSamples();
        }

        private void TrimSamples()
        {
            var fps = MathF.Max(1f, ImGui.GetIO().Framerate);
            var maxSamples = Math.Max(10, (int)(this.Settings.SampleWindowSeconds * fps));
            while (this.fpsSamples.Count > maxSamples)
            {
                this.fpsSum -= this.fpsSamples.Dequeue();
            }

            while (this.memorySamples.Count > maxSamples)
            {
                this.memorySum -= this.memorySamples.Dequeue();
            }
        }

        private void DrawMetrics()
        {
            var fps = ImGui.GetIO().Framerate;
            var avgFps = this.fpsSamples.Count == 0 ? fps : this.fpsSum / this.fpsSamples.Count;
            var avgMemory = this.memorySamples.Count == 0 ? GC.GetTotalMemory(false) : this.memorySum / this.memorySamples.Count;

            using var proc = Process.GetCurrentProcess();
            ImGui.Text($"FPS: {fps:0.0}  avg: {avgFps:0.0}");
            ImGui.Text($"Process memory: {proc.PrivateMemorySize64 / 1048576d:0.0} MB");
            ImGui.Text($"Managed memory avg: {avgMemory / 1048576d:0.0} MB");
            ImGui.Text($"Window: {Core.Process.WindowArea.Width}x{Core.Process.WindowArea.Height}");
            ImGui.Text($"State: {Core.States.GameCurrentState}");

            if (this.Settings.CompactMode)
            {
                return;
            }

            if (this.Settings.ShowEntityStats)
            {
                this.DrawEntityStats();
            }

            if (this.Settings.ShowCoroutineStats)
            {
                this.DrawCoroutineStats();
            }

            if (ImGui.Button("Export snapshot"))
            {
                this.ExportSnapshot(fps, avgFps, proc.PrivateMemorySize64, avgMemory);
            }

            if (!string.IsNullOrEmpty(this.exportStatus))
            {
                ImGui.SameLine();
                ImGui.TextWrapped(this.exportStatus);
            }
        }

        private void DrawEntityStats()
        {
            ImGui.SeparatorText("Area");
            if (Core.Process.Pid == 0 || Core.States.GameCurrentState != GameStateTypes.InGameState)
            {
                ImGui.TextUnformatted("No active area.");
                return;
            }

            var area = Core.States.InGameStateObject.CurrentAreaInstance;
            ImGui.Text($"Hash: {area.AreaHash}");
            ImGui.Text($"Awake entities: {area.AwakeEntities.Count}");
            ImGui.Text($"Network bubble: {area.NetworkBubbleEntityCount}");
            ImGui.Text($"Useless awake: {area.UselessAwakeEntities}");
        }

        private void DrawCoroutineStats()
        {
            ImGui.SeparatorText("Coroutines");
            ImGui.Text($"Events: {CoroutineHandler.EventCount}");
            ImGui.Text($"Ticking: {CoroutineHandler.TickingCount}");

            var active = Core.CoroutinesRegistrar.Count(x => !x.IsFinished);
            ImGui.Text($"Registered active: {active}");
        }

        private void ExportSnapshot(float fps, float avgFps, long privateMemory, long avgManagedMemory)
        {
            var exportDir = Path.Join(this.DllDirectory, "exports");
            Directory.CreateDirectory(exportDir);
            var file = Path.Join(exportDir, $"performance-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            var area = Core.States.GameCurrentState == GameStateTypes.InGameState
                ? Core.States.InGameStateObject.CurrentAreaInstance
                : null;
            var payload = new
            {
                Timestamp = DateTimeOffset.Now,
                Core.Process.Pid,
                Core.Process.Foreground,
                State = Core.States.GameCurrentState.ToString(),
                Fps = fps,
                AverageFps = avgFps,
                PrivateMemoryMb = privateMemory / 1048576d,
                AverageManagedMemoryMb = avgManagedMemory / 1048576d,
                CoroutineHandler.EventCount,
                CoroutineHandler.TickingCount,
                Area = area == null ? null : new
                {
                    area.AreaHash,
                    area.CurrentAreaLevel,
                    AwakeEntities = area.AwakeEntities.Count,
                    area.NetworkBubbleEntityCount,
                    area.UselessAwakeEntities,
                },
            };

            File.WriteAllText(file, JsonConvert.SerializeObject(payload, Formatting.Indented));
            this.exportStatus = $"Exported: {file}";
        }
    }
}
