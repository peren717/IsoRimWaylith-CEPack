using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using CombatExtended;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace IsoRimCEPackZ
{
    // Test harness only (mode "perf", -cepacktest=perf): one fixed big fight for cost comparisons between builds. 24 rifles
    // (8 on a deck shooting down, 8 on the ground shooting up at a second deck's edge, 8 on the ground shooting along it),
    // 6 frag grenadiers (4 on the ground, 2 throwing down from the deck) and a manned mortar shelling the second deck. Held
    // targets are replaced when they fall, shooters re-ordered when idle, so the fight runs the whole time. Measured on the
    // main thread: the game ticks alone (Stopwatch and allocated bytes around TickManager.DoSingleTick) in batches, then the
    // frame time with one tick per frame. Same seed, same ticks for every build: the impact hash shows the same fight.
    public static partial class TestZ
    {
        // Harness counters the tick loop (Update) fills while PerfOn.
        internal static bool PerfOn, Quiet;
        internal static long PerfTicks, PerfTickTicks, PerfAlloc;

        // Registrations for the perf run (implemented by TestApi; a build without it has none).
        static partial void PerfRegistrations(List<IDisposable> into);

        // idle: no job, or waiting (CE drafted idle is Wait_Combat), or its target fell
        private static bool Idle(Pawn p) => p.CurJob == null || p.CurJobDef == JobDefOf.Wait_Combat || p.CurJobDef == JobDefOf.Wait
            || (p.CurJob.targetA.Thing is Pawn t && (t.Dead || t.Downed || t.Destroyed));

        private static IEnumerator<int> PerfFight()
        {
            Out("--- PERF big fight");
            if (L == null || !L.Has(1))
            {
                Check("perf", false, "needs a map with floors");
                yield break;
            }
            Wipe(20, 145, 125, 220);
            Slab(30, 200, 70, 212);   // deck A: shooters and grenadiers at its edge row, firing down
            Slab(80, 200, 120, 212);  // deck B: targets at its edge row, shot at from below and shelled
            List<Thing> fixture = new List<Thing>();
            List<(Pawn pawn, IntVec3 home)> targets = new List<(Pawn, IntVec3)>();
            List<(Pawn shooter, int first, int count)> shooters = new List<(Pawn, int, int)>();
            // ground targets G (index 0..7) behind a sandbag row, deck B targets (index 8..15)
            for (int i = 0; i < 8; i++)
            {
                IntVec3 g = On(32 + 5 * i, 182, 0);
                targets.Add((Target(g), g));
                fixture.Add(Build("Sandbags", On(32 + 5 * i, 179, 0)));
            }
            for (int i = 0; i < 8; i++)
            {
                IntVec3 d = On(82 + 5 * i, 200, 1);
                targets.Add((Target(d), d));
            }
            for (int i = 0; i < 8; i++)
            {
                shooters.Add((Colonist(On(32 + 5 * i, 201, 1), "Gun_AssaultRifle"), 0, 8));   // deck A, down at G
                shooters.Add((Colonist(On(82 + 5 * i, 166, 0), "Gun_AssaultRifle"), 8, 8));   // ground, up at deck B
                shooters.Add((Colonist(On(32 + 5 * i, 152, 0), i % 2 == 0 ? "Gun_AssaultRifle" : "Gun_LMG"), 0, 8));   // ground, along it at G
            }
            List<Pawn> grenadiers = new List<Pawn>();
            for (int i = 0; i < 4; i++)
            {
                grenadiers.Add(Colonist(On(36 + 10 * i, 172, 0), "Weapon_GrenadeFrag"));
            }
            grenadiers.Add(Colonist(On(45, 202, 1), "Weapon_GrenadeFrag"));
            grenadiers.Add(Colonist(On(60, 202, 1), "Weapon_GrenadeFrag"));
            Building_TurretGunCE mortar = (Building_TurretGunCE)Build("Turret_Mortar", On(100, 150, 0));
            Pawn gunner = Colonist(On(100, 148, 0), null);
            gunner.drafter.Drafted = false;
            mortar.CompAmmo?.ResetAmmoCount(mortar.CompAmmo.SelectedAmmo);
            gunner.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.ManTurret, mortar));
            yield return 120;
            mortar.OrderAttack(new LocalTargetInfo(On(100, 206, 1)));
            int t0 = Find.TickManager.TicksGame;
            int rr = 0, replaced = 0, reorders = 0;

            // keeps the fight going: fallen targets replaced, idle shooters and grenadiers given a living target
            void Keep()
            {
                for (int i = 0; i < targets.Count; i++)
                {
                    Pawn p = targets[i].pawn;
                    if (p == null || p.Destroyed || p.Dead || p.Downed)
                    {
                        Gone(p);
                        targets[i] = (Target(targets[i].home), targets[i].home);
                        replaced++;
                    }
                }
                foreach ((Pawn s, int first, int count) in shooters)
                {
                    if (s.Dead || s.Downed || !s.Spawned)
                    {
                        continue;
                    }
                    if (Idle(s))
                    {
                        Order(s, targets[first + rr++ % count].pawn);
                        reorders++;
                    }
                }
                foreach (Pawn g in grenadiers)
                {
                    if (g.Dead || g.Downed || !g.Spawned)
                    {
                        continue;
                    }
                    if (g.equipment.Primary == null || g.equipment.Primary.Destroyed)
                    {
                        g.equipment.DestroyAllEquipment();
                        ThingDef wd = DefDatabase<ThingDef>.GetNamed("Weapon_GrenadeFrag");
                        g.equipment.AddEquipment((ThingWithComps)ThingMaker.MakeThing(wd));
                    }
                    if (Idle(g))
                    {
                        Order(g, targets[rr++ % 8].home + IntVec3.North);
                        reorders++;
                    }
                }
                if (mortar.CompAmmo != null && !mortar.CompAmmo.CanBeFiredNow)
                {
                    mortar.CompAmmo.ResetAmmoCount(mortar.CompAmmo.SelectedAmmo);
                }
            }

            List<IDisposable> extra = new List<IDisposable>();
            PerfRegistrations(extra);   // (-cepackperfreg: the Api with registrations that claim nothing; TestApi)
            Quiet = true;
            Out("  shooter job after the order: " + shooters[0].shooter.CurJobDef + ", grenadier " + grenadiers[0].CurJobDef);
            // warm-up: the fight starts (JIT, first bursts)
            for (int i = 0; i < 10; i++)
            {
                Keep();
                yield return 30;
            }
            // ticks alone: 3000 ticks, 30 per harness step (the tick loop in Update measures only DoSingleTick)
            GC.Collect();
            int gc0 = GC.CollectionCount(0), gc1 = GC.CollectionCount(1), gc2 = GC.CollectionCount(2);
            PerfTicks = PerfTickTicks = PerfAlloc = 0;
            PerfOn = true;
            for (int i = 0; i < 100; i++)
            {
                Keep();
                yield return 30;
            }
            PerfOn = false;
            double msTick = PerfTickTicks * 1000.0 / Stopwatch.Frequency / Math.Max(1, PerfTicks);
            double allocTick = PerfAlloc / (double)Math.Max(1, PerfTicks);
            int dg0 = GC.CollectionCount(0) - gc0, dg1 = GC.CollectionCount(1) - gc1, dg2 = GC.CollectionCount(2) - gc2;
            // frames: 600 frames, one tick each (Wait is rendered frames without ticks: here one tick per step instead)
            Find.UIRoot.screenshotMode.Active = true;
            Find.CameraDriver.SetRootPosAndSize(new Vector3(75f, 0f, 190f), 30f);
            long f0 = Stopwatch.GetTimestamp();
            int frames = 0;
            PerfTicks = PerfTickTicks = PerfAlloc = 0;
            PerfOn = true;
            for (int i = 0; i < 600; i++)
            {
                if (i % 30 == 0)
                {
                    Keep();
                }
                frames++;
                yield return 1;
            }
            PerfOn = false;
            double msFrame = (Stopwatch.GetTimestamp() - f0) * 1000.0 / Stopwatch.Frequency / frames;
            double msTickF = PerfTickTicks * 1000.0 / Stopwatch.Frequency / Math.Max(1, PerfTicks);
            Find.UIRoot.screenshotMode.Active = false;
            Quiet = false;
            List<Shot> hits = Impacts.Where(s => s.tick >= t0).ToList();
            string line = string.Join("|", hits.Select(s => (s.tick - t0) + ":" + s.def + ":" + s.frame + ":" + s.ground + ":" + s.height.ToString("0.000") + ":" + (s.hit == "-" ? "-" : s.hit.Substring(0, s.hit.IndexOf('#')))));
            ulong h = 1469598103934665603UL;
            foreach (char ch in line)
            {
                h = (h ^ ch) * 1099511628211UL;
            }
            int across = hits.Count(s => s.frame == 1);
            Out("PERF ms/tick " + msTick.ToString("0.000") + " alloc/tick " + allocTick.ToString("0") + " B, gc0 " + dg0 + " gc1 " + dg1 + " gc2 " + dg2
                + " | frame " + msFrame.ToString("0.00") + " ms (tick in frame " + msTickF.ToString("0.000") + " ms)");
            Out("PERF fight: launches " + Launches.Count(s => s.tick >= t0) + ", impacts " + hits.Count + " (on floor 1 " + across + ", overhead " + hits.Count(s => s.overhead)
                + "), targets replaced " + replaced + ", re-orders " + reorders + ", pack changed " + PackZ.Changed + ", frame moves " + FrameMoves());
            Out("HASH perf=" + h.ToString("x16"));
            Check("perf fight ran (shots both ways, explosions)", hits.Count > 100 && across > 0 && hits.Any(s => s.overhead), hits.Count + " impacts, " + across + " on floor 1");
            foreach (IDisposable d in extra)
            {
                d.Dispose();
            }
            mortar.ResetForcedTarget();
            Gone(shooters.Select(s => (Thing)s.shooter).Concat(grenadiers).Concat(targets.Select(t => (Thing)t.pawn)).Concat(fixture).Concat(new Thing[] { gunner, mortar }).ToArray());
        }
    }
}
