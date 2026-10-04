using System;
using System.Collections;
using System.IO;
using System.Reflection;
using HarmonyLib;
using InControl;
using UnityEngine;

namespace SlimeCraft.Testing
{
    /// <summary>
    /// Drives SlimeCraft's real input paths through the IInputGate test hook (SetTestInput / ClearTestInput /
    /// SimulateKeyDown). Every call is guarded: a missing or throwing hook is logged once and reported as false.
    /// The runner clears the override after every step, so a failed step can never leave a "held" button behind.
    /// </summary>
    internal static class TI
    {
        private static bool warned;
        public static bool Holding { get; private set; }

        public static bool Available => SC.Input != null;

        public static bool Set(bool attack, bool use, bool sneak = false)
        {
            var inp = SC.Input;
            if (inp == null) return false;
            try
            {
                inp.SetTestInput(attack, use, sneak);
                Holding = attack || use || sneak;
                return true;
            }
            catch (Exception e)
            {
                if (!warned) { warned = true; SC.Log?.LogWarning("[Test] IInputGate.SetTestInput failed: " + e.Message); }
                return false;
            }
        }

        public static void Clear()
        {
            Holding = false;
            try { SC.Input?.ClearTestInput(); }
            catch (Exception e)
            {
                if (!warned) { warned = true; SC.Log?.LogWarning("[Test] IInputGate.ClearTestInput failed: " + e.Message); }
            }
        }

        public static bool KeyDown(KeyCode key)
        {
            if (SC.Input == null || key == KeyCode.None) return false;
            try { SC.Input.SimulateKeyDown(key); return true; }
            catch (Exception e)
            {
                SC.Log?.LogWarning("[Test] IInputGate.SimulateKeyDown(" + key + ") failed: " + e.Message);
                return false;
            }
        }

        /// <summary>
        /// One click: held for at least 2 frames and <paramref name="hold"/> seconds (rising edge → *Pressed), then
        /// released for 2 frames (falling edge). Keep hold well below 0.2 s for "use" (Minecraft repeats a held right
        /// click every 4 ticks).
        /// </summary>
        public static IEnumerator Click(bool attack, bool use, float hold = 0.04f)
        {
            Set(attack, use);
            float t0 = Time.realtimeSinceStartup;
            yield return null;
            yield return null;
            while (Time.realtimeSinceStartup - t0 < hold) yield return null;
            Set(false, false);
            yield return null;
            yield return null;
        }
    }

    /// <summary>
    /// Makes Slime Rancher's own WeaponVacuum vacuum during the test. The SR vac reads InControl actions
    /// (SRInput.Actions.vac), not SlimeCraft's
    /// input gate, so <see cref="IInputGate.SetTestInput"/> may not reach it. Fallbacks, in order:
    ///  1. a temporary InControl <see cref="BindingSource"/> added to SRInput.Actions.vac whose state we control —
    ///     SR then runs its real vac code (cone, FX, sound, VacActive). BindingSourceType.None keeps it out of SR's
    ///     binding UI / profile (SavedProfile.PullBindings only looks at key/mouse/device bindings); it is
    ///     hard-removed again right after the step;
    ///  2. forcing the private WeaponVacuum.vacMode to VAC every frame after SR's Update (reflection) so
    ///     ISRBridge.VacActive / VacRay report a running vac to SlimeCraft's item pull.
    /// </summary>
    internal static class VacDriver
    {
        private sealed class TestBinding : BindingSource
        {
            public bool Pressed;
            public override float GetValue(InputDevice inputDevice) => Pressed ? 1f : 0f;
            public override bool GetState(InputDevice inputDevice) => Pressed;
            public override bool Equals(BindingSource other) => ReferenceEquals(this, other);
            public override bool Equals(object other) => ReferenceEquals(this, other);
            public override int GetHashCode() => 0x5C7E57;
            public override string Name => "SlimeCraft test";
            public override string DeviceName => "SlimeCraft test";
            public override InputDeviceClass DeviceClass => InputDeviceClass.Unknown;
            public override InputDeviceStyle DeviceStyle => InputDeviceStyle.Unknown;
            public override BindingSourceType BindingSourceType => BindingSourceType.None;
            public override void Save(BinaryWriter writer) { }
            public override void Load(BinaryReader reader, ushort dataFormatVersion) { }
        }

        private static TestBinding binding;
        private static PlayerAction boundTo;
        private static bool forcing;
        private static FieldInfo vacModeField;

        public static bool BindingActive => binding != null && boundTo != null;
        public static bool Forcing => forcing;

        /// <summary>Adds the test binding to SRInput.Actions.vac and presses it.</summary>
        public static bool PressViaBinding(out string error)
        {
            error = null;
            try
            {
                if (binding == null)
                {
                    var actions = SRInput.Actions;
                    if (actions == null || actions.vac == null) { error = "SRInput.Actions.vac is null"; return false; }
                    var b = new TestBinding();
                    if (!actions.vac.AddBinding(b)) { error = "PlayerAction.AddBinding returned false"; return false; }
                    binding = b;
                    boundTo = actions.vac;
                }
                binding.Pressed = true;
                return true;
            }
            catch (Exception e)
            {
                error = e.GetType().Name + ": " + e.Message;
                RemoveBinding();
                return false;
            }
        }

        public static void ReleaseBinding()
        {
            if (binding != null) binding.Pressed = false;
        }

        public static void RemoveBinding()
        {
            var b = binding;
            var a = boundTo;
            binding = null;
            boundTo = null;
            if (b == null || a == null) return;
            b.Pressed = false;
            // PlayerAction.HardRemoveBinding is internal: call it by reflection (removes the binding at once); the
            // public RemoveBinding only unbinds it and lets the next UpdateBindings drop it from the list.
            try
            {
                var hard = AccessTools.Method(typeof(PlayerAction), "HardRemoveBinding", new[] { typeof(BindingSource) });
                if (hard != null) hard.Invoke(a, new object[] { b });
                else a.RemoveBinding(b);
            }
            catch (Exception e)
            {
                SC.Log?.LogWarning("[Test] removing the vac test binding failed: " + e.Message);
                try { a.RemoveBinding(b); } catch { }
            }
        }

        private static WeaponVacuum FindVac()
        {
            var p = G.Player;
            WeaponVacuum v = p != null ? p.GetComponentInChildren<WeaponVacuum>(true) : null;
            return v != null ? v : UnityEngine.Object.FindObjectOfType<WeaponVacuum>();
        }

        /// <summary>Fallback 2: sets WeaponVacuum.vacMode (private enum VacMode {NONE, SHOOT, VAC, GADGET}). Call every frame.</summary>
        public static bool ForceVacMode(bool on)
        {
            try
            {
                if (vacModeField == null) vacModeField = AccessTools.Field(typeof(WeaponVacuum), "vacMode");
                var vac = FindVac();
                if (vacModeField == null || vac == null) return false;
                vacModeField.SetValue(vac, Enum.Parse(vacModeField.FieldType, on ? "VAC" : "NONE"));
                forcing = on;
                return true;
            }
            catch (Exception e)
            {
                SC.Log?.LogWarning("[Test] forcing WeaponVacuum.vacMode failed: " + e.Message);
                forcing = false;
                return false;
            }
        }

        /// <summary>Releases everything (called after every step by the runner).</summary>
        public static void ReleaseAll()
        {
            RemoveBinding();
            if (forcing) ForceVacMode(false);
            forcing = false;
        }
    }
}
