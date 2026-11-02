using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using CombatExtended;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace IsoRimCEPackZ
{
    // Turrets, CIWS and CE's AI between floors (plan §2d, P4): one mechanism for the CE code that looks at targets without
    // a verb of its own (turret buildings, their fire arcs, CE's own target finders and AI tactics, CIWS bullets).
    //
    // Such code reads cells and points in raw map rows (a target on another floor is a whole band away) and measures flat
    // distances. A scope says who is looking: a prefix on the entry method (the turret, the comp's pawn, the searcher, the
    // CIWS bullet) sets it, a finalizer restores the one before (scopes nest). The scope transpiler swaps the raw reads in
    // those methods (and in the compiler's lambdas they call) for the frame versions below:
    //   IntVec3Utility.DistanceTo / DistanceToSquared, IntVec3.InHorDistOf, GenClosest.ClosestThing_Global
    //        -> the 3D distance (ProjectZ Api Distance3D: over the ground plus the storeys between; the range rule D1)
    //   IntVec3 - IntVec3                                   -> over the ground (as Aim.GroundSub)
    //   Thing.DrawPos, LocalTargetInfo.CenterVector3        -> in the viewer's frame (Views.DrawPosFrom)
    //   Skyfaller_Utility.CurrentPosition                   -> in the viewer's frame (rows and height)
    //   CoverUtility.CalculateOverallBlockChance            -> the shooter's cell moved onto the target's floor
    //   CE GenClosest.PawnsNearSegment                      -> the segment's ground line on every floor it passes
    // Each swap gives the game's own value for two things on one floor (or no scope, or a map without floors), so pairs
    // on one floor stay 100 % Combat Extended; PackZ.Changed counts every value it changed.
    internal static class Scope
    {
        internal struct State
        {
            public Map map;
            public Layout L;
            public int frame;
            public Thing viewer;
        }

        [ThreadStatic] private static State cur;

        // A null viewer (a lambda's closure without one) keeps the scope it is called in.
        public static State Enter(Thing viewer)
        {
            State old = cur;
            if (viewer == null)
            {
                return old;
            }
            Map map = QueryContext.SubjectMap(viewer) ?? (viewer.Spawned ? viewer.Map : null);   // (a placement query's turret: its map)
            Layout L = Layout.Of(map);
            cur = new State { map = map, L = L, frame = L != null ? L.FrameOf(viewer.Position) : Layout.None, viewer = viewer };
            return old;
        }

        public static void Exit(State old) => cur = old;

        internal static Thing ViewerOf(object o)
        {
            switch (o)
            {
                case Thing th:
                    return th;
                case ThingComp c:
                    return c.parent;
                case Verb v:
                    return v.caster;
                case Verse.AI.JobDriver d:
                    return d.pawn;
                case IAttackTargetSearcher s:
                    return s.Thing;
                case null:
                    return null;
                default:
                    // a compiler closure or iterator holding its method's 'this'
                    Type t = o.GetType();
                    if (!outerThis.TryGetValue(t, out FieldInfo f))
                    {
                        f = AccessTools.Field(t, "<>4__this");
                        outerThis[t] = f;
                    }
                    return f != null ? ViewerOf(f.GetValue(o)) : null;
            }
        }

        private static readonly Dictionary<Type, FieldInfo> outerThis = new Dictionary<Type, FieldInfo>();

        // Scope every method of t (and of its lambdas, see Users) that reads a swapped value: instance methods enter the
        // scope of their instance (an entry point), static ones and closures without a viewer run in their caller's.
        public static int ScopeAll(Harmony h, Type t, bool own, params string[] lambdaOf)
        {
            List<MethodInfo> users = Users(t, own, lambdaOf);
            foreach (MethodInfo m in users)
            {
                if (!m.IsStatic)
                {
                    Around(h, m, nameof(Instance_Prefix));
                }
                Swap(h, m);
            }
            return users.Count;
        }

        // Enter (prefix by name) and exit (finalizer) the scope round method m.
        public static void Around(Harmony h, MethodBase m, string prefix)
        {
            PackZ.Prefix(h, m, typeof(Scope), prefix);
            h.Patch(m, finalizer: new HarmonyMethod(typeof(Scope), nameof(Exit_Finalizer)));
        }

        // ---- entry patches (the viewer: the instance, or the method's first Thing / searcher argument)
        public static void Instance_Prefix(object __instance, out State __state) => __state = Enter(ViewerOf(__instance));

        public static void Searcher_Prefix(IAttackTargetSearcher searcher, out State __state) => __state = Enter(searcher?.Thing);

        public static void Pawn_Prefix(Pawn pawn, out State __state) => __state = Enter(pawn);

        public static void Shooter_Prefix(Pawn shooter, out State __state) => __state = Enter(shooter);

        public static void Attacker_Prefix(Pawn attacker, out State __state) => __state = Enter(attacker);

        public static Exception Exit_Finalizer(Exception __exception, State __state)
        {
            cur = __state;
            return __exception;
        }

        // A scoped entry method: entry prefix (by name), exit finalizer and the swap transpiler.
        public static void Entry(Harmony h, MethodBase m, string prefix)
        {
            Around(h, m, prefix);
            Swap(h, m);
        }

        public static void Swap(Harmony h, MethodBase m)
        {
            PackZ.Transpiler(h, m, typeof(Scope), nameof(Scope_Transpiler));
        }

        // The methods (declared in t when own, and the compiler's lambdas in t and its nested types) whose IL calls a swapped
        // method; the lambdas only when their name holds one of the given names, if any ("<Name>b__", "<Name>g__").
        public static List<MethodInfo> Users(Type t, bool own, params string[] lambdaOf)
        {
            List<MethodInfo> found = new List<MethodInfo>();
            IEnumerable<Type> types = new[] { t }.Concat(AllNested(t));
            foreach (Type x in types)
            {
                foreach (MethodInfo m in x.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (m.IsAbstract || m.IsGenericMethodDefinition || m.ContainsGenericParameters)
                    {
                        continue;
                    }
                    bool lambda = x != t || m.Name.Contains(">b__") || m.Name.Contains(">g__");
                    if (lambda ? lambdaOf.Length > 0 && !lambdaOf.Any(n => m.Name.Contains("<" + n + ">")) : !own)
                    {
                        continue;
                    }
                    if (CallsSwapped(m))
                    {
                        found.Add(m);
                    }
                }
            }
            return found;
        }

        private static IEnumerable<Type> AllNested(Type t)
        {
            foreach (Type n in t.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
            {
                yield return n;
                foreach (Type nn in AllNested(n))
                {
                    yield return nn;
                }
            }
        }

        private static bool CallsSwapped(MethodInfo m)
        {
            try
            {
                if (Fault.Is("scoperead") && (m.DeclaringType == typeof(CombatExtended.AI.CompFireSelection) || m.DeclaringType?.DeclaringType == typeof(CombatExtended.AI.CompFireSelection)))
                {
                    throw new InvalidProgramException("harness fault scoperead");
                }
                return PatchProcessor.GetOriginalInstructions(m).Any(ci => (ci.opcode == OpCodes.Call || ci.opcode == OpCodes.Callvirt) && ci.operand is MethodBase mb && Swaps.ContainsKey(mb));
            }
            catch (Exception e)
            {
                // (that method stays as CE has it: one line per method)
                string key = m.DeclaringType?.Name + "." + m.Name;
                PackZ.Once("scope:" + key, "Combat Extended's " + key + " could not be read (" + PackZ.Reason(e) + "): it stays on one floor.");
                return false;
            }
        }

        // The scoped methods found per type (harness), and the check against the CE build the pack was tested on: none
        // found fails the group; fewer than there were is one line (some of that type's code then stays on one floor).
        public static readonly Dictionary<string, int> UsersPer = new Dictionary<string, int>();

        public static void Expect(string what, int found, int tested)
        {
            UsersPer[what] = found;
            if (found == 0)
            {
                throw new MissingMethodException(what + ": no distance or target reads found");
            }
            if (found < tested)
            {
                PackZ.Once("users:" + what, "Combat Extended's " + what + ": " + found + " of " + tested + " methods with target reads found (a CE update); the rest stays on one floor.");
            }
        }

        // ---- the swaps
        private static Dictionary<MethodBase, MethodInfo> swaps;

        private static Dictionary<MethodBase, MethodInfo> Swaps => swaps ??= new Dictionary<MethodBase, MethodInfo>
        {
            { AccessTools.Method(typeof(IntVec3Utility), nameof(IntVec3Utility.DistanceTo), new[] { typeof(IntVec3), typeof(IntVec3) }), Mine(nameof(DistanceTo)) },
            { AccessTools.Method(typeof(IntVec3Utility), nameof(IntVec3Utility.DistanceToSquared), new[] { typeof(IntVec3), typeof(IntVec3) }), Mine(nameof(DistanceToSquared)) },
            { AccessTools.Method(typeof(IntVec3), nameof(IntVec3.InHorDistOf), new[] { typeof(IntVec3), typeof(float) }), Mine(nameof(InHorDistOf)) },
            { AccessTools.Method(typeof(IntVec3), "op_Subtraction", new[] { typeof(IntVec3), typeof(IntVec3) }), Mine(nameof(Sub)) },
            { AccessTools.PropertyGetter(typeof(Thing), nameof(Thing.DrawPos)), Mine(nameof(DrawPos)) },
            { AccessTools.PropertyGetter(typeof(LocalTargetInfo), nameof(LocalTargetInfo.CenterVector3)), Mine(nameof(CenterVector3)) },
            { AccessTools.Method(typeof(Skyfaller_Utility), nameof(Skyfaller_Utility.CurrentPosition), new[] { typeof(Skyfaller) }), Mine(nameof(SkyfallerPos)) },
            { AccessTools.Method(typeof(CoverUtility), nameof(CoverUtility.CalculateOverallBlockChance), new[] { typeof(LocalTargetInfo), typeof(IntVec3), typeof(Map) }), Mine(nameof(BlockChance)) },
            { AccessTools.Method(typeof(Verse.GenClosest), nameof(Verse.GenClosest.ClosestThing_Global),
                new[] { typeof(IntVec3), typeof(IEnumerable), typeof(float), typeof(Predicate<Thing>), typeof(Func<Thing, float>), typeof(bool) }), Mine(nameof(ClosestThing_Global)) },
            { AccessTools.Method(typeof(CombatExtended.Utilities.GenClosest), nameof(CombatExtended.Utilities.GenClosest.PawnsNearSegment),
                new[] { typeof(IntVec3), typeof(IntVec3), typeof(Map), typeof(float), typeof(bool), typeof(bool) }), Mine(nameof(PawnsNearSegment)) },
        };

        private static MethodInfo Mine(string name) => AccessTools.Method(typeof(Scope), name) ?? throw new MissingMethodException("Scope." + name);

        // Every swapped method exists (a CE or game update renamed one: the group fails).
        public static void CheckSwaps()
        {
            foreach (KeyValuePair<MethodBase, MethodInfo> kv in Swaps)
            {
                if (kv.Key == null || kv.Value == null)
                {
                    throw new MissingMethodException("a swapped method");
                }
            }
        }

        public static int Swapped;

        public static IEnumerable<CodeInstruction> Scope_Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
        {
            int n = 0;
            foreach (CodeInstruction ci in instructions)
            {
                if ((ci.opcode == OpCodes.Call || ci.opcode == OpCodes.Callvirt) && ci.operand is MethodBase mb && Swaps.TryGetValue(mb, out MethodInfo mine))
                {
                    // the same arguments on the stack (an instance call's receiver: a Thing, or a struct's address)
                    ci.opcode = OpCodes.Call;
                    ci.operand = mine;
                    n++;
                }
                yield return ci;
            }
            if (n == 0)
            {
                throw new InvalidOperationException("scope transpiler: nothing to swap in " + __originalMethod.DeclaringType?.Name + "." + __originalMethod.Name);
            }
            Swapped += n;
        }

        // ---- frame versions (the game's value on one floor, without a scope or without floors)
        private static bool Cross(IntVec3 a, IntVec3 b, out int ka, out int kb)
        {
            Layout L = cur.L;
            if (L == null)
            {
                ka = kb = Layout.None;
                return false;
            }
            ka = L.FrameOf(a);
            kb = L.FrameOf(b);
            return ka != kb && ka != Layout.None && kb != Layout.None;
        }

        public static float DistanceTo(IntVec3 a, IntVec3 b)
        {
            if (!Cross(a, b, out _, out _))
            {
                return a.DistanceTo(b);
            }
            PackZ.Changed++;
            return ProjectZ.Api.Distance3D(a, b, cur.map);
        }

        public static int DistanceToSquared(IntVec3 a, IntVec3 b)
        {
            if (!Cross(a, b, out _, out _))
            {
                return a.DistanceToSquared(b);
            }
            PackZ.Changed++;
            float d = ProjectZ.Api.Distance3D(a, b, cur.map);
            return Mathf.RoundToInt(d * d);
        }

        public static bool InHorDistOf(ref IntVec3 a, IntVec3 b, float maxDist)
        {
            if (!Cross(a, b, out _, out _))
            {
                return a.InHorDistOf(b, maxDist);
            }
            PackZ.Changed++;
            return ProjectZ.Api.Distance3D(a, b, cur.map) <= maxDist;
        }

        public static IntVec3 Sub(IntVec3 a, IntVec3 b)
        {
            if (!Cross(a, b, out _, out _))
            {
                return a - b;
            }
            PackZ.Changed++;
            return cur.L.Ground(a) - cur.L.Ground(b);
        }

        public static Vector3 DrawPos(Thing t)
        {
            Layout L = cur.L;
            if (L == null || t == null || !t.Spawned || t.Map != cur.map)
            {
                return t.DrawPos;
            }
            // (a thing another mod gives its own aim: that point in the viewer's frame)
            if (TargetGeometry.Active && TargetGeometry.TryAimFrom(t, cur.viewer, cur.map, out TargetAim aim))
            {
                return L.FromVirtual(aim.PointVirtual, cur.frame);
            }
            int kt = L.FrameOf(t.Position);
            if (kt == cur.frame || kt == Layout.None || cur.frame == Layout.None)
            {
                return t.DrawPos;
            }
            PackZ.Changed++;
            return Views.DrawPosFrom(t, L, kt, cur.frame);
        }

        public static Vector3 CenterVector3(ref LocalTargetInfo t)
        {
            if (t.HasThing)
            {
                return DrawPos(t.Thing);
            }
            Vector3 v = t.CenterVector3;
            Layout L = cur.L;
            if (L == null || !t.IsValid)
            {
                return v;
            }
            int kt = L.FrameOf(t.Cell);
            if (kt == cur.frame || kt == Layout.None || cur.frame == Layout.None)
            {
                return v;
            }
            PackZ.Changed++;
            return L.TranslateRows(v, kt, cur.frame);
        }

        private static readonly Func<Skyfaller, Vector3> skyPos = AccessTools.MethodDelegate<Func<Skyfaller, Vector3>>(AccessTools.Method(typeof(Skyfaller_Utility), nameof(Skyfaller_Utility.CurrentPosition), new[] { typeof(Skyfaller) }));

        public static Vector3 SkyfallerPos(Skyfaller s)
        {
            Vector3 v = skyPos(s);
            Layout L = cur.L;
            if (L == null || s == null || !s.Spawned)
            {
                return v;
            }
            int kt = L.FrameOf(s.Position);
            if (kt == cur.frame || kt == Layout.None || cur.frame == Layout.None)
            {
                return v;
            }
            PackZ.Changed++;
            return L.Translate(v, kt, cur.frame);
        }

        public static float BlockChance(LocalTargetInfo target, IntVec3 shooterLoc, Map map)
        {
            Layout L = Layout.Of(map);
            if (L != null && target.IsValid)
            {
                int kt = L.FrameOf(target.Cell), ks = L.FrameOf(shooterLoc);
                if (kt != ks && kt != Layout.None && ks != Layout.None)
                {
                    PackZ.Changed++;
                    shooterLoc = L.Cell(L.Ground(shooterLoc), kt);
                }
            }
            return CoverUtility.CalculateOverallBlockChance(target, shooterLoc, map);
        }

        // Verse.GenClosest.ClosestThing_Global with the 3D distance (a scope on a map with floors); else the game's.
        public static Thing ClosestThing_Global(IntVec3 center, IEnumerable searchSet, float maxDistance, Predicate<Thing> validator, Func<Thing, float> priorityGetter, bool lookInHaulSources)
        {
            Layout L = cur.L;
            if (L == null || searchSet == null)
            {
                return Verse.GenClosest.ClosestThing_Global(center, searchSet, maxDistance, validator, priorityGetter, lookInHaulSources);
            }
            Map map = cur.map;
            int kc = L.FrameOf(center);
            float best = float.MaxValue, bestPrio = float.MinValue, max2 = maxDistance * maxDistance;
            Thing chosen = null;
            foreach (object o in searchSet)
            {
                if (!(o is Thing t) || !t.Spawned)
                {
                    continue;
                }
                IntVec3 p = t.PositionHeld;
                int kp = L.FrameOf(p);
                float d2;
                if (kp != kc && kp != Layout.None && kc != Layout.None)
                {
                    PackZ.Changed++;
                    float d = ProjectZ.Api.Distance3D(center, p, map);
                    d2 = d * d;
                }
                else
                {
                    d2 = (center - p).LengthHorizontalSquared;
                }
                if (d2 > max2 || (priorityGetter == null && d2 >= best) || (validator != null && !validator(t)))
                {
                    continue;
                }
                float prio = 0f;
                if (priorityGetter != null)
                {
                    prio = priorityGetter(t);
                    if (prio < bestPrio || (Mathf.Approximately(prio, bestPrio) && d2 >= best))
                    {
                        continue;
                    }
                }
                chosen = t;
                best = d2;
                bestPrio = prio;
            }
            return chosen;
        }

        // CE's pawns near a shot's segment (Retarget). Between floors the raw segment runs through other bands and the void
        // rows; the frame version takes the segment's ground line on every floor from the origin's to the destination's
        // (the storeys the 3D line passes), each on that floor's band. On one floor: CE's own.
        public static IEnumerable<Thing> PawnsNearSegment(IntVec3 origin, IntVec3 destination, Map map, float range, bool behind, bool infront)
        {
            Layout L = Layout.Of(map);
            int ko = L != null ? L.FrameOf(origin) : Layout.None, kd = L != null ? L.FrameOf(destination) : Layout.None;
            if (L == null || ko == kd || ko == Layout.None || kd == Layout.None)
            {
                return CombatExtended.Utilities.GenClosest.PawnsNearSegment(origin, destination, map, range, behind, infront);
            }
            PackZ.Changed++;
            PawnsNearSegmentCalls++;
            IntVec3 go = L.Ground(origin), gd = L.Ground(destination);
            List<Thing> all = new List<Thing>();
            int step = kd > ko ? 1 : -1;
            for (int k = kd; ; k -= step)   // the destination's floor first
            {
                foreach (Thing t in CombatExtended.Utilities.GenClosest.PawnsNearSegment(L.Cell(go, k), L.Cell(gd, k), map, range, behind, infront))
                {
                    if (t != null && L.FrameOf(t.Position) == k && !all.Contains(t))   // (CE measures the segment loosely: things far ahead on it, on other bands too)
                    {
                        all.Add(t);
                    }
                }
                if (k == ko)
                {
                    break;
                }
            }
            Debug?.Invoke("    pawns near the segment " + origin + " -> " + destination + " (floors " + ko + "->" + kd + "): " + string.Join(", ", all) + "; raw: " + string.Join(", ", CombatExtended.Utilities.GenClosest.PawnsNearSegment(origin, destination, map, range, behind, infront)));
            return all;
        }

        public static int PawnsNearSegmentCalls;
        public static Action<string> Debug;   // harness
    }

    // Things seen from another floor's frame.
    internal static class Views
    {
        private static readonly AccessTools.FieldRef<ProjectileCE, int> ticksToTrueRef = AccessTools.FieldRefAccess<ProjectileCE, int>("ticksToTruePosition");
        private static readonly Func<BaseTrajectoryWorker, Vector3, int, int, float, Vector3> toDraw = AccessTools.MethodDelegate<Func<BaseTrajectoryWorker, Vector3, int, int, float, Vector3>>(AccessTools.Method(typeof(BaseTrajectoryWorker), nameof(BaseTrajectoryWorker.ExactPosToDrawPos)));

        // Thing t (on floor kt: its Position's band) seen from frame k: a CE projectile's exact point moved into frame k
        // (rows and height) and drawn there by CE's own rule (its lift grows with the height); anything else: its rows moved.
        public static Vector3 DrawPosFrom(Thing t, Layout L, int kt, int k)
        {
            if (t is ProjectileCE p)
            {
                Vector3 e = L.Translate(Frames.exactGet(p), kt, k);
                BaseTrajectoryWorker w = p.TrajectoryWorker;
                if (w != null)
                {
                    return toDraw(w, e, p.FlightTicks, ticksToTrueRef(p), p.def.Altitude);
                }
            }
            return L.TranslateRows(t.DrawPos, kt, k);
        }
    }
}
