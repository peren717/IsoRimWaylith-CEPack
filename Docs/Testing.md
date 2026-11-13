# Testing the CE pack

For contributors and their coding agents. Read this before changing anything under `Source/` or `SourceZ/`.

## How we work

- `main` changes only through pull requests. Open a PR, the other maintainer reviews it, then it is merged as one squashed commit. Never push to `main` directly, never rewrite pushed history.
- Keep a PR to one topic. Say in the PR which test modes you ran, on how many floors, and the `done.txt` results.
- Do not commit a built DLL from a PR branch unless the PR says so: the maintainer rebuilds from source at merge.
- The Workshop item is uploaded by peren717, after the PR is merged.
- Per-mod logic stays in that mod. New or changed Api follows the rules in `Docs/Api.md` ("Adding to the Api").
- Everything must fail soft: a CE update or another mod's bad answer switches that part off with one warning, it never breaks shooting.
- Bugs of CE or other mods themselves are noted, not fixed here.
- Call CE's own code rather than copy it (inside ProjectZ's 3D scope, Api 10, where that answers what it asks). A copy that can't be avoided says "Copied from CE <Type.Method>" and gets a row in `SourceZ/CopyWatches.cs` with the CE method's hash (mode `cereads`).

## Set up

1. A separate save data folder for tests, so your own game is never touched, e.g. `D:\RimTest`. Every run uses `-savedatafolder=<that folder>`.
2. Mods active in that folder's `Config/ModsConfig.xml`, in this order, and nothing else:
   Harmony, Core and the DLCs, Combat Extended, IsoRim Waylith, IsoRimZ (ProjectZ), this pack. Unrelated mods make results meaningless.
   Mode `m230lf` also needs `sumghai.defensivemachinegunturretpack` after CE.
3. CE settings in that folder: `Config/Mod_2890901044_Controller.xml` with the ammo system off:
   ```xml
   <?xml version="1.0" encoding="utf-8"?>
   <SettingsBlock>
     <ModSettings Class="CombatExtended.Settings">
       <enableAmmoSystem>False</enableAmmoSystem>
     </ModSettings>
   </SettingsBlock>
   ```
4. Build. The pack has two assemblies:
   ```bash
   dotnet build Source/IsoRimCEPack.csproj -c Release
   dotnet build SourceZ/IsoRimCEPackZ.csproj -c Release -nodeReuse:false --no-incremental
   ```
   The project files read `RimDir` and `WorkshopDir`; pass your own with `-p:RimDir=... -p:WorkshopDir=...`. `-p:IsoRimDll=<path>` and `-p:ProjectZDll=<path>` build against another copy of those mods. Test against the latest `master` of IsoRim and ProjectZ.
5. Make the game load your build. The builds land in the working copy: `1.7/Assemblies/IsoRimWaylith_CEPack.dll` and `1.7/IsoRimZ/Assemblies/IsoRimWaylith_CEPackZ.dll`. The game only reads mods in its `Mods` folder and the Workshop folder, so work in a folder under `Mods`, link one there, or copy both DLLs over the installed pack. Keep only one copy of the pack active, unsubscribe the Workshop one in the test setup. Before trusting a result, compare the hash of both DLLs in the folder the game really loads with your build: a stale DLL passes the old code's tests.

## Run a test

Start the game directly (not through Steam) with:

```bash
./RimWorld.exe -savedatafolder="D:\RimTest" -quicktest -cepacktest=<mode> -cepacktag=<tag> \
  -cepackfloors=2 -cap17hide -isorim3d -isorimbiome=TemperateForest -isorimseed=7 -NoFileLog
```

- `-cepacktest=<mode>`: see the table. The test code is dormant without `-quicktest` and this argument.
- `-cepacktag=<tag>`: the output folder name. Use a new one per run.
- `-cepackfloors=2|3`: storeys on the test map.
- `-isorimseed=7`, `-isorimbiome`: keep these fixed, results are compared between builds.
- `-cap17hide`: hides the window.

Results go to `<save data folder>/renders/<tag>/`:

- `done.txt`: written at the end, e.g. `2026-11-02T12:40:36 fails=0 errors=0`. A run is good only with both 0. Stop the game when it appears; no `done.txt` within about 15 minutes means it hung.
- `log.txt`: every check as `CHECK PASS <id> <what>: <values>` or `CHECK FAIL ...`, and the pack's startup line (patch counts, parts switched off).

Run one game at a time per save data folder. After a run, restore anything you changed in `Config/`.

## The suite

Before a PR: every mode below on `-cepackfloors=2` and `3` (posture, popup, drafted on 2 only; cereads once). About 50 runs; for a small change run the modes that touch it, plus `suite`.

| Mode | Covers |
|---|---|
| `fixb` | fixed bugs, kept as regressions |
| `suite` | shots between floors: flight, sight, hits, floor planes |
| `p4` | CIWS, turrets, CE's AI between floors |
| `areas` | explosions, suppression between floors |
| `shields` | shields, other mods' blockers |
| `firing` | direct fire between floors: lead, per-shot aim, slab lips, Sight against the fired line, lean shots. `-cepacknofire`, `-cepacknolean` and `-cepacknoleanfix` turn a fix off to show what the checks catch |
| `edge` | map edges, void rows, odd placements |
| `save` | save and load in flight |
| `fight` | a real fight; also run with `-cepacknoz` (Z part off) |
| `fault` | no extra argument: a healthy run. Then `-cepackfault=<x>` for each of `frames aim turrets shields draw flightgroup areasgroup aimswap scoperead partoff`: a part breaks, the rest must carry on |
| `m230lf` | a modded turret (needs the turret pack above) |
| `posture` | CE postures on the 3D figures |
| `popup` | popping up to fire over cover |
| `drafted` | the drafted idle rule |
| `bench` | CE's loading bench built and worked in the real flow. Needs the ammo system ON, see below |
| `cereads` | CE's reads of positions and distances against `Docs/CeReads.tsv`, no game state. Fails on a new read after a CE update: review `cereads_new.tsv` in the run's folder, then update the list (`-cepackreadswrite` writes the full list as `cereads_all.tsv`). Also checks every CE method the pack copies (`SourceZ/CopyWatches.cs`) against the IL hash its copy follows: after a CE update a failing row names the copy to review; `cecopies_now.tsv` has the hashes now to paste once it is. `-cepackfault=watchcopy` gives the first row a wrong hash: one IsoRim warning at startup and that check fails |
| `api` | the extension Api with a fake siege engine (`Docs/Api.md`); `-cepackapi=<ids>` picks checks |
| `apiload` | with `-cepacknoz`: a save made in `api` loads without the Z part. Always straight after `api`, see below |
| `perf` | one fixed big fight, for comparing cost; `-cepackperfreg` adds idle registrations. Interleave runs of the two builds: the fight varies about 4% between runs |

`-cepackonly=<ids>` runs single checks in `fixb` and `p4` only. `api` has `-cepackapi=`, posture, popup and drafted have `-ceposture=`, `-cepopup=`, `-cedrafted=`; the other modes have no filter.

`api` and `apiload` are a pair: `api` writes a save with a fixed name, `apiload` reads and deletes it. Run them in the same save data folder and with the same floors, one pair at a time: `api` then `apiload` on 2 floors, then the same on 3. Otherwise the save is overwritten, comes from the wrong floor count, or is not found.

`bench` is the one exception to the ammo setting in Set up: set `<enableAmmoSystem>True</enableAmmoSystem>` for that run and restore it after.

Comparing builds matters more than a single result: run `main` and your branch the same way.

## Known noise

Some checks are not fully deterministic and pass on a rerun: `save` check 13, `areas` 8c, 9b, mortar 6, P4i, m230 D2, drafted D2c, TripleRocket respawn lines. `firing` F7 needs a hit and can fail when the seeds shift. Rerun once; a check that fails twice, or fails only on your branch, is real.

Filtered CE noise: "Spawning destroyed thing" for a spent Gun_TripleRocket, and the `/damagedThings` load ID warning from a save made mid-explosion. Both are CE's own.

## Writing tests

- Use the real game flow: real pawns, jobs, ticks and projectiles. Do not call the function under test in place of the gameplay.
- Fixed seeds, and one isolated fixture per check, so checks do not depend on each other.
- Every bug fix adds a check that fails without the fix.
- A fresh CE gun starts with an empty magazine: load it first, as the existing tests do.
- Test code stays dormant in normal play (`SourceZ/Test*.cs`, gated on `-quicktest`).
- Close every game you start.
