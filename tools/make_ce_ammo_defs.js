// IsoRim Waylith - Combat Extended Pack: writes the ammo ModelDefs, the graphicPath patch and the rule's look table from the
// generators' look lists (tools/ce_ammo_looks.json = generic looks, tools/ce_ammo2_looks.json = non-generic looks) and the
// OBJ files in Models/ (a/b/c stack versions as made by make_ce_ammo.py / make_ce_ammo2.py, its key GAE for ce_ammo_gae_*), and the
// thingDef bindings of another mod's own ammo (tools/ce_ammo_gae_binds.json: defName -> look, optional scale).
// usage: node tools/make_ce_ammo_defs.js   (from the pack root or anywhere: paths are relative to this file)
// Writes: Defs/CE_Ammo_Generic.xml, Defs/CE_Ammo_NonGeneric.xml, Defs/CE_Ammo_GAE.xml, Patches/CE_Ammo.xml, Source/AmmoLooks.cs
const fs = require("fs");
const path = require("path");
const root = path.join(__dirname, "..");
const models = fs.readdirSync(path.join(root, "Models"));
const gen = JSON.parse(fs.readFileSync(path.join(__dirname, "ce_ammo_looks.json"), "utf8"));
const ng = JSON.parse(fs.readFileSync(path.join(__dirname, "ce_ammo2_looks.json"), "utf8"));
const FUEL = "Things/Ammo/FuelCell/Large";

function stacks(prefix) {   // Models/<prefix>_a.obj, _b, _c that exist
  const out = [];
  for (const s of ["a", "b", "c"]) {
    const f = `${prefix}_${s}.obj`;
    if (models.includes(f)) out.push(`Models/${f}`);
  }
  if (out.length === 0) throw new Error("no OBJ for " + prefix);
  return out;
}
function modelXml(files, ind) {
  if (files.length === 1) return `${ind}<model>${files[0]}</model>\n`;
  return `${ind}<stackModels>\n` + files.map(f => `${ind}  <li>${f}</li>\n`).join("") + `${ind}</stackModels>\n`;
}
function def(name, files, extra) {
  return `  <IsoRim.ModelDef>\n    <defName>${name}</defName>\n` + (extra.pre || "") + modelXml(files, "    ")
    + (extra.post || "") + `    <overArt>true</overArt>\n  </IsoRim.ModelDef>\n`;
}
const short = tp => tp.replace(/^Things\/Ammo\//, "").replace(/[\/\-\s]/g, "_");

// ---- generic looks: one ModelDef per look (the rule names it), plus one per extra texture folder of a look (graphicPath).
const lookFirstPath = {};    // look -> its first (primary) texture folder
const pathDef = {};          // texture folder -> ModelDef that gets its graphicPath
const lookOf = {};           // texture folder -> look (FuelCell/Large: by ammoClass, in code)
const genDefs = [];
const lookDefs = new Set();
for (const tp of Object.keys(gen).sort()) {
  if (tp === FUEL) {
    lookOf[tp] = "fuelcell_demo";
    pathDef[tp] = "R3D_CE_fuelcell_demo";   // same CE art for both; texture-path things (gamma shell) take the demo look
    for (const l of ["fuelcell_demo", "fuelcell_inc"]) { lookDefs.add(l); lookFirstPath[l] = lookFirstPath[l] || tp; }
    continue;
  }
  const look = gen[tp];
  lookOf[tp] = look;
  if (!lookFirstPath[look] || isPrimary(tp, lookFirstPath[look])) {
    if (lookFirstPath[look]) pathDef[lookFirstPath[look]] = null;
    lookFirstPath[look] = tp;
  }
  lookDefs.add(look);
}
function isPrimary(a, b) {   // Rifle/* before Rifle/Battlerifle/*, Charged/Medium* before Charged/Small*
  const rank = p => (/Battlerifle|Charged\/Small/.test(p) ? 1 : 0);
  return rank(a) < rank(b) || (rank(a) === rank(b) && a < b);
}
for (const tp of Object.keys(gen).sort()) {
  if (tp === FUEL) continue;
  const look = gen[tp];
  pathDef[tp] = lookFirstPath[look] === tp ? `R3D_CE_${look}` : `R3D_CE_${look}_${/Battlerifle/.test(tp) ? "battlerifle" : "small"}`;
}
let gx = "";
for (const look of [...lookDefs].sort()) gx += def(`R3D_CE_${look}`, stacks(`ce_ammo_${look}`), {});
const extraGen = Object.keys(pathDef).filter(tp => pathDef[tp] !== `R3D_CE_${lookOf[tp]}` && tp !== FUEL).sort();
for (const tp of extraGen) gx += def(pathDef[tp], stacks(`ce_ammo_${lookOf[tp]}`), {});
// Mortar re-skin: CE redefines the game's 81mm shells with its own art; these thingDef entries load after IsoRim's own
// R3D_Shell_* (removed by Patches/CE_Ammo.xml) so the 9 shells show CE's mortar look.
const shells = [["Shell_HighExplosive", "mortar_he"], ["Shell_HighExplosive_HFuzed", "mortar_airburst"],
  ["Shell_Incendiary", "mortar_inc"], ["Shell_EMP", "mortar_emp"], ["Shell_Firefoam", "mortar_firefoam"],
  ["Shell_Smoke", "mortar_smoke"], ["Shell_AntigrainWarhead", "mortar_antigrain"], ["Shell_Toxic", "mortar_toxic"],
  ["Shell_Deadlife", "mortar_deadlife"]];
for (const [t, look] of shells)
  gx += def(`R3D_CE_${t}`, stacks(`ce_ammo_${look}`), { pre: `    <thingDef>${t}</thingDef>\n` });

const head = (what) => `<?xml version="1.0" encoding="utf-8"?>\n<!-- IsoRim Waylith - Combat Extended Pack: ${what}\n     Written by tools/make_ce_ammo_defs.js (do not edit by hand). -->\n<Defs>\n`;
fs.writeFileSync(path.join(root, "Defs", "CE_Ammo_Generic.xml"), head(
  "CE's generic ammo looks (models from tools/make_ce_ammo.py, approved 2026-10-30).\n" +
  "     R3D_CE_<look>: one per look, bound by the pack's model rule (IsoRim Api 42, Source/Ammo.cs): every ammo def of a\n" +
  "     calibre set with similarTo gets the look of its generic counterpart (same ammoClass), as CE's generic ammo option does.\n" +
  "     graphicPath (Patches/CE_Ammo.xml) also gives each look to things drawn with its CE texture; the _battlerifle / _small\n" +
  "     defs carry the look's second texture folder. Stack models a/b/c follow CE's Graphic_StackCount. R3D_CE_Shell_*:\n" +
  "     the 9 81mm mortar shells re-skinned to CE's art.") + gx + "</Defs>\n");

// ---- non-generic looks: one ModelDef per CE texture folder (graphicPath, Api 16)
let nx = "";
for (const tp of Object.keys(ng).sort()) {
  nx += def(`R3D_CE_${short(tp)}`, stacks(`ce_ammo2_${ng[tp]}`), { pre: `    <graphicPath>${tp}</graphicPath>\n` });
}
// The 50mm rocket shares the APKWS art, which CE draws at drawSize 0.7.
nx += def("R3D_CE_Ammo_50mmRocket_HE", stacks("ce_ammo2_apkws_heat"),
  { pre: "    <thingDef>Ammo_50mmRocket_HE</thingDef>\n", post: "    <scale>0.7</scale>\n" });
fs.writeFileSync(path.join(root, "Defs", "CE_Ammo_NonGeneric.xml"), head(
  "CE's other ammo (rockets, cells, medieval, neolithic ...; models from tools/make_ce_ammo2.py,\n" +
  "     approved 2026-10-30), bound by CE texture folder (graphicPath). LaserPack and PlasmaCell/Heavy share one art and one\n" +
  "     model. The 50mm rocket: the APKWS model at CE's drawSize 0.7. Pila keeps IsoRim's own javelin model.") + nx + "</Defs>\n");

// ---- "The Generic Ammo Experience for Combat Extended": its own new ammo defs, bound by thingDef (tools/ce_ammo_gae_binds.json,
// the one place their defNames are named). Not by texture folder: three of them reuse CE art that CE's own defs also use, and
// those stay as they are. Without that mod the defs are missing and IsoRim skips each entry silently (GetNamedSilentFail).
const gae = JSON.parse(fs.readFileSync(path.join(__dirname, "ce_ammo_gae_binds.json"), "utf8"));
let gx2 = "";
for (const d of Object.keys(gae).sort()) {
  const b = gae[d];
  gx2 += def(`R3D_CE_${d}`, stacks(`ce_ammo_gae_${b.look}`),
    { pre: `    <thingDef>${d}</thingDef>\n`, post: b.scale && b.scale !== 1 ? `    <scale>${b.scale}</scale>\n` : "" });
}
fs.writeFileSync(path.join(root, "Defs", "CE_Ammo_GAE.xml"), head(
  "the new ammo of \"The Generic Ammo Experience for Combat Extended\" (Workshop 3284460811;\n" +
  "     models from tools/make_ce_ammo2.py GAE, approved 2026-11-13), bound by thingDef from tools/ce_ammo_gae_binds.json. Without\n" +
  "     that mod the things are missing and IsoRim skips these entries silently. Scale = the mod's drawSize.") + gx2 + "</Defs>\n");

// ---- patch: graphicPath for the generic looks (Api 16), IsoRim's own mortar shell entries removed
let px = `<?xml version="1.0" encoding="utf-8"?>\n<!-- IsoRim Waylith - Combat Extended Pack: written by tools/make_ce_ammo_defs.js (do not edit by hand). -->\n<Patch>\n`;
px += `  <!-- Every generic look also binds things drawn with its CE texture (other mods' ammo reusing CE's art, CE's Halo defs;\n`
  + `       on IsoRim 16..41, without model rules, the calibres too). A thingDef entry or the model rule still wins. -->\n`;
px += `  <Operation Class="IsoRimCEPack.PatchOperation_IfIsoRimApi">\n    <minVersion>16</minVersion>\n    <apply Class="PatchOperationSequence">\n      <operations>\n`;
for (const tp of Object.keys(pathDef).sort()) {
  px += `        <li Class="PatchOperationAdd">\n          <xpath>/Defs/IsoRim.ModelDef[defName="${pathDef[tp]}"]</xpath>\n`
    + `          <value><graphicPath>${tp}</graphicPath></value>\n        </li>\n`;
}
px += `      </operations>\n    </apply>\n  </Operation>\n`;
const iso = shells.map(s => s[0]).filter(s => s !== "Shell_HighExplosive_HFuzed").map(s => `R3D_${s}`);
const xp = `/Defs/IsoRim.ModelDef[${iso.map(n => `defName="${n}"`).join(" or ")}]`;
px += `  <!-- Mortar shells: CE gives the game's shells its own art; IsoRim's vanilla-look entries make way for R3D_CE_Shell_*. -->\n`;
px += `  <Operation Class="PatchOperationConditional">\n    <xpath>${xp}</xpath>\n    <match Class="PatchOperationRemove">\n      <xpath>${xp}</xpath>\n    </match>\n  </Operation>\n</Patch>\n`;
fs.mkdirSync(path.join(root, "Patches"), { recursive: true });
fs.writeFileSync(path.join(root, "Patches", "CE_Ammo.xml"), px);

// ---- C# look table for the rule
// ByArt: every CE texture folder the pack has a model for, and the ModelDef its graphicPath binds (the generic folders' from
// the patch above, the non-generic ones' from CE_Ammo_NonGeneric.xml): the rule gives an ammo def that another mod re-pointed
// away from its CE art the model of that art (Source/Ammo.cs, AmmoRule.Build).
const byArt = {};
for (const tp of Object.keys(pathDef)) byArt[tp] = pathDef[tp];
for (const tp of Object.keys(ng)) {
  if (byArt[tp]) throw new Error("texture folder in both look lists: " + tp);
  byArt[tp] = `R3D_CE_${short(tp)}`;
}
let cs = `// Written by tools/make_ce_ammo_defs.js (do not edit by hand): CE's generic ammo texture folders and their looks
// (R3D_CE_<look>), and every CE ammo texture folder with the ModelDef that binds it by graphicPath.
using System.Collections.Generic;

namespace IsoRimCEPack
{
    internal static class AmmoLooks
    {
        internal const string FuelCellLarge = "${FUEL}";

        internal static readonly Dictionary<string, string> Generic = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase)
        {
`;
for (const tp of Object.keys(lookOf).sort()) cs += `            { "${tp}", "${lookOf[tp]}" },\n`;
cs += `        };

        internal static readonly Dictionary<string, string> ByArt = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase)
        {
`;
for (const tp of Object.keys(byArt).sort()) cs += `            { "${tp}", "${byArt[tp]}" },\n`;
cs += `        };
    }
}
`;
fs.writeFileSync(path.join(root, "Source", "AmmoLooks.cs"), cs);
console.log("generic looks", lookDefs.size, "extra path defs", extraGen.length, "graphicPaths", Object.keys(pathDef).length,
  "shells", shells.length, "non-generic", Object.keys(ng).length, "by art", Object.keys(byArt).length);
