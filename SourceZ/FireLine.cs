using System;
using System.Collections.Generic;
using System.Linq;
using CombatExtended;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRimCEPackZ
{
    // The fired line between floors: the one vertical aim of direct fire at another floor, for the shot (ShiftTarget) and
    // for Sight alike.
    //
    // CE works out a shot's vertical angle on a flat world: from its range estimate, set once per burst, and a burst locks
    // the angle. On one floor that is nearly flat, so neither matters. Between floors the same angle is steep: a target that
    // closes in gets a stale angle and the bullets fly over it, and CE's aim height (the lower body) can run the line into
    // the shooter's own slab, its floor's lip, a line Sight (the target's top) approved but nobody fired. So for direct fire
    // at another floor, per shot:
    //  - the angle comes from the true distance to the predicted target point (the target plus CE's lead), also in a locked
    //    burst. CE's sway, recoil, spread, lead and rotation stay as they are: the angle's change is added to CE's sum.
    //  - the aim height is CE's, moved to the nearest height on the target whose line passes every floor plane (TryHeight):
    //    the shooter's slab going down, the target's slab going up, any floor between.
    // Sight asks the same function from the same source: a thing has a line when some height on it clears. If none does
    // Sight refuses it; a shot already in a burst keeps CE's height.
    // Direct fire is a fast shot (not Aim.Slow: a slow one has its arcs in Aim), instant ones too (CE locks their angle
    // alike), not overhead, no throw. Pairs on one floor stop at the first test: none of this runs for them.
    internal static class FireLine
    {
        // Heights tried on a target, in CE units. A height found by moving away from CE's aim goes one step further: the
        // line would only just pass the lip.
        public const float Step = 0.1f;

        // Harness.
        public static int Adjusted, Raised, NoHeight, Refused;
        public static float LastDeltaDeg, LastWant;

        private static readonly AccessTools.FieldRef<Verb_LaunchProjectileCE, float> shotAngleRef = AccessTools.FieldRefAccess<Verb_LaunchProjectileCE, float>("shotAngle");
        private static readonly AccessTools.FieldRef<Verb_LaunchProjectileCE, float> angleRadiansRef = AccessTools.FieldRefAccess<Verb_LaunchProjectileCE, float>("angleRadians");
        private static readonly AccessTools.FieldRef<Verb_LaunchProjectileCE, float> lastShotAngleRef = AccessTools.FieldRefAccess<Verb_LaunchProjectileCE, float>("lastShotAngle");
        private static readonly Func<BaseTrajectoryWorker, ProjectilePropertiesCE, Vector3, Vector3, float?, float> workerAngle =
            AccessTools.MethodDelegate<Func<BaseTrajectoryWorker, ProjectilePropertiesCE, Vector3, Vector3, float?, float>>(Aim.ByName(typeof(BaseTrajectoryWorker), "ShotAngle", 4));
        private static readonly List<ProjectZ.Api.LineStep> steps = new List<ProjectZ.Api.LineStep>();
        private static Vector2 lead;

        // Harness: -cepacknofire leaves the fired line out (to show what the firing checks catch).
        private static readonly bool Off = GenCommandLine.CommandLineArgPassed("cepacknofire");

        public static void Patch(Harmony h)
        {
            PackZ.Postfix(h, AccessTools.Method(typeof(ShiftVecReport), nameof(ShiftVecReport.GetRandLeadVec)), typeof(FireLine), nameof(Lead_Postfix));
            PackZ.Postfix(h, Aim.ByName(typeof(Verb_LaunchProjectileCE), "ShiftTarget", 5), typeof(FireLine), nameof(ShiftTarget_Postfix));
        }

        // The lead of the shot being aimed (ShiftTarget asks once per shot; an instant shot has none).
        public static void Lead_Postfix(Vector2 __result) => lead = __result;

        internal static bool Direct(Verb_LaunchProjectileCE verb)
        {
            ProjectilePropertiesCE props = verb.projectilePropsCE;
            return !Off && props != null && !props.flyOverhead && !(verb is Verb_ThrowGrenade) && (props.isInstant || !Aim.Slow(verb, props));
        }

        // Where the shot leaves from: CE's launch point (the caster's centre at its shot height), or the centre of the cell
        // being tested. Sight's own lean and cover heights are not the shot's: CE fires from the centre at ShotHeight (a
        // lean only decides whether the shot may be taken; see Leaves).
        internal static Vector3 Source(Verb_LaunchProjectileCE verb)
        {
            Thing caster = verb.caster;
            IntVec3 root = Aim.RootOf(verb);
            Vector3 c = root == caster.Position ? caster.TrueCenter() : root.ToVector3Shifted();
            c.y = verb.ShotHeight;
            return c;
        }

        // Harness: -cepacknoleanfix judges planes and arcs from CE's lean point (to show what the lean checks catch).
        private static readonly bool LeanPoint = GenCommandLine.CommandLineArgPassed("cepacknoleanfix");

        // The point a sight line really leaves from: CE tests a lean from up to half a cell off the centre, at the height it
        // tests (a pawn's lean only; every other line is its own source). Cover is met from the lean point; a plane or an arc
        // is the shot's: a line that only clears a slab from the lean point would be approved and fired into the slab.
        internal static Vector3 Leaves(Verb_LaunchProjectileCE verb, Vector3 tested)
        {
            Vector3 centre = Aim.RootOf(verb).ToVector3Shifted();
            if (LeanPoint || !verb.CasterIsPawn || (Mathf.Abs(tested.x - centre.x) < 0.01f && Mathf.Abs(tested.z - centre.z) < 0.01f))
            {
                return tested;
            }
            Vector3 source = Source(verb);
            source.y = tested.y;
            return source;
        }

        // The line from source to the point (frame ks) passes no solid plane.
        private static bool Clears(Layout L, Map map, int ks, Vector3 source, Vector2 point, float y)
        {
            int n = ProjectZ.Api.WalkLine3D(L.ToVirtual(source, ks), L.ToVirtual(new Vector3(point.x, y, point.y), ks), map, steps, true);
            return n > 0 && steps[n - 1].solidPlane == int.MinValue;
        }

        // The height on a target (over its floor; lo to hi its body, rise its floor over the shooter's) nearest to want whose
        // line clears: want itself, else the first above it, else the first below. False: no height on it clears.
        internal static bool TryHeight(Layout L, Map map, int ks, Vector3 source, Vector2 point, float rise, float want, float lo, float hi, out float height)
        {
            height = want;
            if (Clears(L, map, ks, source, point, rise + want))
            {
                return true;
            }
            for (float h = want + Step; h < hi + Step; h += Step)
            {
                float y = Mathf.Min(h, hi);
                if (Clears(L, map, ks, source, point, rise + y))
                {
                    float further = Mathf.Min(y + Step, hi);
                    height = Clears(L, map, ks, source, point, rise + further) ? further : y;
                    return true;
                }
            }
            for (float h = Mathf.Min(want, hi) - Step; h > lo - Step; h -= Step)
            {
                float y = Mathf.Max(h, lo);
                if (Clears(L, map, ks, source, point, rise + y))
                {
                    float further = Mathf.Max(y - Step, lo);
                    height = Clears(L, map, ks, source, point, rise + further) ? further : y;
                    return true;
                }
            }
            return false;
        }

        // Sight: some height on the target clears from the fired source, tried from the top (the height CE's sight uses, the
        // one that sees past cover best). height: that height, in the shooter's frame.
        internal static bool Reaches(Verb_LaunchProjectileCE verb, Thing target, Layout L, Map map, int ks, int kt, out float height)
        {
            Vector3 p = L.TranslateRows(target.DrawPos, kt, ks);
            CollisionVertical body = new CollisionVertical(target);
            float rise = (kt - ks) * Layout.S;
            bool clear = TryHeight(L, map, ks, Source(verb), new Vector2(p.x, p.z), rise, body.Max, body.Min, body.Max, out height);
            height += rise;
            if (clear)
            {
                return true;
            }
            Refused++;
            Sight.Debug?.Invoke("    fired line: from " + L.ToVirtual(Source(verb), ks).ToString("F2") + " to " + p.ToString("F2") + " rise " + ((kt - ks) * Layout.S).ToString("0.00") + ", body " + body.Min.ToString("0.00") + ".." + body.Max.ToString("0.00") + ", steps " + string.Join(" ", steps.Select(x => L.Ground(x.cell) + "f" + x.floor + "[" + x.lo.ToString("0.00") + "," + x.hi.ToString("0.00") + "]p" + (x.solidPlane == int.MinValue ? "-" : x.solidPlane.ToString()))));
            return false;
        }

        // The shot: CE has aimed it (see the class header). Pellets after the first keep the first one's angle, as CE does
        // (angleRadians; calculateMechanicalOnly).
        public static void ShiftTarget_Postfix(Verb_LaunchProjectileCE __instance, ShiftVecReport report, Vector3 v, ref float targetHeight, bool calculateMechanicalOnly, bool isInstant)
        {
            if (calculateMechanicalOnly)
            {
                return;
            }
            int up = Aim.FloorsUp(__instance, report.target.Cell);
            if (up == 0 || !Direct(__instance))
            {
                return;
            }
            Thing caster = __instance.caster;
            Map map = caster.Map;
            Layout L = Layout.Of(map);
            Vector3 source = Source(__instance);
            Vector2 point = new Vector2(v.x, v.z) + (isInstant ? Vector2.zero : lead);
            if ((point - new Vector2(source.x, source.z)).sqrMagnitude < 0.01f)
            {
                return;
            }
            float rise = up * Layout.S;
            float want = targetHeight - rise;
            float height = want;
            Thing target = report.target.Thing;
            if (target != null && !(TargetGeometry.Active && TargetGeometry.TryAim(target, __instance, Aim.RootOf(__instance), out _)))
            {
                CollisionVertical body = new CollisionVertical(target);
                if (!TryHeight(L, map, L.FrameOf(caster.Position), source, point, rise, want, body.Min, body.Max, out height))
                {
                    NoHeight++;
                }
                else if (height != want)
                {
                    Raised++;
                }
            }
            ProjectilePropertiesCE props = __instance.projectilePropsCE;
            targetHeight = report.targetHeight = rise + height;
            float angle = workerAngle(props.TrajectoryWorker, props, source, new Vector3(point.x, rise + height, point.y), Aim.Speed(__instance));
            if (float.IsNaN(angle))
            {
                return;
            }
            float delta = angle - lastShotAngleRef(__instance);
            angleRadiansRef(__instance) += delta;
            shotAngleRef(__instance) += delta;
            PackZ.Changed++;
            Adjusted++;
            LastDeltaDeg = delta * Mathf.Rad2Deg;
            LastWant = want;
        }
    }
}
