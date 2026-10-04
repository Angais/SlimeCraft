using System;
using UnityEngine;

namespace SlimeCraft.Testing
{
    /// <summary>
    /// Testing module (Order 900, no service). Inert unless BepInEx config Testing.AutoTest = true; then it runs the
    /// automated scenario (see TestRunner/Scenario) inside a sandboxed SR save folder and quits.
    /// </summary>
    public sealed class TestingModule : MonoBehaviour, IModule
    {
        public string ModuleName => "Testing";
        public int Order => 900;

        private TestRunner runner;
        private bool startPending;
        private float startTime;
        private bool watchdogFired;
        private bool tickErrorLogged;

        public void Init()
        {
            TestConfig.Bind();
            if (!TestConfig.Active)
            {
                enabled = false; // zero per-frame cost for players
                return;
            }
            SC.Log.LogWarning("=== SlimeCraft AUTOMATED TEST MODE === Slime Rancher saves/profile/settings are redirected to " +
                              SandboxStats.Dir() + " for this session; output goes to " + TestConfig.OutputDir);
            // one-shot flag: a crash or a killed game never leaves the next normal launch in test mode
            if (!TestConfig.KeepAutoTestEnabled.Value)
            {
                try { TestConfig.AutoTest.Value = false; }
                catch (Exception e) { SC.Log.LogWarning("Could not reset Testing.AutoTest: " + e.Message); }
            }
            Application.runInBackground = true; // keep running when the window loses focus
            try { Screen.SetResolution(Mathf.Max(800, TestConfig.Width.Value), Mathf.Max(600, TestConfig.Height.Value), false); }
            catch (Exception e) { SC.Log.LogWarning("SetResolution failed: " + e.Message); }
            runner = new TestRunner(this);
            startPending = true;
            startTime = Time.realtimeSinceStartup;
        }

        private void Start()
        {
            if (!startPending) return;
            startPending = false;
            StartCoroutine(runner.Run());
        }

        private void Update()
        {
            if (runner == null || runner.Finished) return;
            try
            {
                runner.Tick(Time.unscaledDeltaTime);
                float limit = Mathf.Max(1, TestConfig.MaxMinutes.Value) * 60f;
                if (!watchdogFired && Time.realtimeSinceStartup - startTime > limit)
                {
                    watchdogFired = true;
                    StopAllCoroutines();
                    runner.Abort("watchdog: run exceeded Testing.MaxMinutes (" + TestConfig.MaxMinutes.Value + ")", true);
                }
            }
            catch (Exception e)
            {
                if (!tickErrorLogged) SC.Log.LogError("[Test] tick failed: " + e); // log once, never every frame
                tickErrorLogged = true;
            }
        }

        private void OnApplicationQuit()
        {
            // window closed by hand or SR quit on its own: still leave a (partial) report behind
            if (runner != null && !runner.Finished) runner.Abort("game quit before the run finished", false);
        }
    }
}
