using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Xml;
using CombatExtended;
using HarmonyLib;
using Verse;

namespace IsoRimCEPack
{
    // CE ammo models (Defs/CE_Ammo_Generic.xml, Defs/CE_Ammo_NonGeneric.xml, written by tools/make_ce_ammo_defs.js).
    //
    // Generic looks: CE's generic-ammo option maps every ammo def of a calibre set (an AmmoSetDef with similarTo) to the def
    // of the same ammoClass in the generic set (AmmoGeneralizer.GeneralizeAmmo). The pack gives each calibre def that
    // counterpart's look, always (whether the option is on or off), through IsoRim's model rules (Api 42): the rule answers
    // "R3D_CE_<look>" for an ammo def, the look keyed by the counterpart's CE texture folder (AmmoLooks.Generic).
    //  - A generic def itself: its own texture's look.
    //  - FuelCell/Large is one CE art for two looks: by ammoClass (GrenadeIncendiary: the incendiary look; else demolition).
    //  - A def in several calibre sets with different looks (.38 Special in Pistol and PistolMagnum sets ...): the look of
    //    the same family as its own texture folder (Pistol/* gets the pistol look, Revolver/* the revolver look).
    //  - A link whose ammoClass the generic set lacks (CE leaves it as it is): no answer when its own texture has a look of
    //    its own (graphicPath binds it then: 105mm HEAT, the APCR rounds); else (12.7x55mm HP) the generic set's FMJ look.
    // The mapping is read from the original set links: on the rule's first call or, if CE's generalizer runs first (generic
    // ammo option on), just before it rewrites the links (prefix below). Older IsoRim (no Api 42): no rule; the graphicPath
    // patch still gives things drawn with a generic CE texture that texture's look.
    // Every texture folder above is the def's SHIPPED art when the pack has a model for it (ShippedArt, AmmoRule.ArtOf): the
    // texPath its own mod's Defs give it, not one another mod patched in. The models were made after CE's art, so a def
    // re-pointed from it to other art (e.g. "The Generic Ammo Experience for Combat Extended") keeps its model in 3D while the
    // 2D view shows the new art. A re-pointed def the set links do not cover gets the model graphicPath gave its shipped art
    // (AmmoLooks.ByArt), as before the re-point. A def shipped with art the pack has no model for keys on its current texPath,
    // as before. Without a patch that re-points CE's ammo art shipped and current are the same: every def binds as before.
    public class CEPackMod : Mod
    {
        public static CEPackSettings Settings;

        public CEPackMod(ModContentPack content) : base(content)
        {
            Settings = GetSettings<CEPackSettings>();
            int v;
            try
            {
                v = IsoRim.Api.Version;
            }
            catch (Exception)
            {
                v = 0;
            }
            if (v < 42)
            {
                return;
            }
            try
            {
                AmmoRule.Register();
            }
            catch (Exception e)
            {
                Log.Error(Pack.Tag + "Ammo model rule not added (CE ammo keeps its texture-path models): " + e);
                return;
            }
            try
            {
                ShippedArt.Register();
            }
            catch (Exception e)
            {
                Log.Warning(Pack.Tag + "Ammo defs' own textures not read (a def another mod re-textured loses its model): " + e);
            }
        }

        public override string SettingsCategory() => Content.Name;

        public override void DoSettingsWindowContents(UnityEngine.Rect inRect)
        {
            Listing_Standard list = new Listing_Standard();
            list.Begin(inRect);
            list.CheckboxLabeled("CEPack_CookOffSparks".Translate(), ref Settings.cookOffSparks, "CEPack_CookOffSparksTip".Translate());
            list.End();
        }
    }

    // The pack's settings. Scribe keys are never renamed or removed (a retired one is hidden, not deleted).
    public class CEPackSettings : ModSettings
    {
        public bool cookOffSparks = true;   // sparks, a flash and a puff of smoke over ammo that cooks off (Puffs)

        public override void ExposeData()
        {
            Scribe_Values.Look(ref cookOffSparks, "cookOffSparks", true);
        }
    }

    public static class AmmoRule
    {
        private static Dictionary<ThingDef, string> map;
        private static Dictionary<ThingDef, string> shipped;   // ammo defs whose texPath another mod changed -> their shipped one
        private static bool failed;

        // Counts for the harness (cepack:ammobind).
        internal static int Generic, Calibre, ByFolder, Fallback, Unmatched, Retextured, Repointed;

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void Register()
        {
            IsoRim.Api.AddModelRule(new Func<ThingDef, string>(Rule));
            new Harmony("peren.isorimwaylith.cepack.ammo").Patch(AccessTools.Method(typeof(AmmoGeneralizer), nameof(AmmoGeneralizer.GeneralizeAmmo)),
                prefix: new HarmonyMethod(typeof(AmmoRule), nameof(Generalizer_Prefix)));
        }

        public static void Generalizer_Prefix()
        {
            Build();
        }

        public static string Rule(ThingDef def)
        {
            if (!(def is AmmoDef))
            {
                return null;
            }
            Build();
            string name;
            return map.TryGetValue(def, out name) ? name : null;
        }

        internal static string Answer(ThingDef def)
        {
            Build();
            string name;
            return def != null && map.TryGetValue(def, out name) ? name : null;
        }

        // The shipped texPath of a def another mod re-textured (null for any other def), for the harness.
        internal static string ShippedTexPath(ThingDef def)
        {
            Build();
            string p;
            return def != null && shipped != null && shipped.TryGetValue(def, out p) ? p : null;
        }

        private static string TexPath(ThingDef d)
        {
            return Normal(d?.graphicData?.texPath);
        }

        internal static string Normal(string p)
        {
            return p == null ? null : p.Replace('\\', '/').Trim('/');
        }

        // The texture folder every look of the rule is keyed on (the one place): the art the def is shipped with if another mod
        // re-textured it away from art the pack has a model for (ShippedArt.Retextured), else its current texPath.
        private static string ArtOf(ThingDef d)
        {
            string p;
            return d != null && shipped != null && shipped.TryGetValue(d, out p) ? p : TexPath(d);
        }

        private static string Family(string texPath)   // "Things/Ammo/<family>/..."
        {
            if (texPath == null)
            {
                return null;
            }
            string[] s = texPath.Split('/');
            return s.Length > 2 ? s[2] : null;
        }

        private static string LookOf(AmmoDef d)
        {
            string p = ArtOf(d);
            if (p == null)
            {
                return null;
            }
            if (string.Equals(p, AmmoLooks.FuelCellLarge, StringComparison.OrdinalIgnoreCase))
            {
                return d.ammoClass != null && d.ammoClass.defName == "GrenadeIncendiary" ? "fuelcell_inc" : "fuelcell_demo";
            }
            string look;
            return AmmoLooks.Generic.TryGetValue(p, out look) ? look : null;
        }

        private struct Candidate
        {
            public string look;
            public string family;   // the counterpart's texture family
        }

        private static void Build()
        {
            if (map != null)
            {
                return;
            }
            map = new Dictionary<ThingDef, string>();
            if (failed)
            {
                return;
            }
            try
            {
                shipped = ShippedArt.Retextured();
                Dictionary<AmmoDef, List<Candidate>> calibre = new Dictionary<AmmoDef, List<Candidate>>();
                Dictionary<AmmoDef, AmmoSetDef> unmatched = new Dictionary<AmmoDef, AmmoSetDef>();
                HashSet<AmmoDef> generic = new HashSet<AmmoDef>();
                foreach (AmmoSetDef set in DefDatabase<AmmoSetDef>.AllDefs)
                {
                    AmmoSetDef similar = set.similarTo;
                    if (similar == null || set.ammoTypes == null || similar.ammoTypes == null)
                    {
                        continue;
                    }
                    foreach (AmmoLink g in similar.ammoTypes)
                    {
                        if (g?.ammo != null)
                        {
                            generic.Add(g.ammo);
                        }
                    }
                    foreach (AmmoLink link in set.ammoTypes)
                    {
                        AmmoDef a = link?.ammo;
                        if (a == null)
                        {
                            continue;
                        }
                        AmmoLink cp = similar.ammoTypes.Find(x => x?.ammo != null && x.ammo.ammoClass == a.ammoClass);
                        string look = cp != null ? LookOf(cp.ammo) : null;
                        if (look == null)
                        {
                            if (cp == null && !unmatched.ContainsKey(a))
                            {
                                unmatched[a] = similar;
                            }
                            continue;
                        }
                        List<Candidate> list;
                        if (!calibre.TryGetValue(a, out list))
                        {
                            calibre[a] = list = new List<Candidate>();
                        }
                        list.Add(new Candidate { look = look, family = Family(ArtOf(cp.ammo)) });
                    }
                }
                Generic = Calibre = ByFolder = Fallback = Unmatched = Repointed = 0;
                Retextured = shipped != null ? shipped.Count : -1;
                foreach (AmmoDef g in generic)
                {
                    string look = LookOf(g);
                    if (look != null)
                    {
                        map[g] = "R3D_CE_" + look;
                        Generic++;
                    }
                }
                foreach (KeyValuePair<AmmoDef, List<Candidate>> kv in calibre)
                {
                    if (map.ContainsKey(kv.Key))
                    {
                        continue;   // a generic def that is also in a calibre set keeps its own look
                    }
                    List<Candidate> c = kv.Value;
                    string pick = c[0].look;
                    bool mixed = false;
                    for (int i = 1; i < c.Count; i++)
                    {
                        mixed |= c[i].look != pick;
                    }
                    if (mixed)
                    {
                        string own = Family(ArtOf(kv.Key));
                        for (int i = 0; i < c.Count; i++)
                        {
                            if (string.Equals(c[i].family, own, StringComparison.OrdinalIgnoreCase))
                            {
                                pick = c[i].look;
                                break;
                            }
                        }
                        ByFolder++;
                    }
                    map[kv.Key] = "R3D_CE_" + pick;
                    Calibre++;
                }
                foreach (KeyValuePair<AmmoDef, AmmoSetDef> kv in unmatched)
                {
                    if (map.ContainsKey(kv.Key))
                    {
                        continue;   // matched in another set
                    }
                    string own = ArtOf(kv.Key);
                    if (own != null && AmmoLooks.ByArt.ContainsKey(own))
                    {
                        if (shipped == null || !shipped.ContainsKey(kv.Key))
                        {
                            Unmatched++;   // its own CE texture has a look: graphicPath binds it
                        }
                        continue;   // (re-textured: the re-point step below gives it that look)
                    }
                    AmmoLink fmj = kv.Value.ammoTypes.Find(x => x?.ammo?.ammoClass != null && x.ammo.ammoClass.defName == "FullMetalJacket")
                        ?? (kv.Value.ammoTypes.Count > 0 ? kv.Value.ammoTypes[0] : null);
                    string look = fmj != null ? LookOf(fmj.ammo) : null;
                    if (look != null)
                    {
                        map[kv.Key] = "R3D_CE_" + look;
                        Fallback++;
                    }
                }
                // Re-pointed: a re-textured def the steps above leave to graphicPath (LaserChargePack, a generic look's
                // unmatched def) gets the ModelDef graphicPath gives its shipped art, which its new texPath no longer reaches.
                if (shipped != null)
                {
                    foreach (KeyValuePair<ThingDef, string> kv in shipped)
                    {
                        string model;
                        if (!map.ContainsKey(kv.Key) && AmmoLooks.ByArt.TryGetValue(kv.Value, out model))
                        {
                            map[kv.Key] = model;
                            Repointed++;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                failed = true;
                shipped = null;
                map = new Dictionary<ThingDef, string>();
                Log.Error(Pack.Tag + "Reading CE's generic ammo table failed (CE ammo keeps its texture-path models): " + e);
            }
        }
    }

    // The art each ammo def is shipped with: its graphicData/texPath as its own mod's Defs write it, before any mod's patches.
    // A prefix on LoadedModManager.ApplyPatches (Priority.First: before any other mod's prefix there, which could apply
    // patches early) reads the unified def XML the game has just built from every mod's Defs files, right before the first
    // patch runs (the pack's Mod constructor, which adds the prefix, runs before the game loads the def XML, so this is the
    // same whatever the load order). Only art the pack has a model for is kept (AmmoLooks.ByArt). Per defName the last node
    // counts, as in the game: a later node of the same defName with other art, or none of its own (it inherits its texPath
    // from a parent; none of CE's ammo does), drops the earlier entry, so that def keeps its current texPath (the rule as
    // before). A def made in code has no node and keeps its current texPath too. AmmoRule takes the result once (Retextured),
    // and the table is dropped. Fails soft: no read, every def keeps its current texPath. Read from the game, not copied: no
    // list of CE's texPaths is kept in the pack (AmmoLooks lists only the folders the pack has models for).
    internal static class ShippedArt
    {
        private static Dictionary<string, string> byDefName;   // ThingDef defName -> own texPath with a model, until AmmoRule takes it

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void Register()
        {
            new Harmony("peren.isorimwaylith.cepack.shippedart").Patch(
                AccessTools.Method(typeof(LoadedModManager), nameof(LoadedModManager.ApplyPatches)),
                prefix: new HarmonyMethod(typeof(ShippedArt), nameof(ApplyPatches_Prefix)) { priority = Priority.First });
        }

        public static void ApplyPatches_Prefix(XmlDocument xmlDoc)
        {
            try
            {
                Dictionary<string, string> read = new Dictionary<string, string>();
                XmlElement root = xmlDoc?.DocumentElement;
                if (root != null)
                {
                    foreach (XmlNode n in root.ChildNodes)
                    {
                        if (n.NodeType != XmlNodeType.Element || n.Name != "ThingDef")
                        {
                            continue;
                        }
                        XmlElement name = n["defName"];
                        if (name == null)
                        {
                            continue;
                        }
                        string own = AmmoRule.Normal(n["graphicData"]?["texPath"]?.InnerText);
                        if (own != null && AmmoLooks.ByArt.ContainsKey(own))
                        {
                            read[name.InnerText] = own;
                        }
                        else
                        {
                            read.Remove(name.InnerText);   // a later node of this defName without such art of its own
                        }
                    }
                }
                byDefName = read;
            }
            catch (Exception e)
            {
                byDefName = null;
                Log.Warning(Pack.Tag + "Reading the ammo defs' own textures failed (a def another mod re-textured loses its model): " + e);
            }
        }

        // Every ammo def re-textured away from art the pack has a model for: its current texPath differs from the one it is
        // shipped with (read above, so in AmmoLooks.ByArt) -> that shipped texPath (normalised as AmmoRule.Normal). A def
        // shipped with art the pack has no model for keeps its current texPath (CE turns the game's grenades into ammo with
        // its own art: they stay keyed on CE's art). Null if the defs were never read. Empty when no mod re-points CE's ammo art.
        internal static Dictionary<ThingDef, string> Retextured()
        {
            Dictionary<string, string> read = byDefName;
            byDefName = null;
            if (read == null)
            {
                return null;
            }
            Dictionary<ThingDef, string> changed = new Dictionary<ThingDef, string>();
            foreach (ThingDef d in DefDatabase<ThingDef>.AllDefsListForReading)
            {
                string own;
                if (!(d is AmmoDef) || d.defName == null || !read.TryGetValue(d.defName, out own))
                {
                    continue;
                }
                if (!string.Equals(own, AmmoRule.Normal(d.graphicData?.texPath), StringComparison.OrdinalIgnoreCase))
                {
                    changed[d] = own;
                }
            }
            return changed;
        }
    }

    // IsoRim's pattern for fields of newer Apis (Docs/Api.md, "Gating fields by version"): the operation runs only when IsoRim
    // is at least minVersion.
    public class PatchOperation_IfIsoRimApi : PatchOperation
    {
        public int minVersion;
        public PatchOperation apply;

        protected override bool ApplyWorker(XmlDocument xml)
        {
            int have;
            try
            {
                have = IsoRim.Api.Version;
            }
            catch (Exception)
            {
                have = 0;
            }
            return have < minVersion || apply == null || apply.Apply(xml);
        }
    }
}
