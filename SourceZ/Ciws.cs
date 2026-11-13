using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using CombatExtended;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace IsoRimCEPackZ
{
    // CIWS between floors (D9b, required): CE's anti-projectile and anti-skyfaller turrets (VerbCIWS: Building_CIWS_CE, the
    // KPV, the mech charge blaster) find, aim at and meet targets by their raw positions. On a map with floors most targets
    // are in another frame: a shell over the top floor is in the top storey's frame, a drop pod lands on a floor's band.
    //  - Target finding and aim (VerbCIWS<T>.TryFindCEShootLineFromTo overrides CE's aim without calling it, so the frame
    //    transpiler of Aim does not reach it): the predicted positions it walks (PredictPositions of the projectile and
    //    skyfaller verbs, a postfix) are moved into the turret's frame (rows and height: Layout.Translate). Everything
    //    downstream is then CE's own: range, CanReachPos, the intercept tick, the target height (GetTargetHeight returns
    //    the point's height), the shot angle and lead. A predicted point in another storey than the turret's is only kept
    //    when the 3D line from the muzzle to it crosses no solid floor plane (ProjectZ Api WalkLine3D: slabs, roofs); a
    //    blocked point is handed on far out of range, so CE passes over it as it passes over any point out of range.
    //  - The CIWS bullet meeting its target (ProjectileCE_CIWS.CanCollideWith: CE compares the two draw positions flat, or
    //    the skyfaller's position with the bullet's shot line): through the scope (Scope), the target is seen in the
    //    bullet's frame. The bullet itself crosses floor planes through Frames (solid planes stop it).
    //  - The turret top's aim (Building_CIWS_CE.Tick sets it from the raw draw positions): the target seen in the turret's
    //    frame.
    // A turret and a target in one storey (and every map without floors): CE's own values.
    internal static class Ciws
    {
        private static readonly List<ProjectZ.Api.LineStep> steps = new List<ProjectZ.Api.LineStep>();
        public static int Translated, LineChecks, BlockedPoints, TopAims;
        public static Action<string> Debug;   // harness

        public static bool On;   // every patch of the group landed

        public static void Patch(Harmony h)
        {
            MethodInfo pp = Aim.ByName(typeof(VerbCIWSProjectile), "PredictPositions", 2);
            MethodInfo ps = Aim.ByName(typeof(VerbCIWSSkyfaller), "PredictPositions", 2);
            MethodInfo cc = Aim.ByName(typeof(ProjectileCE_CIWS), "CanCollideWith", 2);
            MethodInfo tick = Aim.ByName(typeof(Building_CIWS_CE), "Tick", 0);
            Scope.CheckSwaps();
            PackZ.Postfix(h, pp, typeof(Ciws), nameof(Predict_Postfix));
            PackZ.Postfix(h, ps, typeof(Ciws), nameof(Predict_Postfix));
            Scope.Entry(h, cc, nameof(Scope.Instance_Prefix));
            PackZ.Postfix(h, tick, typeof(Ciws), nameof(Top_Postfix));
            On = true;
        }

        // Not key: CIWS against comp targets (VerbCIWS_Comp<T>, other mods' targets; none in CE itself). The postfix lands
        // on the generic class's shared code.
        public static void PatchComp(Harmony h)
        {
            Type comp = typeof(VerbCIWS_Comp<>).MakeGenericType(typeof(CompCIWSTarget));
            PackZ.Postfix(h, Aim.ByName(comp, "PredictPositions", 2), typeof(Ciws), nameof(Predict_Postfix));
            PackZ.Transpiler(h, Aim.ByName(typeof(ProjectileCE_CIWS), "CanCollideWith", 2), typeof(Ciws), nameof(CompCollide_Transpiler));
        }

        // ---- the CIWS bullet meeting a comp target (CompCIWSTarget.CanCollideWith: the other mod's own test, in raw rows
        // and local heights). Between floors the bullet is seen in the target's frame for that call (FrameView: rows and
        // heights moved, a pure translation), so the mod's test compares both in one frame; a contact it finds counts only
        // when the floor planes between the bullet's storey and the target's are open at the bullet's column (ProjectZ Api
        // PlaneSolid: a slab between them stops it, as the bullet itself would be stopped).
        public static int CompViews, CompViewContacts, CompPlaneRefused;

        public static IEnumerable<CodeInstruction> CompCollide_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo target = AccessTools.Method(typeof(CompCIWSTarget), nameof(CompCIWSTarget.CanCollideWith));
            MethodInfo mine = AccessTools.Method(typeof(Ciws), nameof(CompCollide));
            int found = 0;
            foreach (CodeInstruction ci in instructions)
            {
                if ((ci.opcode == OpCodes.Call || ci.opcode == OpCodes.Callvirt) && ci.operand is MethodInfo mi && mi == target)
                {
                    ci.opcode = OpCodes.Call;
                    ci.operand = mine;
                    found++;
                }
                yield return ci;
            }
            if (found != 1)
            {
                throw new InvalidOperationException("ProjectileCE_CIWS.CanCollideWith: " + found + " CompCIWSTarget.CanCollideWith calls (expected 1)");
            }
        }

        public static bool? CompCollide(CompCIWSTarget comp, ProjectileCE_CIWS p, out float dist)
        {
            Thing t = comp.parent;
            Layout L = Layout.Of(p.Map);
            if (L == null || t == null || !t.Spawned || t.Map != p.Map)
            {
                return comp.CanCollideWith(p, out dist);
            }
            int kp = L.FrameOf(Frames.exactGet(p)), kt = L.FrameOf(t.Position);
            if (kp == kt || kp == Layout.None || kt == Layout.None)
            {
                return comp.CanCollideWith(p, out dist);
            }
            CompViews++;
            Vector3 b = L.ToVirtual(Frames.exactGet(p), kp);
            bool? met;
            try
            {
                FrameView.Move(p, L, kp, kt);
                met = comp.CanCollideWith(p, out dist);
            }
            finally
            {
                if (p.Spawned && !p.Destroyed)
                {
                    FrameView.Move(p, L, kt, kp);
                }
            }
            if (met == true)
            {
                // (a contact found in the view, or refused at a plane: the outcome differs from the raw rows; a view
                // without contact changes nothing CE computes)
                PackZ.Changed++;
                CompViewContacts++;
                float storey = ProjectZ.Api.StoreyHeight;
                Vector3 edge = new Vector3(b.x, kt > kp ? kt * storey + 0.01f : (kt + 1) * storey - 0.01f, b.z);
                if (!Shields.PlanesOpen(L, p.Map, b, edge))
                {
                    CompPlaneRefused++;
                    dist = -1f;
                    return false;
                }
            }
            return met;
        }

        public static void Predict_Postfix(VerbCIWS __instance, Thing target, int maxTicks, ref IEnumerable<Vector3> __result)
        {
            Thing caster = __instance?.caster;
            Map map = caster?.Map;
            Layout L = Layout.Of(map);
            if (L == null || target == null || __result == null || !target.Spawned || target.Map != map)
            {
                return;
            }
            int ks = L.FrameOf(caster.Position), kt = L.FrameOf(target.Position);
            if (ks == Layout.None || kt == Layout.None)
            {
                return;
            }
            __result = Seen(__result, __instance, caster, target, map, L, ks, kt, maxTicks);
        }

        // CE walks PredictPositions(target, warmup + 60).Skip(warmup) (and (target, warmup + 1).Skip(warmup) for instant
        // shots): the skipped warmup ticks are only translated, the line is checked from there on.
        private const int CeWindowTicks = 60;   // Copied from CE VerbCIWS<T>.TryFindCEShootLineFromTo, a local there (watched, CopyWatches)

        private static IEnumerable<Vector3> Seen(IEnumerable<Vector3> src, VerbCIWS verb, Thing caster, Thing target, Map map, Layout L, int ks, int kt, int maxTicks)
        {
            bool moved = ks != kt;
            if (moved)
            {
                PackZ.Changed++;
                Translated++;
            }
            float S = Layout.S;
            float range = verb.verbProps.range;
            Vector3 c = caster.Position.ToVector3Shifted();
            int first = maxTicks <= 1 ? int.MaxValue : Mathf.Max(0, maxTicks - CeWindowTicks);
            int i = 0;
            bool overhead = target.def.projectile?.flyOverhead ?? true;
            float aPrev = float.NaN;
            foreach (Vector3 p0 in src)
            {
                // CE predicts the target's flight in its own frame as if nothing stood in its way below or above that
                // storey; the target really stops on the first floor it comes down on (and a direct-fire projectile under
                // a solid plane): the prediction ends there.
                float a = kt * S + p0.y;
                if (!float.IsNaN(aPrev) && Stops(L, map, p0, kt, aPrev, a, S, overhead))
                {
                    PackZ.Changed++;
                    CutPaths++;
                    yield break;
                }
                aPrev = a;
                Vector3 p = moved ? L.Translate(p0, kt, ks) : p0;
                if (i++ >= first && ((p.y < 0f && ks > L.min) || (p.y >= S && ks < L.max)))
                {
                    float dx = p.x - c.x, dz = p.z - c.z;
                    if (dx * dx + dz * dz <= range * range && Blocked(verb, c, p, map, L, ks, target))
                    {
                        PackZ.Changed++;
                        BlockedPoints++;
                        yield return new Vector3(c.x + 1e6f, p.y, c.z + 1e6f);   // out of range: CE passes over it
                        continue;
                    }
                }
                yield return p;
            }
        }

        // The step of a predicted path from absolute height aPrev to a (CE units, floor k at k * S) at point p0 (frame kt's
        // rows) passes down onto a floor (a floor plane with a floor in that column) or, for direct fire, up into a solid
        // plane.
        private static bool Stops(Layout L, Map map, Vector3 p0, int kt, float aPrev, float a, float S, bool overhead)
        {
            int jPrev = Mathf.FloorToInt(aPrev / S), j = Mathf.FloorToInt(a / S);
            if (j == jPrev)
            {
                return false;
            }
            IntVec3 g = new IntVec3(Mathf.FloorToInt(p0.x), 0, Mathf.FloorToInt(p0.z) - L.Shift(kt));
            if (!L.OnGround(g.z) || g.x < 0 || g.x >= L.width)
            {
                return false;
            }
            if (j < jPrev)
            {
                for (int pl = Mathf.Min(jPrev, L.max); pl > j && pl > L.min; pl--)
                {
                    if (ProjectZ.Api.HasFloorAt(g, pl, map))
                    {
                        return true;
                    }
                }
                return false;
            }
            if (!overhead)
            {
                for (int pl = jPrev + 1; pl <= j && pl <= L.max; pl++)
                {
                    if (ProjectZ.Api.PlaneSolid(g, pl, map))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        public static int CutPaths;

        // The 3D line from the turret's muzzle to point p (both in frame ks) crosses a solid floor plane.
        private static bool Blocked(VerbCIWS verb, Vector3 c, Vector3 p, Map map, Layout L, int ks, Thing target)
        {
            LineChecks++;
            Vector3 src = new Vector3(c.x, verb.ShotHeight, c.z);
            int n = ProjectZ.Api.WalkLine3D(L.ToVirtual(src, ks), L.ToVirtual(p, ks), map, steps, true);
            if (n == 0)
            {
                return true;
            }
            // (WalkLine3D reports a plane crossed where the line passes a cell corner too: ProjectZ pzapi6 494275d)
            float storey = ProjectZ.Api.StoreyHeight;
            for (int i = 0; i < n; i++)
            {
                if (steps[i].solidPlane != int.MinValue)
                {
                    return true;
                }
            }
            Debug?.Invoke("    CIWS clear line to " + target + " " + L.ToVirtual(src, ks).ToString("F2") + " -> " + L.ToVirtual(p, ks).ToString("F2") + ", " + n + " steps: "
                + string.Join(" ", steps.GetRange(0, n).FindAll(x => x.floor > 0 || x.hi + x.floor * storey > storey - 0.2f).ConvertAll(x => L.Ground(x.cell) + "f" + x.floor + "[" + x.lo.ToString("0.00") + "," + x.hi.ToString("0.00") + "]" + (ProjectZ.Api.PlaneSolid(L.Ground(x.cell), 1, map) ? "S" : "o"))) + " | target " + (target is ProjectileCE pc ? Frames.exactGet(pc).ToString("F2") + " at " + pc.Position + " landed " + pc.landed + " ticks " + pc.FlightTicks + "/" + pc.ticksToImpact : ""));
            return false;
        }

        // The turret top turned to the target seen in the turret's frame.
        public static void Top_Postfix(Building_CIWS_CE __instance)
        {
            LocalTargetInfo t = __instance.CurrentTarget;
            if (!t.IsValid || !t.HasThing || !t.Thing.Spawned || __instance.top == null)
            {
                return;
            }
            Layout L = Layout.Of(__instance.Map);
            if (L == null)
            {
                return;
            }
            int ks = L.FrameOf(__instance.Position), kt = L.FrameOf(t.Thing.Position);
            if (ks == kt || ks == Layout.None || kt == Layout.None)
            {
                return;
            }
            PackZ.Changed++;
            TopAims++;
            __instance.top.CurRotation = (Views.DrawPosFrom(t.Thing, L, kt, ks) - __instance.DrawPos).AngleFlat();
        }
    }
}
