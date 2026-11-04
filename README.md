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
- Contributing and tests: `Docs/Testing.md`.
- Api for other mods (IsoRimZ part: placement queries, target geometry, surface explosion rules): `Docs/Api.md`, calls in `SourceZ/Api.cs`. Harness: `-cepacktest=api` (and `apiload`), `-cepacktest=perf`.
