using System;
using CombatExtended;
using Verse;

namespace IsoRimCEPackZ
{
    // Placement queries (Api.OpenPlacement): the map and operator of an unspawned query turret.
    //
    // A query turret is never spawned, so CE's own reads of its map (VerbProperties.AdjustedRange) and of its operator
    // (Verb_LaunchProjectileCE.ShooterPawn) would find nothing. While a query runs, Enter names the turret (the subject)
    // with the map and operator it stands for; the readers below answer for the subject and give the game's own value for
    // every other thing. Queries nest: the caller keeps what Enter returned and hands it back to Exit in a finally.
    // Main thread only (the Api's rule). Cost with no query open: one static int test per read.
    internal static class QueryContext
    {
        internal readonly struct State
        {
            public readonly Thing Subject;
            public readonly Map Map;
            public readonly Pawn Operator;

            public State(Thing subject, Map map, Pawn @operator)
            {
                Subject = subject;
                Map = map;
                Operator = @operator;
            }
        }

        [ThreadStatic] private static State current;

        // Queries open now (the readers' fast path: 0 = no subject anywhere).
        private static int depth;

        internal static State Enter(Thing subject, Map map, Pawn @operator)
        {
            State before = current;
            current = new State(subject, map, @operator);
            depth++;
            return before;
        }

        internal static void Exit(State before)
        {
            current = before;
            depth--;
        }

        // The map a query stands its subject on; null for every other thing (and with no query open).
        internal static Map SubjectMap(Thing thing) => depth != 0 && thing != null && thing == current.Subject ? current.Map : null;

        // Thing.Map and Thing.MapHeld as the transpiled CE code reads them (a null thing gives null where CE would throw).
        internal static Map MapOf(Thing thing) => SubjectMap(thing) ?? thing?.Map;

        internal static Map MapHeldOf(Thing thing) => SubjectMap(thing) ?? thing?.MapHeld;

        // CE_Utility.TryGetTurretOperator as Verb_LaunchProjectileCE.ShooterPawn reads it.
        internal static Pawn OperatorOf(Thing thing)
        {
            if (depth != 0 && thing != null && thing == current.Subject)
            {
                return current.Operator;
            }
            return CE_Utility.TryGetTurretOperator(thing);
        }
    }
}
