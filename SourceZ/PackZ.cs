using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Verse;

namespace IsoRimCEPackZ
{
    // IsoRim Waylith - Combat Extended Pack, IsoRimZ part (loaded only with IsoRimZ: LoadFolders.xml 1.7/IsoRimZ).
    // Combat Extended between IsoRimZ's stacked floors (design: isorim-ce-pack-plan.md; ProjectZ public Api 6 only).
    //
    //  - ProjectZ's combat parts: the pack keeps every part but the two height bonuses (D1, D2): range between floors is the
    //    plain 3D distance and shooting down gives no accuracy bonus, the same rule for every shot (KeepCombatParts, from
    //    this Mod constructor, so ProjectZ counts it whatever the load order).
    //  - Flight (Frames): CE's projectile fields always hold the position in the frame of the storey the projectile is in
    //    (its band rows, its height over that storey's floor); a projectile crossing an open floor plane is moved into the
    //    next storey's frame (a pure translation), solid planes are CE's ground and roofs. Every CE rule then runs as it is.
    //  - Aim and sight (Aim, Sight): a target on another floor is seen in the shooter's frame (rows moved, heights raised
    //    by the storeys between); CE's line-of-sight, cover and smoke walks run on ProjectZ's 3D walk for those pairs only.
    //    Pairs on one floor stay 100 % Combat Extended.
    //  - Areas (Areas, Shields, SlabHits): CE's blasts and their suppression reach other floors in 3D (ProjectZ's explosion
    //    picker); shields are ProjectZ's dome, other mods' shields too (Blockers: CE's BlockerRegistry, offered in the
    //    dome's frame); floor slabs judge a CE blast at vanilla level (its own radius, damage capped at 50).
    //  - World shells (WorldShells): arriving without a cell, CE's random impact cell is picked over the ground rect.
    //  - Turrets, CIWS and CE's own AI (Turrets, Ciws, Ai; one mechanism, Scope): CE code that looks at targets without a
    //    verb of its own (turret orders and fire arcs, non-snap turrets, CE's target scores and AI tactics, mid-burst
    //    retargeting) reads distances in 3D and points in the viewer's frame; CIWS turrets see their targets' predicted
    //    flight in their own frame and only through open floor planes.
    //  - Api (Api.cs, Docs/Api.md): placement queries, other mods' target geometry and surface explosion rules. Their CE
    //    members are checked here at startup; their patches land on first use (ApplyLate), so a game without a user of a
    //    part has none of its patches.
    //  - Copies (CopyWatches): where CE's own method can't be called between floors, the pack's copy of it is listed with
    //    the CE method's IL hash and watched through IsoRim's Api.WatchCopy (one warning after a CE update changed it).
    //  - Fail-soft: if a key flight or aim patch fails (a CE update), shots between floors are refused (CE's "no line of
    //    sight") rather than flying half-patched; one log line says so. Shots on one floor are untouched. A group is all
    //    or nothing (a failure undoes what it applied); the CE members a group reads are resolved before it patches; the
    //    ProjectZ parts the pack builds on are checked once startup has settled.
    public class PackZMod : Mod
    {
        public PackZMod(ModContentPack content) : base(content)
        {
            try
            {
                if (ProjectZ.Api.Version >= 6)
                {
                    Keep();
                }
            }
            catch (Exception e)
            {
                Log.Error(PackZ.Tag + "Declaring ProjectZ's combat parts failed: " + e);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Keep()
        {
            if (GenCommandLine.CommandLineArgPassed("cepacknoz"))
            {
                return;   // harness: the pack's cross-floor part off (regression runs)
            }
            ProjectZ.CombatPart keep = ProjectZ.CombatPart.All & ~(ProjectZ.CombatPart.RangeHeightBonus | ProjectZ.CombatPart.HeightAccuracy);
            if (Fault.Is("partoff"))
            {
                keep &= ~ProjectZ.CombatPart.TargetPicking;   // harness: a part the pack builds on is off in ProjectZ
            }
            ProjectZ.Api.KeepCombatParts(keep, "Peren.IsoRimWaylith.CEPack");
        }
    }

    [StaticConstructorOnStartup]
    public static class PackZ
    {
        internal const string Tag = "[IsoRim CE Pack Z] ";

        // Every patch landed (false: a key patch failed, shots between floors are refused).
        public static bool Healthy { get; private set; }

        // The flight patches landed (crossing floor planes).
        public static bool FlightOn { get; private set; }

        // Harness: the cross-floor part was switched off (-cepacknoz).
        public static bool SwitchedOff { get; private set; }

        internal static readonly List<string> Failed = new List<string>();
        internal static int Applied;

        // Harness: how often the pack changed a value CE computed (an aim, sight or flight site that met two floors).
        public static int Changed;

        static PackZ()
        {
            try
            {
                if (ProjectZ.Api.Version < 6)
                {
                    Log.Message(Tag + "IsoRimZ is older than ProjectZ Api 6: Combat Extended stays on one floor at a time (no shots between floors).");
                    return;
                }
                if (GenCommandLine.CommandLineArgPassed("cepacknoz"))
                {
                    SwitchedOff = true;
                    Log.Message(Tag + "harness: cross-floor part off (-cepacknoz)");
                    return;
                }
                Setup();
                LongEventHandler.ExecuteWhenFinished(CheckParts);
            }
            catch (Exception e)
            {
                Healthy = false;
                Log.Error(Tag + "Setup failed (shots between floors are refused): " + e);
            }
        }

        // Every group lists the pack classes its patches and its run-time code use: their type initializers (CE members
        // resolved there) run before its first patch, so a CE update that renamed one fails the group here, at startup.
        // Groups whose run-time code reads projectiles through Frames' accessors apply only when the flight group landed.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Setup()
        {
            Harmony h = new Harmony("peren.isorimwaylith.cepack.z");
            H = h;
            bool gate = Try(h, "Gate", Gate.Patch, false, false, typeof(Gate), typeof(Layout));   // the refusal itself first
            FlightOn = Try(h, "Flight", Frames.Patch, true, false, typeof(Frames), typeof(Layout));
            Try(h, "Aim", Aim.Patch, true, false, typeof(Aim), typeof(FireLine), typeof(Layout));
            Try(h, "Aim extras", Aim.PatchExtras, false, false, typeof(Aim), typeof(Layout));
            Try(h, "Sight", Sight.Patch, true, false, typeof(Sight), typeof(Aim), typeof(FireLine), typeof(Layout));
            Try(h, "Throws", Throws.Patch, true, false, typeof(Throws), typeof(Aim), typeof(Layout));
            Try(h, "Flight extras", Frames.PatchExtras, false, true, typeof(Frames));
            Try(h, "World shells", WorldShells.Patch, false, false, typeof(WorldShells), typeof(Layout));
            Try(h, "Pod drops", PodDrops.Patch, false, false, typeof(PodDrops), typeof(Layout));
            // areas (not key: a failure leaves CE's own blasts, suppression and shields, each on its floor)
            bool areasOn = Try(h, "Areas", Areas.Patch, false, true, typeof(Areas), typeof(Frames));
            Try(h, "Shields", Shields.Patch, false, true, typeof(Shields), typeof(Frames));
            Try(h, "Other mods' shields", Blockers.Patch, false, true, typeof(Blockers), typeof(Shields), typeof(FrameView), typeof(Frames));
            Try(h, "Radius clips", _ => Areas.PatchClips(), false, false, typeof(Areas));
            bool slabHitsOn = Try(h, "Slab hits", SlabHits.Patch, false, false, typeof(SlabHits), typeof(Layout));
            // P4 (not key: a failure leaves that case as CE has it, on one floor): CIWS, turrets, CE's AI.
            // Harness: -cepacknop4 leaves them out (to show what the P4 checks catch).
            bool turretsOn = false;
            if (!GenCommandLine.CommandLineArgPassed("cepacknop4"))
            {
                Try(h, "CIWS", Ciws.Patch, false, true, typeof(Ciws), typeof(Scope), typeof(Views), typeof(Shields), typeof(Aim), typeof(Frames));
                Try(h, "CIWS comp targets", Ciws.PatchComp, false, true, typeof(Ciws), typeof(FrameView), typeof(Shields), typeof(Aim), typeof(Frames));
                turretsOn = Try(h, "Turrets", Turrets.Patch, false, true, typeof(Turrets), typeof(Scope), typeof(Views), typeof(Aim), typeof(Frames));
                Try(h, "AI", Ai.Patch, false, true, typeof(Ai), typeof(Scope), typeof(Views), typeof(Aim), typeof(Frames));
            }
            // Api parts (not key, nothing patched now: their CE members are checked, the patches land on first use). Each
            // builds on the turret views (Scope); target geometry and surface explosions on the flight frames; surface
            // explosions on the 3D areas and slab hits.
            bool placement = turretsOn && Try(h, "Api placement queries", PlacementQuery.Prepare, false, false, typeof(PlacementQuery), typeof(QueryContext));
            bool geometry = turretsOn && Try(h, "Api target geometry", TargetGeometry.Prepare, false, true, typeof(TargetGeometry));
            bool surface = areasOn && slabHitsOn && Try(h, "Api surface explosions", SurfaceExplosions.Prepare, false, true, typeof(SurfaceExplosions));
            Healthy = Failed.Count == 0;
            PlacementQuery.Ready = Healthy && placement;
            TargetGeometry.Ready = Healthy && geometry;
            SurfaceExplosions.Ready = Healthy && surface;
            string notes = (Problems.Count > 0 ? "; failed: " + string.Join("; ", Problems) : "") + (Skipped.Count > 0 ? "; not applied without the flight patches: " + string.Join(", ", Skipped) : "");
            if (!Healthy)
            {
                Log.Warning(Tag + "Some Combat Extended methods were not found: shots between floors are "
                    + (gate ? "refused" : "NOT refused (the refusal patch failed too)") + ", shots on one floor are as Combat Extended has them" + notes + ".");
            }
            else if (Problems.Count > 0)
            {
                Log.Warning(Tag + "Combat Extended between floors: " + Applied + " patches; some parts stay on one floor as Combat Extended has them" + notes + ".");
            }
            else
            {
                Log.Message(Tag + "Combat Extended between floors: " + Applied + " patches.");
            }
            CopyWatches.Register();
        }

        // The groups that failed (with the reason) and the ones left out because the flight patches failed.
        internal static readonly List<string> Problems = new List<string>();
        internal static readonly List<string> Skipped = new List<string>();

        // All or nothing per group: every patch the group applied before it failed is undone (Harmony's own record of this
        // instance's patches, before and after), so no group runs half-patched.
        private static bool Try(Harmony h, string group, Action<Harmony> patch, bool key, bool needsFlight, params Type[] uses)
        {
            if (needsFlight && !FlightOn)
            {
                Skipped.Add(group);
                return false;
            }
            HashSet<(MethodBase, MethodInfo)> before = Owned(h);
            int applied = Applied;
            try
            {
                foreach (Type t in uses)
                {
                    RuntimeHelpers.RunClassConstructor(t.TypeHandle);
                }
                patch(h);
                return true;
            }
            catch (Exception e)
            {
                int undone = Undo(h, before);
                Applied = applied;
                if (key)
                {
                    Failed.Add(group);
                }
                Problems.Add(group + " (" + Reason(e) + (undone > 0 ? ", " + undone + " patches undone" : "") + ")");
                return false;
            }
        }

        internal static string Reason(Exception e)
        {
            while (e.InnerException != null)
            {
                e = e.InnerException;
            }
            return e.GetType().Name + ": " + e.Message;
        }

        // This Harmony instance's patches now: (original, patch method).
        internal static HashSet<(MethodBase, MethodInfo)> Owned(Harmony h)
        {
            HashSet<(MethodBase, MethodInfo)> set = new HashSet<(MethodBase, MethodInfo)>();
            foreach (MethodBase m in h.GetPatchedMethods())
            {
                Patches info = Harmony.GetPatchInfo(m);
                if (info == null)
                {
                    continue;
                }
                foreach (IEnumerable<Patch> list in new[] { info.Prefixes, info.Postfixes, info.Transpilers, info.Finalizers })
                {
                    foreach (Patch p in list)
                    {
                        if (p.owner == h.Id)
                        {
                            set.Add((m, p.PatchMethod));
                        }
                    }
                }
            }
            return set;
        }

        private static int Undo(Harmony h, HashSet<(MethodBase, MethodInfo)> before)
        {
            int n = 0;
            foreach ((MethodBase m, MethodInfo p) in Owned(h))
            {
                if (before.Contains((m, p)))
                {
                    continue;
                }
                try
                {
                    h.Unpatch(m, p);
                    n++;
                }
                catch (Exception e)
                {
                    Log.Warning(Tag + "Undoing a patch on " + m.DeclaringType?.Name + "." + m.Name + " failed: " + Reason(e));
                }
            }
            return n;
        }

        // Once startup has settled (ProjectZ settles its combat parts in a StaticConstructorOnStartup, in no fixed order
        // with this one): the ProjectZ parts the pack builds on (3D range, sight, target picking) must be on. If one is off
        // (ProjectZ closed its combat group after a game or CE update), shots between floors are refused.
        public static bool PartsOff { get; private set; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void CheckParts()
        {
            try
            {
                List<string> off = new List<string>();
                foreach (ProjectZ.CombatPart part in new[] { ProjectZ.CombatPart.Range, ProjectZ.CombatPart.Sight, ProjectZ.CombatPart.TargetPicking })
                {
                    if (!ProjectZ.Api.CombatPartOn(part))
                    {
                        off.Add(part.ToString());
                    }
                }
                if (off.Count == 0)
                {
                    return;
                }
                PartsOff = true;
                PlacementQuery.Disable();
                TargetGeometry.Disable();
                SurfaceExplosions.Disable();
                bool was = Healthy;
                Healthy = false;
                if (was)
                {
                    Log.Warning(Tag + "ProjectZ's combat parts " + string.Join(", ", off) + " are off: shots between floors are refused, shots on one floor are as Combat Extended has them.");
                }
            }
            catch (Exception e)
            {
                Healthy = false;
                Log.Error(Tag + "Reading ProjectZ's combat parts failed (shots between floors are refused): " + Reason(e));
            }
        }

        // The pack's Harmony instance (the Api parts patch with it on first use).
        internal static Harmony H { get; private set; }

        // An Api part's patches on its first use (main thread; the caller calls it once): all or nothing as a startup group.
        // False: they could not be applied (undone, one warning); the part stays off and the game is as without it.
        internal static bool ApplyLate(string group, Action<Harmony> patch)
        {
            if (H == null)
            {
                return false;
            }
            HashSet<(MethodBase, MethodInfo)> before = Owned(H);
            int applied = Applied;
            try
            {
                patch(H);
                Log.Message(Tag + group + ": " + (Applied - applied) + " patches, on first use.");
                return true;
            }
            catch (Exception e)
            {
                int undone = Undo(H, before);
                Applied = applied;
                Problems.Add(group + " (" + Reason(e) + ")");
                Log.Warning(Tag + group + " could not be patched (" + Reason(e) + (undone > 0 ? ", " + undone + " patches undone" : "") + "): it stays off, Combat Extended as before.");
                return false;
            }
        }

        // The parameters a patch binds by name: a CE update that renamed one fails here, at startup, not at first use.
        internal static void NeedParams(MethodBase m, params string[] names)
        {
            foreach (string n in names)
            {
                if (!m.GetParameters().Any(p => p.Name == n))
                {
                    throw new MissingMemberException(m.DeclaringType?.Name + "." + m.Name + ": no parameter " + n);
                }
            }
        }

        // One log line per key (a fallback met at run time).
        private static readonly HashSet<string> logged = new HashSet<string>();
        public static readonly List<string> OnceKeys = new List<string>();   // harness

        internal static void Once(string key, string text, bool error = false)
        {
            if (!logged.Add(key))
            {
                return;
            }
            OnceKeys.Add(key);
            if (error)
            {
                Log.Error(Tag + text);
            }
            else
            {
                Log.Warning(Tag + text);
            }
        }

        // Patch helpers: a missing method throws (the group fails).
        internal static MethodInfo Method(Type t, string name, Type[] args = null)
        {
            MethodInfo m = args == null ? AccessTools.Method(t, name) : AccessTools.Method(t, name, args);
            if (m == null)
            {
                throw new MissingMethodException(t.Name + "." + name);
            }
            return m;
        }

        internal static void Prefix(Harmony h, MethodBase target, Type owner, string patch)
        {
            h.Patch(target, prefix: new HarmonyMethod(owner, patch));
            Applied++;
        }

        internal static void Postfix(Harmony h, MethodBase target, Type owner, string patch)
        {
            h.Patch(target, postfix: new HarmonyMethod(owner, patch));
            Applied++;
        }

        internal static void Transpiler(Harmony h, MethodBase target, Type owner, string patch)
        {
            h.Patch(target, transpiler: new HarmonyMethod(owner, patch));
            Applied++;
        }

        // A key part failed in play (a CE update met at run time): from now on shots between floors are refused and the Api
        // parts are off, as after a key group failed at startup (or ProjectZ's parts were off: CheckParts). One warning.
        internal static void FailInPlay(string part, string reason)
        {
            if (!Healthy)
            {
                return;
            }
            Healthy = false;
            Failed.Add(part);
            PlacementQuery.Disable();
            TargetGeometry.Disable();
            SurfaceExplosions.Disable();
            Log.Warning(Tag + part + " failed in play (" + reason + "): shots between floors are refused from now on, shots on one floor are as Combat Extended has them.");
        }

        // A delegate that calls CE's instance method m itself, not an override (IL call, not callvirt; Harmony's patches on
        // m still run): the instance first, then m's parameters. The delegate type must match that exactly (checked: a
        // class's closed, non-static method; else ArgumentException).
        internal static D NonVirtual<D>(MethodInfo m) where D : Delegate
        {
            Type owner = m?.DeclaringType;
            if (m == null || m.IsStatic || owner == null || owner.IsValueType || owner.ContainsGenericParameters || m.ContainsGenericParameters)
            {
                throw new ArgumentException("NonVirtual: not a closed instance method of a class: " + owner?.Name + "." + m?.Name);
            }
            Type[] args = new[] { owner }.Concat(m.GetParameters().Select(p => p.ParameterType)).ToArray();
            MethodInfo invoke = typeof(D).GetMethod("Invoke");
            if (invoke.ReturnType != m.ReturnType || !invoke.GetParameters().Select(p => p.ParameterType).SequenceEqual(args))
            {
                throw new ArgumentException("NonVirtual: " + typeof(D).Name + " does not match " + owner.Name + "." + m.Name);
            }
            DynamicMethod dm = new DynamicMethod("IsoRimCEPackZ_call_" + m.Name, m.ReturnType, args, typeof(PackZ).Module, true);
            ILGenerator il = dm.GetILGenerator();
            for (int i = 0; i < args.Length; i++)
            {
                il.Emit(OpCodes.Ldarg, (short)i);
            }
            il.Emit(OpCodes.Call, m);
            il.Emit(OpCodes.Ret);
            return (D)dm.CreateDelegate(typeof(D));
        }
    }

    // Harness only: -cepackfault=<name>[,<name>] simulates a CE update (a renamed member, a group failing partway, a
    // method whose reads are not found) or a ProjectZ part switched off, to test the fail-soft paths. Off in play.
    internal static class Fault
    {
        private static readonly string[] on = GenCommandLine.TryGetCommandLineArg("cepackfault", out string v) && v != null ? v.Split(',') : new string[0];

        public static bool Is(string name) => Array.IndexOf(on, name) >= 0;

        // A member name as CE has it, or a missing one under the named fault.
        public static string Name(string real, string fault) => Is(fault) ? real + "_cepackfault" : real;

        public static void Throw(string fault)
        {
            if (Is(fault))
            {
                throw new MissingMethodException("harness fault " + fault);
            }
        }
    }
}
