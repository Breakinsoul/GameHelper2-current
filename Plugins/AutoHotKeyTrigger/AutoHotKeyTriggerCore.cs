// <copyright file="AutoHotKeyTriggerCore.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace AutoHotKeyTrigger
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Numerics;
    using System.Threading;
    using ClickableTransparentOverlay.Win32;
    using Coroutine;
    using GameHelper;
    using GameHelper.CoroutineEvents;
    using GameHelper.Plugin;
    using GameHelper.RemoteEnums;
    using GameHelper.RemoteObjects.Components;
    using GameHelper.Utils;
    using ImGuiNET;
    using Newtonsoft.Json;
    using AutoHotKeyTrigger.ProfileManager;
    using AutoHotKeyTrigger.ProfileManager.DynamicConditions;

    /// <summary>
    ///     <see cref="AutoHotKeyTrigger" /> plugin.
    /// </summary>
    public sealed class AutoHotKeyTriggerCore : PCore<AutoHotKeyTriggerSettings>
    {
        private readonly string warningMsg = "The current condition you have put for AutoQuit is yielding true.\n" +
            "This mean you will automatically logout as soon as you leave town/hideout.\n" +
            "Please update your AutoQuit condition and/or disable it and/or fix your exile state.";

        private readonly List<(string name, Profile value)> clonesToAdd = new();
        private readonly Vector4 impTextColor = new(255, 255, 0, 255);
        private readonly Vector2 size = new(624, 380);
        private int statusEffectDumpPending;
        private readonly List<string> keyPressInfo = new();
        private bool keyPressInfoAdded = false;
        private bool isDebugWindowHovered = false;
        private ActiveCoroutine? onAreaChange;
        private string debugMessage = string.Empty;
        private string newProfileName = string.Empty;
        private bool stopShowingAutoQuitWarning = false;

        private string SettingPathname => Path.Join(this.DllDirectory, "config", "settings.txt");
        private bool ShouldExecuteAutoQuit =>
            this.Settings.EnableAutoQuit &&
            this.Settings.AutoQuitCondition.Evaluate();

        /// <inheritdoc />
        public override void DrawSettings()
        {
            ImGui.TextColored(this.impTextColor, "Review imported profiles before enabling them.");

            if (!ImGui.BeginTabBar("AutoHotKeyTriggerSettingsTabs"))
            {
                return;
            }

            if (ImGui.BeginTabItem("General"))
            {
                ImGui.Checkbox("Run in hideout", ref this.Settings.ShouldRunInHideout);
                ImGuiHelper.IEnumerableComboBox("Profile", this.Settings.Profiles.Keys, ref this.Settings.CurrentProfile);
                if (ImGui.Button("Add/Reset and Activate League Start Default Profile"))
                {
                    this.CreateDefaultProfile();
                }

                ImGui.EndTabItem();
            }

            DynamicCondition.UpdateState();
            if (ImGui.BeginTabItem("Profiles"))
            {
                ImGui.InputText("Name", ref this.newProfileName, 100);
                ImGui.SameLine();
                if (ImGui.Button("Add"))
                {
                    if (!string.IsNullOrEmpty(this.newProfileName))
                    {
                        this.Settings.Profiles.Add(this.newProfileName, new Profile());
                        this.newProfileName = string.Empty;
                    }
                }

                foreach (var (key, profile) in this.Settings.Profiles.ToArray())
                {
                    var isOpened = ImGui.TreeNode($"{key} (?)");
                    ImGuiHelper.ToolTip("Rules (tabs) can be moved via drag and drop. They can be cloned by right click.");
                    if (isOpened)
                    {
                        ImGui.SameLine();
                        if (ImGui.SmallButton("Delete Profile"))
                        {
                            this.Settings.Profiles.Remove(key);
                            if (this.Settings.CurrentProfile == key)
                            {
                                this.Settings.CurrentProfile = string.Empty;
                            }
                        }

                        ImGui.SameLine();
                        if (ImGui.SmallButton("Clone Profile"))
                        {
                            this.clonesToAdd.Add(($"{key}1", new(profile)));

                        }

                        profile.DrawSettings(key, this.Settings.Profiles);
                        ImGui.TreePop();
                    }
                }

                this.clonesToAdd.RemoveAll(k => this.Settings.Profiles.TryAdd(k.name, k.value) || true); // remove even if add fails.

                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Auto Quit"))
            {
                ImGui.PushItemWidth(ImGui.GetContentRegionAvail().X / 6);
                ImGui.Checkbox("Enable AutoQuit", ref this.Settings.EnableAutoQuit);
                this.Settings.AutoQuitCondition.Display(true);
                ImGui.Separator();
                ImGui.Checkbox("Enable AutoQuit Manual Hotkey", ref this.Settings.EnableAutoQuitKey);
                ImGui.Text("Hotkey to manually quit game connection: ");
                ImGui.SameLine();
                ImGuiHelper.NonContinuousEnumComboBox("##Manual Quit HotKey", ref this.Settings.AutoQuitKey);
                ImGui.PopItemWidth();

                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Debug"))
            {
                ImGui.Checkbox("Debug mode", ref this.Settings.DebugMode);
                ImGuiHelper.NonContinuousEnumComboBox("Dump player status effects", ref this.Settings.DumpStatusEffectOnMe);
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }

        /// <inheritdoc />
        public override void DrawUI()
        {
            if (this.Settings.DebugMode)
            {
                ImGui.SetNextWindowSize(size, ImGuiCond.FirstUseEver);
                if (ImGui.Begin($"AHK Debug Window", ref this.Settings.DebugMode,
                    this.isDebugWindowHovered ? ImGuiWindowFlags.MenuBar : ImGuiWindowFlags.None))
                {
                    this.isDebugWindowHovered =  ImGui.IsWindowHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem);
                    if (ImGui.BeginMenuBar())
                    {
                        if (ImGui.Button("Clear History"))
                        {
                            this.keyPressInfo.Clear();
                        }

                        ImGui.EndMenuBar();
                    }

                    for (var i = 0; i < this.keyPressInfo.Count; i++)
                    {
                        ImGui.Text($"{i}-{this.keyPressInfo[i]}");
                    }

                    if (this.keyPressInfoAdded)
                    {
                        ImGui.SetScrollHereY();
                        this.keyPressInfoAdded = false;
                    }

                    if (!string.IsNullOrEmpty(this.debugMessage))
                    {
                        ImGui.Separator();
                        ImGui.TextWrapped($"Issues: {this.debugMessage}");
                    }
                }

                ImGui.End();
            }

            this.AutoQuitWarningUi();
            if (!this.ShouldExecutePlugin())
            {
                return;
            }

            DynamicCondition.UpdateState();
            if (this.ShouldExecuteAutoQuit ||
                (this.Settings.EnableAutoQuitKey &&
                Utils.IsKeyPressedAndNotTimeout(this.Settings.AutoQuitKey, 200)))
            {
                MiscHelper.KillTCPConnectionForProcess(Core.Process.Pid);
            }

            if (Utils.IsKeyPressedAndNotTimeout(this.Settings.DumpStatusEffectOnMe, 200))
            {
                if (Core.States.InGameStateObject.CurrentAreaInstance.Player.TryGetComponent<Buffs>(out var buff))
                {
                    var data = "===========================================" + Environment.NewLine;
                    foreach (var statusEffect in buff.StatusEffects)
                    {
                        data += $"{statusEffect.Key} {statusEffect.Value}\n";
                    }

                    if (!string.IsNullOrEmpty(data))
                    {
                        this.QueueStatusEffectDump(data + Environment.NewLine);
                    }
                }
            }

            if (Core.GHSettings.EnableControllerMode)
            {
                // this is actually disabled in <see cref="MiscHelper.KeyUp"/> function.
                // follow is done just to provide debug msg to end users.
                this.debugMessage = "Controller mode enabled. this plugin doesn't support controllers";
                return;
            }

            if (string.IsNullOrEmpty(this.Settings.CurrentProfile))
            {
                this.debugMessage = "No Profile Selected.";
                return;
            }

            if (!this.Settings.Profiles.ContainsKey(this.Settings.CurrentProfile))
            {
                this.debugMessage = $"{this.Settings.CurrentProfile} not found.";
                return;
            }

            if (Core.States.InGameStateObject.GameUi.ChatParent.IsChatActive)
            {
                this.debugMessage = "Chat window is active, so can not drink flasks or trigger skills.";
                return;
            }

            foreach (var rule in this.Settings.Profiles[this.Settings.CurrentProfile].Rules)
            {
                rule.Execute(this.DebugLog);
            }
        }

        private void DebugLog(string logText)
        {
            if (this.Settings.DebugMode)
            {
                this.keyPressInfo.Add($"{DateTime.Now.TimeOfDay}: {logText}");
            }

            this.keyPressInfoAdded = true;
        }

        /// <inheritdoc />
        public override void OnDisable()
        {
            this.onAreaChange?.Cancel();
            this.onAreaChange = null;
        }

        /// <inheritdoc />
        public override void OnEnable(bool isGameOpened)
        {
            var jsonData2 = File.ReadAllText(this.DllDirectory + @"/StatusEffectGroup.json");
            JsonDataHelper.StatusEffectGroups = JsonConvert.DeserializeObject<
                Dictionary<string, List<string>>>(jsonData2)
                ?? new Dictionary<string, List<string>>();

            if (File.Exists(this.SettingPathname))
            {
                var content = File.ReadAllText(this.SettingPathname);
                this.Settings = JsonConvert.DeserializeObject<AutoHotKeyTriggerSettings>(
                    content,
                    new JsonSerializerSettings
                    {
                        TypeNameHandling = TypeNameHandling.Auto
                    }) ?? new AutoHotKeyTriggerSettings();
            }
            else
            {
                this.CreateDefaultProfile();
            }

            this.onAreaChange = CoroutineHandler.Start(this.EnableAutoQuitWarningUiOnAreaChange());
        }

        /// <inheritdoc />
        public override void SaveSettings()
        {
            var settingsData = JsonConvert.SerializeObject(this.Settings,
                Formatting.Indented,
                new JsonSerializerSettings
                {
                    TypeNameHandling = TypeNameHandling.Auto
                });
            JsonHelper.SafeTextToFile(settingsData, new FileInfo(this.SettingPathname));
        }

        private bool ShouldExecutePlugin()
        {
            var cgs = Core.States.GameCurrentState;
            if (cgs != GameStateTypes.InGameState)
            {
                this.debugMessage = $"Current game state isn't InGameState, it's {cgs}.";
                return false;
            }

            if (!Core.Process.Foreground)
            {
                this.debugMessage = "Game is minimized.";
                return false;
            }

            var areaDetails = Core.States.InGameStateObject.CurrentWorldInstance.AreaDetails;
            if (areaDetails.IsTown)
            {
                this.debugMessage = "Player is in town.";
                return false;
            }

            if (!this.Settings.ShouldRunInHideout && areaDetails.IsHideout)
            {
                this.debugMessage = "Player is in hideout & hideout execution is turned off.";
                return false;
            }

            if (Core.States.InGameStateObject.CurrentAreaInstance.Player.TryGetComponent<Life>(out var lifeComp))
            {
                if (lifeComp.Health.Current <= 0)
                {
                    this.debugMessage = "Player is dead.";
                    return false;
                }
            }
            else
            {
                this.debugMessage = "Can not find player Life component.";
                return false;
            }

            if (Core.States.InGameStateObject.CurrentAreaInstance.Player.TryGetComponent<Buffs>(out var buffComp))
            {
                if (buffComp.StatusEffects.ContainsKey("grace_period"))
                {
                    this.debugMessage = "Player has Grace Period.";
                    return false;
                }
            }
            else
            {
                this.debugMessage = "Can not find player PlayerBuffs component.";
                return false;
            }

            if (!Core.States.InGameStateObject.CurrentAreaInstance.Player.TryGetComponent<Actor>(out var _))
            {
                this.debugMessage = "Can not find player Actor component.";
                return false;
            }

            this.debugMessage = string.Empty;
            return true;
        }

        /// <summary>
        ///     Creates a default profile that is only valid for flasks on newly created character.
        /// </summary>
        private void CreateDefaultProfile()
        {
            Profile profile = new();
            foreach (var rule in Rule.CreateDefaultRules())
            {
                profile.Rules.Add(rule);
            }

            this.Settings.Profiles["LeagueStartDefaultProfile"] = profile;
            this.Settings.CurrentProfile = "LeagueStartDefaultProfile";
            this.Settings.Profiles["ProfileMidGame"] = new();
            this.Settings.Profiles["ProfileEndGame"] = new();
        }

        private void AutoQuitWarningUi()
        {

            if (!this.stopShowingAutoQuitWarning &&
                (Core.States.InGameStateObject.CurrentWorldInstance.AreaDetails.IsTown ||
                Core.States.InGameStateObject.CurrentWorldInstance.AreaDetails.IsHideout) &&
                this.ShouldExecuteAutoQuit)
            {
                ImGui.OpenPopup("AutoQuitWarningUi");
            }

            if (ImGui.BeginPopup("AutoQuitWarningUi"))
            {
                ImGui.Text(this.warningMsg);
                if (ImGui.Button("I understand", new Vector2(ImGui.CalcTextSize(this.warningMsg).X, 50f)))
                {
                    this.stopShowingAutoQuitWarning = true;
                    ImGui.CloseCurrentPopup();
                }

                ImGui.EndPopup();
            }
        }

        private void QueueStatusEffectDump(string data)
        {
            if (Interlocked.Exchange(ref this.statusEffectDumpPending, 1) == 1)
            {
                return;
            }

            var outputPath = Path.Join(this.DllDirectory, "player_status_effect.txt");
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    File.AppendAllText(outputPath, data);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[AutoHotKeyTrigger.QueueStatusEffectDump] {ex}");
                }
                finally
                {
                    Interlocked.Exchange(ref this.statusEffectDumpPending, 0);
                }
            });
        }

        private IEnumerator<Wait> EnableAutoQuitWarningUiOnAreaChange()
        {
            while (true)
            {
                yield return new Wait(RemoteEvents.AreaChanged);
                this.stopShowingAutoQuitWarning = false;
            }
        }
    }
}
