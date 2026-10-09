# V1 in Risk of Rain 2 — Phase 0 design

Play RoR2 as V1 using the real ULTRAKILL process, the way
[Ultracraft](https://github.com/alfr0762/ultracraft) does it for Minecraft.
Two BepInEx 5 plugins:

- **RorBridge** — inside Risk of Rain 2 (replaces Ultracraft's Fabric mod).
- **UkBridge** — inside ULTRAKILL (the counterpart of Ultracraft's UltraBridge).

Nothing from either game is stored in this repo; both plugins compile against the
DLLs in the local installs.

## 1. What Ultracraft actually does (read from source, commit cloned 2026-10-09)

| Problem | Ultracraft's solution | Where |
|---|---|---|
| Process link | ULTRAKILL listens on TCP `127.0.0.1:27110`; Minecraft connects and retries every second. Line-based UTF-8 text protocol (`IN`, `P`, `HP`, `SEC`, `ENTS`, `DMG`, `HURT`, ...). `TcpNoDelay`, one reader thread per side feeding a queue drained on the main thread. | `UkLink.java`, `Plugin.cs` `Net` |
| Launch / close | Minecraft starts ULTRAKILL through `steam.exe -applaunch 1229490 -ultracraft -screen-fullscreen 0 ...`; on exit sends `QUIT`, then kills after 6 s. The plugin parks ULTRAKILL's window at -32000,-32000 (never minimised: minimised Unity stops rendering), sets `runInBackground`, and opts out of Windows timer throttling. | `UkLauncher.java`, `Win32` in `Plugin.cs` |
| Level | ULTRAKILL loads the Sandbox (`uk_construct`), waits for its start sequence, then disables every renderer/collider/light that is not V1's rig, HUD or gore pools, clears the camera to transparent black, and performs by hand what the spawn pit's activator does (`ActivatePlayer`, HUD relay). | `PrepareLevel`, `LandV1` |
| Who renders what | The **host game renders the world and its mobs**. **ULTRAKILL renders only its own layer**: arms, weapons, HUD, projectiles, explosions, blood, its own enemies. Host terrain exists in ULTRAKILL as an *occluder* material (writes depth + transparent black) so ULTRAKILL's effects are hidden behind host walls. | `OccluderMaterial`, `BuildSection` |
| Picture transport | ULTRAKILL retargets its final "Virtual Camera" to a RenderTexture, then each frame `AsyncGPUReadback.RequestIntoNativeArray` writes colour + a mask (the pre-post-process target, for alpha/additive light) **directly into a slot of a memory-mapped file** (`%TEMP%`, 64-byte header: seq, w, h, slot, mask bpp, draw timestamp, camera pose). The host uploads straight from the mapping into two textures and composites with a small shader. | `Capture`, `ReadbackDone`, `UkFrame.java`, `v1_composite.fsh` |
| Camera agreement | Each frame carries the exact camera pose/FOV it was drawn from; the host draws its world from *that* pose, so the two layers never slide apart. Optional lock-step: the host waits for ULTRAKILL's next frame instead of its own limiter, falling back to free-run if ULTRAKILL misses >20% of frames. Clock-offset pings measure frame age. | `UkFrame.waitForNext`, `CLOCK` |
| Input | Host captures keys/mouse and sends `IN dx dy wheel buttons keys...`. ULTRAKILL adds **virtual InputSystem devices** (`InputSystem.AddDevice<Keyboard/Mouse>`), sets `backgroundBehavior = IgnoreFocus`, and queues state events. Mouse look is applied directly to `CameraController.rotationX/Y` with ULTRAKILL's own sensitivity maths. | `InjectInput` |
| Geometry | Host sends chunk meshes (greedy-merged faces) → `MeshCollider` on layer 8 "Environment", tag `Floor`, plus a renderer (weapons only treat renderer-carrying colliders as static environment). Coordinates scaled by `K = 3.5/1.8` (V1's capsule vs. the host player) with Z mirrored; a floating origin keeps floats precise. | `WorldMesh.java`, `BuildSection` |
| Enemies | Each host mob gets a stand-in: `EnemyIdentifier` (lifecycle methods skipped via Harmony), head/body box colliders on layer 10 tagged `Head`/`Body` each with a kinematic Rigidbody and `EnemyIdentifierIdentifier`, registered in `EnemyTracker` and the `TargetTracker` (coins, auto-aim). | `MakeProxy`, `McProxy` |
| Damage out | Harmony prefix on `EnemyIdentifier.DeliverDamage`: for a stand-in, compute multiplier (+crit for head), send `DMG id mult head explosion parry ...`, spawn real ULTRAKILL gore (which drives blood healing), and skip the original. | `EidDamage` |
| Damage in | Host sends `HURT n`; plugin calls `NewMovement.GetHurt`, so dash i-frames etc. are ULTRAKILL's. Melee hits are held for a short parry window; host projectiles are mirrored on ULTRAKILL's projectile layer with a `ParryReceiver`. ULTRAKILL reports `HP hp dead max` back. | `HURT`, `MeleeHit`, `McProjectile` |
| Saves | Only *reads* of `GameProgressSaver` are patched (tutorial/intro seen, gear, money); it relies on the Sandbox + cheats to avoid writes. | `Progress.cs` |

## 2. What is on this PC (checked 2026-10-09)

| | ULTRAKILL (1229490) | Risk of Rain 2 (632360) |
|---|---|---|
| Folder | `C:\Program Files (x86)\Steam\steamapps\common\ULTRAKILL` | `...\common\Risk of Rain 2` |
| Unity | 2022.3.29f1, Mono | **2021.3.45f2**, Mono (updated 2026-10-08/09, "Hallowed Concepts" DLC; previous logs show 2021.3.33f1) |
| Graphics | Direct3D 11 (threaded + graphics jobs on) | Direct3D 11 |
| Input | New Input System | Rewired |
| Loader in game folder | Doorstop **3** `winhttp.dll` + config, but **no `BepInEx\core`** (only stale `BepInEx\config`). Also ReShade (`dxgi.dll`). | Doorstop **4** `winhttp.dll` + config, no `BepInEx` folder. Empty `Mods\enabled_mods.txt`. |
| Mod manager | r2modman, profile `Default` (empty) | r2modman, 8 profiles (`big 2026`, `Alloyed Collective`, ...) on BepInEx 5.4.21 |
| Saves | `<game>\Saves\Slot1..2`, `<game>\Preferences` (Steam Cloud) | `Steam\userdata\<id>\632360\remote\UserProfiles` |

GPU: Radeon RX 590 8 GB, desktop 4096x2160 @ 59 Hz. C: has 25 GB free. Steam is running.
The .NET SDK was missing (runtimes only) — installed via winget.

Modding stack (Thunderstore API, 2026-10-09): `bbepis-BepInExPack 5.4.2122`,
`RiskofThunder-RoR2BepInExPack 1.44.0` (updated today for DLC4), `HookGenPatcher 1.2.9`,
`R2API_Core 5.3.0`, `R2API_Prefab 1.1.1`, `R2API_ContentManagement 1.0.11`,
`R2API_Language 1.1.0`, `R2API_RecalculateStats 1.6.6`, `Risk_Of_Options 2.8.6`.

## 3. Architecture

```
 Risk of Rain 2 (visible window, D3D11)              ULTRAKILL (window parked off-screen, D3D11)
 ┌──────────────────────────────────────┐           ┌──────────────────────────────────────┐
 │ RorBridge.dll                        │  TCP      │ UkBridge.dll                         │
 │  V1 SurvivorDef + placeholder body   │◄────────► │  Sandbox stripped to V1's rig        │
 │  launcher (steam -applaunch)         │  27120    │  virtual keyboard + mouse            │
 │  input capture (raw, RoR2 input off) │           │  stage MeshColliders + occluders     │
 │  camera driven by frame pose         │  mmap     │  monster stand-ins (EnemyIdentifier) │
 │  composite ULTRAKILL layer over HUD  │◄───────── │  frame readback → mapped file        │
 │  stage mesh export, monster export   │  frames   │  damage/heal/parry hooks             │
 │  damage/heal/kill bookkeeping        │           │  save + leaderboard guards           │
 └──────────────────────────────────────┘           └──────────────────────────────────────┘
```

- **Link**: UkBridge listens on `127.0.0.1:27120` (not Ultracraft's 27110, so both mods can coexist).
  Text lines for control, with a `HELLO <protocol> <version>` handshake both ways. Bulk geometry goes
  through a second memory-mapped file rather than text (RoR2 stages are hundreds of thousands of
  triangles, not block faces).
- **Rendering split** (same as Ultracraft): RoR2 draws the stage, monsters, items and its own HUD
  elements we keep (objective, money, items, boss bar). ULTRAKILL draws arms, weapons, its HUD,
  style meter, projectiles, explosions and blood over transparent black, with the stage as depth-only
  occluders. RorBridge composites the layer full-screen under RoR2's remaining HUD.
- **Frames**: start with Ultracraft's proven path (AsyncGPUReadback → mapped file → texture upload),
  measure, and only then consider a D3D11 shared texture (zero-copy; both games are D3D11) as the
  Phase 3/7 optimisation. Latency is measured with the same draw-timestamp + clock-ping scheme.
- **Camera**: RoR2's `CameraRigController` is overridden with the pose and FOV of the frame being
  shown. The RoR2 body's model is hidden in first person.
- **Authority**: ULTRAKILL owns V1's position, velocity, HP and weapons. RoR2 owns the stage,
  monsters, items, economy and run state. The RoR2 body is teleported to V1's pose every frame so
  RoR2's AI, interactables, zone checks and pickups see a real player there.
- **Scale**: `K = 3.5 / 1.82` ULTRAKILL units per RoR2 metre as a starting point (V1's capsule vs. a
  survivor capsule); V1's walk speed then lands near RoR2's sprint speed. To be tuned in Phase 4.

### Design defaults chosen

| Topic | Decision |
|---|---|
| Movement | Pure ULTRAKILL. RoR2 move-speed/jump items do not alter it (revisit in Phase 6 as an opt-in setting). |
| Damage out | ULTRAKILL's damage multiplier (1.0 ≈ one revolver shot) × the V1 body's RoR2 `damage` stat × a tunable coefficient, delivered as a real `DamageInfo` with V1 as attacker. Levels, damage items, crit, on-hit procs, on-kill items, gold, XP and the director all work natively because RoR2 does the kill. |
| Damage in | RoR2 damage is first reduced by RoR2 (armor, shield, barrier, block items). The remainder is converted to a fraction of max health × 100 and sent as `HURT`; ULTRAKILL's `GetHurt` decides (dash i-frames, hard damage). |
| Health | V1's HP (0–100, overheal to 200) is the source of truth; RoR2 health is set to the same fraction. RoR2 healing is forwarded as `HEAL`. Blood healing in ULTRAKILL raises RoR2 health with it. |
| Interact | RoR2's interact key stays RoR2's (not forwarded to ULTRAKILL) and drives the normal `Interactor`. |
| Weapons | All weapons and arms from the start (patched *reads* of gear, as Ultracraft does). No shop. |
| Character select look | Placeholder capsule + generated icon. V1's model can come from the local install at runtime: UkBridge already has it loaded and can render a portrait (Ultracraft's "doll") — Phase 7. |

### Staying out of the user's installs

- **No game folder is modified.** Both games already have a Doorstop proxy; each bridge profile lives
  under `profiles/` in this repo (git-ignored) and is selected per launch with Doorstop's command-line
  switches (`--doorstop-enable/--doorstop-target` for v3, `--doorstop-enabled/--doorstop-target-assembly`
  for v4). A normal Steam launch of either game is untouched, as are the r2modman profiles.
- **ULTRAKILL saves**: beyond Ultracraft's read patches, UkBridge redirects `GameProgressSaver.BaseSavePath`
  and `PrefsManager.PrefsPath` into the bridge profile and blocks any `WriteFile` outside it. Verified in
  test mode by hashing the real `Saves`/`Preferences` before and after.
- **Leaderboards / stats**: `LeaderboardController.Submit*` and `SteamController` stat writers are
  skipped while the bridge is active (the Sandbox has none, but a guard costs nothing).
- **RoR2 saves**: the test profile uses its own RoR2 user profile; achievements/unlocks are not granted
  from bridge runs (to verify in Phase 1 how RoR2 isolates this under a modded launch).

## 4. Biggest risks

1. **RoR2 was patched yesterday** (new DLC, Unity 2021.3.33 → 2021.3.45). RoR2BepInExPack caught up
   today; R2API modules have not been re-released. Phase 1 starts by proving the stack loads; if a
   module is broken the survivor is registered through RoR2's own `ContentPack` API instead.
2. **GPU budget**: two games on one RX 590 at a 4K desktop. Mitigation: ULTRAKILL renders at a capped
   internal resolution (720p default), FPS cap, lock-step with fallback.
3. **Frame latency** from CPU readback (1–2 frames). Mitigation: Ultracraft's low-latency wait; shared
   texture as the escape hatch.
4. **Stage geometry size and moving geometry** (elevators, doors, destructibles). Static colliders
   first; moving colliders mirrored as transforms later.
5. **Input ownership**: RoR2 (Rewired) must go quiet for movement/skills while V1 is active but keep
   interact, pause, scoreboard and menus. Cursor/menus need a clean hand-off.
6. **Health mapping edge cases**: one-shot protection, shields, barrier decay, Transcendence, revives.
7. **ULTRAKILL updates** can break Harmony targets; all reflective lookups are checked at startup and
   reported in the handshake so failures are loud.
8. **Parry semantics** for RoR2 attacks without projectiles (overlap/blast attacks) need a
   telegraph window; fidelity is a feel question that needs a human.
9. **Steam launch**: `-applaunch` needs Steam running, and ReShade is present in ULTRAKILL's folder
   (hooks the swap chain; should not affect RenderTexture readback — to confirm).
