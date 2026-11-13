using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using CombatExtended;
using HarmonyLib;
using RimWorld;
using Verse;

namespace IsoRimCEPackZ
{
    // The pack's copies of Combat Extended code, and the CE methods it calls on an assumption, in one list. A copy stays
    // only where calling CE's own method can't work between floors: CE walks its own line of cells or rays, intersects with
    // map.Size, or measures an IntVec3 difference, none of which ProjectZ's 3D scope (Api 10) answers. Each row: the CE
    // method, the pack's code that relies on it, and the IL hash of the CE build it follows (IsoRim Api 70 CopyHash; CE
    // DLL md5 de5aa969).
    //  - At startup every row is registered with IsoRim's Api.WatchCopy (IsoRim Api 70 or later; older: nothing): one
    //    warning per CE method a CE update changed, naming the copy to review.
    //  - Mode cereads checks every row against CE now (TestCeReads) and writes the hashes now to cecopies_now.tsv.
    // After reviewing a copy against a changed CE method, paste the new hash here.
    internal static class CopyWatches
    {
        internal sealed class Row
        {
            public string Copy;              // the pack's copy (for the warning)
            public Func<MethodBase> Find;    // the CE method (null: not found)
            public string Hash;              // its CopyHash when the copy was last checked
            public string About = "copied from Combat Extended";   // what the pack relies on (for the warning)
        }

        internal static readonly Row[] Rows =
        {
            new Row { Copy = "Sight.CanHit", Find = () => Method(typeof(Verb_LaunchProjectileCE), "CanHitCellFromCellIgnoringRange", 3), Hash = "93218d18a9891341" },
            new Row { Copy = "Sight.CellClear", Find = () => Lambda(typeof(Verb_LaunchProjectileCE), "CanHitCellFromCellIgnoringRange"), Hash = "3b2d3c95648ac16d" },
            new Row { Copy = "Sight.Cover_Prefix", Find = () => Method(typeof(Verb_LaunchProjectileCE), "GetHighestCoverAndSmokeForTarget", 4), Hash = "2f3b939405ff3806" },
            new Row { Copy = "Aim.AdjustShotHeight_Prefix", Find = () => Method(typeof(Verb_LaunchProjectileCE), "AdjustShotHeight", 3), Hash = "f452e0cb49185e2e" },
            new Row { Copy = "Throws.FindAngle_Prefix", Find = () => Method(typeof(Verb_ThrowGrenade), "FindAngle", 6), Hash = "054ab37f92ecbe78" },
            new Row { Copy = "Turrets.OrderAttack_Prefix", Find = () => Method(typeof(Building_TurretGunCE), "OrderAttack", 1), Hash = "6a91011aeb6a1886" },
            new Row { Copy = "Frames.RayCast_Prefix", Find = () => Method(typeof(ProjectileCE), "RayCast", 11), Hash = "bdf5c0cb9f2d99c6" },
            new Row { Copy = "Frames.Homing_Prefix", Find = () => Method(typeof(HomingBulletTrajectoryWorker), "ReactiveAcceleration", 1), Hash = "bb1572c95664f85f" },
            new Row { Copy = "Frames.SmartRocket_Prefix", Find = () => Method(typeof(SmartRocketTrajectoryWorker), "ReactiveAcceleration", 1), Hash = "f32155c7b754f742" },
            new Row { Copy = "Frames.ShellArrival_Prefix", Find = () => Method(typeof(TravelingShell), "TryShell", 1), Hash = "e889ce29e25eb59b" },
            new Row { Copy = "Ciws.CeWindowTicks", Find = () => Method(typeof(VerbCIWS<>), "TryFindCEShootLineFromTo", 4), Hash = "d4d9a2143062c890" },
            new Row { Copy = "WorldShells.ExitOnBand", Find = () => Method(typeof(CE_Utility), "ExitCell", 2), Hash = "da9cc609b81c15ee" },
            new Row { Copy = "WorldShells.OrderWorld_Prefix", Find = () => Method(typeof(Building_TurretGunCE), "TryOrderAttackWorldTile", 2), Hash = "083dd4af960f201c" },
            new Row { Copy = "WorldShells.RandomCell", Find = () => Method(typeof(ShellingUtility), "GetRandomImpactCell", 0), Hash = "1d4a0d5d2ff1a28c" },
            // not a copy: called non-virtually with a null verb, so it must not read instance state
            new Row { Copy = "Frames.FixMinCollision", Find = () => Method(typeof(Verb_LaunchProjectileCE), "GetMinCollisionDistance", 1), Hash = "a35f97cfa2fb2078",
                About = "calls Combat Extended's method non-virtually with a null verb: it must not read instance state" },
        };

        // The hash a row is checked against. Harness: -cepackfault=watchcopy gives the first row a wrong one (one IsoRim
        // warning, and mode cereads fails that row).
        internal static string Expected(Row r) => Fault.Is("watchcopy") && r == Rows[0] ? "0000000000000000" : r.Hash;

        internal static string Note(Row r) => "IsoRim CE Pack, " + r.Copy + " (" + r.About + ")";

        // Registers every row with IsoRim's watch (IsoRim Api 70; an older IsoRim: nothing). Never throws.
        public static void Register()
        {
            try
            {
                if (IsoRim.Api.Version >= 70)
                {
                    Watch();
                }
            }
            catch (Exception e)
            {
                Log.Warning(PackZ.Tag + "Watching the copies of Combat Extended code failed: " + PackZ.Reason(e));
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Watch()
        {
            foreach (Row r in Rows)
            {
                IsoRim.Api.WatchCopy(Found(r), Expected(r), Note(r));
            }
        }

        // The row's CE method now (null: not found, or the lookup threw).
        internal static MethodBase Found(Row r)
        {
            try
            {
                return r.Find();
            }
            catch (Exception)
            {
                return null;
            }
        }

        // IsoRim's hash of a method now (IsoRim Api 70; null on an older IsoRim).
        internal static string HashNow(MethodBase m) => IsoRim.Api.Version >= 70 ? HashNow70(m) : null;

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static string HashNow70(MethodBase m) => IsoRim.Api.CopyHash(m);

        private static MethodBase Method(Type t, string name, int args) => t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .FirstOrDefault(m => m.Name == name && m.GetParameters().Length == args);

        // The one compiler-made lambda of t's method (null when there is not exactly one).
        private static MethodBase Lambda(Type t, string method)
        {
            MethodInfo[] found = t.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic).Prepend(t)
                .SelectMany(x => x.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                .Where(m => m.Name.StartsWith("<" + method + ">b__")).ToArray();
            return found.Length == 1 ? found[0] : null;
        }
    }
}
