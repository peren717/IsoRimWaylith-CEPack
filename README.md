# IsoRim Waylith - Combat Extended Pack

[Combat Extended](https://steamcommunity.com/sharedfiles/filedetails/?id=2890901044) in the 3D view of [IsoRim Waylith](https://github.com/peren717/IsoRim-1.7) (RimWorld 1.7), also on the floors of [IsoRimZ](https://github.com/peren717/IsoRim-ProjectZ).

- Bullets, shells and grenades fly at Combat Extended's real height, from the muzzle
- Mortar shells drawn on their real arc, like IsoRim's own shells
- Works on IsoRimZ floors

Requires IsoRim Waylith and Combat Extended. IsoRimZ is optional. Load after all of them.

让 Combat Extended 在 IsoRim Waylith 的 3D 视角中正常显示（支持 IsoRimZ 楼层）。

- 子弹、炮弹和手雷按 Combat Extended 的真实高度从枪口飞出
- 迫击炮弹沿真实弹道显示，与 IsoRim 自己的炮弹一致
- 支持 IsoRimZ 的楼层

需要 IsoRim Waylith 和 Combat Extended。IsoRimZ 可选。请放在它们之后加载。

Combat Extended is made by the CE team. This pack contains no files of the original mod.

MIT License.

## Notes

- One hook: a prefix on `ProjectileCE.DrawAt` (see `Source/Pack.cs` for the design and the height mapping). IsoRim's public Api only.
- Height mapping: a CE height is drawn at `ce * Api.WallHeight / CollisionVertical.WallCollisionHeight` cells (a CE wall is 2 units, IsoRim's wall `Api.WallHeight` cells).
- Test harness (`Source/Harness.cs`): dormant unless RimWorld is started with `-cepackprobe`; adds `cepack:...` tokens to IsoRim's harness probe (drawing on/off for before-and-after frames, CE turret ammo and attack orders, ticking until a shell flies or comes down, flight read-outs).
- Ammo models (`Defs/CE_Ammo_*.xml`, written by `tools/make_ce_ammo_defs.js`): an IsoRim model rule in `Source/Ammo.cs` gives each calibre the look of its generic counterpart. Looks are keyed on the CE texture a def is shipped with (read from the defs before any mod's patches), so a mod that re-textures CE's ammo (e.g. The Generic Ammo Experience) keeps the 3D models while the 2D view shows its art. That mod's own new ammo has models too (`Defs/CE_Ammo_GAE.xml`, bound by thingDef from `tools/ce_ammo_gae_binds.json`; without the mod they bind nothing). Harness: `cepack:ammobind`.
- Turret models (`Source/TurretModels.cs`): when a mod gives a turret a footprint of another shape than its model was made for (Turret_Heavy 2x1), the model is scaled evenly to fit inside it instead of stretched.
- Contributing and tests: `Docs/Testing.md`.
- Api for other mods (IsoRimZ part: placement queries, target geometry, surface explosion rules): `Docs/Api.md`, calls in `SourceZ/Api.cs`. Harness: `-cepacktest=api` (and `apiload`), `-cepacktest=perf`.
- Fire between floors: `SourceZ/FireLine.cs` (the vertical aim of direct fire, for the shot and for Sight). Harness: `-cepacktest=firing` (per-shot bounds, `-cepacknofire` leaves FireLine out; lean shots: `SourceZ/TestZLean.cs`, `-cepackleanall` the full matrix, `-cepacknolean` and `-cepacknoleanfix` the off cases), `-cepacktest=cereads` (CE's position reads against the allow-list `Docs/CeReads.tsv`; run it after every CE update).
