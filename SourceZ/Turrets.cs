using System.Reflection;
using CombatExtended;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRimCEPackZ
{
    // CE's turrets between floors (plan §2d, D7). Target picking for snap turrets stays IsoRimZ's (its BestAttackTarget
    // postfix, with CE's own CanHitTargetFrom, fixed by Aim and Sight); the pack fixes CE's own turret code:
    //  - Building_TurretGunCE.OrderAttack (the player's forced target, mortars): CE checks the range by the raw rows. For a
    //    target on another floor the same order with the range rule's distances (ProjectZ Api Distance3D, RangeDistance:
    //    plain 3D distance, D1), as IsoRimZ does for the game's own turrets. One floor: CE's own.
    //  - TryFindNewTarget's hostile-mortar pick (a random colonist building in range): through the scope, by the 3D distance.
    //  - The fire arc (CompFireArc.WithinFireArc) and a non-snap turret's turn to its target (DeltaAngle): the target seen in
    //    the turret's frame (its ground direction), through the scope.
    // The non-snap target finder and CE's AI: Ai.
    internal static class Turrets
    {
        private static readonly AccessTools.FieldRef<Building_Turret, LocalTargetInfo> forced = AccessTools.FieldRefAccess<Building_Turret, LocalTargetInfo>(Fault.Name("forcedTarget", "turrets"));
        private static readonly AccessTools.FieldRef<Building_TurretGunCE, bool> holdFire = AccessTools.FieldRefAccess<Building_TurretGunCE, bool>("holdFire");
        public static int OrdersAcross, OrdersRefused;

        public static bool On;   // every patch of the group landed

        // On the CE build the pack was tested on (Scope.Expect).
        private const int TestedTurretLambdas = 1;

        public static void Patch(Harmony h)
        {
            System.Type t = typeof(Building_TurretGunCE);
            MethodInfo order = Aim.ByName(t, "OrderAttack", 1);
            MethodInfo find = Aim.ByName(t, "TryFindNewTarget", 0);
            MethodInfo delta = AccessTools.PropertyGetter(t, "DeltaAngle") ?? throw new System.MissingMethodException("Building_TurretGunCE.DeltaAngle");
            MethodInfo arc = Aim.ByName(typeof(CompFireArc), "WithinFireArc", 1);
            Scope.CheckSwaps();
            PackZ.Prefix(h, order, typeof(Turrets), nameof(OrderAttack_Prefix));
            Scope.Around(h, find, nameof(Scope.Instance_Prefix));
            Scope.Expect("Building_TurretGunCE.TryFindNewTarget lambdas", Scope.ScopeAll(h, t, false, "TryFindNewTarget"), TestedTurretLambdas);
            Scope.Entry(h, delta, nameof(Scope.Instance_Prefix));
            Scope.Entry(h, arc, nameof(Scope.Instance_Prefix));
            On = true;
        }

        // Copied from CE Building_TurretGunCE.OrderAttack (watched, CopyWatches), for a target on another floor. Why a copy:
        // CE measures both range limits as (targ.Cell - Position).LengthHorizontal. ProjectZ's 3D scope does not answer an
        // IntVec3 difference (it has no height to carry), and the pack's Scope swap of it gives the ground distance, so the
        // limits would be judged flat. A transpiler swapping each subtraction-plus-LengthHorizontal pair for the 3D distance
        // was judged more fragile than this short watched copy: it must match a multi-instruction IL pattern (the
        // difference goes through a local) and tell the minimum range (Distance3D) from the maximum (RangeDistance, the
        // range rule) by their order in CE's body.
        public static bool OrderAttack_Prefix(Building_TurretGunCE __instance, LocalTargetInfo targ)
        {
            Map map = __instance.Map;
            Layout L = Layout.Of(map);
            if (L == null || !targ.IsValid)
            {
                return true;
            }
            int ks = L.FrameOf(__instance.Position), kt = L.FrameOf(targ.Cell);
            if (ks == kt || ks == Layout.None || kt == Layout.None)
            {
                return true;
            }
            PackZ.Changed++;
            OrdersAcross++;
            if (__instance.globalTargetInfo.IsValid)
            {
                __instance.ResetForcedTarget();
            }
            CompFireArc arc = __instance.CompFireArc;
            if (arc != null && !arc.WithinFireArc(targ))
            {
                OrdersRefused++;
                __instance.ResetForcedTarget();
                return false;
            }
            VerbProperties vp = __instance.GunCompEq.PrimaryVerb.verbProps;
            if (ProjectZ.Api.Distance3D(__instance.Position, targ.Cell, map) < vp.minRange)
            {
                OrdersRefused++;
                Messages.Message("MessageTargetBelowMinimumRange".Translate(), __instance, MessageTypeDefOf.RejectInput);
                return false;
            }
            if (ProjectZ.Api.RangeDistance(__instance.Position, targ.Cell, map) > vp.range)
            {
                OrdersRefused++;
                Messages.Message("MessageTargetBeyondMaximumRange".Translate(), __instance, MessageTypeDefOf.RejectInput);
                return false;
            }
            if (forced(__instance) != targ)
            {
                forced(__instance) = targ;
                if (__instance.burstCooldownTicksLeft <= 0)
                {
                    __instance.TryStartShootSomething(canBeginBurstImmediately: false);
                }
            }
            if (holdFire(__instance))
            {
                Messages.Message("MessageTurretWontFireBecauseHoldFire".Translate(__instance.def.label), __instance, MessageTypeDefOf.RejectInput, historical: false);
            }
            return false;
        }
    }
}
