// <copyright file="Life.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace GameHelper.RemoteObjects.Components
{
    using System;
    using GameOffsets.Objects.Components;
    using ImGuiNET;
    using Utils;

    /// <summary>
    ///     The <see cref="Life" /> component in the entity.
    /// </summary>
    public class Life : ComponentBase
    {
        public readonly record struct VitalCandidate(int Offset, int Current, int Total, IntPtr Owner, bool Plausible);

        private static readonly int[] HealthOffsetCandidates =
        [
            0x1B0,
            0x1A8,
            0x1B8,
            0x1A0,
            0x1C0,
            0x190,
            0x198,
            0x1C8,
            0x1D0,
            0x1D8,
            0x1E0,
        ];

        private static int healthOffset = 0x1B0;

        /// <summary>
        ///     Initializes a new instance of the <see cref="Life" /> class.
        /// </summary>
        /// <param name="address">address of the <see cref="Life" /> component.</param>
        public Life(IntPtr address)
            : base(address) { }

        /// <summary>
        ///     Gets a value indicating whether the entity is alive or not.
        /// </summary>
        public bool IsAlive { get; private set; } = true;

        /// <summary>
        ///     Gets the health related information of the entity.
        /// </summary>
        public VitalStruct Health { get; private set; }

        /// <summary>
        ///     Gets the energyshield related information of the entity.
        /// </summary>
        public VitalStruct EnergyShield { get; private set; }

        /// <summary>
        ///     Gets the mana related information of the entity.
        /// </summary>
        public VitalStruct Mana { get; private set; }

        public static int HealthOffset => healthOffset;

        /// <summary>
        ///     Converts the <see cref="Life" /> class data to ImGui.
        /// </summary>
        internal override void ToImGui()
        {
            base.ToImGui();

            if (ImGui.TreeNode("Health"))
            {
                this.VitalToImGui(this.Health);
                ImGui.Text($"Health Offset: 0x{healthOffset:X}");
                foreach (var candidate in this.GetHealthDiagnostics())
                {
                    ImGui.Text($"  0x{candidate.Offset:X}: {candidate.Current}/{candidate.Total}, owner=0x{candidate.Owner.ToInt64():X}, ok={candidate.Plausible}");
                }

                ImGui.TreePop();
            }

            if (ImGui.TreeNode("Energy Shield"))
            {
                this.VitalToImGui(this.EnergyShield);
                ImGui.TreePop();
            }

            if (ImGui.TreeNode("Mana"))
            {
                this.VitalToImGui(this.Mana);
                ImGui.TreePop();
            }
        }

        /// <inheritdoc />
        protected override void UpdateData(bool hasAddressChanged)
        {
            var reader = Core.Process.Handle;
            var header = reader.ReadMemory<ComponentHeader>(this.Address);
            this.OwnerEntityAddress = header.EntityPtr;
            this.Health = this.ResolveHealth();
            this.Mana = this.ReadVitalAtOffset(healthOffset + 0x58);
            this.EnergyShield = this.ReadVitalAtOffset(healthOffset + 0x98);
            this.IsAlive = this.Health.Current > 0;
        }

        public VitalCandidate[] GetHealthDiagnostics()
        {
            var ret = new VitalCandidate[HealthOffsetCandidates.Length];
            for (var i = 0; i < HealthOffsetCandidates.Length; i++)
            {
                var offset = HealthOffsetCandidates[i];
                var value = this.ReadVitalAtOffset(offset);
                ret[i] = new VitalCandidate(
                    offset,
                    value.Current,
                    value.Total,
                    value.PtrToLifeComponent,
                    this.IsPlausibleHealth(value));
            }

            return ret;
        }

        private VitalStruct ResolveHealth()
        {
            var cached = this.ReadVitalAtOffset(healthOffset);
            if (this.IsPlausibleHealth(cached))
            {
                return cached;
            }

            foreach (var offset in HealthOffsetCandidates)
            {
                var value = this.ReadVitalAtOffset(offset);
                if (!this.IsPlausibleHealth(value))
                {
                    continue;
                }

                if (offset != healthOffset)
                {
                    healthOffset = offset;
                    Console.WriteLine($"[Life] Auto-selected Health offset 0x{healthOffset:X}.");
                }

                return value;
            }

            return cached;
        }

        private VitalStruct ReadVitalAtOffset(int offset)
        {
            try
            {
                return Core.Process.Handle.ReadMemory<VitalStruct>(IntPtr.Add(this.Address, offset));
            }
            catch
            {
                return default;
            }
        }

        private bool IsPlausibleHealth(VitalStruct value)
        {
            return value.PtrToLifeComponent == this.Address &&
                value.Total is > 0 and < 100000000 &&
                value.Current >= 0 &&
                value.Current <= value.Total;
        }

        private void VitalToImGui(VitalStruct data)
        {
            ImGuiHelper.IntPtrToImGui("PtrToSelf", data.PtrToLifeComponent);
            ImGui.Text($"Regeneration: {data.Regeneration}");
            ImGui.Text($"Total: {data.Total}");
            ImGui.Text($"ReservedFlat: {data.ReservedFlat}");
            ImGui.Text($"Current: {data.Current}");
            ImGui.Text($"Reserved(%%): {data.ReservedPercent}");
            ImGui.Text($"Current(%%): {data.CurrentInPercent()}");
        }
    }
}
