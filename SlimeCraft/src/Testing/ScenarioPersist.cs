using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using UnityEngine;

namespace SlimeCraft.Testing
{
    /// <summary>
    /// Step 20: persistence round trip through Slime Rancher's own flow. Remembers SC.Blocks.Count, a marker block and
    /// the serialized Minecraft inventory, saves (AutoSaveDirector.SaveAllNow, which also fires SlimeCraft's per-save
    /// write), quits to the main menu through the pause menu's own "Save and Quit" (PauseMenu.Quit), then loads the
    /// same sandbox game again with AutoSaveDirector.BeginLoad (as the main menu's Continue button does) and compares.
    /// Refuses to save unless SR's storage provider still points at the sandbox folder.
    /// </summary>
    internal sealed partial class Scenario
    {
        private IEnumerator PersistenceRoundTrip(StepResult r)
        {
            var gc = SRSingleton<GameContext>.Instance;
            if (gc == null || gc.AutoSaveDirector == null) { r.Fail("GameContext.AutoSaveDirector available"); yield break; }
            if (SC.SR == null || SC.Blocks == null || SC.Inventory == null || SC.Inventory.Inv == null)
            {
                r.Fail("SC.SR, SC.Blocks and SC.Inventory available");
                yield break;
            }
            var asd = gc.AutoSaveDirector;

            // ---- sandbox guard: never save anywhere but the sandbox
            string srPath = null;
            try
            {
                if (asd.StorageProvider is FileStorageProvider)
                    srPath = Traverse.Create(asd.StorageProvider).Method("SavePath").GetValue<string>();
            }
            catch (Exception e) { r.Note("could not query SR save path: " + e.Message); }
            bool sandboxed = srPath != null && TestRunner.SamePath(srPath, SandboxStats.Dir()) && !TestRunner.SamePath(srPath, TestConfig.RealSaveDir);
            if (!r.Check("SR still saves into the sandbox folder", sandboxed, srPath ?? "unknown")) yield break;
            string saveId = SC.SR.SaveGameId;
            if (!r.Check("the loaded game is the sandbox test game", saveId != null && saveId.Contains(TestConfig.TestGameName), saveId ?? "null")) yield break;

            // ---- state to compare
            try { if (SC.Hud != null && SC.Hud.ScreenOpen && SC.Commands != null) SC.Commands.Execute("/screen none"); } catch { }
            G.EnsureUnpaused();
            TI.Clear();
            try { SC.Entities?.ClearAll(); } catch { } // no loose drops that could be picked up between snapshot and save
            const string markerId = "minecraft:diamond_block";
            Vector3Int marker;
            bool markerOk = FindFreeCellAhead(3f, -0.6f, out marker, out string why);
            if (markerOk)
            {
                try { markerOk = SC.Blocks.SetBlock(marker, markerId, BlockFacing.North, true); }
                catch (Exception e) { markerOk = false; r.Note("SetBlock threw " + e.Message); }
            }
            else r.Note("no free cell for the marker block (" + why + ")");
            yield return G.Wait(0.4f);

            int blocks0 = SC.Blocks.Count;
            string inv0 = SC.Inventory.Inv.Serialize();
            int sel0 = SC.Inventory.SelectedSlot;
            bool creative0 = SC.Inventory.Creative;
            r.Note("before: SC.Blocks.Count " + blocks0 + ", marker " + (markerOk ? markerId + "@" + marker : "none") + ", game mode " +
                   (creative0 ? "creative" : "survival") + ", selected slot " + sel0 + ", hotbar " + G.HotbarText());
            if (blocks0 == 0) r.Note("no blocks in the world: the block comparison is trivial");

            // ---- 1) SR save now
            string sandbox = SandboxStats.Dir();
            DateTime newest0 = NewestSave(sandbox, out int savs0);
            bool saved = false;
            try { saved = asd.SaveAllNow(); }
            catch (Exception e) { r.Note("SaveAllNow threw " + e.Message); }
            r.Check("AutoSaveDirector.SaveAllNow() succeeded", saved);
            DateTime newest1 = NewestSave(sandbox, out int savs1);
            r.Check("a new .sav was written to the sandbox", newest1 > newest0 || savs1 > savs0, savs0 + " -> " + savs1 + " file(s) in " + sandbox);
            string modFile = ModSaveFile(saveId);
            bool modOk = modFile != null && File.Exists(modFile) && (DateTime.UtcNow - File.GetLastWriteTimeUtc(modFile)).TotalSeconds < 30;
            r.Check("SlimeCraft per-save data written on SR's save (ISRBridge.Saving)", modOk, modFile ?? "SC.DataDir unknown");
            yield return G.Wait(0.5f);

            // ---- 2) quit to the main menu through SR's own "Save and Quit"
            inv0 = SC.Inventory.Inv.Serialize();
            blocks0 = SC.Blocks.Count;
            const string quitVia = "PauseMenu.Quit()";
            var pm = SRSingleton<PauseMenu>.Instance;
            if (pm == null)
            {
                // the pause menu lives in the world scene; without it there is no SR-driven way to quit
                r.Fail("SR's pause menu is not available, so the game cannot be quit through its own Save and Quit");
                yield break;
            }
            try
            {
                pm.Quit();
            }
            catch (Exception e)
            {
                r.Fail("quitting to the main menu threw " + e.GetType().Name + ": " + e.Message);
                yield break;
            }
            float tq = G.Now;
            yield return G.WaitUntil(() => G.AtMainMenu, 90f);
            if (!r.Check("back at the main menu (" + quitVia + ")", G.AtMainMenu, G.F1(G.Now - tq) + " s")) yield break;
            r.Check("SC.SR.InGame is false at the main menu", !SC.SR.InGame);
            r.Note("at the main menu: SC.Blocks.Count " + SC.Blocks.Count + ", SaveGameId " + (SC.SR.SaveGameId ?? "null"));
            yield return G.Wait(2f); // menu fade-in
            yield return Shot(r, "20a_main_menu_after_quit");

            // ---- 3) load the newest save of the same sandbox game
            GameData.Summary summary = null;
            try
            {
                var games = asd.AvailableGamesByGameName();
                if (games != null && games.TryGetValue(saveId, out List<GameData.Summary> saves) && saves != null && saves.Count > 0) summary = saves[0];
                if (summary == null) summary = asd.GetSaveToContinue();
            }
            catch (Exception e) { r.Note("listing saves threw " + e.Message); }
            if (!r.Check("the test game's newest save is listed", summary != null && summary.name == saveId,
                summary != null ? summary.name + " / " + summary.saveName : "none")) yield break;
            bool loadError = false;
            try { asd.BeginLoad(summary.name, summary.saveName, () => loadError = true); }
            catch (Exception e) { r.Fail("AutoSaveDirector.BeginLoad threw " + e.Message); yield break; }
            float tl = G.Now;
            yield return G.Wait(1f);
            float rawSince = -1f;
            while (G.Now - tl < 150f)
            {
                if (loadError) break;
                if (G.InGameRaw)
                {
                    if (rawSince < 0f) rawSince = G.Now;
                    if (G.InGame || G.Now - rawSince > 20f) break;
                }
                yield return G.Wait(0.25f);
            }
            if (!r.Check("sandbox game loaded again (AutoSaveDirector.BeginLoad)", !loadError && G.InGame,
                (loadError ? "SR reported a load error; " : "") + G.F1(G.Now - tl) + " s")) yield break;
            yield return G.Wait(3f); // regions wake up, SlimeCraft persistence + chunk meshes
            G.EnsureUnpaused();

            // ---- 4) compare
            r.Check("same SR save id after reload", SC.SR.SaveGameId == saveId, SC.SR.SaveGameId ?? "null");
            r.Check("SC.Blocks.Count restored", SC.Blocks.Count == blocks0, blocks0 + " -> " + SC.Blocks.Count);
            if (markerOk)
                r.Check("marker block restored", SC.Blocks.GetBlock(marker) == markerId, marker + ": " + (SC.Blocks.GetBlock(marker) ?? "air"));
            string inv1 = SC.Inventory.Inv.Serialize();
            r.Check("Minecraft inventory restored", inv1 == inv0, inv1 == inv0 ? "identical (" + CountStacks(inv1) + " stacks)" : DiffInventories(inv0, inv1));
            r.Check("game mode restored", SC.Inventory.Creative == creative0, creative0 ? "creative" : "survival");
            if (SC.Inventory.SelectedSlot != sel0) r.Note("selected hotbar slot " + sel0 + " -> " + SC.Inventory.SelectedSlot);

            if (markerOk) G.LookAt(G.BlockCenter(marker));
            yield return G.Wait(0.8f);
            yield return Shot(r, "20b_after_reload");
        }

        private static DateTime NewestSave(string dir, out int count)
        {
            count = 0;
            DateTime newest = DateTime.MinValue;
            try
            {
                foreach (var f in Directory.GetFiles(dir, "*.sav"))
                {
                    count++;
                    var t = File.GetLastWriteTimeUtc(f);
                    if (t > newest) newest = t;
                }
            }
            catch { }
            return newest;
        }

        /// <summary>Core's per-save file: SC.DataDir/saves/&lt;sanitized save id&gt;.json (same sanitizing as Core).</summary>
        private static string ModSaveFile(string saveId)
        {
            if (string.IsNullOrEmpty(SC.DataDir) || saveId == null) return null;
            var sb = new System.Text.StringBuilder(saveId.Length);
            foreach (char c in saveId) sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.' ? c : '_');
            return Path.Combine(Path.Combine(SC.DataDir, "saves"), sb + ".json");
        }

        private static int CountStacks(string serialized)
        {
            int n = 0;
            foreach (var p in (serialized ?? "").Split(';')) if (p.Length > 0) n++;
            return n;
        }

        private static string DiffInventories(string a, string b)
        {
            var pa = (a ?? "").Split(';');
            var pb = (b ?? "").Split(';');
            var diffs = new List<string>();
            for (int i = 0; i < Math.Max(pa.Length, pb.Length) && diffs.Count < 6; i++)
            {
                string x = i < pa.Length ? pa[i] : "", y = i < pb.Length ? pb[i] : "";
                if (x != y) diffs.Add("slot " + i + ": '" + x + "' -> '" + y + "'");
            }
            return diffs.Count == 0 ? "different" : string.Join("; ", diffs.ToArray());
        }
    }
}
