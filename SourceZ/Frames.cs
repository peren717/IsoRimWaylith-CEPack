using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using CombatExtended;
using CombatExtended.Lasers;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRimCEPackZ
{
    // FloorFrames (plan §1.2): the one mechanism that carries a ProjectileCE between floors.
    //
    // CE's fields always hold the projectile in the frame of the storey it is in: x, z on that storey's band rows, heights
    // in CE units over that storey's floor. Every tick, right after CE's MoveForward (a transpiler on ProjectileCE.Tick),
    // Step checks the new point against the frame's two planes (local y <= 0 and y >= S, S = one storey in CE units):
    //  - Down through plane k (the floor of storey k): a solid plane is CE's ground (y <= 0: CE's ImpactSomething lands it
    //    on that slab, deck or roof), except that an overhead shell over a roof with no floor above goes on down so CE's own
    //    mortar-vs-roof rule meets that roof. An open plane (open air, a stairwell's opening): the step is clipped to just
    //    above the plane and the projectile is moved into storey k-1 at the end of the tick (Rebase, a Tick postfix).
    //  - Up through plane k+1: an open plane: clipped just past it, then moved into storey k+1. A solid plane stops direct
    //    fire there (the projectile hits its underside: CE's own roof test misses most roofs from below, it only looks at
    //    cells with tall cover); overhead shells leave through roofs and slabs on the way up, as CE lets them (D3 c).
    //  - Leaving the band's rows (a miss past a band's north or south edge): an out-of-map point, so CE's own off-map branch
    //    runs (Destroy, or a world shell) (D9g). Nothing ever flies through the void rows between bands.
    // Rebase translates the positional fields (exact and last position, origin, destination, origin cell, shot height,
    // the smart rockets' aim height) and clears CE's caches (predicted positions, shot line); the velocity is unchanged
    // (frames are pure translations). Lerped trajectories land where their height reaches the frame's floor: after a move
    // down their end is worked out again for the new storey (the same horizontal speed, so the path is unchanged).
    // Api surface explosions (SurfaceExplosions): the plane contacts are reported there (BeginStep, Contact); both return
    // at once while no rule is registered.
    // Cost without floors: one map lookup per projectile tick (Layout.Of caches the last map).
    internal static class Frames
    {
        private const float Eps = 0.001f;

        // ------------------------------------------------------------------ CE members (Unity-typed: accessors)
        internal static readonly AccessTools.FieldRef<ProjectileCE, Vector3> exactRef = AccessTools.FieldRefAccess<ProjectileCE, Vector3>(Fault.Name("exactPosition", "frames"));
        internal static readonly AccessTools.FieldRef<ProjectileCE, Vector3> lastPosRef = AccessTools.FieldRefAccess<ProjectileCE, Vector3>("LastPos");
        internal static readonly AccessTools.FieldRef<ProjectileCE, Vector2> originRef = AccessTools.FieldRefAccess<ProjectileCE, Vector2>("origin");
        internal static readonly AccessTools.FieldRef<ProjectileCE, Vector2> destRef = AccessTools.FieldRefAccess<ProjectileCE, Vector2>("Destination");
        internal static readonly AccessTools.FieldRef<ProjectileCE, Vector3> velocityRef = AccessTools.FieldRefAccess<ProjectileCE, Vector3>("velocity");
        private static readonly AccessTools.FieldRef<ProjectileCE, List<Vector3>> predictedRef = AccessTools.FieldRefAccess<ProjectileCE, List<Vector3>>("cachedPredictedPositions");
        private static readonly AccessTools.FieldRef<ProjectileCE, int> shotLineTickRef = AccessTools.FieldRefAccess<ProjectileCE, int>("lastShotLine");
        internal static readonly Func<ProjectileCE, Vector3> exactGet =
            AccessTools.MethodDelegate<Func<ProjectileCE, Vector3>>(AccessTools.PropertyGetter(typeof(ProjectileCE), "ExactPosition"));
        internal static readonly Action<ProjectileCE, Vector3> exactSet =
            AccessTools.MethodDelegate<Action<ProjectileCE, Vector3>>(AccessTools.PropertySetter(typeof(ProjectileCE), "ExactPosition"));

        // Harness counters.
        public static int RebasesUp, RebasesDown, PlaneHits, EdgeExits, Normalised;

        // The projectile whose step was clipped at a plane this tick, and what follows at the end of its tick.
        private static ProjectileCE pending;
        private static int pendingDir;           // +1 up, -1 down, 0 hit the plane's underside

        public static void Patch(Harmony h)
        {
            MethodInfo tick = PackZ.Method(typeof(ProjectileCE), nameof(ProjectileCE.Tick));
            PackZ.Transpiler(h, tick, typeof(Frames), nameof(Tick_Transpiler));
            PackZ.Postfix(h, tick, typeof(Frames), nameof(Tick_Postfix));
            Fault.Throw("flightgroup");   // harness: the group fails partway (its first two patches are undone)
            PackZ.Prefix(h, PackZ.Method(typeof(ProjectileCE), "TryCollideWithRoof"), typeof(Frames), nameof(Roof_Prefix));
            PackZ.Transpiler(h, PackZ.Method(typeof(ProjectileCE), nameof(ProjectileCE.ImpactSomething)), typeof(Frames), nameof(ImpactSomething_Transpiler));
            MethodInfo launch = typeof(ProjectileCE).GetMethods(BindingFlags.Instance | BindingFlags.Public).FirstOrDefault(m => m.Name == "Launch" && m.GetParameters().Length == 3 && m.DeclaringType == typeof(ProjectileCE));
            if (launch == null)
            {
                throw new MissingMethodException("ProjectileCE.Launch(Thing, Vector2, Thing)");
            }
            BindMinCollision();
            PackZ.Postfix(h, launch, typeof(Frames), nameof(Launch_Postfix));
            PackZ.Postfix(h, PackZ.Method(typeof(ProjectileCE), nameof(ProjectileCE.Throw)), typeof(Frames), nameof(Throw_Postfix));
            PackZ.Prefix(h, PackZ.Method(typeof(ProjectileCE), nameof(ProjectileCE.RayCast)), typeof(Frames), nameof(RayCast_Prefix));
        }

        // Not key (a failure leaves these cases as CE has them): world shells arriving, homing and smart rockets.
        public static void PatchExtras(Harmony h)
        {
            PackZ.Prefix(h, PackZ.Method(typeof(TravelingShell), "LaunchProjectile"), typeof(Frames), nameof(ShellArrival_Prefix));
            PackZ.Prefix(h, PackZ.Method(typeof(HomingBulletTrajectoryWorker), "ReactiveAcceleration"), typeof(Frames), nameof(Homing_Prefix));
            PackZ.Prefix(h, PackZ.Method(typeof(SmartRocketTrajectoryWorker), "ReactiveAcceleration"), typeof(Frames), nameof(SmartRocket_Prefix));
        }

        // ------------------------------------------------------------------ the step
        public static IEnumerable<CodeInstruction> Tick_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo move = AccessTools.Method(typeof(ProjectileCE), "MoveForward");
            MethodInfo step = AccessTools.Method(typeof(Frames), nameof(Step));
            int found = 0;
            foreach (CodeInstruction ci in instructions)
            {
                yield return ci;
                if ((ci.opcode == OpCodes.Call || ci.opcode == OpCodes.Callvirt) && ci.operand is MethodInfo mi && mi == move)
                {
                    yield return new CodeInstruction(OpCodes.Ldarg_0);
                    yield return new CodeInstruction(OpCodes.Call, step);
                    found++;
                }
            }
            if (found != 1)
            {
                throw new InvalidOperationException("ProjectileCE.Tick: " + found + " MoveForward calls (expected 1)");
            }
        }

        // The point CE moves to this tick, checked against the frame's planes and band (see the class header).
        public static Vector3 Step(Vector3 next, ProjectileCE p)
        {
            pending = null;
            SurfaceExplosions.BeginStep(p);
            Layout L = Layout.Of(p.Map);
            if (L == null)
            {
                return next;
            }
            Vector3 cur = exactGet(p);
            int k = L.FrameOf(cur);
            if (k == Layout.None)
            {
                // a projectile in a void row (an old save): off the map
                EdgeExits++;
                return OffMap(next);
            }
            int shift = L.Shift(k);
            if (!L.OnGround(next.z - shift))
            {
                EdgeExits++;
                return OffMap(next);
            }
            float S = Layout.S;
            bool overhead = p.def.projectile.flyOverhead;
            if (next.y <= 0f && k > L.min && cur.y > 0f)
            {
                float t = cur.y / (cur.y - next.y);
                Vector3 at = Vector3.Lerp(cur, next, t);
                IntVec3 g = new IntVec3(Mathf.FloorToInt(at.x), 0, Mathf.FloorToInt(at.z) - shift);
                bool floor = ProjectZ.Api.HasFloorAt(g, k, p.Map);
                if (floor || (!overhead && ProjectZ.Api.PlaneSolid(g, k, p.Map)))
                {
                    SurfaceExplosions.Contact(p, L, k, at, k, false, false);
                    return next;   // CE's ground: a slab, deck or roof top
                }
                at.y = Eps;
                pending = p;
                pendingDir = -1;
                if (p.TrajectoryWorker is LerpedTrajectoryWorker && p.ticksToImpact <= 0)
                {
                    p.ticksToImpact = 1;   // the lerped end is worked out again in the storey below (Rebase)
                }
                return at;
            }
            if (next.y >= S && k < L.max + 1 && cur.y < S)
            {
                float t = (S - cur.y) / (next.y - cur.y);
                Vector3 at = Vector3.Lerp(cur, next, t);
                IntVec3 g = new IntVec3(Mathf.FloorToInt(at.x), 0, Mathf.FloorToInt(at.z) - shift);
                if (!overhead && ProjectZ.Api.PlaneSolid(g, k + 1, p.Map))
                {
                    SurfaceExplosions.Contact(p, L, k + 1, at, k, true, false);
                    at.y = S - Eps;
                    pending = p;
                    pendingDir = 0;
                    return at;
                }
                if (k >= L.max)
                {
                    return next;   // over the top floor: it stays in the top frame
                }
                at.y = S + Eps;
                pending = p;
                pendingDir = 1;
                return at;
            }
            return next;
        }

        private static Vector3 OffMap(Vector3 v) => new Vector3(-1000f, v.y, -1000f);

        public static void Tick_Postfix(ProjectileCE __instance)
        {
            if (pending != __instance)
            {
                return;
            }
            ProjectileCE p = pending;
            pending = null;
            if (!p.Spawned || p.Destroyed || p.landed)
            {
                return;
            }
            if (pendingDir == 0)
            {
                // direct fire into the underside of a solid plane (a slab or roof over this storey)
                PlaneHits++;
                if (SurfaceExplosions.Active)
                {
                    Layout l = Layout.Of(p.Map);
                    Vector3 at = exactGet(p);
                    int frame = l.FrameOf(at);
                    SurfaceExplosions.Contact(p, l, frame + 1, at, frame, true, true);   // (a contact for the Api's surface rules)
                }
                p.landed = true;
                p.Impact(null);
                return;
            }
            Rebase(p, pendingDir);
        }

        // Moves the projectile one storey up (+1) or down (-1): its fields into the next storey's frame.
        public static void Rebase(ProjectileCE p, int dir)
        {
            Layout L = Layout.Of(p.Map);
            if (L == null)
            {
                return;
            }
            int k = L.FrameOf(exactGet(p));
            int k2 = k + dir;
            if (k == Layout.None || !L.Has(k2))
            {
                return;
            }
            float dz = L.Shift(k2) - L.Shift(k);
            float dy = -dir * Layout.S;
            lastPosRef(p) = lastPosRef(p) + new Vector3(0f, dy, dz);
            Vector2 o = originRef(p);
            originRef(p) = new Vector2(o.x, o.y + dz);
            Vector2 d = destRef(p);
            destRef(p) = new Vector2(d.x, d.y + dz);
            p.OriginIV3 = new IntVec3(p.OriginIV3.x, 0, p.OriginIV3.z + (int)dz);
            p.shotHeight += dy;
            p.intendedTargetHeight += dy;
            predictedRef(p) = null;
            shotLineTickRef(p) = -1;
            exactSet(p, exactGet(p) + new Vector3(0f, dy, dz));   // (the setter moves Position onto the new band)
            if (dir > 0)
            {
                RebasesUp++;
            }
            else
            {
                RebasesDown++;
                if (p.TrajectoryWorker is LerpedTrajectoryWorker)
                {
                    LerpedEnd(p);
                }
            }
        }

        // A lerped projectile's flight ends where its height reaches the floor of the frame it is in now: the end tick and
        // destination again from CE's own formulas (CE: Launch), with the shot height over this floor.
        private static void LerpedEnd(ProjectileCE p)
        {
            BaseTrajectoryWorker w = p.TrajectoryWorker;
            float h = p.shotHeight;
            float flight = w.GetFlightTime(p.shotAngle, p.shotSpeed, p.GravityPerWidth, h) * 60f;
            float dist = w.DistanceTraveled(h, p.shotSpeed, p.shotAngle, p.GravityPerWidth);
            if (float.IsNaN(flight) || float.IsNaN(dist) || flight <= 0f || dist <= 0f)
            {
                return;
            }
            Vector2 o = originRef(p);
            Vector2 dir = destRef(p) - o;
            if (dir.sqrMagnitude < 1e-6f)
            {
                return;
            }
            destRef(p) = o + dir.normalized * dist;
            p.startingTicksToImpact = flight;
            p.ticksToImpact = Mathf.Max(1, Mathf.CeilToInt(flight) - p.FlightTicks);
        }

        // ------------------------------------------------------------------ launch normalisation
        // A projectile starting outside [0, S) of its frame (a world shell arriving high, a tall launcher) is moved to the
        // frame of its absolute height. Also: CE's minimum collision distance from the shooter is worked out from the raw
        // distance to the target (TryCastShot), which is a band too far for a target on another floor.
        public static void Launch_Postfix(ProjectileCE __instance)
        {
            Normalise(__instance);
            FixMinCollision(__instance);
        }

        public static void Throw_Postfix(ProjectileCE __instance) => Normalise(__instance);

        private static void Normalise(ProjectileCE p)
        {
            Layout L = Layout.Of(p.Map);
            if (L == null || !p.Spawned)
            {
                return;
            }
            float S = Layout.S;
            for (int i = 0; i < 64; i++)
            {
                Vector3 e = exactGet(p);
                int k = L.FrameOf(e);
                if (k == Layout.None)
                {
                    return;
                }
                if (e.y >= S && k < L.max)
                {
                    Rebase(p, 1);
                }
                else if (e.y < 0f && k > L.min)
                {
                    Rebase(p, -1);
                }
                else
                {
                    return;
                }
                Normalised++;
            }
        }

        private static void FixMinCollision(ProjectileCE p)
        {
            Layout L = Layout.Of(p.Map);
            if (L == null || !p.intendedTarget.IsValid || p.def.projectile.alwaysFreeIntercept)
            {
                return;
            }
            Vector2 o = originRef(p);
            int ks = L.FrameOfRow(Mathf.FloorToInt(o.y));
            int kt = L.FrameOf(p.intendedTarget.Cell);
            if (ks == kt || ks == Layout.None || kt == Layout.None)
            {
                return;
            }
            if (minCollision == null)
            {
                return;
            }
            Vector3 t = L.TranslateRows(p.intendedTarget.Cell.ToVector3Shifted(), kt, ks);
            float dist = new Vector2(t.x - o.x, t.z - o.y).magnitude;
            float min;
            try
            {
                min = minCollision(null, dist);
            }
            catch (Exception e)
            {
                minCollision = null;
                PackZ.Once("mincollision", "Combat Extended's GetMinCollisionDistance could not be called (" + PackZ.Reason(e) + "): shots between floors keep CE's own minimum collision distance.");
                return;
            }
            PackZ.Changed++;
            p.minCollisionDistance = min;
        }

        // CE's own Verb_LaunchProjectileCE.GetMinCollisionDistance(distance): a protected helper that reads no verb state,
        // called without a verb through a non-virtual delegate. Null when CE no longer has it (one warning; CE's own value
        // then stays between floors).
        private static Func<Verb_LaunchProjectileCE, float, float> minCollision;

        private static void BindMinCollision()
        {
            MethodInfo m = AccessTools.Method(typeof(Verb_LaunchProjectileCE), "GetMinCollisionDistance", new[] { typeof(float) });
            if (m == null || m.IsStatic || m.ReturnType != typeof(float))
            {
                PackZ.Once("mincollision", "Combat Extended's Verb_LaunchProjectileCE.GetMinCollisionDistance(float) was not found: shots between floors keep CE's own minimum collision distance.");
                return;
            }
            try
            {
                minCollision = PackZ.NonVirtual<Func<Verb_LaunchProjectileCE, float, float>>(m);
            }
            catch (Exception e)
            {
                PackZ.Once("mincollision", "Combat Extended's GetMinCollisionDistance could not be bound (" + PackZ.Reason(e) + "): shots between floors keep CE's own minimum collision distance.");
            }
        }

        // ------------------------------------------------------------------ roofs
        // A stairwell's opening is a roof to CE: no collision where the ceiling is open (ProjectZ Api CeilingSolid).
        public static bool Roof_Prefix(ProjectileCE __instance, IntVec3 cell, ref bool __result)
        {
            Map map = __instance.Map;
            if (Layout.Of(map) == null || !cell.Roofed(map) || ProjectZ.Api.CeilingSolid(cell, map))
            {
                return true;
            }
            PackZ.Changed++;
            __result = false;
            return false;
        }

        // ImpactSomething (an overhead shell landing): the roof over the cell is read as CE reads it, but a stairwell's
        // opening is no roof (CE would collapse it).
        public static IEnumerable<CodeInstruction> ImpactSomething_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo roofAt = AccessTools.Method(typeof(RoofGrid), nameof(RoofGrid.RoofAt), new[] { typeof(IntVec3) });
            MethodInfo mine = AccessTools.Method(typeof(Frames), nameof(RoofAtFor));
            int found = 0;
            foreach (CodeInstruction ci in instructions)
            {
                if ((ci.opcode == OpCodes.Call || ci.opcode == OpCodes.Callvirt) && ci.operand is MethodInfo mi && mi == roofAt)
                {
                    yield return new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(ci);
                    yield return new CodeInstruction(OpCodes.Call, mine);
                    found++;
                    continue;
                }
                yield return ci;
            }
            if (found == 0)
            {
                throw new InvalidOperationException("ProjectileCE.ImpactSomething: no RoofAt call");
            }
        }

        public static RoofDef RoofAtFor(RoofGrid grid, IntVec3 c, ProjectileCE p)
        {
            RoofDef r = grid.RoofAt(c);
            if (r != null && Layout.Of(p.Map) != null && !ProjectZ.Api.CeilingSolid(c, p.Map))
            {
                PackZ.Changed++;
                return null;
            }
            return r;
        }

        // ------------------------------------------------------------------ instant shots (lasers): RayCast
        private static readonly Func<Thing, Bounds> boundsFor = AccessTools.MethodDelegate<Func<Thing, Bounds>>(AccessTools.Method(typeof(CE_Utility), nameof(CE_Utility.GetBoundsFor), new[] { typeof(Thing) }));
        private static readonly Action<LaserBeamCE, Vector3, Vector3> spawnBeam = AccessTools.MethodDelegate<Action<LaserBeamCE, Vector3, Vector3>>(AccessTools.Method(typeof(LaserBeamCE), "SpawnBeam"));
        private static readonly Action<LaserBeamCE, Thing, Vector3> laserImpact = AccessTools.MethodDelegate<Action<LaserBeamCE, Thing, Vector3>>(
            typeof(LaserBeamCE).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).First(m => m.Name == "Impact" && m.GetParameters().Length == 2));
        private static readonly Action<ProjectileCE, IntVec3, IntVec3, Map> rayCastSuppression = AccessTools.MethodDelegate<Action<ProjectileCE, IntVec3, IntVec3, Map>>(AccessTools.Method(typeof(ProjectileCE), "RayCastSuppression"));

        public static int RayCastsAcross;

        // Copied from CE ProjectileCE.RayCast (watched, CopyWatches: CE marches its own ray over the raw map), with the same
        // plane rule per step: the ray is marched in the frame of the storey each point is in (open planes move it on, solid
        // planes stop it as the ground does). On a map without floors CE's own runs.
        public static bool RayCast_Prefix(ProjectileCE __instance, Thing launcher, VerbProperties verbProps, Vector2 origin, float shotAngle, float shotRotation,
            float shotHeight, float shotSpeed, float spreadDegrees, float aperatureSize, Thing equipment, bool useSameHeight)
        {
            Map map = launcher?.Map;
            Layout L = Layout.Of(map);
            if (L == null)
            {
                return true;
            }
            ProjectileCE self = __instance;
            int k = L.FrameOfRow(Mathf.FloorToInt(origin.y));
            if (k == Layout.None)
            {
                return true;
            }
            float S = Layout.S;
            float num = Mathf.Sin(0.00052359875f) + aperatureSize;
            float num2 = 1f / (num * num * 3.14159f);
            ProjectilePropertiesCE props = self.def.projectile as ProjectilePropertiesCE;
            shotRotation = (float)Math.PI / 180f * shotRotation + 1.570795f;
            Vector3 dir = new Vector3(Mathf.Cos(shotRotation) * Mathf.Cos(shotAngle), Mathf.Sin(shotAngle), Mathf.Sin(shotRotation) * Mathf.Cos(shotAngle));
            Vector3 start = new Vector3(origin.x, shotHeight, origin.y);
            Vector3 end = dir * verbProps.range + start;
            self.shotAngle = shotAngle;
            self.shotHeight = shotHeight;
            self.shotRotation = shotRotation;
            self.launcher = launcher;
            originRef(self) = origin;
            self.equipment = equipment;
            self.equipmentDef = equipment?.def;
            LaserBeamCE beam = self as LaserBeamCE;
            float spread = Mathf.Sin(spreadDegrees / 2f * ((float)Math.PI / 180f));
            Ray ray = new Ray(start, dir);
            Vector3 muzzle = ray.GetPoint((self.equipmentDef as LaserGunDef)?.barrelLength ?? 0.9f);
            Vector3 off = Vector3.zero;   // the launch frame to the current frame
            int frame = k;
            Thing intended = self.intendedTargetThing;
            for (int i = 1; (float)i < verbProps.range; i++)
            {
                float area = ((float)i * spread + aperatureSize) * ((float)i * spread + aperatureSize) * 3.14159f;
                if (props.damageFalloff && beam != null)
                {
                    beam.DamageModifier = 1f / (num2 * area);
                }
                Vector3 p2 = ray.GetPoint(i) + off;
                bool stop = false;
                for (int guard = 0; guard < 8; guard++)
                {
                    IntVec3 g = new IntVec3(Mathf.FloorToInt(p2.x), 0, Mathf.FloorToInt(p2.z) - L.Shift(frame));
                    if (p2.y < 0f && frame > L.min)
                    {
                        if (ProjectZ.Api.PlaneSolid(g, frame, map))
                        {
                            break;
                        }
                        off += new Vector3(0f, S, L.Shift(frame - 1) - L.Shift(frame));
                        frame--;
                    }
                    else if (p2.y >= S && frame < L.max + 1)
                    {
                        if (ProjectZ.Api.PlaneSolid(g, frame + 1, map))
                        {
                            SurfaceExplosions.Contact(self, L, frame + 1, p2, frame, true, true);
                            p2.y = S - Eps;
                            stop = true;
                            break;
                        }
                        if (frame >= L.max)
                        {
                            break;
                        }
                        off += new Vector3(0f, -S, L.Shift(frame + 1) - L.Shift(frame));
                        frame++;
                    }
                    else
                    {
                        break;
                    }
                    RayCastsAcross++;
                    p2 = ray.GetPoint(i) + off;
                }
                if (p2.y < 0f || stop)
                {
                    end = p2;
                    self.landed = true;
                    exactSet(self, p2);
                    self.Position = p2.ToIntVec3();
                    break;
                }
                IntVec3 c = p2.ToIntVec3();
                if (!c.InBounds(map) || !L.OnGround(p2.z - L.Shift(frame)))
                {
                    Vector3 back = ray.GetPoint(i - 1) + off;
                    exactSet(self, back);
                    end = back;
                    self.landed = true;
                    lastPosRef(self) = end;
                    self.Position = back.ToIntVec3();
                    Vector3 from = L.Translate(muzzle, k, frame);
                    if (useSameHeight)
                    {
                        from.y = end.y;
                    }
                    if (beam != null)
                    {
                        spawnBeam(beam, from, end);
                        rayCastSuppression(self, from.ToIntVec3(), end.ToIntVec3(), null);
                        laserImpact(beam, null, from);
                    }
                    else
                    {
                        self.Impact(null);
                    }
                    return false;
                }
                Ray local = new Ray(start + off, dir);
                // (other mods' volumes over the cell too, Api target geometry: only while one is registered)
                foreach (Thing item in TargetGeometry.Active ? TargetGeometry.ThingsAt(map.thingGrid, c, self) : map.thingGrid.ThingsListAtFast(c))
                {
                    if (self == item)
                    {
                        continue;
                    }
                    Bounds b = TargetGeometry.Active && TargetGeometry.TryVolumeIn(item, map, frame, out Bounds volume) ? volume : boundsFor(item);
                    if (!b.IntersectRay(local) || (i < 2 && item != intended))
                    {
                        continue;
                    }
                    if (item is Plant plant)
                    {
                        if (!Rand.Chance(item.def.fillPercent * plant.Growth))
                        {
                            continue;
                        }
                    }
                    else if (item is Building && !Rand.Chance(item.def.fillPercent))
                    {
                        continue;
                    }
                    exactSet(self, p2);
                    end = p2;
                    self.landed = true;
                    lastPosRef(self) = end;
                    self.Position = p2.ToIntVec3();
                    Vector3 from = L.Translate(muzzle, k, frame);
                    if (useSameHeight)
                    {
                        from.y = end.y;
                    }
                    if (beam != null)
                    {
                        spawnBeam(beam, from, end);
                        rayCastSuppression(self, from.ToIntVec3(), end.ToIntVec3(), null);
                        laserImpact(beam, item, from);
                    }
                    else
                    {
                        self.Impact(item);
                    }
                    return false;
                }
            }
            if (beam != null)
            {
                Vector3 from = L.Translate(muzzle, k, frame);
                if (useSameHeight)
                {
                    from.y = end.y;
                }
                spawnBeam(beam, from, end);
                rayCastSuppression(self, from.ToIntVec3(), end.ToIntVec3(), null);
                self.Destroy();
            }
            return false;
        }

        // ------------------------------------------------------------------ world shells arriving
        // CE picks the arrival edge cell and the impact cell over the raw map (every band and the void rows). On a floored
        // map both are worked out over the ground rows: the target's column (its ground cell), the edge cell where the
        // shell's line from that column leaves the ground rows (as CE does: the same direction, three quarters of the way
        // for a shell). The shell then flies in the ground frame and Launch moves it to the frame of its height; it lands
        // on the first floor it meets coming down (the column top, D3 a). Copied from CE TravelingShell.TryShell's arrival
        // ray (watched, CopyWatches): CE works it out inline over map.Size, which can't be pointed at the ground rect.
        public static bool ShellArrival_Prefix(ref IntVec3 sourceCell, ref LocalTargetInfo target, Map map)
        {
            Layout L = Layout.Of(map);
            if (L == null)
            {
                return true;
            }
            IntVec3 t = target.Cell;
            IntVec3 gt = L.Ground(t);
            if (!gt.IsValid)
            {
                gt = new IntVec3(Mathf.Clamp(t.x, 0, L.width - 1), 0, Mathf.Clamp(t.z, 0, L.rows - 1));
            }
            Vector2 dir = new Vector2(sourceCell.x - t.x, sourceCell.z - t.z);
            if (dir.sqrMagnitude < 0.01f)
            {
                dir = new Vector2(0f, 1f);
            }
            dir.Normalize();
            // the distance from gt to the ground rect's edge along dir
            float tx = dir.x > 0f ? (L.width - 0.01f - gt.x) / dir.x : dir.x < 0f ? (gt.x - 0f) / -dir.x : float.MaxValue;
            float tz = dir.y > 0f ? (L.rows - 0.01f - gt.z) / dir.y : dir.y < 0f ? (gt.z - 0f) / -dir.y : float.MaxValue;
            float dist = Mathf.Min(tx, tz) * 0.75f;   // Copied from CE TravelingShell.TryShell (watched)
            IntVec3 src = new IntVec3(Mathf.Clamp(Mathf.FloorToInt(gt.x + dir.x * dist), 0, L.width - 1), 0, Mathf.Clamp(Mathf.FloorToInt(gt.z + dir.y * dist), 0, L.rows - 1));
            sourceCell = src;
            target = new LocalTargetInfo(gt);
            return true;
        }

        // ------------------------------------------------------------------ homing (D9d: the target in the bullet's frame)
        private static Vector3 TargetInFrame(ProjectileCE p, Thing thing, IntVec3 cell, out bool moved)
        {
            moved = false;
            Vector3 v = thing?.DrawPos ?? cell.ToVector3Shifted();
            Layout L = Layout.Of(p.Map);
            if (L == null)
            {
                return v;
            }
            int kp = L.FrameOf(exactGet(p));
            int kt = L.FrameOf(thing?.Position ?? cell);
            if (kp == kt || kp == Layout.None || kt == Layout.None)
            {
                return v;
            }
            moved = true;
            return L.TranslateRows(v, kt, kp);
        }

        // Copied from CE HomingBulletTrajectoryWorker.ReactiveAcceleration (watched, CopyWatches), the target in the bullet's
        // frame (only when it is on another floor; else CE's own runs).
        public static bool Homing_Prefix(ProjectileCE projectile)
        {
            if (projectile.homingAcceleration <= 0f || projectile.FlightTicks < 3)
            {
                return true;
            }
            LocalTargetInfo target = projectile.intendedTarget;
            if (target.ThingDestroyed)
            {
                return true;
            }
            Vector3 val = TargetInFrame(projectile, target.Thing, target.Cell, out bool moved);
            if (!moved)
            {
                return true;
            }
            Vector3 e = exactGet(projectile);
            val.y = e.y;
            Vector3 to = val - e;
            Vector3 vel = velocityRef(projectile);
            if (!(to.sqrMagnitude < 0.01f) && !(Vector3.Dot(vel, to) <= 0f))
            {
                float ramp = Mathf.Clamp01((float)(projectile.FlightTicks - 3) / 8f);
                velocityRef(projectile) = Vector3.RotateTowards(vel, to.normalized * vel.magnitude, projectile.homingAcceleration * ramp, 0f);
            }
            return false;
        }

        // Copied from CE SmartRocketTrajectoryWorker.ReactiveAcceleration (watched, CopyWatches), the target in the rocket's
        // frame (its aim height is moved with the rocket by Rebase).
        public static bool SmartRocket_Prefix(ProjectileCE projectile)
        {
            LocalTargetInfo target = projectile.intendedTarget;
            if (target.ThingDestroyed || projectile.fuelTicks < 1)
            {
                return true;
            }
            Vector3 v = TargetInFrame(projectile, target.Thing, target.Cell, out bool moved);
            if (!moved)
            {
                return true;
            }
            projectile.fuelTicks--;
            v.y = projectile.intendedTargetHeight;
            Vector3 to = v - exactGet(projectile);
            velocityRef(projectile) = velocityRef(projectile) + to.normalized * projectile.Props.speedGain / 60f / 60f;
            return false;
        }
    }
}
