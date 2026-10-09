# NOTES

Working log for the V1-in-RoR2 bridge. Design: [docs/DESIGN.md](docs/DESIGN.md).

## Status

| Phase | State |
|---|---|
| 0 Recon | done 2026-10-09 |
| 1 Handshake, launch/stop, test mode | in progress |
| 2–7 | not started |

## Decisions

- Two BepInEx 5 plugins: `RorBridge` (RoR2) and `UkBridge` (ULTRAKILL). net472, compiled against local installs.
- TCP `127.0.0.1:27120`, UkBridge listens. Line protocol + `HELLO` handshake. Frames and geometry via memory-mapped files.
- Game folders are never written. Bridge profiles live in `profiles/` (git-ignored) and are selected with
  Doorstop command-line switches. ULTRAKILL has Doorstop 3, RoR2 has Doorstop 4 (both placed by r2modman).
- ULTRAKILL launched with `steam.exe -applaunch 1229490 ...` like Ultracraft; window parked off-screen, never minimised.
- ULTRAKILL saves/prefs redirected into the bridge profile; leaderboard and Steam stat writes blocked.
- Frame transport starts as Ultracraft's AsyncGPUReadback → mmap; D3D11 shared texture only if latency demands it.
- Gameplay defaults: see "Design defaults chosen" in the design doc.

## Findings

- ULTRAKILL: Unity 2022.3.29f1, Mono, D3D11, new Input System. Saves in `<game>\Saves`, prefs in `<game>\Preferences`.
  Game folder has ReShade and a Doorstop 3 proxy but no BepInEx core, so it currently runs unmodded.
- RoR2: Unity 2021.3.45f2 (patched 2026-10-08, DLC "Hallowed Concepts"), Mono, D3D11, Rewired. Doorstop 4 proxy in
  the game folder, no BepInEx folder; the user's mods are r2modman profiles.
- The session folder's real path is ~200 characters; nested `git clone` fails there and MSBuild output paths
  would exceed 260. Build output is redirected to a short temp path until the project moves to a real folder.
- Ultracraft reference clone: `%LOCALAPPDATA%\Temp\uc-ref` (not part of this repo).

## Test results

(none yet)

## Known issues

(none yet)
