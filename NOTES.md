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
- V1's model in character select comes from the local ULTRAKILL at runtime (`UkBridge/ModelExport.cs` → `V1Model.cs`):
  once its level is ready UkBridge instantiates the platformer V1 (`PlayerTracker.platformerPlayerPrefab`, the source
  Ultracraft uses), lets its animator pose it for 0.6 s, bakes each SkinnedMeshRenderer, reads the textures back
  through a RenderTexture, and writes positions/normals/UVs/triangles (normalised to one unit tall, feet at the origin)
  plus PNGs to `%TEMP%\v1bridge_model.bin`; `MODEL <path>` has RorBridge build meshes with HGStandard materials
  (texture also as emission at 0.6) under the display prefab and any live display, 2.05 m tall, and hide the placeholder.
  The file is copied to `profiles\ror2\cache\v1model.bin` (git-ignored, never shipped) and loaded at start-up on later
  sessions, so the model shows before ULTRAKILL has started. The placeholder remains for a first-ever visit.
- Oiled monsters: UkBridge checks every 0.25 s how much fuel each stand-in's `flammables` hold and sends
  `OILED id 0-10` on change (after Ultracraft); RorBridge puts a `TemporaryOverlayInstance` with RoR2's own goo material
  (`CharacterModel.TankerAccelerantGoo`, else `clayGooMaterial`) on the monster's model until the amount is 0. Looks only:
  no RoR2 debuff. Gasoline on the ground is not drawn (ULTRAKILL paints it on the stand-in stage, which is discarded).
  `TESTOIL id` (tests) calls `AddFlammable(1)` on a stand-in.
- Sound: ULTRAKILL plays through its own process. It was silent because the bridge's first boot ran the first-time
  intro on the new sandbox save, which writes `sfxVolume = 0`, `musicVolume = 0`, `allVolume = 1` to the preferences and
  fades the mixers to -80 dB until its sound set-up is completed, which the bridge skips. `Level.ApplyVolumes` now
  reads the three volumes from the real `Preferences\Prefs.json` (read only) every time the level is prepared, writes
  them to the sandbox prefs, sets `AudioListener.volume` and re-applies the SFX and music mixers. A real SFX or master
  volume of 0 is treated as 1 so the bridge is never mute.
- Blast knockback: ULTRAKILL's explosions call `DeliverDamage` with a force (50000 x push multiplier, lifted off the
  ground) even when the damage is 0, as a rocket landing beside an enemy does. The stand-in prefix now sends
  `KNOCK id dir strength` for any explosion hit with a force (once per stand-in per 0.3 s: a blast touches every box and
  is several explosions in one); RorBridge applies `HealthComponent.TakeDamageForce(dir x 1600 x strength, alwaysApply,
  disableAirControl)`, i.e. 16 m/s for a 100 kg monster and less in proportion to mass. `TESTBLAST x y z` (tests) spawns
  ULTRAKILL's stock explosion with damage 0.
- Jump pads: a Harmony postfix on RoR2's `JumpVolume.OnTriggerStay` sees V1's body in a pad (the pad sets the velocity
  of the body's motor, which is off while ULTRAKILL moves V1) and sends `LAUNCH v g` once per 0.4 s: the pad's velocity
  in ULTRAKILL units and RoR2's gravity in the same units. UkBridge throws V1 the way ULTRAKILL's own `JumpPad` does
  (`LaunchFromPoint(self, 0)`, out of slide/dash, then `rb.velocity`), with every speed scaled by
  sqrt(ULTRAKILL gravity / RoR2 gravity) so the path, and so the landing spot, is the pad's.
- The Sandbox's Spawner Arm (weapon slot 6) is never equipped: `SummonSandboxArm.Enable` is skipped. Its on-screen
  "Spawner Arm in slot 6" hint goes with it.
- Out of bounds: a Harmony postfix on `TeleportHelper.TeleportBody(TeleportBodyArgs)` forwards any RoR2 teleport of
  V1's body (the stage's OutOfBounds `MapZone` uses it, with `Run.FindSafeTeleportPosition`) as a `TP` to ULTRAKILL,
  and the body is held where RoR2 put it until V1 is within 4 m of it or 1.5 s pass, so it is not dragged back out and
  teleported again. The older "30 m below the stage's bounds → back to the start" fallback is still there.
- Ping: RoR2's Ping action is read from Rewired like Interact and Equipment; a press (not a hold) calls the silenced
  controller's own `PingerController.AttemptPing` with a ray from the eye and direction of the ULTRAKILL frame on screen
  (the rig has an override camera, so RoR2 does not re-aim the ray). Mouse buttons bound to RoR2's three actions are now
  masked out of `IN` as their keys already were (`InputCapture.keptButtons`).
- Nothing on V1's hidden body is drawn: RoR2 hides an item display's listed renderers itself when the model is invisible
  (also for items picked up later), but not what a display carries outside that list (particles, decals, a few meshes),
  nor the body's own footstep dust and jet flames. `V1Drive.HideBodyParts` switches off every enabled renderer under the
  model every 0.1 s and puts them back on disengage. Effects that are not under the model (wards, bubbles and auras
  round the body, followers, drones) are RoR2's world and stay.
- Unreadable collision meshes (Siphoned Forest is the first stage met that has them: 25 of its meshes, the terrain
  among them): `GraphicsBuffer.GetData` on a mesh's vertex buffer returns all zeros on this Direct3D 11 set-up,
  `AsyncGPUReadback.Request(GraphicsBuffer)` reports an error, and setting `vertexBufferTarget` to Raw only swaps in
  an empty buffer. `D3D11Read` (in `StageGeometry.cs`) reads the native buffer instead: `GetNativeVertexBufferPtr` /
  `GetNativeIndexBufferPtr` → `GetDevice` → a staging buffer → `CopyResource` → `Map`, through the COM function tables,
  with the device's context guarded (`ID3D10Multithread.SetMultithreadProtected`, once, then 60 ms for the render
  thread to clear) and every copy inside `Enter`/`Leave`. Every GPU read is checked against `mesh.bounds`; a mesh
  that does not fit is skipped and named in the log (`skipped:` / `problem:`), so a stage with no floor says why.
- Close-up fading: RoR2 dithers monsters (`CharacterModel`) and props (`DitherModel`) away as its camera comes within
  4 m, to nothing at 1 m, so they do not hide the survivor. Both ask the rig's `enableFading`; it is switched off on
  V1's rig while the bridge has it and put back on disengage. Other survivors are untouched.
- The Newt: `ENTS` lists hostile teams only, and the Bazaar's shopkeeper (`ShopkeeperBody`) is on the Neutral team, so
  he had no stand-in and V1's weapons passed through him. `Monsters.IsNewt` lets him through the filter; every other
  None/Neutral body (pots, the Trial Gong, other bystanders) is still left out. He counts as heavy for the Whiplash.
- Worms (`MagmaWormBody`, `ElectricWormBody`; `Monsters.Segmented`) are given to ULTRAKILL by their own hurt boxes: after
  `ENTS`, `PARTS id cx,cy,cz,sx,sy,sz;...` lists the world bounds of every enabled `HurtBox` collider each frame, and
  UkBridge keeps one unit-cube box per entry on the stand-in (layer 10 "Body" with an `EnemyIdentifierIdentifier`, a
  depth-only occluder, and a layer-12 trigger for the Whiplash), moved and scaled each frame; the stand-in's one
  head/body box, whip box and occluder are switched off. No part is a head: RoR2 gives the worm no weak point. The
  boxes are axis-aligned bounds, so slightly generous on a turned segment. Protocol 8. This is the first use of the
  "mirror the real hurt boxes" plan; every other monster is still one box from its capsule.
- V1's RoR2 health: config `V1Health` (Balance, default 90, slider 10-500 in Mod Options) is the body's `baseMaxHealth`,
  with `levelMaxHealth` 30% of it as RoR2's survivors have; `V1Survivor.ApplyHealth` sets the prefab and any live V1
  when the setting changes. `baseRegen` and `levelRegen` are 0: V1 no longer regenerates by itself (regeneration from
  items still reaches it as healing). RoR2 heals a body by whatever its maximum health grew (`RecalculateStats`), and
  that heal is forwarded to ULTRAKILL like any other: the slider recalculates with forwarding muted
  (`HealsReachV1.Muted`), but a level-up or a health item still heals V1 this way (see known issues).
- Shield (`V1HealthLink.Pool` = full health + full shield; `V1Drive.MirrorHealth`): ULTRAKILL's 100 HP stands for the
  body's health and shield together, and a hit costs what the two lost as a share of that pool (both are put back on
  the body; ULTRAKILL's answer decides). Which part of what is left is shield is kept in `V1Drive` (`shieldPts`):
  any loss of HP comes off the shield first; healing fills health only, whether ULTRAKILL's (blood, parries) or RoR2's;
  the shield fills only as RoR2 fills it (out of danger 7 s, then half of it a second), which is seen as growth of
  `hc.shield` since it was last written and passed on as `HEAL` with a matching credit. So that ULTRAKILL's own healing
  cannot fill the shield's share, RoR2 sends `CAP hp` (999: none) = full health + the shield as it stands, and UkBridge
  holds V1's HP at or under it. ULTRAKILL counts whole HP, so up to one HP of shield can come back with a heal. The
  split is re-made in proportion on a new stage. Protocol 9. History: first RoR2's shield soaked hits up unseen (with
  Transcendence V1 took nothing until the hit that killed it); then, for a few hours on 2026-10-11, shield was simply
  more health that blood could heal; the owner asked for ULTRAKILL's healing not to reach the shield. Barrier is
  untouched: RoR2 spends it first and it decays as usual.
- Growth is not healing (`GrowthIsNotHealing`): RoR2 tops a body up by whatever its maximum health grew, inside
  `CharacterBody.RecalculateStats`; heal forwarding is muted for V1's body for the length of that call, so levels,
  health items and a lifted curse no longer heal V1 in ULTRAKILL.
- Sharpshooter against projectiles: ULTRAKILL's `RevolverBeam` with `canHitProjectiles` (the Sharpshooter's ricochet shot)
  looks for a `Projectile` component on layer 14, sets off `superExplosion` there (the stock explosion for further ones
  in the same shot), destroys the object and flashes. Each RoR2 projectile's copy now carries ULTRAKILL's own
  `Projectile`, switched off and inert (`StandInProjectile` skips its `Awake` and `OnTriggerEnter`; `speed` 1 so the
  beam accepts it; `unparryable`/`undeflectable` so the fist goes on to the `ParryReceiver`; `ignoreExplosions`).
  `RorShot.OnDestroy` tells the bridge when ULTRAKILL destroyed a copy that the bridge did not remove itself:
  `PARRY b id`, and RorBridge destroys the RoR2 projectile (no return damage: the explosion is ULTRAKILL's and hurts
  the stand-ins near it by the usual path). A blown id is not rebuilt for 2 s. Protocol 10. `STATE` now carries
  `weapon=<name>/revolver<variant>` and `blown=`; `TESTARM n` (tests) picks the arm.
- Coins against hitscan (`Coins.cs`; `UkBridge/Parry.cs` `SendCoins`/`CoinHit`): UkBridge lists V1's coins that have not
  been shot yet every frame there are any (`COINS id,x,y,z;...`, from `CoinTracker.revolverCoinsList`). A prefix on
  RoR2's `BulletAttack.BulletHitsFromRaycast(Ray, ...)` looks, for a shot whose owner is not on V1's team, for the
  nearest coin within 0.75 m + the bullet's radius of the ray, with neither the stage nor V1 in front of it; the
  bullet's `maxDistance` is cut to the coin for that one bullet (put back in a `FireSingle` postfix) and `COINHIT id`
  has UkBridge call ULTRAKILL's own `Coin.DelayedReflectRevolver(position)`: the coin sends the shot on as it sends on
  a shot of V1's (the ricoshot: through any other coins, then into the nearest monster, a headshot where there is a
  head, by the usual `DMG` path). The first version called `Coin.DelayedEnemyReflect()`, ULTRAKILL's rule for a coin
  shot by one of its own enemies (the coin fires at V1 0.5 s later unless V1 shoots it first); the owner asked for the
  ricoshot instead, 2026-10-11. A coin is not listed in its first 0.1 s (`checkingSpeed` false): until then ULTRAKILL's
  coin takes no shot, and the bullet would be swallowed for nothing. The two patches are applied by hand
  (`Coins.Apply`) so a rename cannot take `PatchAll` down.
  Only `BulletAttack` is covered: monsters whose "laser" is a blast or a projectile are not hitscan to RoR2. Protocol 11.
- No effects on V1's body (`NoBodyEffects` in `Coins.cs`, `V1Drive.HideBodyParts`): a prefix on both
  `CharacterBody.UpdateSingleTemporaryVisualEffect` overloads turns `active` off for V1's body index, so RoR2 never
  hangs its buff and item visuals on it (shield and armor bubbles, glows, debuff marks), for every player who has the
  mod, not only the one playing V1. The 0.1 s sweep now also switches off renderers under the body object itself and
  under every `NetworkedBodyAttachment` on it (item auras, range rings, tethers), and their lights; that part is
  local to the V1 player. Not touched: zones an item puts in the world (healing and buff wards, a Warbanner), and
  anything on allies and drones.
- Mithrix's exit orbs (`MoonExitArenaOrb`: `MapZone` KickOutPlayers/TriggerEnter with an explicit destination, a 9 m
  trigger sphere on CollideWithCharacterHullOnly) need nothing of their own: they go through
  `TeleportHelper.TeleportBody`, which has moved V1 since the out-of-bounds work of 2026-10-10. The owner's report
  that V1 could not use them most likely predates that build; the `moonorbs` scenario now covers them.
- The last HP (`V1HealthLink.OnTakeDamageServer`): hits on V1 are NonLethal on the RoR2 side, so RoR2 stops the body's
  health at 1 and the rest of an overkilling hit was never measured. With the body's health under 100 (90 since
  `V1Health`), ULTRAKILL's 1 HP is a body health of 0.9, shown as 1: at the floor already, every hit measured 0, no
  `HURT` went out, and V1 could not die to RoR2 damage (with 110 health it still could, just: 1 HP was 1.1). Where the
  body's health ends a hit at the floor, the hit now counts for `DamageReport.damageDealt` (RoR2's figure after
  armor and one-shot protection) less what the barrier absorbed, if that is more than what was measured.
- Install without .NET (2026-10-12, on a second PC): the .NET SDK installer failed there (Windows Installer could not find
  one of the bundle's own packages and rolled back), and the old `Install.ps1` threw before its winget step on a PC with
  no `dotnet` at all. Now: `Install.ps1` copies `prebuilt\RorBridge.dll` and `prebuilt\UkBridge.dll` into the profiles
  when both are there (no compiling, no .NET); otherwise, or with `-Build`, it builds, using an installed SDK or, when
  there is none, Microsoft's SDK ZIP unpacked into `.tools\dotnet` (`Install-LocalSdk` in `Common.ps1`: latest 8.0 from
  the release list, SHA-512 checked, no installer, no administrator rights). `Build.ps1 -Pack` fills `prebuilt\`.
  `Install.cmd` and `Play.cmd` are double-click launchers that keep the window open. `prebuilt\` holds only the mod's
  own code; whether it is committed or attached to a release is the owner's call, and it has to be re-packed whenever
  the source changes (both DLLs together: they share the protocol number).
- The placeholder figure in character select (first-ever visit, before a model has come from ULTRAKILL) was rebuilt
  2026-10-12: a jointed machine of 40-odd primitives with blue wing blades and a lit eye. It also stood with its back
  to the camera (its `Model` child was turned 180 degrees; the display's front is +z, as for the real model): fixed.
  Checked with the `model` scenario and the cache set aside (`ror2-lobby-before.png`).
- What the other players see (`V1Figure.cs`, 2026-10-12; the owner's choice: the mod's own figure for everyone, whether
  or not they own ULTRAKILL, and nothing of ULTRAKILL's sent to anyone: not the model, not its skeleton or animations).
  `V1Figure` sits on the body prefab's model: it builds the primitive figure (`V1Survivor.BuildFigure`, shared with the
  character-select placeholder) as a child, 1.9 m tall, shown while the body is alive and is not a local player's own.
  The Commando's meshes are kept off by a postfix on `CharacterModel.UpdateMaterials` (RoR2 sets each base renderer's
  `enabled` there, also after a skin is applied), patched by hand, for models carrying a `V1Figure`. So that the
  figure turns, `V1Drive.LateTick` writes V1's yaw to `CharacterDirection.yaw`: `CharacterNetworkTransform`'s snapshot
  sends that (not the transform's rotation) as the body's facing. The figure's limbs are moved by `V1FigureMotion`
  (2026-10-13), code of the mod's own: the parts hang on named joints (`BuildFigure`), and each game works the pose out
  from how the figure is carried about (speed over the ground: stride, arm swing, lean, wings swept back; no ground
  within 0.7 m under it: a falling pose; still: an idle sway), so nothing extra is networked and no ULTRAKILL
  animation is involved. Slides, dashes and punches are not told apart: a slide looks like a fast run. Item displays are
  untouched: they still hang on the hidden Commando skeleton, so friends may see items floating about the figure.
  `Install.ps1` no longer needs ULTRAKILL when `prebuilt\` is there: it sets up Risk of Rain 2's side only, for
  joining a V1 host. With no .decomp on this PC, RoR2's code was read as IL with the Mono.Cecil in the profile.
- V1 in someone else's game, stage 1 (`V1Net.cs`, 2026-10-13; the owner asked for V1 without hosting). Until now
  everything V1 did to RoR2's world ran in the V1 player's own game, which had to be the host. A joining player's
  game may not deal damage or write health, so those are now the host's to carry out, on lines of text over RoR2's
  own connection (UNET message types 27121 to the host, 27122 to the joining game, reliable channel; registered on
  `NetworkManagerSystem.onStartServerGlobal` / `onStartClientGlobal`):
  - joining V1 -> host: `ENG 0|1` (my bridge has hold of my body), `HP hp dead` (on change and every 0.5 s),
    `DMG <monster net id> mult head explosion hitter x y z` (RoR2 coordinates).
  - host -> joining V1: `HURT`, `HEAL`, `CAP` (passed straight on to its ULTRAKILL), `KILL <monster net id>`, `TP x y z`.
  On the host the joining V1's body carries the state: `V1HealthLink.Remote` (the player's connection, set by `ENG`),
  its own `HealthMirror` (the health/shield/CAP logic, moved out of `V1Drive` into `HealthMirror.cs` so there is one
  per V1), `LateUpdate` writing the reported HP onto the body and killing it when its ULTRAKILL says dead.
  `Monsters.Hit` is the one place a hit of V1's becomes RoR2 damage, for the host's own V1 and for `DMG` lines (the
  sender must be playing V1, the target must be on another team, the multiplier is clamped). What is the player's
  own game's as before: picture, input, view, stage collision, movement (the body has client authority, so its
  position and `CharacterDirection.yaw` reach the host through RoR2's own transform sync), interact, equipment, ping.
  (Parrying came in stage 2 and the Whiplash, blasts and coins in stage 3: see below.) Untested for a joining V1: stage changes, shields, revives, jump pads,
  out-of-bounds teleports (the `TP` line), more than one V1 at once, a host who is also V1.
  `UkLink` now looks for UkBridge only once this game has asked for an ULTRAKILL (`UkLink.Wanted`, set by
  `UkLauncher.Launch`): two RoR2 on one PC no longer race for the one listener.
- V1 in someone else's game, stage 2: parrying (2026-10-13). The hits waiting on a parry were one static list for the
  one V1; they are now a `ParryState` per V1 (`Parry.cs`): the host's own, and one on each joining V1's
  `V1HealthLink` (`RemoteParry`), whose windows are 0.15 s longer (`Parry.JoinedSlack`) for the trip between the two
  games. The joining game lists the projectiles to its own ULTRAKILL as a host does; a catch or a Sharpshooter blast
  there goes to the host as `PARRY p|b <projectile net id>` (0: one that had just landed), and the host removes the
  projectile and deals the return (`Parry.Caught`, shared with the host's own V1). A punch arrives as a `DMG ... punch`
  line: if it meets a held melee hit on that V1, or comes just before one, the host cancels the hit, deals the
  return and sends `PARRIED <monster net id>`, on which the joining game's ULTRAKILL plays its parry. So a joining
  V1's melee hits land about 0.35 s after the host works them out (0.2 for the host's own V1).
- V1 in someone else's game, stage 3 (2026-10-13): what moves a monster, and the coins. The joining game asks the
  host: `PULL <monster> x y z` every frame its Whiplash drags a light monster (unreliable channel: said again next
  frame) and `PULLEND <monster> vx vy vz`, `KNOCK <monster> dx dy dz strength` for a blast, all in RoR2 coordinates;
  the host carries them out with the same `Monsters.Pull` / `PullEnd` / `Knock` its own V1 uses (only on a monster of
  another team; a pull only on one that counts as light, and only within 90 m of that V1). Coins: the joining game
  forwards its ULTRAKILL's `COINS` list in RoR2 coordinates every frame there are any (unreliable; once more,
  reliably, when the last is gone); the host keeps it on that V1's link (`CoinList`, `V1HealthLink.RemoteCoins`) and
  `Coins.BeforeCast` now tries the host's own V1's coins and then each joining V1's; a shot taken goes back as
  `COINHIT <coin>`, which the joining game hands to its ULTRAKILL, whose ricoshot then arrives as an ordinary `DMG`.
  Still only in the joining player's own game: the oil overlay on a soaked monster.
- What V1's weapons do, shown to the other players (`UkBridge/Fx.cs`, `V1Fx.cs`, 2026-10-13; the owner asked for
  V1's attacks not to be invisible to friends, readable rather than pretty). ULTRAKILL's picture of its shots is the
  V1 player's alone, so UkBridge now also says in plain facts what happened, while RoR2 asks for it (`FXON 1`: more
  than one player in the game): `FX b <from> <to> <0|1>` once for each hitscan shot (the two ends of each
  `RevolverBeam`'s line; 1 = railcannon), `FX e <x y z> <size>` once for each `Explosion` of V1's, and
  `FXP id,kind,x,y,z;...` every frame for what is in the air (g rockets and grenades, n nails, s saws, p pellets,
  c coins, h the Whiplash's hook; at most 40). Found by looking the objects up each frame (the stripped Sandbox holds
  little else), so no ULTRAKILL method is patched for it. Protocol 12. The V1 player's game turns them into RoR2
  coordinates, adds its body's network id and passes them on (`V1Net.ShareFx`): a host to every joining game, a
  joining V1 to the host, which draws them and passes them to the other joining games. Each other game draws, from
  primitives with the figure's emissive material: a thin box along a shot that thins away in 0.3 s (0.5 s and
  wider, blue, for the railcannon), a ball that swells to the blast's size and is gone in 0.4 s, a small lit ball
  for each thing in the air, eased towards where it was last said to be, and a line from V1 to its hook. Never
  drawn in the V1 player's own game. Nothing of ULTRAKILL's art or sound is involved. Not shown: punches, the
  shotgun's muzzle blast as such (its pellets are), fire, the oil on a monster, and there is no sound.
- The Whiplash's pull was wrong when several pulls landed in one frame (2026-10-13, found by the two-game test: the
  dragged monster flew out of the stage and was killed by its bounds, 2 runs in 3). `Monsters.Pull` moved the motor
  by the distance from the body's core to the target; the core only catches up when the motor next simulates, so a
  second pull in the same frame added the whole distance again. A host's own V1 sends one a frame and never showed
  it; a joining V1's arrive in bunches. It now measures the motor-to-core offset once a frame and sets the
  position outright.
- Two games on one PC (`tools\Run-MpTest.ps1`, scenarios `mphost` / `mpjoin`): the host is started through Steam, the
  joining game directly from its exe with `SteamAppId` in its environment (Steam starts a game once), both on the
  bridge profile; `connect localhost:7777` joins a `host 1` game. They keep step through marker files. Both are the
  same Steam user, and a host rejects a second connection from an account it has (`ServerAuthManager`), so the
  joining TEST game gives another id (`SecondPlayer` in `TestMode.cs`, patched only in the `mpjoin` scenario). The
  profile remembers the last survivor: both scenarios set it to Commando first, or a remembered V1 starts an
  ULTRAKILL in the lobby. The joining game is started only once the host is in its lobby: two games loading the one
  profile at the same moment collide on its config file, and the second then runs without the plugin. A test monster with its AI off still swings: the target's damage is set to 0.
- V1's weapons and arms are now built in separate try blocks: a failing weapon used to skip the arms, leaving the Whiplash unequipped.
- Frame transport starts as Ultracraft's AsyncGPUReadback → mmap; D3D11 shared texture only if latency demands it.
- Gameplay defaults: see "Design defaults chosen" in the design doc.

- Publishing prep (2026-10-10): MIT licence (`LICENSE`), Ultracraft's notice in `THIRD-PARTY-NOTICES.md`. Every commit
  is authored with the owner's GitHub no-reply address (repo-local git config uses it too). Published to
  github.com/Mrmanhasnoplan/UltraRain---Risk-of-Rain-2-x-Ultrakill-Passthrough-mod-V1-on-Petrichor-V- (branch main).

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
| 2026-10-13 | fx (golemplains), and mphost + mpjoin with the effects and the pull fix | fx PASS (2 runs, 21/21); two games PASS (final version: host 50/50, joining V1 48/48); whiplash 27/27 afterwards; 0 real files changed, 0 exceptions throughout | fx, one game made to draw its own V1's effects: a revolver shot at the sky = 1 line, a blast = 2 balls (the stock explosion is two), a coin, the hook and a rocket each got a light (kinds c, h, g); about 220 facts from ULTRAKILL in the run. Two games: the host drew 18 shots, 2 blasts, the coin and the hook of the joining V1, whose own game drew none. With the pull fix the dragged Lemurian ended alive 1.1 m from the joining V1 (before: dead out of bounds in 2 runs of 3); host mode 14.2 m -> 0.03 m, alive. Seen by eye only in the fx screenshots (first person, so lines are foreshortened); the host's camera sat over its drop pod and did not have a shot in frame. NOT run: a third player (the host passing a joining V1's effects on), nails, saws and pellets (the kinds exist in the code only), how it reads in real play. An earlier two-game run had the joining V1 firing rockets because the fx test had left the launcher out: both tests now pick the revolver |
| 2026-10-13 | mphost + mpjoin with the Whiplash, blast and coin steps (golemplains) | PASS (1 run: host 48/48, joining V1 47/47, 0 real files changed, 0 exceptions); as regressions whiplash 27/27, blast 22/22, coinshot 25/25 | joining V1: its Whiplash dragged a Lemurian 14.3 m -> 4.1 m from it in the host's game (34 pull updates; 1.4 m from V1 in its own game); a zero-damage blast beside it threw it 7.0 m up and 9.4 m along on the host, unhurt; a TESTCOIN coin was known to the host, a monster's BulletAttack fired through where it hung was taken by it (no HURT), and the ricoshot came back as `revolver` x4 to the head = 57.6 on the shooter, V1 90 -> 90. The shot was aimed through the coin, not at V1: that it would otherwise have hit V1 was not shown in this run. One run before it never joined: the two games collided on the profile's config file (fixed in the runner) |
| 2026-10-13 | mphost + mpjoin with the parry steps (golemplains) | PASS (1 run: host 37/37, joining V1 37/37, 0 real files changed, 0 exceptions); as regressions sharpshooter 21/21, parry 26/28 twice (see right) | joining V1: a punch on a Beetle Guard followed at once by that monster's 25% hit = 1 parry, no HURT, the monster lost 72 (5 units), ULTRAKILL played the parry and healed 80 -> 100; an unparried 10% melee hit was held, then landed 0.45 s on (100 -> 90); a fireball from 30 m was listed to the joining ULTRAKILL (1 stand-in), and a catch SAID to the host by the test (the same line a real catch sends, not ULTRAKILL's fist) removed it on the host 1.1 s later, no HURT, its owner lost 72. Single-player `parry`: melee parts pass now that test monsters with their AI off do no damage (their own swings took parries and killed V1 at random); the scripted fireball catch failed in both runs (ULTRAKILL counted the catches, they reached RoR2 after the 0.12 s window: the known timing flake, here with ULTRAKILL capped at 60 fps; not shown to be older than this change by a run of the old code) |
| 2026-10-13 | mphost + mpjoin: V1 joining a game hosted by a Commando, two games on one PC (golemplains) | PASS (1 run of the final version: host 28/28, joining V1 30/30, 0 real files changed, 0 exceptions); afterwards as regressions: maxhealth 29/29, runloop 45/45, whiplash 27/27 | joined over localhost in 1.4 s; V1 landed on the host's stage (142 colliders), feet 4 mm above the ground; the host saw the figure; V1's revolver x1.25 = 15 on the host's Beetle Guard (480 -> 450), killed in 31 hits, the joining player paid (gold 15 -> 21) and its ULTRAKILL told of the kill; a 30% hit on its body by the host = HURT 30, ULTRAKILL HP 100 -> 70, body 91 of 130; a 10% heal = 70 -> 80; V1 walked 12.9 m and its body went with it on the host; HURT 999 in its ULTRAKILL killed its body on the host, the host's run went on. An earlier run failed because the target's own swings (2 x 52.8) killed V1: which is the path working. Same Steam account on both sides (test-only id on the joining side), same PC: a real second player over the internet was NOT there. Not run: see the Decisions entry |
| 2026-10-13 | figure, with the limb motion (golemplains) | PASS (2 runs, 36/36, 0 real files changed, 0 exceptions) | standing: swing 0; carried along the ground at 6 m/s: swing 0.88, legs up to 73 degrees apart, leaning into the run in the screenshot; put down: swing 0, no falling pose. The falling pose itself and a real player's movement (dashes, slides, jumps) were not run |
| 2026-10-12 | figure (golemplains) | PASS (1 run of the final version, 32/32, 0 real files changed, 0 exceptions); items re-run afterwards: PASS 26/26 | a V1 body that is nobody's, looked at as Commando: 36 parts drawn, feet 1 cm above the body's, facing as the body was turned, 0 of the Commando's 3 renderers on. As V1: own figure not shown, 0 renderers on under the model, the body's facing equal to V1's view after a turn. One game only: NOT run with a second player, so that the figure and its facing arrive on a friend's screen is untested; so is the install on a PC without ULTRAKILL |
| 2026-10-12 | second PC (Windows 11, Intel Arc 130V, RoR2BepInExPack 1.46.0, R2API ContentManagement 1.1.0), built with the SDK ZIP: handshake, picture (golemplains), geometry (snowyforest) | PASS: handshake 16/16, picture 32/32, geometry 29/29, 0 real files changed, 0 exceptions. The first handshake run reported 1 real file changed: the RoR2 profile, replaced by Steam Cloud at launch with a newer one from the other PC (its timestamp was two days before the run; a re-run changed nothing) | picture: 1280x720, 55 fps shown, frame age 28 ms avg / 44 ms p95, input to frame 41 ms, merge 8.4 ms. Siphoned Forest: 355 colliders, 327,073 triangles, 25 meshes straight from Direct3D (first run of D3D11Read on a second graphics card), 1 skipped (SF_FirepitRing), ground 81/81. Not run here: every other scenario. The install from `prebuilt\` was run only on this PC, where the profiles already existed |
| 2026-10-11 | lasthp (golemplains) | PASS (1 run, 19/19, 0 real files changed, 0 exceptions) | at 10 HP a 5.4-damage hit cost 6 (10 -> 4); at 1 HP (body health 1 of 90) an 18-damage hit went out as HURT 20, V1 died and the run ended 2 s later. Before the fix that last hit sent nothing (the owner's report: V1 could not fall below 1) |
| 2026-10-11 | moonorbs (moon2, Commencement) | PASS (1 run, 21/21, 0 real files changed) with no code change | the escape objects switched on by hand as beating Mithrix does: 5 exit orbs; V1 put into one (9.2 m across) was taken 0.25 s later, stood 9.8 m from the orb's destination 670 m below, 2 mm above the ground, body with it, one teleport, HP 100 -> 100. Commencement itself: 1,491 colliders, 662,271 triangles, exported in 482 ms. Not run: the fight, the orbs as it really leaves them, the ship. The run logs 1 RoR2 exception from `ModelSkinController.ApplySkinAsync` (also seen once in an items run; not looked into) |
| 2026-10-11 | items, with the body-effect checks (golemplains) | PASS (1 run, 26/26, 0 real files changed) | every non-lunar item: 0 buff or item visuals on V1 (33 refused), 10 things attached to the body (Focus Crystal's ring, the Unstable Tesla Coil's aura, Headstompers...) with 0 renderers on, 0 on under the body; the screenshot no longer has the bubbles round the view. Still there: ground zones, allies and drones with their own items |
| 2026-10-11 | coinshot, ricoshot version (golemplains) | PASS (1 run of the final version, 25/25, 0 real files changed, 0 exceptions) | with a coin 6 m in front of V1 on the line: taken by the coin, no HURT, and the Beetle Guard that fired (the nearest monster) took `revolver` x4 to the head = 48, V1 100 -> 100, coin gone. The run before it failed: the shot was fired inside the coin's first 0.1 s, when ULTRAKILL's coin takes nothing; such coins are no longer listed. Not run: a thrown coin, several coins, a real monster's hitscan |
| 2026-10-11 | coinshot (golemplains) | PASS (1 run, 23/23, 0 real files changed, 0 exceptions) | a monster's BulletAttack at V1's head from 20 m: with no coin, HURT 20 (100 -> 80); with a coin (TESTCOIN) 6 m in front of V1 on the line: taken by the coin (ULTRAKILL counts 1), no HURT, HP 100 -> 100; two seconds on the coin was gone and V1 still at 100: the coin's own shot at V1 did no damage this once, not looked into. Not run: a thrown coin, several coins, V1 shooting the flashing coin, a real monster's hitscan (Lesser Wisp, Clay Templar...) |
| 2026-10-11 | sharpshooter (golemplains) | PASS (1 run, 21/21, 0 real files changed, 0 exceptions) | revolver turned to the Sharpshooter in 2 presses (`Alternative_Revolver_Twirl/revolver2alt`); alternate fire held 1.3 s, a fireball fired at V1 from 26 m along the aim line, shot released 0.12 s later: 1 blown up on the first shot (ULTRAKILL counts 1), V1 100 -> 100 HP with no hit, the Beetle Guard behind it lost 30. Parry re-run afterwards: 2 runs failed with the Knuckleblaster out (it does not parry; punches came through as x2.5), a third with `TESTARM 0` parried a melee hit and caught the fireball (its one failed check: the Beetle Guard's own swing took the parry before the test's scripted hit arrived) |
| 2026-10-11 | maxhealth, with the shield layer (golemplains) | PASS (1 run of the final version, 28/28, 0 real files changed, 0 exceptions) | at level 10: Personal Shield Generator (333 health + 26.6 shield): a 66.6 hit = 19 HP with the shield gone first; 60 HP of healing given in ULTRAKILL stopped at 93 (the limit: full health), shield 1.5; left alone 7 s: 100, shield full. Transcendence (1 + 499.5): a 66.6 hit = 13 HP; 50 HP of healing in ULTRAKILL: 87 -> 87; left alone 5.4 s: 100; a hit of 80% = 80 HP, alive. "Healing in ULTRAKILL" was given with the HEAL message (ULTRAKILL's GetHealth), not by real blood or a parry. One earlier run began at 61 HP: four real hits from the stage's monsters before the test cleared them |
| 2026-10-11 | maxhealth (golemplains) | before the fixes: FAIL 3 checks (losing Shaped Glass healed V1 60 -> 86; with Transcendence an 18-damage hit and a 109-damage hit both cost V1 nothing, only RoR2's shield). After: PASS (1 run, 23/23, 0 real files changed, 0 exceptions); health re-run afterwards: PASS 22/22 | 18 damage on 90 health = 20 HP; Shaped Glass: max 45, HP unchanged on pickup, the same hit = 40 HP, removal 60 -> 60; level 1 -> 10 (max 90 -> 333): HP 60 -> 60; Transcendence at level 10 (1 health + 499.5 shield): pickup HP unchanged, a fifth-of-health hit = 13 HP (13.3 expected), a hit of 80% of the pool = 80 HP, not dead. Not run: Personal Shield Generator alone, barrier, a real curse (Eclipse 8, void fields) |
| 2026-10-11 | health (golemplains) | PASS (1 run of the final version, 22/22, 0 real files changed, 0 exceptions) | level 1: max health 90, regen 0; hurt to 60 HP and left alone 8 s: still 60, no RoR2 healing forwarded; body at 54 of 90; setting 200 mid-run gave max health 200 with ULTRAKILL HP still 60 (the first version of this healed V1 to 100: fixed), back to 90 gave 90; the config file was left at 90. The slider itself was not operated in the menu |
| 2026-10-11 | worm (golemplains) | PASS (1 run, 22/22, 0 real files changed, 0 exceptions); whiplash re-run afterwards as a regression: PASS 27/27 | Magma Worm: 23 hurt boxes on the monster, 23 listed, 23 boxes in ULTRAKILL; 3 of 3 shots aimed at the segment furthest from its head landed, the furthest 33.9 m from its core (segments reach 39.9 m; the old single box reached 7.6 m). Overloading Worm, explosions across several segments and the Whiplash on a worm not run |
| 2026-10-11 | newt (bazaar) | PASS (1 run, 21/21, 0 real files changed, 0 exceptions) | the Newt appears 18 s after a run started straight in the Bazaar; team Neutral, 500,000 health, mass 100,000; ULTRAKILL has his stand-in; a shotgun pellet x0.25 took 1.5 off him. His reaction (kicking V1 out of the Bazaar) was not provoked. The Bazaar itself: 227 colliders, V1 stands on it |
| 2026-10-11 | closeup (golemplains) | PASS (1 run, 24/24, 0 real files changed, 0 exceptions) | Beetle Guard with its nearest hurt box 0.41 m from the eye: fade 1, solid in the screenshot; chest 0.48 m from the eye: fade 1. Stage geometry that fades by its own shader, if any does, is not covered |
| 2026-10-11 | geometry (snowyforest, Siphoned Forest) | before: FAIL, 4 of 81 ground points, V1 fell through (all 23 GPU-read meshes came back as zeros). After: PASS 1 of 2 runs (30/30; 0 real files changed); the other run had the ground right too (81/81) and failed only the after-walk checks, V1 having spawned on a cliff top and walked off it | 353 colliders, 326,845 triangles, 25 meshes straight from Direct3D; export 489 ms, build in ULTRAKILL 412 ms; ground equal within 6 mm at 81/81 points; V1 walked 35 m, feet 5 mm above RoR2's ground. Spawn point differs per run |
| 2026-10-11 | items (golemplains) | PASS (1 run of the final version, 24/24, 0 real files changed) | 8 common items: 9 displays, RoR2 hid all of them itself. Every non-lunar item (191 given, 165 displays): 0 renderers left on, 0 followers; the bridge switched off 17 that RoR2 had left on (Plasma Shrimp's mesh, Tooth necklace decal, H3AD-5T, Chronic Expansion, Elusive Antlers particles, footstep dust, jet flames...). The run logs 1 RoR2 exception from its item-notification queue, caused by the test handing out 191 items in one frame. Screenshots with every item are too full of wards, bubbles and allies to judge by eye |
| 2026-10-11 | ping (golemplains) | PASS (1 run, 22/22, 0 real files changed, 0 exceptions) | key held 0.5 s = 1 ping; Beetle Guard 8 m ahead pinged as target; ground 6 m ahead pinged 0 m from the aim point; held back from ULTRAKILL on this profile: T, Q, middle mouse. Scripted input: the real middle-mouse press was not exercised |
| 2026-10-10 | audio (golemplains) | PASS (1 run) | volumes all 0.52 / sfx 0.17 / music 0 (the real settings); SFX mixer at -15.4 dB, one listener, 3 sources playing idle and 4 while firing. Audibility itself cannot be checked by the test |
| 2026-10-10 | blast (golemplains) | PASS (2 runs; the first, before de-duplicating, threw a Lemurian 37 m up) | zero-damage blast 1.5 m from a monster: Lemurian (100 kg) up 4.4 m; Beetle Guard (300 kg) up 0.8 m; neither hurt. The distances along the ground (33 m, 10 m) are inflated: test monsters have no acceleration, so they do not brake |
| 2026-10-10 | jumppad (wispgraveyard, a third stage) | PASS (1 run) | pad velocity (51.3, 42.2, -2.6), time 1.5 s: V1 rose 29.4 m (RoR2 apex 29.7 m) and came down 79.7 m along (pad aims 77.1 m), 1.9 s in the air; body alive, 0 exceptions |
| 2026-10-10 | outofbounds (golemplains) | PASS (1 run) | V1 put 25 m below the play area: RoR2 teleported the body 0.05 s later, V1 followed, standing 2 cm above the ground inside the area; one teleport, HP 100 → 100, 0 exceptions |
| 2026-10-10 02:55 | oil (golemplains) | PASS (1 run) | `TESTOIL` on a Beetle Guard: overlay on 0.7 s later, monster visibly covered in dark goo in the screenshot |
| 2026-10-10 02:50 | model | PASS (2 runs) | 9 parts, 6,441 vertices, 2 textures handed over 10.9 s after picking V1; display 2.05 m tall; second run showed the cached model 1.5 s after the pick, before ULTRAKILL was up |
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
- Unreadable collision meshes are read straight from Direct3D 11 (see Decisions); tested on Siphoned Forest only. Other DLC stages, another graphics card or driver, and a non-D3D11 renderer are untested (the last cannot work: those meshes are skipped and named in the log). The golemplains geometry run was not repeated after this change (its meshes are readable and take the old path).
- Parry windows (0.2 s melee, 0.12 s projectile, 5 units returned) are first guesses, untested by a human. Melee damage on V1 is delayed by the 0.2 s window.
- The scripted fireball catch in the `parry` scenario fails about half the time (timing); the parried projectile is removed and its owner damaged, it is not flung back as an ULTRAKILL projectile.
- The `combat` scenario is not deterministic: the scripted punch sometimes misses a Lemurian spawned 1.5 m away, and the
  "own attack" check can be confused by regeneration or invincibility frames. The mechanics behind both passed in other runs.
- V1 has no base regeneration since 2026-10-11; regeneration given by items is still forwarded as healing. Gaining maximum health in RoR2 no longer heals V1 (see Decisions).
- A monster's stand-in is one box from its movement capsule (x1.15), not its real hurt boxes; big or oddly shaped bosses will be off. The two worms are the exception (see Decisions).
- Overheal (ULTRAKILL HP above 100) is not shown on the RoR2 body.
- The Whiplash pull starts when the hook key is released (ULTRAKILL's rule); a pulled monster ignores RoR2 collision while dragged. Flying monsters without a CharacterMotor are moved by transform and untested.
- The user's bridge config currently has DamageCoefficient = 1 and MouseScale = 2.1 (set through the in-game menu, so that menu works).
- Water conduction was only tested with monsters flagged wet by the test, not standing in real stage water; whether RoR2's Water-layer colliders sit where the visible water is has not been checked on any stage. V1 standing in water while shocking it (ULTRAKILL shocks V1 too) is untested.
- The oil overlay was tested with the test hook, not the Firestarter's spray; its removal when the fuel burns off or runs out, and whether burning itself shows and damages, are untested. Gasoline on the environment is not shown.
- Blast knockback was tested with ULTRAKILL's stock explosion set to zero damage, not an actual rocket missing an enemy; the push strength (16 m/s per 100 kg) is a first guess and flying monsters are untested.
- Jump pads: one pad on one stage tested, with no steering in the air. The flight takes about 1.9 s where RoR2's takes 1.5 s (same path, ULTRAKILL's weaker gravity). RoR2's pad setting that removes air control is not applied to V1, and pads that only accept one team are not checked.
- Coin conduction was tested with a coin spawned by the test hook, not one thrown by the Marksman; a thrown coin is the same prefab, but the throw itself was not scripted.
- Magnets (Attractor nailgun) as conductors are implemented by the same code path but untested.
- The `conduction` scenario's weapon switching is scripted with number keys and depends on which variant ULTRAKILL had out last; the Jumpstart step failed 1 run in 3.
- Phase 7 is unverified beyond "it loads and the regression passes": nobody has opened the Mod Options screen to see the
  sliders, changed a setting mid-run, or run `tools\Install.ps1` end to end on a clean checkout (its parts were run separately).
- No performance options beyond ULTRAKILL's resolution and frame cap (no low-end preset, no lock-step, no D3D11 shared texture);
  the colour/coverage join still costs ~5 ms a frame on a worker thread.
- V1's character-select model is a static pose (no idle animation), and the survivor icon and the in-run body are unchanged (drawn icon; hidden Commando body).
- RoR2's chat lines and tutorial hints draw over ULTRAKILL's weapon panel (bottom left).
- Key overlaps not resolved: RoR2's Equipment key (Q by default) is taken from ULTRAKILL (its "last weapon"), and its Interact key (E) from ULTRAKILL's "change variation"; RoR2's Ping bind (middle mouse) is taken too; RoR2 sprint is not routed at all.
- Not tested: revives (Dio's Best Friend), the teleporter boss fight to completion, the bazaar, void fields, Mithrix, and any stage other than golemplains and blackbeach.
- RoR2 instant-kill effects cannot kill V1 (every hit is NonLethal on the RoR2 side). Out of bounds was tested by dropping V1 below one stage's play area; walking off an edge and other zone types (kick-out zones, kill zones) are untested.
- Mouse sensitivity mapping (Rewired counts → ULTRAKILL look) is untested by a human.
- The colour/coverage join costs ~5 ms per frame on a worker thread at 720p; a candidate for optimisation in Phase 7.
- RoR2 settings changed while on the bridge profile are saved to the sandbox, not to the real `Config\config.cfg`.
- **ULTRAKILL's registry settings are shared with the real game** (found 2026-10-11, not fixed, the owner has been told): Unity `PlayerPrefs` live in `HKCU\Software\Hakita\ULTRAKILL`, outside the save sandbox and outside what `Run-Test.ps1` watches. ULTRAKILL keeps the arm and weapon last used there (`CurArm`, `CurSlo`, `CurVar`, `Slot*Var`...), so the bridge and the real game change each other's; and Unity itself keeps the window there, which after bridge runs reads windowed 1064x598 at X = -31993 (the parked window). Whether the real game then starts off-screen or puts itself right from its own settings has not been checked.
- `Playlist.directory` and `CustomPaletteSelector` in ULTRAKILL still point at the real `Preferences\Playlists` and
  `Palettes` folders (Cyber Grind playlists, custom palettes); nothing in the bridge writes them, but they are not redirected.
