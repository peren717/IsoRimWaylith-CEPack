using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using CombatExtended;
using HarmonyLib;
using RimWorld;
using Verse;

namespace IsoRimCEPackZ
{
    // World shells between floors (plan §2a TravelingShell, user decision M13). What floors change, call site by call site:
    //  - a shell fired from this map at a world target flies as any shell (Frames) and leaves through the ground rect's
    //    edge (CE's off-map branch makes the world shell). The cell CE aims it at (Verb_ShootMortarCE.GetLocalTargetFor:
    //    where the ray from the shooter towards the target leaves the map) was worked out over the raw map, every band and
    //    the void rows: a void row or another floor's band, a band too far (CE: "unreachable target cell" warnings). It is
    //    worked out over the ground rect from the shooter's ground cell, on the shooter's own floor (and so is the turret's
    //    forced target, Building_TurretGunCE.TryOrderAttackWorldTile, the same exit cell);
    //  - a world shell arriving at a cell: Frames.ShellArrival_Prefix (the target's column, the edge over the ground rect;
    //    it lands on the column top);
    //  - a world shell arriving without a cell: CE picks a random impact cell (ShellingUtility.GetRandomImpactCell, a
    //    Gaussian round the middle of map.Size): on a map with floors that middle is the void rows between the ground and
    //    floor 1, so the shells piled up on the ground's north and south edges. Here the same Gaussian runs over the ground
    //    rect; the cell is the column's top (the first floor a shell coming down meets) and CE's roof rule is read there;
    //  - the world object's damage (WorldObjectDamageWorker: numbers) and the shelled site's attrition when its map is
    //    made (GenStep_Attrition: CE's own blasts while ProjectZ has the map at its ground size, before the floors exist):
    //    floors don't come in.
    internal static class WorldShells
    {
        public static int RandomCells;

        private static readonly MethodInfo getRandom = AccessTools.Method(typeof(ShellingUtility), "GetRandomImpactCell");
        private static readonly FieldInfo mapField = AccessTools.Field(typeof(ShellingUtility), "map");
        private static Func<IntVec3> original;
        private static Func<IntVec3, bool> valid;

        public static void Patch(Harmony h)
        {
            MethodInfo v = AccessTools.Method(typeof(ShellingUtility), "Valid");
            if (getRandom == null || mapField == null || v == null)
            {
                throw new MissingMethodException("ShellingUtility.GetRandomImpactCell / Valid / map");
            }
            original = AccessTools.MethodDelegate<Func<IntVec3>>(getRandom);
            valid = AccessTools.MethodDelegate<Func<IntVec3, bool>>(v);
            PackZ.Transpiler(h, PackZ.Method(typeof(ShellingUtility), nameof(ShellingUtility.FindRandomImpactCell)), typeof(WorldShells), nameof(Find_Transpiler));
            PackZ.Postfix(h, PackZ.Method(typeof(Verb_ShootMortarCE), "GetLocalTargetFor"), typeof(WorldShells), nameof(LocalTarget_Postfix));
            PackZ.Prefix(h, PackZ.Method(typeof(Building_TurretGunCE), nameof(Building_TurretGunCE.TryOrderAttackWorldTile)), typeof(WorldShells), nameof(OrderWorld_Prefix));
        }

        public static int ExitCells;

        // CE: GetLocalTargetFor(targetInfo), the map exit cell towards a world target (CE_Utility.ExitCell over map.Size).
        public static void LocalTarget_Postfix(Verb_ShootMortarCE __instance, RimWorld.Planet.GlobalTargetInfo targetInfo, ref LocalTargetInfo __result)
        {
            if (targetInfo.IsValid && ExitOnBand(__instance.caster, targetInfo.Tile, out IntVec3 c) && c != __result.Cell)
            {
                PackZ.Changed++;
                ExitCells++;
                __result = new LocalTargetInfo(c);
            }
        }

        // CE: Building_TurretGunCE.TryOrderAttackWorldTile: the same exit cell (its own copy of CE_Utility.ExitCell over
        // map.Size) becomes the turret's forced and current target; on a floored map the cell over the ground rect, on the
        // turret's floor, the rest as CE has it.
        private static readonly AccessTools.FieldRef<Building_Turret, LocalTargetInfo> forcedRef = AccessTools.FieldRefAccess<Building_Turret, LocalTargetInfo>("forcedTarget");
        private static readonly AccessTools.FieldRef<Building_TurretGunCE, LocalTargetInfo> currentRef = AccessTools.FieldRefAccess<Building_TurretGunCE, LocalTargetInfo>("currentTargetInt");
        private static readonly Action<Building_TurretGunCE, bool> startShooting = AccessTools.MethodDelegate<Action<Building_TurretGunCE, bool>>(AccessTools.Method(typeof(Building_TurretGunCE), "TryStartShootSomething"));

        public static bool OrderWorld_Prefix(Building_TurretGunCE __instance, RimWorld.Planet.GlobalTargetInfo targetInf, IntVec3? cell)
        {
            if (!ExitOnBand(__instance, targetInf.Tile, out IntVec3 c))
            {
                return true;
            }
            PackZ.Changed++;
            ExitCells++;
            if (cell.HasValue)
            {
                object boxed = targetInf;   // (cellInt is internal to the game: set through reflection)
                AccessTools.Field(typeof(RimWorld.Planet.GlobalTargetInfo), "cellInt").SetValue(boxed, cell.Value);
                targetInf = (RimWorld.Planet.GlobalTargetInfo)boxed;
            }
            __instance.globalTargetInfo = targetInf;
            forcedRef(__instance) = new LocalTargetInfo(c);
            currentRef(__instance) = forcedRef(__instance);
            startShooting(__instance, false);
            return false;
        }

        // Where the ray from the shooter (its ground cell) towards a world tile leaves the ground rect (CE's direction:
        // this map's tile centre minus the target's, its x and z), as a cell on the shooter's floor. False without floors.
        // Copied from CE CE_Utility.ExitCell and Building_TurretGunCE.TryOrderAttackWorldTile (watched, CopyWatches): CE
        // intersects the ray with map.Size, which can't be pointed at the ground rect.
        private static bool ExitOnBand(Thing caster, RimWorld.Planet.PlanetTile tile, out IntVec3 c)
        {
            c = IntVec3.Invalid;
            Map map = caster?.Map;
            Layout L = Layout.Of(map);
            if (L == null || !tile.Valid)
            {
                return false;
            }
            int ks = L.FrameOf(caster.Position);
            if (ks == Layout.None)
            {
                return false;
            }
            UnityEngine.Vector3 dir = (Find.WorldGrid.GetTileCenter(map.Tile) - Find.WorldGrid.GetTileCenter(tile)).normalized;
            float dx = dir.x, dz = dir.z;
            if (dx * dx + dz * dz < 1e-8f)
            {
                return false;
            }
            UnityEngine.Vector3 p = caster.DrawPos;
            float px = p.x, pz = p.z - L.Shift(ks);
            float tx = dx > 0f ? (L.width - px) / dx : dx < 0f ? -px / dx : float.MaxValue;
            float tz = dz > 0f ? (L.rows - pz) / dz : dz < 0f ? -pz / dz : float.MaxValue;
            float t = UnityEngine.Mathf.Max(0f, UnityEngine.Mathf.Min(tx, tz));
            IntVec3 g = new IntVec3(UnityEngine.Mathf.Clamp((int)(px + dx * t), 0, L.width - 1), 0, UnityEngine.Mathf.Clamp((int)(pz + dz * t), 0, L.rows - 1));
            c = L.Cell(g, ks);
            return true;
        }

        public static IEnumerable<CodeInstruction> Find_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo mine = AccessTools.Method(typeof(WorldShells), nameof(RandomCell));
            int found = 0;
            foreach (CodeInstruction ci in instructions)
            {
                if ((ci.opcode == OpCodes.Call || ci.opcode == OpCodes.Callvirt) && ci.operand is MethodInfo mi && mi == getRandom)
                {
                    ci.operand = mine;
                    found++;
                }
                yield return ci;
            }
            if (found != 1)
            {
                throw new InvalidOperationException("ShellingUtility.FindRandomImpactCell: " + found + " GetRandomImpactCell calls (expected 1)");
            }
        }

        // Copied from CE ShellingUtility.GetRandomImpactCell (watched, CopyWatches), over the ground rect (map: ShellingUtility's,
        // set by FindRandomImpactCell).
        public static IntVec3 RandomCell()
        {
            Map map = mapField.GetValue(null) as Map;
            Layout L = Layout.Of(map);
            if (L == null)
            {
                return original();
            }
            PackZ.Changed++;
            RandomCells++;
            for (int i = 0; i < 200; i++)
            {
                IntVec3 g = new IntVec3((int)CE_Utility.RandomGaussian(1f, L.width - 1), 0, (int)CE_Utility.RandomGaussian(1f, L.rows - 1));
                IntVec3 top = ProjectZ.Api.ColumnTop(g, map);
                if (top.IsValid && valid(top))
                {
                    return top;
                }
            }
            return IntVec3.Invalid;
        }
    }
}
