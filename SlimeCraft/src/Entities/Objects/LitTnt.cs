using System;
using UnityEngine;

namespace SlimeCraft.Entities
{
    /// <summary>
    /// Lit TNT with Minecraft's behaviour and look: 0.98 hitbox, gravity 0.04 b/t², drag 0.98, ground friction 0.7,
    /// a small hop when lit (0.2 b/t up, 0.02 b/t in a random direction), fuse ticks (default 80), a smoke particle
    /// every tick, a white flash every other 5 ticks, swelling by up to 30 % in the last 10 ticks, and an explosion of
    /// power 4 at y + 1/16. Position given to Spawn is the cube CENTER.
    /// </summary>
    internal sealed class LitTnt : McEntity
    {
        public float FuseTicks;
        public float Power = 4f;
        public GameObject Igniter;
        private Transform vis;
        private MeshRenderer overlay;
        private MaterialPropertyBlock mpb;
        private bool grounded;
        private float smokeAcc;

        public override float BBWidth => 0.98f;
        public override float BBHeight => 0.98f;

        public static LitTnt Spawn(Vector3 center, int fuseTicks, Vector3 velocity, bool playSound, GameObject igniter = null)
        {
            var go = new GameObject("MC PrimedTnt");
            go.layer = ELayers.Mob;
            go.transform.position = center - Vector3.up * 0.49f;
            var t = go.AddComponent<LitTnt>();
            t.FuseTicks = Mathf.Max(1, fuseTicks);
            t.Igniter = igniter;
            t.Init(velocity, playSound);
            return t;
        }

        private void Init(Vector3 velocity, bool playSound)
        {
            SetupBody(1f);
            if (velocity == Vector3.zero)
            {
                Vector2 sideways = Rng.OnUnitCircle() * 0.02f;
                velocity = new Vector3(sideways.x, 0.2f, sideways.y) * Mc.BptToMs;
            }
            Body.velocity = velocity;
            var col = AddChildCollider<BoxCollider>("col", ELayers.Mob);
            col.size = new Vector3(0.98f, 0.98f, 0.98f);
            col.center = new Vector3(0f, 0.49f, 0f);
            col.sharedMaterial = Frictionless;

            vis = new GameObject("vis").transform;
            vis.SetParent(transform, false);
            vis.localPosition = new Vector3(0f, 0.5f, 0f);
            Mesh mesh = null; Material mat = null;
            try { mesh = SC.ItemVisuals?.GetItemMesh("minecraft:tnt"); mat = SC.ItemVisuals?.GetItemMaterial("minecraft:tnt"); }
            catch (Exception e) { ELog.Error("TNT visuals", e); }
            if (mesh == null) mesh = EMat.UnitCube();
            var mgo = new GameObject("cube");
            mgo.layer = gameObject.layer;
            mgo.transform.SetParent(vis, false);
            mgo.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = mgo.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat != null ? mat : EMat.Lit("tnt_fallback", EMat.Tex("block/tnt_side"));

            var omat = EMat.Overlay(null);
            if (omat != null)
            {
                var ogo = new GameObject("flash");
                ogo.layer = gameObject.layer;
                ogo.transform.SetParent(mgo.transform, false);
                ogo.AddComponent<MeshFilter>().sharedMesh = EMat.WhiteColored(mesh);
                overlay = ogo.AddComponent<MeshRenderer>();
                overlay.sharedMaterial = omat;
                overlay.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                overlay.enabled = false;
                mpb = EMat.SpriteBlock(new Color(1f, 1f, 1f, 0.75f));
                overlay.SetPropertyBlock(mpb);
            }
            if (playSound) SC.Audio?.Play("entity.tnt.primed", transform.position + Vector3.up * 0.5f, 1f, 1f);
            UpdateVisual(0f);
        }

        private void Update()
        {
            if (Removed) return;
            try
            {
                float dt = Time.deltaTime;
                if (dt <= 0f) return;
                FuseTicks -= dt * Mc.TPS;
                smokeAcc += dt * Mc.TPS;
                while (smokeAcc >= 1f) { smokeAcc -= 1f; Particles.Smoke(transform.position + Vector3.up * 0.5f, Vector3.zero); }
                if (FuseTicks <= 0f) { Detonate(); return; }
                UpdateVisual(0f);
            }
            catch (Exception e) { ELog.Error("LitTnt.Update", e); }
        }

        private void UpdateVisual(float partial)
        {
            // swell by up to 30 % over the last 10 ticks (slow start, fast end) and flash white every other 5 ticks
            float ticksLeft = FuseTicks + 1f;
            float swell = Mathf.Pow(Mathf.Clamp01((10f - ticksLeft) / 10f), 4f);
            vis.localScale = Vector3.one * (1f + 0.3f * swell);
            if (overlay != null) overlay.enabled = Mathf.FloorToInt(ticksLeft / 5f) % 2 == 0;
        }

        private void FixedUpdate()
        {
            if (Removed || Body == null || Body.isKinematic) return;
            try
            {
                float dt = Time.fixedDeltaTime;
                var v = Body.velocity;
                grounded = ProbeGround(0.49f, 0.06f, out var _);
                v.y -= 0.04f * Mc.Bpt2ToMs2 * dt;
                float drag = Mc.PerTick(0.98f, dt);
                v *= drag;
                if (grounded)
                {
                    float f = Mc.PerTick(0.7f, dt);
                    v.x *= f; v.z *= f;
                }
                Body.velocity = v;
            }
            catch (Exception e) { ELog.Error("LitTnt.FixedUpdate", e); }
        }

        public void Detonate()
        {
            if (Removed) return;
            var pos = transform.position + Vector3.up * 0.0625f;
            float power = Power;
            Discard();
            // the TNT itself is the damage source (SR's hurt direction points at it; Destroy is deferred to the end of the frame),
            // the igniter is reported as the attacker, so Minecraft mobs react to whoever lit the TNT
            try { ExplosionSystem.Boom(pos, power, gameObject, true, "TNT", Igniter); }
            catch (Exception e) { ELog.Error("TNT explode", e); }
        }

        public override void OnExplosion(Vector3 knockbackMs, float damage, GameObject source)
        {
            if (Body != null && !Body.isKinematic) Body.velocity += knockbackMs;
        }

        protected override void OnSRKilled(DeathHandler.Source source) => Discard();
    }
}
