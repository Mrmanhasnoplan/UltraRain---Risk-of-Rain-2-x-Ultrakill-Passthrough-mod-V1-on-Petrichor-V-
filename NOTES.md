# NOTES

Working log for the V1-in-RoR2 bridge. Design: [docs/DESIGN.md](docs/DESIGN.md).

## Status

| Phase | State |
|---|---|
| 0 Recon | done 2026-10-09 |
| 1 Handshake, launch/stop, test mode | done 2026-10-09 (`handshake` scenario passes) |
| 2 V1 in character select | next |
| 3–7 | not started |

## How to work on it

```
tools\Setup-Profiles.ps1      # once: downloads BepInEx/R2API, builds profiles\ror2 and profiles\uk (-Force to redo)
tools\Install-UkDoorstop.ps1  # once: Doorstop 4 into ULTRAKILL's folder, with backup (-Restore undoes it)
tools\Build.ps1               # builds both plugins against the local installs and deploys them to the profiles
tools\Run-Test.ps1 -Scenario handshake   # unattended run; output in test-output\<time>-<scenario>\
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
- Frame transport starts as Ultracraft's AsyncGPUReadback → mmap; D3D11 shared texture only if latency demands it.
- Gameplay defaults: see "Design defaults chosen" in the design doc.

## Findings

- ULTRAKILL: Unity 2022.3.29f1, Mono, D3D11, new Input System. Saves in `<game>\Saves`, prefs in `<game>\Preferences`.
- RoR2: Unity 2021.3.45f2, version 1.5.0 #1210 (patched 2026-10-08, DLC "Hallowed Concepts"), Mono, D3D11, Rewired.
  It now has its own `ModLoader` (`Mods\enabled_mods.txt`); we stay on BepInEx. BepInEx 5.4.2122 + our plugin load fine on it.
- **Doorstop 3.4 (what r2modman had left in ULTRAKILL's folder) does not inject on Unity 2022.3**: the game simply
  starts unmodded. Doorstop 4.5 (from BepInEx 5.4.23.5) works. Installed with `enabled = false` in the config so
  normal launches stay vanilla; backup in `backups\ULTRAKILL-doorstop\`.
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
| 2026-10-09 23:31 | handshake | PASS (16/16 checks, 0 of 128 watched real files changed) | RoR2 to menu 40 s; ULTRAKILL process to handshake 4.6 s; ping 0.20 ms avg / 1.07 ms max over 100; ULTRAKILL quit 0.6 s after QUIT |

## Incidents

- 2026-10-09 23:24, first test run: ULTRAKILL was started with Doorstop 3 switches, did not load BepInEx, and so ran
  **vanilla for about two minutes** before being closed. In that time the game rewrote its real
  `Preferences\Prefs.json` (still valid JSON, 137 keys; no pre-run copy existed to diff against). No file in `Saves\`
  changed. Since then: Doorstop 4 is required before launching, the 25 s watchdog exists, and `Run-Test.ps1` copies the
  real save folders to `backups\saves\<run>\` before every run.

## Known issues

- R2API core does not load in the bridge profile: `Could not load [R2API 5.3.0] because it has missing dependencies:
  riskofthunder.RoR2BepInExPack` even though `RoR2BepInExPack.dll` 1.44.0 is in `plugins`. First thing to solve in Phase 2.
- RoR2 settings changed while on the bridge profile are saved to the sandbox, not to the real `Config\config.cfg`.
- `Playlist.directory` and `CustomPaletteSelector` in ULTRAKILL still point at the real `Preferences\Playlists` and
  `Palettes` folders (Cyber Grind playlists, custom palettes); nothing in the bridge writes them, but they are not redirected.
