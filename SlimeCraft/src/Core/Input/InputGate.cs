using System;
using System.Collections.Generic;
using UnityEngine;

namespace SlimeCraft.Core
{
    /// <summary>
    /// <see cref="IInputGate"/>. While a Minecraft screen is open Core pushes SR input mode NONE on SR's own input
    /// mode stack (SRInput.SetInputMode with our handle, like LockOnDeath/PauseMenu do), which disables SR's move,
    /// look, fire, map/pedia and pause actions, and frees the cursor through SR's public TimeDirector.EnableCursor
    /// (with a direct fallback on the cursor flags if no TimeDirector exists). Escape is left to the screen owner (it pops its screen);
    /// SR's pause menu is suppressed while a screen is open and for a moment after the last one closed.
    /// </summary>
    internal sealed class InputGate : IInputGate
    {
        private const int SRInputHandle = 0x5C0FF1CE;
        private const float PauseGrace = 0.25f;

        private readonly SRBridge bridge;
        private readonly HashSet<string> screens = new HashSet<string>(StringComparer.Ordinal);
        private bool holdsMode;
        private float lastClosed = -10f;
        private bool suppressWeapon;
        private int allowedFrame = -1;
        private bool allowedCache;

        public InputGate(SRBridge bridge) { this.bridge = bridge; }

        public bool HoldsSRInputMode => holdsMode;

        public bool SuppressSRWeapon
        {
            get => suppressWeapon;
            set { suppressWeapon = value; bridge.Vacpack.SetSuppressed(value); }
        }

        public void PushScreen(string id)
        {
            if (string.IsNullOrEmpty(id)) id = "screen";
            if (!screens.Add(id)) return;
            allowedFrame = -1; // invalidate GameplayInputAllowed for the rest of this frame
            if (screens.Count == 1) Acquire();
        }

        public void PopScreen(string id)
        {
            if (string.IsNullOrEmpty(id)) id = "screen";
            if (!screens.Remove(id)) return;
            allowedFrame = -1;
            if (screens.Count == 0) { lastClosed = Time.unscaledTime; Release(); }
        }

        public bool AnyScreenOpen => screens.Count > 0;

        /// <summary>Used by the PauseMenu.Update prefix.</summary>
        public bool ShouldBlockPauseMenu()
        {
            if (screens.Count > 0) return true;
            if (Time.unscaledTime - lastClosed < PauseGrace) return true;
            return false;
        }

        private void Acquire()
        {
            if (holdsMode || !bridge.InGame) return;
            try
            {
                SRInput.Instance.SetInputMode(SRInput.InputMode.NONE, SRInputHandle);
                holdsMode = true;
                FreeCursor();
            }
            catch (Exception e) { CoreLog.Rate("input acquire", e); }
        }

        private void Release()
        {
            if (!holdsMode) return;
            holdsMode = false;
            try
            {
                SRInput.Instance.ClearInputMode(SRInputHandle);
                var td = bridge.TimeDirector;
                bool srWantsCursor = Time.timeScale <= 0f || (td != null && td.HasPauser()) || CoreRuntime.AnySRWindowOpen();
                if (!srWantsCursor)
                {
                    var input = FpInput();
                    if (td != null) td.DisableCursor(input);
                    else { if (input != null) input.MouseCursorForced = false; vp_Utility.LockCursor = true; }
                }
            }
            catch (Exception e) { CoreLog.Rate("input release", e); }
        }

        private vp_FPInput FpInput()
        {
            var p = bridge.Player;
            return p != null ? p.GetComponentInChildren<vp_FPInput>() : null;
        }

        private void FreeCursor()
        {
            var input = FpInput();
            var td = bridge.TimeDirector;
            if (td != null) td.EnableCursor(input);
            else { if (input != null) input.MouseCursorForced = true; vp_Utility.LockCursor = false; }
        }

        /// <summary>Per frame: keep SR input/cursor consistent (e.g. after alt-tab SR re-locks the cursor).</summary>
        public void Tick()
        {
            PruneSimKeys();
            try
            {
                if (screens.Count > 0)
                {
                    if (!holdsMode && bridge.InGame) Acquire();
                    if (holdsMode && (vp_Utility.LockCursor || Cursor.lockState == CursorLockMode.Locked)) FreeCursor();
                }
                else if (holdsMode) Release();
            }
            catch (Exception e) { CoreLog.Rate("input tick", e); }
        }

        /// <summary>World unloading: close everything (SR destroys its input stack owners with the scene).</summary>
        public void Reset()
        {
            screens.Clear();
            ClearTestInput();
            simKeys.Clear();
            if (holdsMode)
            {
                holdsMode = false;
                try { SRInput.Instance.ClearInputMode(SRInputHandle); } catch (Exception e) { CoreLog.Rate("input reset", e); }
            }
        }

        public bool GameplayInputAllowed
        {
            get
            {
                int f = Time.frameCount;
                if (f != allowedFrame)
                {
                    allowedFrame = f;
                    allowedCache = bridge.InGame && !bridge.IsPaused && !bridge.SRUIOpen && screens.Count == 0;
                }
                return allowedCache;
            }
        }

        public bool AttackHeld => GameplayInputAllowed && (Input.GetMouseButton(0) || testAttack.Held(Time.frameCount));
        public bool AttackPressed => GameplayInputAllowed && (Input.GetMouseButtonDown(0) || testAttack.Pressed(Time.frameCount));
        public bool UseHeld => GameplayInputAllowed && (Input.GetMouseButton(1) || testUse.Held(Time.frameCount));
        public bool UsePressed => GameplayInputAllowed && (Input.GetMouseButtonDown(1) || testUse.Pressed(Time.frameCount));
        public float Scroll => GameplayInputAllowed ? Input.mouseScrollDelta.y : 0f;

        public bool GameKeyDown(KeyCode key)
        {
            if (key == KeyCode.None || !GameplayInputAllowed) return false;
            if (Input.GetKeyDown(key)) return true;
            int f = Time.frameCount;
            for (int i = 0; i < simKeys.Count; i++)
                if (simKeys[i].Key == key && simKeys[i].Frame == f) return true;
            return false;
        }

        // ------------------------------------------------------------------ test hooks (Testing module)
        // Simulated input is OR-ed with the real mouse/keys and stays subject to GameplayInputAllowed. Changes take
        // effect on the NEXT frame, held and pressed together exactly like a real button (GetMouseButton and
        // GetMouseButtonDown are both true in the press frame): a rising edge reports Pressed during exactly one frame,
        // a simulated key press makes GameKeyDown true during exactly one frame. "Next frame" means every consumer's
        // Update sees it once whatever the script execution order (the Testing module drives input from coroutines,
        // which run after Update).
        private struct TestButton
        {
            public bool Want;   // last requested state (edge detection)
            public int From;    // first frame the button counts as held (= its Pressed frame)
            public int Until;   // first frame it no longer counts as held

            public bool Held(int f) => f >= From && f < Until;
            public bool Pressed(int f) => f == From && f < Until;

            public void Set(bool held, int next)
            {
                if (held && !Want) { From = next; Until = int.MaxValue; }
                else if (!held && Want) Until = next;
                Want = held;
            }

            public static TestButton Off => new TestButton { Want = false, From = int.MaxValue, Until = int.MinValue };
        }

        private struct SimKey { public KeyCode Key; public int Frame; }
        private readonly List<SimKey> simKeys = new List<SimKey>(4);
        private TestButton testAttack = TestButton.Off, testUse = TestButton.Off, testSneak = TestButton.Off;

        public void SetTestInput(bool attackHeld, bool useHeld, bool sneakHeld = false)
        {
            int next = Time.frameCount + 1;
            testAttack.Set(attackHeld, next);
            testUse.Set(useHeld, next);
            testSneak.Set(sneakHeld, next);
        }

        public void ClearTestInput()
        {
            testAttack = TestButton.Off;
            testUse = TestButton.Off;
            testSneak = TestButton.Off;
        }

        public void SimulateKeyDown(KeyCode key)
        {
            if (key == KeyCode.None) return;
            int next = Time.frameCount + 1;
            for (int i = 0; i < simKeys.Count; i++)
                if (simKeys[i].Key == key && simKeys[i].Frame == next) return;
            simKeys.Add(new SimKey { Key = key, Frame = next });
        }

        /// <summary>Drops simulated key presses whose frame has passed (no allocation).</summary>
        private void PruneSimKeys()
        {
            if (simKeys.Count == 0) return;
            int f = Time.frameCount;
            for (int i = simKeys.Count - 1; i >= 0; i--)
                if (simKeys[i].Frame < f) simKeys.RemoveAt(i);
        }

        public KeyCode InventoryKey => CoreConfig.InventoryKey.Value;
        public KeyCode ChatKey => CoreConfig.ChatKey.Value;
        public KeyCode CommandKey => CoreConfig.CommandKey.Value;
        public KeyCode DropKey => CoreConfig.DropKey.Value;
        public KeyCode DebugKey => CoreConfig.DebugKey.Value;
        public KeyCode HudToggleKey => CoreConfig.HudToggleKey.Value;
        public KeyCode SneakKey => CoreConfig.SneakKey != null ? CoreConfig.SneakKey.Value : KeyCode.LeftControl;
        public bool SneakHeld
        {
            get
            {
                if (!GameplayInputAllowed) return false;
                if (testSneak.Held(Time.frameCount)) return true;
                var k = SneakKey;
                return k != KeyCode.None && Input.GetKey(k);
            }
        }
    }
}
