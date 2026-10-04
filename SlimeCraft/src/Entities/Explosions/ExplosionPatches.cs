using System;
using HarmonyLib;
using UnityEngine;

namespace SlimeCraft.Entities
{
    /// <summary>
    /// Slime Rancher's PhysicsUtil.Explode only pushes colliders that carry their own Rigidbody; our entities keep
    /// colliders on child objects, so Boom slime (and other SR) explosions are forwarded
    /// to Minecraft mobs/items/TNT here. Our own SRExplode calls are skipped (ExplosionSystem.InOwnExplosion).
    /// </summary>
    [HarmonyPatch(typeof(PhysicsUtil), nameof(PhysicsUtil.Explode))]
    internal static class PhysicsUtilExplodePatch
    {
        private static void Postfix(GameObject source, float radius, float power, float minPlayerDamage, float maxPlayerDamage)
        {
            try
            {
                if (SC.Entities == null) return;
                ExplosionSystem.OnSRExplosion(source, radius, power, minPlayerDamage, maxPlayerDamage);
            }
            catch (Exception e) { ELog.Error("PhysicsUtil.Explode postfix", e); }
        }
    }
}
