# NOTES

Working log for the V1-in-RoR2 bridge. Design: [docs/DESIGN.md](docs/DESIGN.md).

## Status

| Phase | State |
|---|---|
| 0 Recon | done 2026-10-09 |
| 1 Handshake, launch/stop, test mode | done 2026-10-09 (`handshake` scenario passes) |
| 2 V1 in character select | done 2026-10-09 (`survivor` scenario passes) |
| 3 ULTRAKILL's picture + input | done 2026-10-09 (`picture` scenario passes) |
| 4 Stage geometry as collision | done 2026-10-10 (`geometry` scenario passes) |
| 5 Monsters and damage | damage both ways done 2026-10-10 (`combat` scenario; user confirmed in game). Parry not done |
| 6–7 | not started |

## How to work on it

```
tools\Setup-Profiles.ps1      # once: downloads BepInEx/R2API, builds profiles\ror2 and profiles\uk (-Force to redo)
tools\Install-UkDoorstop.ps1  # once: Doorstop 4 into ULTRAKILL's folder, with backup (-Restore undoes it)
tools\Build.ps1               # builds both plugins against the local installs and deploys them to the profiles
tools\Run-Test.ps1 -Scenario handshake   # unattended run; output in test-output\<time>-<scenario>\
tools\Run-Test.ps1 -Scenario survivor -ExtraArgs '--v1bridge-stage','golemplains'
```

Not in git: `profiles/`, `downloads/`, `build/`, `backups/`, `test-output/`, `.tools/` (ilspycmd 8.2), `.decomp/`
(decompiled RoR2.dll and ULTRAKILL Assembly-CSharp, for reading only).

## Decisions

- Two BepInEx 5 plugins: `RorBridge` (RoR2, netstandard2.1) and `UkBridge` (ULTRAKILL, net472), compiled against the local installs.
- TCP `127.0.0.1:27120`, UkBridge listens. Line protocol + `HELLO <protocol>` handshake (`src/Shared/Proto.cs`).
  Frames and geometry will go through memory-mapped files.
- Profiles live in `profiles/` and are selected per launch with Doorstop 4 switches
  (`--doorstop-enabled true --doorstop-target-assembly <profile>\BepInEx\core\BepInEx.Preloader.dll`), through
  `steam.exe -applaunch`. RoR2's folder is not modified at all. The r2modman profiles are untouched.
- ULTRAKILL is started with `-v1bridge -v1bridge-port N -v1bridge-parent <RoR2 pid>`: window parked off-screen,
  quits on `QUIT` or when the RoR2 process disappears. RorBridge kills an ULTRAKILL it started that has not connected
  25 s after its process appeared (it would be running without the save sandbox).
- Save isolation, both always on in the bridge profiles:
  - ULTRAKILL: `GameProgressSaver.BaseSavePath` and `PrefsManager.PrefsPath` point into `profiles\uk`
    (preferences seeded once as a copy of the real ones), `WriteFile` refuses paths outside, leaderboard submits and
    Steam stat writers are skipped.
  - RoR2: `RoR2Application.cloudStorage` is replaced by a folder in `profiles\ror2\saves` before profiles load
    (seeded once with a copy of the real profiles, read through Steam's storage), `Console.WriteConfigFile` is
    redirected there, Steam remote storage refuses writes, `AchievementSystemSteam.AddAchievement` is skipped.
- V1 survivor (`V1Survivor.cs`): clone of Commando's body named `V1Body` (R2API Prefab/ContentManagement/Language),
  four skill entries whose activation state is `EntityStates.Idle` (RoR2 never fires anything), drawn icons, and a
  primitive-built placeholder for character select. Nothing from ULTRAKILL's files.
- Bridge switch (`Session.cs`): `Wanted` = local player has V1 picked in lobby or run (this starts ULTRAKILL, so it is
  loaded by the time the run begins); `Active` = a run is on and the local body is `V1Body`. ULTRAKILL stays up between
  runs and closes with RoR2.
- Test mode starts runs with `transition_command "gamemode ClassicRun; host 0;"`, `SetSurvivorPreferenceClient`,
  the `run_scene_override` convar (set by reflection; it is cheat-flagged) and `pregame_start_run`.
- ULTRAKILL level (`UkBridge/Level.cs`, after Ultracraft): intro/tutorial reads patched, Sandbox `uk_construct` loaded
  and stripped to V1's rig + HUD + gore pools, camera cleared to transparent black, V1 switched on by hand on the first `TP`.
  All gear owned (`CheckGear` → 1). Until Phase 4 V1 stands on a flat collider at y = 0.
- Coordinates: ULTRAKILL's origin is laid at the RoR2 body's feet when the run engages; `K = 3.5/1.82` ULTRAKILL units
  per metre, same axes (both Unity). Wire positions are ULTRAKILL's; RorBridge converts (`V1Drive.ToRor/ToUk`).
- Picture (`Frames.cs` on both sides): ULTRAKILL's Virtual Camera draws into our RenderTexture; colour and the
  pre-post-process target are read back with AsyncGPUReadback straight into `%TEMP%\v1bridge_frame.bin` (4 slots,
  header with sequence, QPC draw time, the input the frame was drawn with, and the camera pose); a named event wakes a
  worker thread in RoR2 that joins colour + coverage (alpha, or brightness where nothing wrote alpha) into one RGBA
  image; the main thread uploads it to a Texture2D shown by a RawImage on our own canvas on RoR2's UI camera, ordered
  under the HUD. No custom shader is needed on either side.
- View: RoR2's `CameraRigController` gets an `ICameraStateProvider` (must be a MonoBehaviour) returning the pose and
  FOV of the frame on screen. The RoR2 body is moved to V1's feet every LateUpdate with its CharacterMotor and
  KinematicCharacterMotor off and its model hidden; no drop pod for V1.
- Input: Rewired mouse deltas/buttons and `Keyboard.PollForAllKeys` (Escape kept for RoR2) are sent every frame as
  `IN`; UkBridge plays them through its own Input System keyboard and mouse and applies look with ULTRAKILL's own
  sensitivity maths. Nothing is sent while RoR2 is paused, unfocused or has the cursor free.
- RoR2's own key handling for V1 is skipped by a Harmony prefix on `PlayerCharacterMasterController.Update/FixedUpdate`.
- ULTRAKILL renders at 720p (config `UltrakillHeight`), 120 fps cap (`UltrakillFpsCap`), and ticks at 10 fps while RoR2
  is not in a V1 run (`IDLE`).
- Stage geometry (`StageGeometry.cs` → `UkBridge/Geometry.cs`): when a V1 run engages, every enabled non-trigger collider
  on RoR2's World layer (mesh, box, sphere, capsule, terrain; not loose rigidbodies) is written as triangles in
  ULTRAKILL coordinates to `%TEMP%\v1bridge_geo_N.bin`; `GEO <path>` has UkBridge build one MeshCollider each (layer 8,
  tag Floor) with a depth-only, transparent-black renderer (Ultracraft's occluder), a slice per frame; `GEODONE` then
  releases the `TP` that puts V1 down. Unreadable meshes are read back from their GPU vertex/index buffers.
  V1 falling 30 m below the stage's bounds is put back at the start.
- Monsters (`Monsters.cs` → `UkBridge/Enemies.cs`): every frame `ENTS` lists each living non-player-team body (feet,
  width, height from its capsule, health in ULTRAKILL damage units). UkBridge keeps a stand-in per monster
  (EnemyIdentifier with head/body boxes, registered with EnemyTracker and TargetTracker, depth-only occluder), after Ultracraft.
- Damage out: a Harmony prefix on `EnemyIdentifier.DeliverDamage` sends `DMG id multiplier head explosion hitter point`
  and spawns ULTRAKILL's own blood there (so blood healing works). RorBridge turns it into a real `DamageInfo` from V1's
  body: `multiplier x body.damage x DamageCoefficient` (config, default 3, so a revolver shot is 300%), crit rolled by
  the body, proc coefficient = multiplier clamped to 0.1..1, then `OnHitEnemy/OnHitAll`. Kills are RoR2's own
  (gold, experience, on-kill items); `KILL id` gives ULTRAKILL its style points.
- Damage in: `V1HealthLink` on the body prefab marks every hit NonLethal, measures the health RoR2 took after all its own
  mitigation, puts it back, and sends `HURT <share of full health x 100>`; ULTRAKILL's `GetHurt` decides (its hurt and
  dash invincibility apply). ULTRAKILL's HP (`HP hp dead`) is written onto the body every frame as the same share of
  full health; `dead` makes the body `Suicide()`. `HealthComponent.Heal` on V1 is forwarded as `HEAL`.
- Frame transport starts as Ultracraft's AsyncGPUReadback → mmap; D3D11 shared texture only if latency demands it.
- Gameplay defaults: see "Design defaults chosen" in the design doc.

## Findings

- ULTRAKILL: Unity 2022.3.29f1, Mono, D3D11, new Input System. Saves in `<game>\Saves`, prefs in `<game>\Preferences`.
- RoR2: Unity 2021.3.45f2, version 1.5.0 #1210 (patched 2026-10-08, DLC "Hallowed Concepts"), Mono, D3D11, Rewired.
  It now has its own `ModLoader` (`Mods\enabled_mods.txt`); we stay on BepInEx. BepInEx 5.4.2122 + our plugin load fine on it.
- **Doorstop 3.4 (what r2modman had left in ULTRAKILL's folder) does not inject on Unity 2022.3**: the game simply
  starts unmodded. Doorstop 4.5 (from BepInEx 5.4.23.5) works. Installed with `enabled = false` in the config so
  normal launches stay vanilla; backup in `backups\ULTRAKILL-doorstop\`.
- BepInExPack's preloader deletes `plugins\RoR2BepInExPack` on sight (the pack's pre-r2modman location), after which
  R2API refuses to load. Packages must sit in `plugins\<Author-Name>\...` as r2modman lays them out; `Setup-Profiles.ps1` does.
- R2API (Core 5.3.0, ContentManagement 1.0.11, Prefab 1.1.1, Language 1.1.0) works on the 2026-10-08 RoR2 build.
- Disabling `PlayerCharacterMasterController` removes the player from `instances`, and the run ends in defeat at once.
- `CameraRigController` casts its override provider to `UnityEngine.Object`: a plain class throws every frame.
- A child added to RoR2's HUD canvas is deactivated by the HUD; the ULTRAKILL layer needs its own canvas.
- Mono's `Stopwatch.GetTimestamp` counts from process start, so the two games cannot compare it; `Clock.Micros()` calls
  QueryPerformanceCounter directly.
- Once ULTRAKILL's Virtual Camera targets our texture its window stops updating; screenshots are read from the texture.
- RoR2 ran vsynced at ~60 fps during tests, so 60 of ULTRAKILL's 120 frames a second are shown.
- RoR2 reads user profiles a moment *after* the title scene appears, and re-sends every owned achievement to Steam on
  each start (160 calls, skipped by the guard).
- Skipping RoR2's intro (`set_scene title`) before `RoR2Application.loadFinished` breaks its `onLoad` chain
  (NullReferenceException in `BlockObjectOnLoad`, then no profile gets signed in). Test mode waits for `loadFinished`.
- ULTRAKILL with an empty sandbox save boots into its Tutorial; Phase 3 patches the intro/tutorial reads as Ultracraft does.
- The Steam client rewrites `steam_autocloud.vdf` (account id only) in ULTRAKILL's Saves/Preferences whenever the game
  runs; the test runner ignores that file.
- The Claude desktop app redirects this session's writes under `%APPDATA%`/`%LOCALAPPDATA%` into its package cache, so
  nothing the games must see can live there. The project is at `C:\V1Bridge`.
- Ultracraft reference clone: `%LOCALAPPDATA%\Temp\uc-ref` (not part of this repo).

## Test results

| Date | Scenario | Result | Numbers |
|---|---|---|---|
| 2026-10-10 00:2x | combat (golemplains) | PASS 31/31 on 2 of the last 4 runs; the other 2 failed one or two timing-dependent checks (see known issues) | revolver x1.25 = 45 damage at level 1 (unit 36), Lemurian dead in 2 shots, gold 15→27 and experience 0→20 from the kill; punch x1 = 43.2; a 30% hit cost 30 HP; healing 10% gave 10 HP; a Lemurian's own attack arrived as HURT 8–17 |
| 2026-10-10 00:01 | geometry (golemplains) | PASS (30/30, 0 real files changed) | 142 colliders, 206,244 triangles; export 78 ms, build in ULTRAKILL 153 ms; ground height equal within 2 mm at 81/81 points; V1 walked 27 m over terrain, feet 1 mm above RoR2's ground |
| 2026-10-09 23:56 | picture (golemplains) | PASS (32/32, 0 real files changed) | ULTRAKILL ready 13 s after pick; 1280x720, 59.5 fps shown; frame age 23 ms avg / 28 ms p95; input sent → frame uploaded 34 ms avg / 35 ms p95; merge 5.0 ms (worker thread), upload 0.3 ms; walk 13 m in 1.5 s, jump 9.7 units |
| 2026-10-09 23:40 | survivor (golemplains) | PASS (39/39 checks, 0 of 128 watched real files changed) | V1 is survivor 0 of 22; pick-to-handshake 4.2 s; V1 body 110 HP / 12 damage at level 1 |
| 2026-10-09 23:31 | handshake | PASS (16/16 checks, 0 of 128 watched real files changed) | RoR2 to menu 40 s; ULTRAKILL process to handshake 4.6 s; ping 0.20 ms avg / 1.07 ms max over 100; ULTRAKILL quit 0.6 s after QUIT |

## Incidents

- 2026-10-09 23:24, first test run: ULTRAKILL was started with Doorstop 3 switches, did not load BepInEx, and so ran
  **vanilla for about two minutes** before being closed. In that time the game rewrote its real
  `Preferences\Prefs.json` (still valid JSON, 137 keys; no pre-run copy existed to diff against). No file in `Saves\`
  changed. Since then: Doorstop 4 is required before launching, the 25 s watchdog exists, and `Run-Test.ps1` copies the
  real save folders to `backups\saves\<run>\` before every run.

## Known issues

- Stage geometry is a snapshot taken when the run engages: moving platforms, doors and anything spawned later are not mirrored yet.
- RoR2's character-only invisible walls (CollideWithCharacterHullOnly layer) are not mirrored: V1 can walk past map edges RoR2 survivors cannot.
- Only golemplains has been tested; stages with unreadable collision meshes will exercise the GPU read-back path for the first time.
- Parry is not implemented: RoR2 attacks and projectiles cannot be parried yet; every hit arrives as plain damage.
- The `combat` scenario is not deterministic: the scripted punch sometimes misses a Lemurian spawned 1.5 m away, and the
  "own attack" check can be confused by regeneration or invincibility frames. The mechanics behind both passed in other runs.
- RoR2's passive regeneration is forwarded like any heal, so V1 slowly regenerates in ULTRAKILL (hard damage still caps it).
- A monster's stand-in is one box from its movement capsule (x1.15), not its real hurt boxes; big or oddly shaped bosses will be off.
- Overheal (ULTRAKILL HP above 100) is not shown on the RoR2 body.
- RoR2's health bar, skill icons and crosshair still show over ULTRAKILL's HUD (to be hidden in Phase 6/7).
- RoR2's game-over report throws `ArgumentOutOfRangeException` in `GameEndReportPanelController.SetDisplayData` for V1 (seen when a run ended in defeat); to fix in Phase 6.
- ULTRAKILL logs "loadout: The Object you want to instantiate is null" when building the all-gear loadout (weapons 3/3/3/3/3/4 per slot still arrive).
- Mouse sensitivity mapping (Rewired counts → ULTRAKILL look) is untested by a human.
- The colour/coverage join costs ~5 ms per frame on a worker thread at 720p; a candidate for optimisation in Phase 7.
- RoR2 settings changed while on the bridge profile are saved to the sandbox, not to the real `Config\config.cfg`.
- `Playlist.directory` and `CustomPaletteSelector` in ULTRAKILL still point at the real `Preferences\Playlists` and
  `Palettes` folders (Cyber Grind playlists, custom palettes); nothing in the bridge writes them, but they are not redirected.
