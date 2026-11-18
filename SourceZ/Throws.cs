using System;
using System.Collections.Generic;
using CombatExtended;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRimCEPackZ
{
    // Thrown grenades between floors (CE: Verb_ThrowGrenade). CE works the throw out in FindAngle from the raw distance,
    // the target's height over its own floor and the cover on the raw line; TryCastShot then throws along the raw shoot
    // line. Between floors (only): FindAngle with the ground distance, the target's height in the thrower's frame (the
    // storeys between) and CE's lift over cover for the cover on the thrower's own storey (the 3D walk's cells there; a
    // parapet in front of a thrower upstairs); TryCastShot's heading comes over the ground through the frame transpiler
    // (Aim). The grenade is then an ordinary CE projectile and crosses the floor planes through Frames.
    // FindAngle_Prefix is copied from CE Verb_ThrowGrenade.FindAngle (its raw distance and its own line of cells can't be
    // answered by ProjectZ's 3D scope); watched (CopyWatches).
    internal static class Throws
    {
        private static readonly List<ProjectZ.Api.LineStep> steps = new List<ProjectZ.Api.LineStep>();
        private static Func<Verb_LaunchProjectileCE, float> shotSpeed;
        public static int Angles, Lobs, HighLobs, Refused;   // (up: lowest lobs taken, full-speed high arcs as the fallback, throws refused)

        public static void Patch(Harmony h)
        {
            shotSpeed = AccessTools.MethodDelegate<Func<Verb_LaunchProjectileCE, float>>(AccessTools.PropertyGetter(typeof(Verb_LaunchProjectileCE), "ShotSpeed"));
            PackZ.Prefix(h, Aim.ByName(typeof(Verb_ThrowGrenade), "FindAngle", 6), typeof(Throws), nameof(FindAngle_Prefix));
        }

        public static bool FindAngle_Prefix(Verb_ThrowGrenade __instance, LocalTargetInfo target, ref float smokeDensity, ref bool roofed, ref float launchAngle, ref float speed, ref int ticks, ref bool __result)
        {
            Thing caster = __instance.caster;
            Map map = caster?.Map;
            Layout L = Layout.Of(map);
            if (L == null)
            {
                return true;
            }
            int ks = L.FrameOf(caster.Position), kt = L.FrameOf(target.Cell);
            if (ks == kt || ks == Layout.None || kt == Layout.None)
            {
                return true;
            }
            PackZ.Changed++;
            Angles++;
            roofed = false;
            smokeDensity = 0f;
            ticks = 0;
            Thing thing = target.Thing;
            Vector3 from = caster.TrueCenter();
            Vector3 to = L.TranslateRows(thing != null ? thing.DrawPos : target.Cell.ToVector3Shifted(), kt, ks);
            float magnitude = new Vector2(to.x - from.x, to.z - from.z).magnitude;
            ProjectilePropertiesCE props = __instance.projectilePropsCE;
            float g = props.GravityPerHeight / 60f / 60f;
            float manipulation = __instance.ShooterPawn?.health?.capacities?.GetLevel(PawnCapacityDefOf.Manipulation) ?? 1f;
            float v = shotSpeed(__instance) * manipulation / 60f;
            float targetHeight = (kt - ks) * Layout.S;
            if (thing != null)
            {
                CollisionVertical cv = new CollisionVertical(thing);
                targetHeight += (cv.HeightRange.max + cv.HeightRange.min) / 2f;
            }
            float dh = targetHeight - __instance.ShotHeight;
            float disc = v * v * v * v - g * (g * magnitude * magnitude + 2f * dh * v * v);
            if (disc < 0f || magnitude < 0.01f)
            {
                launchAngle = 0f;
                speed = 0f;
                __result = false;
                return false;
            }
            // CE throws on the low arc (also down). A throw up onto a higher floor is lobbed after the cover lift below: the
            // lowest arc onto the target that rises through the floor plane over open cells (Aim.LowestLob: a softer throw,
            // about 1-2 s; user decision 2026-10-30), else CE's full-speed high arc when it too rises through every floor plane
    // between over open cells, else no throw.
            launchAngle = Mathf.Atan((v * v - Mathf.Sqrt(disc)) / (g * magnitude));
            speed = v;
            float highAngle = Mathf.Atan((v * v + Mathf.Sqrt(disc)) / (g * magnitude));
            // CE's lift over the cover on the line, for the cover on the thrower's storey (the arc's start)
            Vector3 src = from;
            src.y = __instance.ShotHeight;
            Vector3 dst = to;
            dst.y = targetHeight;
            int n = ProjectZ.Api.WalkLine3D(L.ToVirtual(src, ks), L.ToVirtual(dst, ks), map, steps, false);
            bool far = false;
            for (int i = 1; i < n; i++)
            {
                ProjectZ.Api.LineStep s = steps[i];
                IntVec3 gc = L.Ground(s.cell);
                IntVec3 c = L.Cell(gc, ks);
                if (!c.IsValid || !c.InBounds(map))
                {
                    continue;
                }
                if (c.AnyGas(map, GasType.BlindSmoke))
                {
                    smokeDensity += 0.7f;   // Copied from CE Verb_ThrowGrenade.FindAngle (watched), as the lift below
                }
                roofed = roofed || map.roofGrid.RoofAt(c) != null;
                Thing t2 = c.GetFirstPawn(map) ?? c.GetCover(map);
                if (t2 == null || (thing != null && t2.Equals(thing)))
                {
                    continue;
                }
                far = far || i >= n / 2;
                float at = far ? i + 0.5f : i - 0.5f;
                float h = new CollisionVertical(t2).Max * 1.1f;
                float a = h / (at * (at - magnitude));
                float b = (0f - a) * magnitude;
                float x = (0f - b) / (2f * a);
                float top = a * x * x + b * x;
                float vy = Mathf.Sqrt(2f * top / g);
                float angle = Mathf.Atan(4f * top / magnitude);
                float sp = magnitude / (2f * vy * Mathf.Cos(angle));
                if (angle > launchAngle)
                {
                    launchAngle = angle;
                    speed = sp;
                    if (speed > v && kt <= ks)   // (up: the lob below finds its own speed over this lift)
                    {
                        __result = false;
                        return false;
                    }
                }
            }
            if (kt > ks)
            {
                Vector2 flat = new Vector2(to.x - from.x, to.z - from.z) / magnitude;
                if (Aim.LowestLob(L, map, src, flat, magnitude, dh, g, v, ks, kt, launchAngle, out float lobAngle, out float lobSpeed))
                {
                    launchAngle = lobAngle;
                    speed = lobSpeed;
                    Lobs++;
                }
                else if (Aim.LobClear(L, map, src, flat, magnitude, highAngle, v, g, ks, kt))
                {
                    launchAngle = highAngle;
                    speed = v;
                    HighLobs++;
                }
                else
                {
                    // no arc gets past the floor planes on the way up (the high arc would meet a slab or roof over the
                    // thrower): no throw (CE: TryCastShot ends), never one into the slab
                    Refused++;
                    PackZ.Once("throw", "A throw up to another floor found no arc past the floor slab on its way: not thrown (noted once).");
                    launchAngle = 0f;
                    speed = 0f;
                    ticks = 0;
                    __result = false;
                    return false;
                }
            }
            ticks = (int)(magnitude / (Mathf.Cos(launchAngle) * speed)) + 1;
            ticks = Rand.RangeInclusive(ticks, props.explosionDelay);
            __result = true;
            return false;
        }
    }
}
