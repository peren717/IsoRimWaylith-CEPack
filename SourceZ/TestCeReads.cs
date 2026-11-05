using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using CombatExtended;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace IsoRimCEPackZ
{
    // Harness, mode cereads: a snapshot of where CE reads a position, a cell distance or a field of its shift report, checked
    // against Docs/CeReads.tsv. The frame transpiler (Aim) only reaches the methods it lists, so a CE update that adds such
    // a read somewhere else goes wrong silently, between floors only. This fails on that instead: a (method, read) pair not in
    // the file, or read more often than the file says. Pure reflection over CE's IL: no game state, no map used.
    //
    // The file is the allow-list: one row per method and read, its count, and how the pack deals with it. Keep every row
    // reviewable: after a CE update run the mode, read the new rows it writes to renders/<tag>/cereads_new.tsv, decide each
    // one (route it through Aim or say why it is harmless) and add it to the file. Lines starting with # are comments.
    // Fewer reads than the file are reported, not failed. -cepackreadswrite writes the whole snapshot to renders/<tag>/
    // cereads_all.tsv, with the old notes kept.
    public static partial class TestZ
    {
        private static IEnumerator<int> CeReads()
        {
            Out("--- CE reads snapshot");
            string file = Path.Combine(ModLister.GetActiveModWithIdentifier("Peren.IsoRimWaylith.CEPackWT3", true)?.RootDir.FullName ?? ModLister.GetActiveModWithIdentifier("Peren.IsoRimWaylith.CEPack", true)?.RootDir.FullName ?? "", "Docs", "CeReads.tsv");
            Dictionary<string, (int count, string note)> known = new Dictionary<string, (int, string)>();
            if (File.Exists(file))
            {
                foreach (string line in File.ReadAllLines(file))
                {
                    string[] f = line.Split('\t');
                    if (line.StartsWith("#") || f.Length < 4)
                    {
                        continue;
                    }
                    known[f[0] + "\t" + f[1]] = (int.Parse(f[2]), f[3]);
                }
            }
            SortedDictionary<string, int> now = ScanCeReads(out int methods);
            List<string> added = new List<string>(), more = new List<string>(), less = new List<string>();
            StringBuilder all = new StringBuilder();
            foreach (KeyValuePair<string, int> kv in now)
            {
                if (!known.TryGetValue(kv.Key, out (int count, string note) k))
                {
                    added.Add(kv.Key + "\t" + kv.Value + "\tNEW");
                }
                else if (kv.Value > k.count)
                {
                    more.Add(kv.Key + "\t" + kv.Value + " (file: " + k.count + ")\t" + k.note);
                }
                else if (kv.Value < k.count)
                {
                    less.Add(kv.Key + "\t" + kv.Value + " (file: " + k.count + ")");
                }
                all.AppendLine(kv.Key + "\t" + kv.Value + "\t" + (known.TryGetValue(kv.Key, out k) ? k.note : "NEW"));
            }
            less.AddRange(known.Keys.Where(k => !now.ContainsKey(k)).Select(k => k + "\tgone"));
            File.WriteAllLines(Path.Combine(outDir, "cereads_new.tsv"), added.Concat(more));
            if (GenCommandLine.CommandLineArgPassed("cepackreadswrite"))
            {
                File.WriteAllText(Path.Combine(outDir, "cereads_all.tsv"), all.ToString());
            }
            foreach (string s in added.Concat(more).Take(40))
            {
                Out("  unlisted: " + s);
            }
            foreach (string s in less.Take(40))
            {
                Out("  fewer: " + s);
            }
            Check("CE reads: no read of a position, distance or shift report field outside the allow-list", known.Count > 0 && added.Count == 0 && more.Count == 0,
                methods + " methods scanned, " + now.Count + " rows, file " + known.Count + " rows (" + file + "), new " + added.Count + ", more " + more.Count + ", fewer " + less.Count);
            yield break;
        }

        // Method and read -> count, over the CE types a shot's aim goes through.
        private static SortedDictionary<string, int> ScanCeReads(out int methods)
        {
            HashSet<MemberInfo> reads = new HashSet<MemberInfo>
            {
                AccessTools.PropertyGetter(typeof(Thing), nameof(Thing.Position)), AccessTools.PropertyGetter(typeof(Thing), nameof(Thing.DrawPos)), AccessTools.Field(typeof(Thing), "positionInt"),
                AccessTools.Method(typeof(GenThing), nameof(GenThing.TrueCenter), new[] { typeof(Thing) }), AccessTools.PropertyGetter(typeof(LocalTargetInfo), nameof(LocalTargetInfo.Cell)), AccessTools.PropertyGetter(typeof(LocalTargetInfo), nameof(LocalTargetInfo.CenterVector3)),
                AccessTools.Method(typeof(IntVec3), nameof(IntVec3.ToVector3Shifted)), AccessTools.Method(typeof(IntVec3), nameof(IntVec3.ToVector3)), AccessTools.Method(typeof(IntVec3), "op_Subtraction", new[] { typeof(IntVec3), typeof(IntVec3) }),
                AccessTools.Method(typeof(IntVec3Utility), nameof(IntVec3Utility.DistanceTo), new[] { typeof(IntVec3), typeof(IntVec3) }), AccessTools.Field(typeof(Pawn_PathFollower), nameof(Pawn_PathFollower.nextCell)), AccessTools.PropertyGetter(typeof(Pawn_PathFollower), nameof(Pawn_PathFollower.Destination)),
                AccessTools.Method(typeof(IntVec3Utility), nameof(IntVec3Utility.ToIntVec3), new[] { typeof(Vector3) }), AccessTools.PropertyGetter(typeof(IntVec3), nameof(IntVec3.LengthHorizontal)),
            };
            reads.Remove(null);
            foreach (FieldInfo f in typeof(ShiftVecReport).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                reads.Add(f);
            }
            bool Wanted(Type t) => typeof(Verb).IsAssignableFrom(t) || t == typeof(ShiftVecReport) || typeof(ProjectileCE).IsAssignableFrom(t) || t.Name.StartsWith("JobGiver") || t.Name == "CE_Utility" || t.Name == "LightingTracker"
                || t.Name.Contains("TrajectoryWorker") || t.Name.Contains("CompSuppressable") || t.Name == "Building_TurretGunCE";
            SortedDictionary<string, int> rows = new SortedDictionary<string, int>(StringComparer.Ordinal);
            methods = 0;
            foreach (Type t in typeof(ProjectileCE).Assembly.GetTypes())
            {
                Type top = t;
                while (top.DeclaringType != null)
                {
                    top = top.DeclaringType;
                }
                if (!Wanted(top))
                {
                    continue;
                }
                foreach (MethodBase m in t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly).Cast<MethodBase>().Concat(t.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)))
                {
                    if (m.GetMethodBody() == null)
                    {
                        continue;
                    }
                    List<CodeInstruction> ins;
                    try
                    {
                        ins = PatchProcessor.GetOriginalInstructions(m);
                    }
                    catch (Exception)
                    {
                        continue;
                    }
                    methods++;
                    foreach (CodeInstruction ci in ins)
                    {
                        // (a report's own constructors and field writes are no reads)
                        if (!(ci.operand is MemberInfo mi) || !reads.Contains(mi) || ci.opcode == System.Reflection.Emit.OpCodes.Stfld)
                        {
                            continue;
                        }
                        string key = NameOf(t, m) + "\t" + mi.DeclaringType.Name + "." + mi.Name;
                        rows[key] = rows.TryGetValue(key, out int c) ? c + 1 : 1;
                    }
                }
            }
            return rows;
        }

        // Stable over rebuilds: a lambda's and a closure's numbers are dropped from the method's name.
        private static string NameOf(Type t, MethodBase m)
        {
            string type = System.Text.RegularExpressions.Regex.Replace(t.FullName, @"<>c__DisplayClass\d+_\d+", "<>c__DisplayClass");
            type = System.Text.RegularExpressions.Regex.Replace(type, @">d__\d+", ">d__");
            string name = System.Text.RegularExpressions.Regex.Replace(m.Name, @"b__\d+(_\d+)?$", "b__");
            name = System.Text.RegularExpressions.Regex.Replace(name, @"g__(\w+)\|\d+_\d+$", "g__$1");
            return type + "::" + name + "/" + m.GetParameters().Length;
        }
    }
}
