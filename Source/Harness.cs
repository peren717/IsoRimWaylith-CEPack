using System;
using System.Linq;
using CombatExtended;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRimCEPack
{
    // Test harness only: dormant unless the game is started with -cepackprobe. IsoRim's own harness probe (IsoRimProbe.txt)
    // drives the scene; a probe token "cepack:<cmd>:<args>" is unknown to it, so it logs "[IsoRim probe] unknown key
    // cepack:..." and goes on with the line. That warning is caught here (Verse.Log.Warning postfix) and the command run at
    // once, in order with the probe's other commands (ticks, bursts):
    //   cepack:off / cepack:on       the pack's drawing off (CE draws) / on, for before-and-after frames in one run
    //   cepack:ammo:x:z              the CE turret in the cell gets a full magazine of its selected ammo
    //   cepack:order:x:z:tx:tz       the CE turret in the cell is ordered to attack the cell (the player's gizmo: OrderAttack)
    //                                (man, aim, order: a z written U<z> is that row on floor 1)
    //   cepack:man:x:z:px:pz         the colonist in (px,pz) takes a ManTurret job on the CE turret in (x,z)
    //   cepack:aim:x:z:deg           the CE turret's gun top turned to deg (0 north, 90 east)
    //   cepack:muzzlecheck:N:label   N real game ticks; after each, every CE gun shot in flight (pawn or turret weapon) must
    //                                find its muzzle owner from its launch point as it stands then: CHECK PASS / FAIL
    //   cepack:bandfix:off|on        the owner lookup without / with the storey mapping (to show what the check catches)
    //   cepack:stats                 counters, the floor layout and every CE projectile in flight with its drawn point
    //   cepack:ammobind              how every CE ammo def got its model -> <save data>/CEPackAmmoBind.tsv + counts in the log
    // Every flight's start (muzzle against CE's launch point) is logged while the harness is on.
    [StaticConstructorOnStartup]
    public static class Harness
    {
        public static bool On;

        static Harness()
        {
            if (!GenCommandLine.CommandLineArgPassed("cepackprobe"))
            {
                return;
            }
            On = true;
            Harmony h = new Harmony("peren.isorimwaylith.cepack.harness");
            h.Patch(AccessTools.Method(typeof(Log), nameof(Log.Warning), new[] { typeof(string) }),
                postfix: new HarmonyMethod(typeof(Harness), nameof(Warning_Postfix)));
            h.Patch(AccessTools.Method(typeof(FleckManager), nameof(FleckManager.CreateFleck), new[] { typeof(FleckCreationData) }),
                prefix: new HarmonyMethod(typeof(Harness), nameof(Fleck_Prefix)) { priority = Priority.First });
            h.Patch(AccessTools.Method(typeof(Mote), nameof(Mote.SpawnSetup)), postfix: new HarmonyMethod(typeof(Harness), nameof(Mote_Postfix)));
            h.Patch(AccessTools.Method(typeof(Game), nameof(Game.UpdatePlay)), postfix: new HarmonyMethod(typeof(Harness), nameof(Watch_Postfix)));
            Log.Message(Pack.Tag + "harness on");
        }

        // cepack:fxlog:on|off: every fleck and mote made while on is logged (what CE shows around a shot).
        private static bool fxlog;

        public static void Fleck_Prefix(FleckCreationData fleckData)
        {
            if (fxlog && fleckData.def != null)
            {
                Log.Message(Pack.Tag + "fx fleck " + fleckData.def.defName + " at " + fleckData.spawnPosition.ToString("F2") + " scale " + fleckData.scale.ToString("0.00") + " tick " + Find.TickManager.TicksGame);
            }
        }

        public static void Mote_Postfix(Mote __instance)
        {
            if (fxlog)
            {
                Log.Message(Pack.Tag + "fx mote " + __instance.def.defName + " at " + __instance.exactPosition.ToString("F2") + " tick " + Find.TickManager.TicksGame);
            }
        }

        // (cefixdisplay probe) frames left to log the live shot flashes after a cookoff
        private static int watch;

        public static void Watch_Postfix()
        {
            if (watch <= 0 || Find.CurrentMap == null)
            {
                return;
            }
            watch--;
            if (watch % 20 != 0)
            {
                return;
            }
            int n = 0;
            string ages = "";
            foreach (FleckSystem fs in Find.CurrentMap.flecks.Systems)
            {
                System.Collections.IList live = Traverse.Create(fs).Field("dataGametime").GetValue() as System.Collections.IList;
                for (int i = 0; live != null && i < live.Count; i++)
                {
                    if (live[i] is FleckStatic fl && fl.def == FleckDefOf.ShotFlash)
                    {
                        n++;
                        ages += fl.ageSecs.ToString("0.000") + "@" + fl.DrawPos.ToString("F1") + " ";
                    }
                }
            }
            Log.Message(Pack.Tag + "probe watch frame " + Time.frameCount + " tick " + Find.TickManager.TicksGame + " speed " + Find.TickManager.CurTimeSpeed + ": live shot flashes " + n + " " + ages);
        }

        private const string Key = "[IsoRim probe] unknown key cepack:";
        private static bool running;

        public static void Warning_Postfix(string text)
        {
            if (running || text == null || !text.StartsWith(Key))
            {
                return;
            }
            running = true;
            try
            {
                Run(text.Substring(Key.Length).Split(':'));
            }
            catch (Exception e)
            {
                Log.Message(Pack.Tag + "probe failed: " + e);
            }
            finally
            {
                running = false;
            }
        }

        private static int I(string s) => int.Parse(s);

        // cepack:ammobind: every CE AmmoDef (all mods) with how IsoRim bound it (IsoRim's HowBound, read by reflection: test
        // only), its entry, the pack rule's answer, stack limit, texture and ammoClass, and the texture it is shipped with if
        // another mod re-textured it -> <save data>/CEPackAmmoBind.tsv, and the counts to the log.
        private static void AmmoBind()
        {
            Type lib = AccessTools.TypeByName("IsoRim.ModelLibrary");
            System.Reflection.MethodInfo howM = lib != null ? AccessTools.Method(lib, "HowBound", new[] { typeof(ThingDef) }) : null;
            System.Reflection.MethodInfo getM = lib != null ? AccessTools.Method(lib, "TryGet") : null;
            System.Text.StringBuilder sb = new System.Text.StringBuilder("defName\tpack\tstackLimit\ttexPath\tammoClass\tmenuHidden\thow\tentry\thasModel\truleAnswer\tshippedTexPath\n");
            System.Collections.Generic.Dictionary<string, int> byHow = new System.Collections.Generic.Dictionary<string, int>();
            int n = 0, bound = 0;
            foreach (ThingDef d in DefDatabase<ThingDef>.AllDefs.OrderBy(x => x.defName))
            {
                AmmoDef ad = d as AmmoDef;
                if (ad == null)
                {
                    continue;
                }
                n++;
                string how = howM != null ? howM.Invoke(null, new object[] { d }) as string : "?";
                string entry = "";
                if (getM != null)
                {
                    object[] args = { d, null };
                    if ((bool)getM.Invoke(null, args) && args[1] != null)
                    {
                        object md = AccessTools.Field(args[1].GetType(), "def")?.GetValue(args[1]);
                        entry = (md as Def)?.defName ?? "";
                    }
                }
                bool has = IsoRim.Api.HasModel(d);
                if (has)
                {
                    bound++;
                }
                string k = how == null ? "(none)" : how.Contains(":") ? how.Substring(0, how.IndexOf(':')) : how;
                if (k == "graphicPath" && entry.StartsWith("R3D_CE_"))
                {
                    k = "graphicPath(pack)";
                }
                byHow[k] = byHow.TryGetValue(k, out int c) ? c + 1 : 1;
                sb.Append(d.defName).Append('\t').Append(d.modContentPack?.PackageId ?? "").Append('\t').Append(d.stackLimit).Append('\t')
                    .Append(d.graphicData?.texPath ?? "").Append('\t').Append(ad.ammoClass?.defName ?? "").Append('\t').Append(ad.menuHidden)
                    .Append('\t').Append(how ?? "").Append('\t').Append(entry).Append('\t').Append(has ? 1 : 0).Append('\t')
                    .Append(AmmoRule.Answer(d) ?? "").Append('\t').Append(AmmoRule.ShippedTexPath(d) ?? "").Append('\n');
            }
            System.IO.File.WriteAllText(System.IO.Path.Combine(GenFilePaths.SaveDataFolderPath, "CEPackAmmoBind.tsv"), sb.ToString());
            Log.Message(Pack.Tag + "probe ammobind: " + n + " ammo defs, " + bound + " with a model; by how: "
                + string.Join(", ", byHow.OrderBy(x => x.Key).Select(x => x.Key + " " + x.Value))
                + "; rule map: generic " + AmmoRule.Generic + ", calibre " + AmmoRule.Calibre + " (by own folder " + AmmoRule.ByFolder
                + "), fallback " + AmmoRule.Fallback + ", unmatched left to graphicPath " + AmmoRule.Unmatched
                + "; re-textured " + AmmoRule.Retextured + " (given their shipped art's model by re-point " + AmmoRule.Repointed + ")"
                + "; turret models refitted " + TurretModels.Refitted
                + "; CE generic ammo " + (Controller.settings != null && Controller.settings.GenericAmmo));
        }

        private static readonly AccessTools.FieldRef<ProjectileCE, Vector2> originRef = AccessTools.FieldRefAccess<ProjectileCE, Vector2>("origin");

        private static Vector2 Frames_Origin(ProjectileCE p) => originRef(p);

        // A row: z, or U<z> = that row on floor 1 (the stacked rows, as IsoRim's probe writes them).
        private static int Z(Map map, string s)
        {
            if (!s.StartsWith("U"))
            {
                return int.Parse(s);
            }
            float h;
            int shift = 0;
            if (IsoRim.Api.Floors != null)
            {
                IsoRim.Api.Floors.Floor(map, 1, out h, out shift);
            }
            return int.Parse(s.Substring(1)) + shift;
        }

        // cepack:gunclass (cefixdisplay probe): every equippable weapon and turret (all mods) with how IsoRim binds a model to it
        // and which look each IsoRim path gives it -> <save data>/CEPackGunClass.tsv.
        private static void GunClass()
        {
            Type lib = AccessTools.TypeByName("IsoRim.ModelLibrary");
            System.Reflection.MethodInfo howM = AccessTools.Method(lib, "HowBound", new[] { typeof(ThingDef) });
            System.Reflection.MethodInfo getM = AccessTools.Method(lib, "TryGet");
            System.Reflection.MethodInfo usesM = AccessTools.Method(AccessTools.TypeByName("IsoRim.WeaponModels"), "Uses", new[] { typeof(ThingDef) });
            System.Reflection.MethodInfo isGear = AccessTools.Method(AccessTools.TypeByName("IsoRim.GroundGear"), "IsGear");
            System.Reflection.MethodInfo formsFor = AccessTools.Method(AccessTools.TypeByName("IsoRim.Forms"), "For");
            System.Reflection.MethodInfo solidFor = AccessTools.Method(AccessTools.TypeByName("IsoRim.SpriteSolids"), "For");
            System.Text.StringBuilder sb = new System.Text.StringBuilder("defName\tpack\tkind\ttexPath\tgraphicClass\tcategories\tisGear\thow\tentry\tentryFile\thasModel\tuses\tgroundForm\tsolidOk\tturretTop\n");
            foreach (ThingDef d in DefDatabase<ThingDef>.AllDefs.OrderBy(x => x.defName))
            {
                bool weapon = d.equipmentType == EquipmentType.Primary && d.category == ThingCategory.Item;
                bool turret = d.building?.turretGunDef != null;
                if (!weapon && !turret)
                {
                    continue;
                }
                string how = howM.Invoke(null, new object[] { d }) as string;
                string entry = "", file = "", top = "";
                bool has = false;
                object[] args = { d, null };
                if ((bool)getM.Invoke(null, args) && args[1] != null)
                {
                    object md = AccessTools.Field(args[1].GetType(), "def")?.GetValue(args[1]);
                    entry = (md as Def)?.defName ?? "";
                    file = md != null ? (AccessTools.Field(md.GetType(), "model")?.GetValue(md) as string ?? "") : "";
                    has = (bool)AccessTools.Property(args[1].GetType(), "HasModel").GetValue(args[1]);
                    top = AccessTools.Field(args[1].GetType(), "top")?.GetValue(args[1]) != null ? "top" : "";
                }
                string form;
                try
                {
                    object f = formsFor.Invoke(null, new object[] { d });
                    form = AccessTools.Field(f.GetType(), "form").GetValue(f).ToString();
                }
                catch (Exception e)
                {
                    form = "err " + (e.InnerException ?? e).GetType().Name;
                }
                string solid = "";
                if (weapon)
                {
                    try
                    {
                        Material mat = d.graphic?.MatSingle;
                        solid = mat == null ? "nomat" : solidFor.Invoke(null, new object[] { mat }) != null ? "1" : "0";
                    }
                    catch (Exception e)
                    {
                        solid = "err " + (e.InnerException ?? e).GetType().Name;
                    }
                }
                sb.Append(d.defName).Append('\t').Append(d.modContentPack?.PackageId ?? "").Append('\t').Append(turret ? "turret:" + d.building.turretGunDef.defName : "weapon")
                    .Append('\t').Append(d.graphicData?.texPath ?? "").Append('\t').Append(d.graphicData?.graphicClass?.Name ?? "")
                    .Append('\t').Append(d.thingCategories != null ? string.Join(",", d.thingCategories.Select(c => c.defName)) : "")
                    .Append('\t').Append(weapon ? ((bool)isGear.Invoke(null, new object[] { d }) ? "1" : "0") : "")
                    .Append('\t').Append(how ?? "").Append('\t').Append(entry).Append('\t').Append(file).Append('\t').Append(has ? 1 : 0)
                    .Append('\t').Append(weapon ? ((bool)usesM.Invoke(null, new object[] { d }) ? "1" : "0") : "")
                    .Append('\t').Append(form).Append('\t').Append(solid).Append('\t').Append(top).Append('\n');
            }
            System.IO.File.WriteAllText(System.IO.Path.Combine(GenFilePaths.SaveDataFolderPath, "CEPackGunClass.tsv"), sb.ToString());
            Log.Message(Pack.Tag + "probe gunclass written");
        }

        private static void Run(string[] a)
        {
            Map map = Find.CurrentMap;
            switch (a[0])
            {
                case "sparks":
                    // sparks:on|off (cefixdisplay probe): the setting "Cook-off sparks" for this session (not saved)
                    CEPackMod.Settings.cookOffSparks = a[1] == "on";
                    Log.Message(Pack.Tag + "probe cook-off sparks " + a[1] + " (extras so far " + Puffs.Extras + ", cook-off flashes " + Puffs.CookOffs + ")");
                    break;
                case "gunclass":
                    GunClass();
                    break;
                case "off":
                case "on":
                    ProjectileCE_DrawAt.Enabled = a[0] == "on";
                    Log.Message(Pack.Tag + "probe drawing " + a[0]);
                    break;
                case "dispoff":
                case "dispon":
                {
                    // the display stage (held guns, beams, trail and flash flecks, grenade models) off / on
                    bool on = a[0] == "dispon";
                    HeldGuns.Enabled = Beams.Enabled = Puffs.Enabled = Grenades.Enabled = Holes.Enabled = on;
                    Log.Message(Pack.Tag + "probe display " + (on ? "on" : "off"));
                    break;
                }
                case "ammobind":
                    AmmoBind();
                    break;
                case "fxlog":
                    fxlog = a[1] == "on";
                    Log.Message(Pack.Tag + "probe fx log " + a[1]);
                    break;
                case "dispstats":
                    Log.Message(Pack.Tag + "probe display stats: held guns drawn " + HeldGuns.Drawn + " (left to CE " + HeldGuns.LeftToCe + "), beam strips " + Beams.Strips + " bolts " + Beams.Bolts
                        + " from muzzle " + Beams.FromMuzzle + " swept " + Beams.Swept + " hidden " + Beams.Hidden + ", last strip " + Beams.LastStart.ToString("F2") + " -> " + Beams.LastEnd.ToString("F2")
                        + ", trail flecks " + Puffs.Trails + " flashes " + Puffs.Flashes + " cook-offs " + Puffs.CookOffs + " live " + Puffs.Live + " draws " + Puffs.Drawn + ", grenade models " + Grenades.Drawn + ", bullet holes " + Holes.Made + " live " + Holes.Live + " draws " + Holes.Drawn);
                    break;
                case "tickbeam":
                {
                    // tickbeam:max: real game ticks until a CE beam is showing (at most max ticks)
                    int max = I(a[1]), done = 0;
                    Func<Thing> beam = () => map.listerThings.AllThings.FirstOrDefault(t => t is CombatExtended.Lasers.LaserBeamGraphicCE);
                    while (done < max && beam() == null)
                    {
                        Find.TickManager.DoSingleTick();
                        done++;
                    }
                    foreach (Pawn tp in map.mapPawns.AllPawnsSpawned)
                    {
                        tp.Drawer.tweener.ResetTweenedPosToRoot();
                    }
                    Thing b = beam();
                    Log.Message(Pack.Tag + "probe tickbeam: " + done + " ticks, " + (b != null ? b.def.defName + " at " + b.Position : "no beam"));
                    break;
                }
                case "ammo":
                {
                    Building_TurretGunCE t = new IntVec3(I(a[1]), 0, I(a[2])).GetFirstBuilding(map) as Building_TurretGunCE;
                    CompAmmoUser ammo = t?.CompAmmo;
                    if (ammo == null)
                    {
                        Log.Message(Pack.Tag + "probe ammo: no CE turret with ammo at " + a[1] + "," + a[2]);
                        break;
                    }
                    ammo.ResetAmmoCount(ammo.SelectedAmmo);
                    Log.Message(Pack.Tag + "probe ammo: " + t.def.defName + " " + ammo.CurrentAmmo?.defName + " x" + ammo.CurMagCount);
                    break;
                }
                case "order":
                {
                    Building_TurretGunCE t = new IntVec3(I(a[1]), 0, Z(map, a[2])).GetFirstBuilding(map) as Building_TurretGunCE;
                    if (t == null)
                    {
                        Log.Message(Pack.Tag + "probe order: no CE turret at " + a[1] + "," + a[2]);
                        break;
                    }
                    IntVec3 tc = new IntVec3(I(a[3]), 0, Z(map, a[4]));
                    t.OrderAttack(new LocalTargetInfo(tc));
                    Log.Message(Pack.Tag + "probe order: " + t.def.defName + " -> " + tc + ", forced " + t.ForcedTarget + ", manned " + (t.TryGetComp<CompMannable>()?.MannedNow));
                    break;
                }
                case "man":
                {
                    // man:x:z:px:pz: the colonist in cell (px,pz) takes a real ManTurret job on the CE turret in cell (x,z), as the
                    // player's "man" order does (walks to the interaction cell, then mans it while the game ticks); a colonist incapable of
                    // violence is replaced by the nearest free one who can. A z written
                    // U<z> is that row on floor 1 (IsoRim's probe notation).
                    Building_TurretGunCE t = new IntVec3(I(a[1]), 0, Z(map, a[2])).GetFirstBuilding(map) as Building_TurretGunCE;
                    Pawn p = new IntVec3(I(a[3]), 0, Z(map, a[4])).GetFirstPawn(map);
                    if (t != null && (p == null || p.WorkTagIsDisabled(WorkTags.Violent)))
                    {
                        // that colonist cannot man a gun (incapable of violence): the nearest free one who can
                        Pawn near = p;
                        p = map.mapPawns.FreeColonistsSpawned.Where(c => c != near && !c.Downed && !c.WorkTagIsDisabled(WorkTags.Violent)
                                && (c.CurJob == null || c.CurJob.def != JobDefOf.ManTurret))
                            .OrderBy(c => c.Position.DistanceToSquared(t.Position)).FirstOrDefault();
                    }
                    if (t == null || p == null)
                    {
                        Log.Message(Pack.Tag + "probe man: no CE turret at " + a[1] + "," + a[2] + " or nobody at " + a[3] + "," + a[4]);
                        break;
                    }
                    if (p.drafter != null)
                    {
                        p.drafter.Drafted = false;
                    }
                    bool ok = p.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.ManTurret, t), Verse.AI.JobTag.Misc);
                    Log.Message(Pack.Tag + "probe man: " + p.LabelShort + " -> " + t.def.defName + " " + t.Position + " ordered " + ok + " job " + (p.CurJob != null ? p.CurJob.def.defName : "none"));
                    break;
                }
                case "aim":
                {
                    // aim:x:z:deg: the CE turret's gun top turned to deg (the game's turret angle: 0 north, 90 east), as it rests
                    // after turning; for looks at a few aims without firing.
                    Building_TurretGunCE t = new IntVec3(I(a[1]), 0, Z(map, a[2])).GetFirstBuilding(map) as Building_TurretGunCE;
                    TurretTop top = t != null ? Traverse.Create(t).Field("top").GetValue<TurretTop>() : null;
                    if (top == null)
                    {
                        Log.Message(Pack.Tag + "probe aim: no CE turret (or its top) at " + a[1] + "," + a[2]);
                        break;
                    }
                    top.CurRotation = float.Parse(a[3], System.Globalization.CultureInfo.InvariantCulture);
                    Log.Message(Pack.Tag + "probe aim: " + t.def.defName + " " + t.Position + " rot " + t.Rotation + " gun " + top.CurRotation.ToString("0"));
                    break;
                }
                case "bandfix":
                    ProjectileCE_DrawAt.BandFix = a[1] != "off";
                    Log.Message(Pack.Tag + "probe band fix " + (ProjectileCE_DrawAt.BandFix ? "on" : "off"));
                    break;
                case "muzzlecheck":
                {
                    // Every tick, every gun shot in flight must find its muzzle owner (the drawing finds it the same way when
                    // it first draws the shot, which may be ticks after launch, after a move to another storey's rows).
                    int n = I(a[1]), checks = 0, missing = 0;
                    string label = a.Length > 2 ? a[2] : "";
                    var shots = new System.Collections.Generic.HashSet<int>();
                    var bad = new System.Collections.Generic.HashSet<int>();
                    var gone = new System.Collections.Generic.HashSet<int>();
                    var byLauncher = new System.Collections.Generic.Dictionary<string, int>();
                    for (int i = 0; i < n; i++)
                    {
                        Find.TickManager.DoSingleTick();
                        foreach (ProjectileCE p in map.listerThings.AllThings.OfType<ProjectileCE>().ToList())
                        {
                            if (!p.Spawned || !ProjectileCE_DrawAt.IsGunShot(p))
                            {
                                continue;
                            }
                            if (!p.launcher.Spawned || p.launcher.Map != map)
                            {
                                gone.Add(p.thingIDNumber);   // its shooter is gone (dead, or removed by the test): nothing to leave from
                                continue;
                            }
                            checks++;
                            shots.Add(p.thingIDNumber);
                            string who = (p.launcher is Building_Turret ? "turret " : "pawn ") + p.equipmentDef.defName;
                            byLauncher[who] = byLauncher.TryGetValue(who, out int c0) ? c0 + 1 : 1;
                            if (ProjectileCE_DrawAt.MuzzleOwnerOf(p) == null)
                            {
                                missing++;
                                if (bad.Add(p.thingIDNumber) && bad.Count <= 5)
                                {
                                    Vector2 o = Frames_Origin(p);
                                    Log.Message(Pack.Tag + "muzzlecheck " + label + ": no owner for " + p.def.defName + " by " + p.launcher.LabelShort + " at " + p.launcher.Position
                                        + ", launch point (" + o.x.ToString("F2") + ", " + o.y.ToString("F2") + "), height " + p.shotHeight.ToString("0.00") + ", tick " + p.FlightTicks);
                                }
                            }
                        }
                    }
                    foreach (Pawn tp in map.mapPawns.AllPawnsSpawned)
                    {
                        tp.Drawer.tweener.ResetTweenedPosToRoot();
                    }
                    bool pass = shots.Count > 0 && bad.Count == 0;
                    Log.Message(Pack.Tag + "CHECK " + (pass ? "PASS" : "FAIL") + " muzzle owners " + label + ": " + shots.Count + " gun shots seen over " + n + " ticks (" + checks
                        + " shot-ticks: " + string.Join(", ", byLauncher.Select(kv => kv.Key + " " + kv.Value)) + "), shots without owner " + bad.Count + " (" + missing + " shot-ticks); shooter gone (not checked) " + gone.Count + "; drawn so far "
                        + ProjectileCE_DrawAt.GunShots + " without owner " + ProjectileCE_DrawAt.GunShotsNoOwner);
                    break;
                }
                case "cookoff":
                {
                    // cookoff:x:z:damage:max[:seed] (cefixdisplay probe): the CE ammo stack in the cell takes a bullet's damage
                    // (seeded), then real game ticks one at a time until it cooks off once (its stack drops), at most max ticks.
                    IntVec3 c = new IntVec3(I(a[1]), 0, Z(map, a[2]));
                    AmmoThing ammo = c.GetThingList(map).OfType<AmmoThing>().FirstOrDefault();
                    if (ammo == null)
                    {
                        Log.Warning(Pack.Tag + "probe cookoff: no ammo at " + c);
                        break;
                    }
                    Rand.Seed = a.Length > 5 ? I(a[5]) : 4242;
                    watch = 400;
                    int before = ammo.stackCount, max = I(a[4]), done = 0;
                    ammo.TakeDamage(new DamageInfo(DamageDefOf.Bullet, float.Parse(a[3], System.Globalization.CultureInfo.InvariantCulture)));
                    int after = ammo.Spawned ? ammo.stackCount : 0;
                    while (done < max && ammo.Spawned && ammo.stackCount >= after)
                    {
                        Find.TickManager.DoSingleTick();
                        done++;
                    }
                    Log.Message(Pack.Tag + "probe cookoff " + ammo.def.defName + " at " + c + ": stack " + before + " -> " + after + " after the hit -> "
                        + (ammo.Spawned ? ammo.stackCount.ToString() : "gone") + " after " + done + " ticks; cooking off " + ammo.IsCookingOff
                        + ", HP " + ammo.HitPoints + "/" + ammo.MaxHitPoints + ", projectiles in flight " + map.listerThings.AllThings.OfType<ProjectileCE>().Count()
                        + ", realistic cook-off " + Controller.settings.RealisticCookOff + ", frame " + Time.frameCount + ", flash scale " + ((ammo.def as AmmoDef)?.cookOffFlashScale ?? 0f));
                    break;
                }
                case "tickfly":
                {
                    // tickfly:max:flightTicks[:defPart]: real game ticks, one at a time, until a CE projectile (defName containing
                    // defPart) has flown that many ticks (at most max ticks).
                    int max = I(a[1]), need = I(a[2]), done = 0;
                    string part = a.Length > 3 ? a[3] : "";
                    Func<ProjectileCE> found = () => map.listerThings.AllThings.OfType<ProjectileCE>()
                        .FirstOrDefault(p => p.FlightTicks >= need && p.def.defName.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0);
                    while (done < max && found() == null)
                    {
                        Find.TickManager.DoSingleTick();
                        done++;
                    }
                    foreach (Pawn tp in map.mapPawns.AllPawnsSpawned)
                    {
                        tp.Drawer.tweener.ResetTweenedPosToRoot();   // paused game: the drawn position follows the ticks
                    }
                    ProjectileCE hit = found();
                    Log.Message(Pack.Tag + "probe tickfly " + part + ": " + done + " ticks, " + (hit != null ? hit.def.defName + " " + ProjectileCE_DrawAt.Describe(hit) : "none flying"));
                    break;
                }
                case "tickdown":
                {
                    // tickdown:max:height[:defPart]: real game ticks until a CE projectile is coming down at or below height (CE units).
                    int max = I(a[1]), done = 0;
                    float below = float.Parse(a[2], System.Globalization.CultureInfo.InvariantCulture);
                    string part = a.Length > 3 ? a[3] : "";
                    Func<ProjectileCE> found = () => map.listerThings.AllThings.OfType<ProjectileCE>()
                        .FirstOrDefault(p => p.FlightTicks > 2 && p.Height <= below && p.Height < ProjectileCE_DrawAt.LastHeight(p) && p.def.defName.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0);
                    while (done < max && found() == null)
                    {
                        Find.TickManager.DoSingleTick();
                        done++;
                    }
                    foreach (Pawn tp in map.mapPawns.AllPawnsSpawned)
                    {
                        tp.Drawer.tweener.ResetTweenedPosToRoot();
                    }
                    ProjectileCE hit = found();
                    Log.Message(Pack.Tag + "probe tickdown " + part + ": " + done + " ticks, " + (hit != null ? hit.def.defName + " tick " + hit.FlightTicks + " " + ProjectileCE_DrawAt.Describe(hit) : "none coming down"));
                    break;
                }
                case "stats":
                {
                    string floors = "none";
                    if (IsoRim.Api.Floors != null && map != null)
                    {
                        float h;
                        int shift;
                        bool f1 = IsoRim.Api.Floors.Floor(map, 1, out h, out shift);
                        floors = "ground rows " + IsoRim.Api.Floors.GroundRows(map) + ", floor 1 " + f1 + " height " + h.ToString("0.00") + " shift " + shift;
                    }
                    Log.Message(Pack.Tag + "probe stats: drawn " + ProjectileCE_DrawAt.Drawn + " (lobbed " + ProjectileCE_DrawAt.DrawnLobbed + ", hidden floor " + ProjectileCE_DrawAt.Hidden + ", lowered " + ProjectileCE_DrawAt.Lowered + ", swept " + ProjectileCE_DrawAt.Swept + "), errors "
                        + ProjectileCE_DrawAt.Errors + ", cells per CE unit " + ProjectileCE_DrawAt.CellsPerUnit.ToString("0.000") + ", floors: " + floors);
                    foreach (Building_TurretGunCE t in map.listerThings.AllThings.OfType<Building_TurretGunCE>().ToList())
                    {
                        CompAmmoUser am = t.CompAmmo;
                        Log.Message(Pack.Tag + "probe turret " + t.def.defName + " " + t.Position + " active " + t.Active + " manned " + (t.mannableComp != null ? t.mannableComp.MannedNow.ToString() : "-")
                            + " forced " + t.ForcedTarget + " current " + t.currentTargetInt + " warmup " + t.burstWarmupTicksLeft + " cooldown " + t.burstCooldownTicksLeft
                            + " reloading " + t.isReloading + " verb " + (t.AttackVerb != null ? t.AttackVerb.state.ToString() : "-")
                            + " ammo " + (am != null ? "use " + am.UseAmmo + " mag " + am.CurMagCount + " canFire " + am.CanBeFiredNow : "-"));
                    }
                    foreach (Pawn cp in map.mapPawns.FreeColonistsSpawned)
                    {
                        Log.Message(Pack.Tag + "probe colonist " + cp.LabelShort + " " + cp.Position + " job " + (cp.CurJob != null ? cp.CurJob.def.defName + " -> " + cp.CurJob.targetA : "none")
                            + " weapon " + (cp.equipment?.Primary != null ? cp.equipment.Primary.def.defName : "-"));
                    }
                    foreach (ProjectileCE p in map.listerThings.AllThings.OfType<ProjectileCE>().ToList())
                    {
                        Log.Message(Pack.Tag + "probe flying " + p.def.defName + " (" + p.GetType().Name + ") tick " + p.FlightTicks + " " + ProjectileCE_DrawAt.Describe(p));
                    }
                    break;
                }
                default:
                    Log.Message(Pack.Tag + "probe: unknown " + string.Join(":", a));
                    break;
            }
        }
    }
}
