using System;
using CombatExtended;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRimCEPackZ
{
    // Slabs hit by Combat Extended's explosions (user decision 2026-10-30): ProjectZ judges a heavy hit on a floor slab by
    // the damage at the cell (SlabDamage: at least the slab's threshold, 35; armoured slabs 55). CE's blast damage is far
    // above vanilla's (a frag grenade 56 with falloff, a rocket 180) and CE adds a ring of neighbour cells to many blasts,
    // so CE blasts broke two to four times the slab vanilla's do. For floor slab cells only, a CE explosion is judged at
    // vanilla level:
    //  - only cells within the explosion's own radius count (a ring cell, CE's applyDamageToExplosionCellsNeighbors, is
    //    judged with 0 damage);
    //  - the damage at the cell is min(CE's damage there, VanillaCap): 50, the vanilla frag grenade and mortar shell (no
    //    falloff), so armoured slabs (55) never take a heavy hit from a CE blast.
    // Nothing else changes: pawns, buildings, ground terrain and every non-slab cell keep CE's damage. Mechanism: a context
    // around DamageWorker.ExplosionDamageTerrain (the game's terrain step, where ProjectZ's slab prefix asks
    // Explosion.GetDamageAmountAt) set for an ExplosionCE's Bomb damage on a slab cell, and a postfix on GetDamageAmountAt
    // that applies the rule in that context only (CE's own prefix there computes CE's value first).
    // The slab test uses the public ProjectZ.Api only (SlabCell): an upper-floor cell with a floor (Api.HasFloorAt) whose
    // terrain or the terrain under it can take a bomb hit (destroyOnBombDamageThreshold >= 0). In effect that is ProjectZ's
    // slab cell (slabs, damaged slabs, floors laid on them): the other floor terrains (decks, rock floors) have no
    // threshold, so the game's terrain step never weighs a blast's damage there.
    internal static class SlabHits
    {
        // The pack's own tuning value (user decision 2026-10-30), not read from the game: vanilla's Bomb damage (Core
        // Damages_Misc.xml defaultDamage 50). The live DamageDefOf.Bomb.defaultDamage can't stand in for it: CE patches it
        // to 156 (CE Patches/Core/DamageDefs/Damages_LocalInjury.xml), which would let CE blasts break armoured slabs (55).
        public const int VanillaCap = 50;

        // Harness counters: slab cells judged (capped), ring cells dropped.
        public static int Judged, RingDropped;

        private static Func<Explosion, IntVec3, bool> ringCell;

        // the context: the explosion and cell the terrain step is judging now (null: none)
        private static Explosion ctx;
        private static IntVec3 ctxCell;
        private static bool ctxRing;

        public static void Patch(Harmony h)
        {
            ringCell = AccessTools.MethodDelegate<Func<Explosion, IntVec3, bool>>(AccessTools.Method(typeof(Explosion), "ShouldCellBeAffectedOnlyByDamage")
                ?? throw new MissingMethodException("Explosion.ShouldCellBeAffectedOnlyByDamage"));
            System.Reflection.MethodInfo terrain = PackZ.Method(typeof(DamageWorker), "ExplosionDamageTerrain");
            // the context first (before ProjectZ's slab prefix and anything else that asks the damage there)
            h.Patch(terrain, prefix: new HarmonyMethod(typeof(SlabHits), nameof(Terrain_Prefix)) { priority = Priority.First },
                finalizer: new HarmonyMethod(typeof(SlabHits), nameof(Terrain_Finalizer)));
            PackZ.Applied += 2;
            PackZ.Postfix(h, PackZ.Method(typeof(Explosion), nameof(Explosion.GetDamageAmountAt)), typeof(SlabHits), nameof(Damage_Postfix));
        }

        public static void Terrain_Prefix(DamageWorker __instance, Explosion explosion, IntVec3 c)
        {
            ctx = null;
            if (!(explosion is ExplosionCE) || __instance.def != DamageDefOf.Bomb)
            {
                return;
            }
            Map map = explosion.Map;
            Layout L = Layout.Of(map);
            if (L == null || !SlabCell(L, c, map))
            {
                return;
            }
            ctx = explosion;
            ctxCell = c;
            ctxRing = ringCell(explosion, c);
        }

        // A slab cell (see above): on floor 1 or higher, a floor there, a bomb threshold on its terrain or the one under it.
        private static bool SlabCell(Layout L, IntVec3 c, Map map)
        {
            int k = L.FrameOf(c);
            if (k == Layout.None || k < 1 || !c.InBounds(map) || !ProjectZ.Api.HasFloorAt(L.Ground(c), k, map))
            {
                return false;
            }
            TerrainGrid tg = map.terrainGrid;
            TerrainDef under = tg.UnderTerrainAt(c);
            return tg.TopTerrainAt(c).destroyOnBombDamageThreshold >= 0f || (under != null && under.destroyOnBombDamageThreshold >= 0f);
        }

        public static Exception Terrain_Finalizer(Exception __exception)
        {
            ctx = null;
            return __exception;
        }

        public static void Damage_Postfix(Explosion __instance, IntVec3 c, ref int __result)
        {
            if (ctx == null || ctx != __instance || c != ctxCell)
            {
                return;
            }
            if (ctxRing)
            {
                RingDropped++;
                __result = 0;
            }
            else
            {
                Judged++;
                __result = Mathf.Min(__result, VanillaCap);
            }
        }
    }
}
