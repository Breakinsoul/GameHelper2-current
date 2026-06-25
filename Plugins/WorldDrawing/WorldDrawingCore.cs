// <copyright file="WorldDrawingCore.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace WorldDrawing
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Numerics;
    using Coroutine;
    using GameHelper;
    using GameHelper.CoroutineEvents;
    using GameHelper.Plugin;
    using GameHelper.RemoteEnums;
    using GameHelper.RemoteEnums.Entity;
    using GameHelper.RemoteObjects.Components;
    using GameHelper.Utils;
    using ImGuiNET;
    using Newtonsoft.Json;


    /// <summary>
    /// <see cref="WorldDrawingCore"/> plugin.
    /// </summary>
    public sealed class WorldDrawingCore : PCore<WorldDrawingSettings>
    {
        private string SettingPathname => Path.Join(this.DllDirectory, "config", "settings.txt");
        private ActiveCoroutine onAreaChangeCoroutine;

        /// <inheritdoc/>
        public override void DrawSettings()
        {
            if (!ImGui.BeginTabBar("WorldDrawingSettingsTabs"))
            {
                return;
            }

            if (ImGui.BeginTabItem("General"))
            {
                ImGui.Checkbox("Only show abyss path when large map is hidden", ref this.Settings.OnlyShowAbyssPathWhenLargeMapHidden);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Abyss Path"))
            {
                for (var i = 0; i < this.Settings.AbyssPath.Length; i++)
                {
                    var path = this.Settings.AbyssPath[i];
                    ImGui.PushID(i);
                    ImGui.Checkbox("Enabled", ref path.enable);
                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(120f);
                    ImGui.SliderFloat("Width", ref path.width, 0.5f, 10f);
                    ImGui.SameLine();
                    ImGui.ColorEdit4("Color", ref path.color, ImGuiColorEditFlags.NoInputs);
                    ImGui.PopID();
                    this.Settings.AbyssPath[i] = path;
                }

                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }

        /// <inheritdoc/>
        public override void DrawUI()
        {
            if (Core.States.InGameStateObject.GameUi.SkillTreeNodesUiElements.Count > 0)
            {
                return;
            }
        }

        /// <inheritdoc/>
        public override void OnDisable()
        {
            this.onAreaChangeCoroutine?.Cancel();
            this.onAreaChangeCoroutine = null;
        }

        /// <inheritdoc/>
        public override void OnEnable(bool isGameOpened)
        {
            if (File.Exists(this.SettingPathname))
            {
                var content = File.ReadAllText(this.SettingPathname);
                this.Settings = JsonConvert.DeserializeObject<WorldDrawingSettings>(content) ?? new WorldDrawingSettings();
            }

            this.onAreaChangeCoroutine = CoroutineHandler.Start(this.onAreaChange());
        }

        /// <inheritdoc/>
        public override void SaveSettings()
        {
            JsonHelper.SafeToFile(this.Settings, new FileInfo(this.SettingPathname));
        }

        private IEnumerable<Wait> onAreaChange()
        {
            while (true)
            {
                yield return new Wait(RemoteEvents.AreaChanged);
                this.ClearAll();
            }
        }

        private void ClearAll()
        {
        }
    }
}
