namespace HealthBars
{
    using System;
    using System.Numerics;
    using GameHelper.Utils;
    using ImGuiNET;
    using Newtonsoft.Json;

    /// <summary>
    ///     Saves config of each type of healthbar.
    /// </summary>
    public class Config
    {
        /// <summary>
        ///     Enables the healthbar
        /// </summary>
        public bool Enable;

        /// <summary>
        ///     Change texture if player can cull strike this healthbar.
        /// </summary>
        public bool ShowCullStrike;

        /// <summary>
        ///     Show the absolute Health + Es as text aboved the healthbar
        /// </summary>
        public bool ShowText;

        /// <summary>
        ///     Gets the color to apply on healthbar background.
        /// </summary>
        public Vector4 BackgroundColor;

        /// <summary>
        ///     Gets the color to apply on healthbar.
        /// </summary>
        public Vector4 HealthbarColor;

        /// <summary>
        ///     Gets the color to apply on ES bar.
        /// </summary>
        public Vector4 ESColor;

        /// <summary>
        ///     Gets the color of the next.
        /// </summary>
        public Vector4 TextColor;

        /// <summary>
        ///     Healthbar size multiplier
        /// </summary>
        public Vector2 Scale;

        /// <summary>
        ///     Healthbar position shift.
        /// </summary>
        public Vector2 Shift;

        /// <summary>
        ///     Gets the half of the scale value.
        /// </summary>
        public Vector2 HalfOfScale;

        /// <summary>
        ///     Total number of Graduations on this healthbar
        /// </summary>
        public int Graduations;

        /// <summary>
        ///     Stores the start location of any given Graduation.
        /// </summary>
        public float GraduationsLocationStart;

        /// <summary>
        ///     Stores the end location of any given Graduation.
        /// </summary>
        public Vector2 GraduationsLocationEnd;

        /// <summary>
        ///     Initializes a new instance of the <see cref="Config" /> class.
        /// </summary>
        /// <param name="healthbarcolor">color of the healthbar</param>
        /// <param name="graduations">number of graduations on the healthbar</param>
        /// <param name="showText">show the absolute Health + Es as text aboved the healthbar</param>
        /// <param name="sizeY">healthbar default size Y axis.</param>
        public Config(Vector4 healthbarcolor, int graduations, bool showText, float sizeY)
        {
            this.Enable = true;
            this.ShowCullStrike = true;
            this.ShowText = showText;
            this.BackgroundColor = new(Vector3.Zero, 1f);
            this.HealthbarColor = healthbarcolor;
            this.ESColor = new(0f, 1f, 1f, 1f);
            this.TextColor = new(0f, 1f, 1f, 1f);
            this.Scale = new(128f, sizeY);
            this.HalfOfScale = this.Scale / 2;
            this.Shift = new(0f, 11f);
            this.Graduations = graduations;
            this.GraduationsLocationStart = 0f;
            this.GraduationsLocationEnd = Vector2.Zero;
            this.UpdateGrauationsLocationData();
        }

        /// <summary>
        ///     Initializes a new instance of the <see cref="Config" /> class.
        /// </summary>
        /// <param name="healthbarcolor">color of the healthbar</param>
        /// <param name="sizeY">healthbar default size Y axis.</param>
        public Config(Vector4 healthbarcolor, float sizeY) :
            this(healthbarcolor, 0, false, sizeY) { }

        /// <summary>
        ///     Initializes a new instance of the <see cref="Config" /> class.
        /// </summary>
        /// <param name="healthbarcolor">color of the healthbar</param>
        /// <param name="graduations">number of graduations on the healthbar</param>
        public Config(Vector4 healthbarcolor, int graduations) :
            this(healthbarcolor, graduations, false, 8f) { }

        /// <summary>
        ///     Initializes a new instance of the <see cref="Config" /> class.
        /// </summary>
        /// <param name="healthbarcolor">color of the healthbar</param>
        public Config(Vector4 healthbarcolor) :
            this(healthbarcolor, 0) { }

        /// <summary>
        ///     Initializes a new instance of the <see cref="Config" /> class.
        /// </summary>
        [JsonConstructor]
        public Config() :
            this(Vector4.One) { }

        /// <summary>
        ///     Display the Config on imgui.
        /// </summary>
        public void Draw()
        {
            ImGui.Checkbox("Enable", ref this.Enable);
            ImGui.SameLine();
            ImGui.Checkbox("Cull strike highlight", ref this.ShowCullStrike);
            ImGui.SameLine();
            ImGui.Checkbox("Value text", ref this.ShowText);

            ImGui.SeparatorText("Layout");
            if (ImGui.BeginTable("layout_table", 2, ImGuiTableFlags.SizingStretchSame))
            {
                ImGui.TableNextColumn();
                if (ImGuiHelper.Vector2SliderInt("Size", ImGui.GetColumnWidth(), ref this.Scale, 1, 500, 1, 128, ImGuiSliderFlags.Logarithmic))
                {
                    this.UpdateGrauationsLocationData();
                }

                ImGui.TableNextColumn();
                ImGuiHelper.Vector2SliderInt("Offset", ImGui.GetColumnWidth(), ref this.Shift, -4000, 4000, -2500, 2500, ImGuiSliderFlags.Logarithmic);
                ImGui.EndTable();
            }

            if (ImGui.DragInt("Graduation marks", ref this.Graduations, 0.05f, 0, 9))
            {
                this.UpdateGrauationsLocationData();
            }

            ImGui.SeparatorText("Colors");
            if (ImGui.BeginTable("color_table", 2, ImGuiTableFlags.SizingStretchSame))
            {
                ImGui.TableNextColumn();
                ImGui.ColorEdit4("Health", ref this.HealthbarColor);
                ImGui.TableNextColumn();
                ImGui.ColorEdit4("Secondary", ref this.ESColor);
                ImGui.TableNextColumn();
                ImGui.ColorEdit4("Background", ref this.BackgroundColor);
                ImGui.TableNextColumn();
                ImGui.ColorEdit4("Text", ref this.TextColor);
                ImGui.EndTable();
            }
        }

        public void Normalize()
        {
            this.Graduations = Math.Clamp(this.Graduations, 0, 9);
            this.Scale.X = Math.Max(1f, this.Scale.X);
            this.Scale.Y = Math.Max(1f, this.Scale.Y);
            this.UpdateGrauationsLocationData();
        }

        private void UpdateGrauationsLocationData()
        {
            this.GraduationsLocationStart = this.Scale.X / (this.Graduations + 1);
            this.GraduationsLocationEnd = Vector2.UnitY * this.Scale.Y;
            this.HalfOfScale = this.Scale / 2;
        }
    }
}
