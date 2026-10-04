using System;
using System.Collections.Generic;
using UnityEngine;

namespace SlimeCraft.Entities
{
    /// <summary>
    /// Base of every Minecraft entity GameObject (items, TNT, falling blocks, arrows, mobs).
    /// Colliders live on CHILD objects (the Rigidbody is on the root): Slime Rancher's PhysicsUtil.Explode and
    /// KillOnTrigger only act on colliders whose own GameObject has a Rigidbody, so SR systems never double-push or
    /// silently delete our entities; we handle SR explosions through a Harmony postfix instead (ExplosionPatches).
    /// Also implements SR's DeathHandler.Interface so any SR code that kills it goes through our removal.
    /// </summary>
    internal abstract class McEntity : MonoBehaviour, DeathHandler.Interface
    {
        public static readonly List<McEntity> All = new List<McEntity>();

        public Rigidbody Body;
        public bool Removed { get; private set; }
        public float SpawnTime;

        /// <summary>Approximate bounding box size (Minecraft hitbox width/height) used for explosion exposure.</summary>
        public virtual float BBWidth => 0.25f;
        public virtual float BBHeight => 0.25f;
        public Vector3 Feet => transform.position;
        public Vector3 Center => transform.position + Vector3.up * (BBHeight * 0.5f);
        public Bounds BB => new Bounds(Center, new Vector3(BBWidth, BBHeight, BBWidth));

        protected virtual void OnEnable() { if (!All.Contains(this)) All.Add(this); }
        protected virtual void OnDestroy() { All.Remove(this); }

        protected virtual void Awake() { SpawnTime = Time.time; }

        /// <summary>Removes the entity (deferred Destroy).</summary>
        public virtual void Discard()
        {
            if (Removed) return;
            Removed = true;
            All.Remove(this);
            try { foreach (var c in GetComponentsInChildren<Collider>()) if (c != null) c.enabled = false; } catch { }
            try { OnDiscard(); } catch (Exception e) { ELog.Error("OnDiscard " + name, e); }
            if (this != null && gameObject != null) Destroy(gameObject);
        }

        protected virtual void OnDiscard() { }

        /// <summary>Minecraft explosion hit: knockback in m/s and the (Minecraft) damage the explosion deals.</summary>
        public virtual void OnExplosion(Vector3 knockbackMs, float damage, GameObject source) { if (Body != null && !Body.isKinematic) Body.velocity += knockbackMs; }

        /// <summary>Slime Rancher's DeathHandler (KillOnTrigger, etc.).</summary>
        public void OnDeath(DeathHandler.Source source, GameObject sourceGameObject, string stackTrace)
        {
            try { OnSRKilled(source); } catch (Exception e) { ELog.Error("OnSRKilled", e); }
        }

        protected virtual void OnSRKilled(DeathHandler.Source source) => Discard();

        protected static bool Paused => SC.SR != null && SC.SR.IsPaused;

        /// <summary>Creates a child collider object on the given layer.</summary>
        protected T AddChildCollider<T>(string childName, int layer) where T : Collider
        {
            var go = new GameObject(childName);
            go.layer = layer;
            go.transform.SetParent(transform, false);
            return go.AddComponent<T>();
        }

        /// <summary>Configures a Minecraft-style rigidbody: custom gravity, no rotation, interpolated.</summary>
        protected Rigidbody SetupBody(float mass, bool continuous = true)
        {
            Body = gameObject.AddComponent<Rigidbody>();
            Body.mass = mass;
            Body.useGravity = false;
            Body.drag = 0f;
            Body.angularDrag = 0f;
            Body.freezeRotation = true;
            Body.interpolation = RigidbodyInterpolation.Interpolate;
            Body.collisionDetectionMode = continuous ? CollisionDetectionMode.Continuous : CollisionDetectionMode.Discrete;
            return Body;
        }

        private static PhysicMaterial frictionless;
        /// <summary>Zero friction / bounce material (we model Minecraft friction ourselves).</summary>
        public static PhysicMaterial Frictionless
        {
            get
            {
                if (frictionless == null)
                    frictionless = new PhysicMaterial("SC_Frictionless")
                    { dynamicFriction = 0f, staticFriction = 0f, bounciness = 0f, frictionCombine = PhysicMaterialCombine.Minimum, bounceCombine = PhysicMaterialCombine.Minimum };
                return frictionless;
            }
        }

        /// <summary>Makes the given colliders ignore every collider of the SR player (used when the item layer collides with the player).</summary>
        protected static void IgnorePlayer(Collider c)
        {
            try
            {
                var p = SC.SR?.Player;
                if (p == null || c == null) return;
                foreach (var pc in p.GetComponentsInChildren<Collider>(true)) if (pc != null) Physics.IgnoreCollision(c, pc, true);
            }
            catch (Exception e) { ELog.Error("IgnorePlayer", e); }
        }

        /// <summary>Ground probe: short sphere cast down from the bottom of the box.</summary>
        protected bool ProbeGround(float halfWidth, float extra, out RaycastHit hit)
        {
            float r = Mathf.Max(0.05f, halfWidth * 0.8f);
            var origin = transform.position + Vector3.up * (r + 0.05f);
            hit = default(RaycastHit);
            var hits = EPhys.HitBuffer;
            int n = Physics.SphereCastNonAlloc(origin, r, Vector3.down, hits, 0.05f + extra, ELayers.Ground, QueryTriggerInteraction.Ignore);
            float best = float.MaxValue; bool found = false;
            for (int i = 0; i < n; i++)
            {
                var h = hits[i];
                if (h.collider == null || h.distance <= 0f && h.point == Vector3.zero) continue;
                if (h.collider.transform.IsChildOf(transform)) continue;
                if (h.normal.y < 0.5f) continue;
                if (h.distance < best) { best = h.distance; hit = h; found = true; }
            }
            return found;
        }
    }
}
