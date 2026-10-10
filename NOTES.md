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
| 5 Monsters and damage | damage both ways done 2026-10-10 (`combat` scenario; user confirmed in game). Parry implemented 2026-10-10 (`parry` scenario passes 2 of 4 runs; user played it and confirmed it works) |
| 6 Run loop | done 2026-10-10 (`runloop` scenario passes 45/45) |
| 7 Settings, performance, install, README | written 2026-10-10; the settings screen and `Install.ps1` have not been exercised by hand (see known issues) |

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
- Parry (`Parry.cs` on both sides). Projectiles: hostile `ProjectileController`s within 80 m are listed every frame
  (`PROJS`); UkBridge keeps a trigger sphere (radius 1.2 units) with a `ParryReceiver` (parryHeal) on layer 14 for each,
  so the Feedbacker's own parry code catches them; `PARRY p id` destroys the RoR2 projectile and returns 5 damage units
  to its owner (Stun1s). A projectile's hit on V1 is held 0.12 s so a catch that arrives just after impact still cancels it.
  Melee: a hit whose inflictor is the attacking body within 7 m is held 0.2 s; a punch landing on that monster in the
  window, or up to 0.25 s before the hit, cancels it, sends `PARRIED id` (ULTRAKILL plays its real parry) and returns
  5 units. Unparried hits land when the window closes.
- `tools\Play.ps1` starts RoR2 on the bridge profile for normal play.
- Run loop (Phase 6):
  - RoR2's Interact and Equipment actions are read from Rewired, latched, and pushed into the body's `InputBankTest`
    in FixedUpdate (what the silenced `PlayerCharacterMasterController` would do); the keys bound to them are not sent
    to ULTRAKILL. Aim for interaction is V1's view.
  - RoR2's health bar and skill icons are hidden and the body's crosshair is off while V1 is driven; restored on disengage.
  - Stage change: the new body re-engages (new origin at its feet, geometry re-exported, `GEO` replaces the old stage,
    `TP` on `GEODONE`); UkBridge answers every `TP` with `LANDED`. V1's health and weapons carry over: `RESPAWN` is
    only sent for a new run or after a death. With no body (loading, dead) one empty `IN` releases every key.
  - Death: ULTRAKILL's `dead` makes the body `Suicide()` with the NonLethal guard lifted (`V1Drive.Dying`); RoR2 then
    ends the run or revives as usual. Nothing in RoR2 can kill the body by itself.
- Phase 7: settings are BepInEx config entries shown through Risk of Options (soft dependency, added to the profile):
  UltrakillHeight, UltrakillFpsCap (both re-sent live as `SIZE`), MouseScale, DamageCoefficient, IncomingDamage,
  HideRoR2HealthAndSkills. `tools\Install.ps1` chains SDK check → Setup-Profiles → Install-UkDoorstop → Build;
  `tools\Play.ps1` launches. README.md is the user guide.
- Whiplash: each stand-in carries a trigger box on ULTRAKILL's layer 12 with its own kinematic Rigidbody (the hook only
  takes hold of layers 11/12; the head/body boxes on layer 10 are for weapons). `ENTS` carries a light flag: not a boss
  or champion and mass ≤ 250 kg (`Monsters.Light`). Light stand-ins are `EnemyType.Puppet`, which UkBridge adds to
  `HookArm.lightEnemies`, and have a root Rigidbody the hook drags; while `eid.hooked`, UkBridge stops following RoR2's
  position and sends `PULL id feet` every frame, RorBridge moves the real monster there (motor `SetPosition`) and
  re-stuns it every 0.25 s; `PULLEND` hands back the release velocity. Heavy stand-ins stay `Mannequin`: V1 flies to them.
  The hook's own 0.2 hit arrives through the normal damage path (hitter `hook`).
- All-gear ownership excludes `arm3` (the gold arm exists in code only): owning it made `FistControl.ResetFists` throw
  before it switched on the HUD's fist panels and applied the current arm. `GunSetter.CheckWeapon` is skipped for a
  variant with no prefab. With both, the loadout set-up runs to the end and the fist icon shows in ULTRAKILL's HUD.
- Conduction: the stand-in damage prefix now ends with `Bridge.Conduct` (after Ultracraft): what
  `EnemyIdentifier.DeliverDamage` does for a real enemy after a hit. An electric hit (railcannon, Jumpstart `zapper`,
  `zap`) on a stand-in with nails, magnets or water sets `beingZapped`, zaps its nails and calls ULTRAKILL's own
  `DelayedAfterShock`, whose `Zap` arcs to the other stand-ins as hitter `zap`; `hitterAttributes` is cleared after
  every hit. Water: RoR2 has no water volumes ULTRAKILL can use, so `ENTS` carries a wet flag per monster (a collider
  on RoR2's Water layer at ankle height) and `WET` says the same for V1; UkBridge keeps one never-activated `Water`
  component that wet stand-ins hold in `touchingWaters` and whose `isPlayerTouchingWater` is V1's flag.
- Coins conduct with no extra code: ULTRAKILL's `EnemyIdentifier.Zap` relays through every `Zappable` (coins) within
  30 units in line of sight, and the stand-ins use that same `Zap`. `TESTCOIN x y z` (tests only) spawns one of the
  revolver's own coins drifting upward at a point (a coin slower than 1 unit/s is deleted by ULTRAKILL).
- V1's weapons and arms are now built in separate try blocks: a failing weapon used to skip the arms, leaving the Whiplash unequipped.
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
| 2026-10-10 02:42 | coins (golemplains) | PASS (1 run, after one failed run whose test coin was deleted for standing still) | two Beetle Guards 24 m apart (46 ULTRAKILL units, beyond the 30-unit reach): railcannon into 22 nails shocked only the first; with a coin midway the second took `zap` |
| 2026-10-10 02:2x | conduction (golemplains) | every check passed in at least one of 3 runs, never all in the same run | Jumpstart cable: `zapper` on the first Beetle Guard, `zap` on the second (2 of 3 runs; the third never got the cable on). Electric railcannon into 212 stuck nails: `zap` on the second. Water (monsters forced wet, no nails): `zap` on the second. golemplains has 2 Water-layer colliders |
| 2026-10-10 01:3x | whiplash (golemplains) | PASS (1 run) | Lemurian (100 kg, light) dragged 14.2 m → 0.27 m from V1 with V1 not moving; Beetle Guard (300 kg, heavy) stayed put and V1 flew 16.1 m → 1.9 m; hook hit x0.2 arrived |
| 2026-10-10 01:1x | runloop, with Risk of Options and the Phase 7 options | PASS (45/45, 0 real files changed, 0 exceptions) | RiskOfOptions 2.8.6 loads on the current RoR2 build |
| 2026-10-10 00:59 | runloop (golemplains → blackbeach) | PASS (45/45, 0 real files changed, 0 exceptions logged) | item droplet picked up; BoostDamage x10 took the damage unit 36→72; chest opened with interact (515→490 gold); teleporter started and reached 5% in 5 s; stage change re-engaged in 1.3 s (215 colliders, 144,085 triangles), HP 70→73 kept; HURT 999 ended the run 2 s later; new run started at 100 HP |
| 2026-10-10 00:3x | parry (golemplains) | PASS 28/28 on 2 of 4 runs; the others missed the scripted fireball catch | melee parry returned 180 damage (5 units) and cost V1 nothing; unparried melee landed after 0.22 s; fireball caught and its owner killed |
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
- Parry windows (0.2 s melee, 0.12 s projectile, 5 units returned) are first guesses, untested by a human. Melee damage on V1 is delayed by the 0.2 s window.
- The scripted fireball catch in the `parry` scenario fails about half the time (timing); the parried projectile is removed and its owner damaged, it is not flung back as an ULTRAKILL projectile.
- The `combat` scenario is not deterministic: the scripted punch sometimes misses a Lemurian spawned 1.5 m away, and the
  "own attack" check can be confused by regeneration or invincibility frames. The mechanics behind both passed in other runs.
- RoR2's passive regeneration is forwarded like any heal, so V1 slowly regenerates in ULTRAKILL (hard damage still caps it).
- A monster's stand-in is one box from its movement capsule (x1.15), not its real hurt boxes; big or oddly shaped bosses will be off.
- Overheal (ULTRAKILL HP above 100) is not shown on the RoR2 body.
- The Whiplash pull starts when the hook key is released (ULTRAKILL's rule); a pulled monster ignores RoR2 collision while dragged. Flying monsters without a CharacterMotor are moved by transform and untested.
- The user's bridge config currently has DamageCoefficient = 1 and MouseScale = 2.1 (set through the in-game menu, so that menu works).
- Water conduction was only tested with monsters flagged wet by the test, not standing in real stage water; whether RoR2's Water-layer colliders sit where the visible water is has not been checked on any stage. V1 standing in water while shocking it (ULTRAKILL shocks V1 too) is untested.
- Coin conduction was tested with a coin spawned by the test hook, not one thrown by the Marksman; a thrown coin is the same prefab, but the throw itself was not scripted.
- Magnets (Attractor nailgun) as conductors are implemented by the same code path but untested.
- The `conduction` scenario's weapon switching is scripted with number keys and depends on which variant ULTRAKILL had out last; the Jumpstart step failed 1 run in 3.
- Phase 7 is unverified beyond "it loads and the regression passes": nobody has opened the Mod Options screen to see the
  sliders, changed a setting mid-run, or run `tools\Install.ps1` end to end on a clean checkout (its parts were run separately).
- No performance options beyond ULTRAKILL's resolution and frame cap (no low-end preset, no lock-step, no D3D11 shared texture);
  the colour/coverage join still costs ~5 ms a frame on a worker thread.
- Character select still shows the primitive placeholder; V1's own model from the local install is not attempted yet.
- RoR2's chat lines and tutorial hints draw over ULTRAKILL's weapon panel (bottom left).
- Key overlaps not resolved: RoR2's Equipment key (Q by default) is taken from ULTRAKILL (its "last weapon"), and its Interact key (E) from ULTRAKILL's "change variation"; RoR2 pings and sprint are not routed at all.
- Not tested: revives (Dio's Best Friend), the teleporter boss fight to completion, the bazaar, void fields, Mithrix, and any stage other than golemplains and blackbeach.
- RoR2 instant-kill effects cannot kill V1 (every hit is NonLethal on the RoR2 side); out-of-bounds zones do not move V1.
- Mouse sensitivity mapping (Rewired counts → ULTRAKILL look) is untested by a human.
- The colour/coverage join costs ~5 ms per frame on a worker thread at 720p; a candidate for optimisation in Phase 7.
- RoR2 settings changed while on the bridge profile are saved to the sandbox, not to the real `Config\config.cfg`.
- `Playlist.directory` and `CustomPaletteSelector` in ULTRAKILL still point at the real `Preferences\Playlists` and
  `Palettes` folders (Cyber Grind playlists, custom palettes); nothing in the bridge writes them, but they are not redirected.
