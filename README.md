# UltraRain: V1 on Petrichor V

A passthrough mod for Risk of Rain 2 and ULTRAKILL, allowing you to play as V1 on Petrichor V. All weapons are in the
game and work, along with conduction and the Whiplash. Items also affect V1, although some items such as Soldier's
Syringe don't. This mod will receive further updates like multiplayer, sound and the ability to actually use jump pads/shrooms in future.

It runs the real ULTRAKILL beside Risk of Rain 2 and draws it into RoR2's window: ULTRAKILL's own movement, weapons,
arms, HUD, style meter and sounds. The RoR2 stage is ULTRAKILL's level and RoR2's monsters are its targets. Nothing of
ULTRAKILL is reimplemented. (In the code and scripts the project is called "V1 Bridge".)

> **Early build.** It has only been run on one PC, on two stages, with the game versions of October 2026. Setup is a
> PowerShell script that builds the mod against your own games. Known gaps: big bosses have rough hit boxes, moving
> platforms and map-edge walls are not solid for V1, and gasoline on the ground is not drawn. The full list is in
> [NOTES.md](NOTES.md).

The idea and several techniques come from [Ultracraft](https://github.com/alfr0762/ultracraft) (MIT), which does the
same for Minecraft. Licence: MIT ([LICENSE](LICENSE), [third-party notices](THIRD-PARTY-NOTICES.md)).

You need to own **ULTRAKILL** and **Risk of Rain 2** on Steam. Windows only. Singleplayer only.
No game files are in this repository; both plugins are compiled against your own installs.

## Install

```
powershell -ExecutionPolicy Bypass -File tools\Install.ps1
```

It finds both games through Steam, installs the .NET SDK if it is missing, downloads BepInEx, R2API and Risk of Options
from Thunderstore/GitHub into `profiles\`, and builds the two plugins. Your games stay as they are, with one exception:

- **ULTRAKILL's folder** gets Doorstop 4 (`winhttp.dll`, `doorstop_config.ini`, `.doorstop_version`), because the older
  Doorstop 3 does not load on ULTRAKILL's Unity version. The replaced files are backed up to
  `backups\ULTRAKILL-doorstop\`, the new config is **disabled by default** (a normal launch stays unmodded), and
  `tools\Install-UkDoorstop.ps1 -Restore` undoes it.

Risk of Rain 2's folder is not changed (it needs the Doorstop proxy r2modman or BepInExPack leaves next to the exe).
Your r2modman profiles are not touched.

## Play

```
powershell -ExecutionPolicy Bypass -File tools\Play.ps1
```

Singleplayer → pick **V1** (the last survivor) → wait a few seconds while ULTRAKILL loads in the background → start.
Picking any other survivor is plain Risk of Rain 2. ULTRAKILL closes when Risk of Rain 2 does.

V1's model in character select is read out of your own ULTRAKILL while it runs (nothing of it ships with the mod). The
very first time you pick V1 a placeholder figure stands there for about ten seconds until ULTRAKILL has loaded; after
that a copy kept in `profiles\ror2\cache` shows at once.

- Keys and mouse are ULTRAKILL's, with ULTRAKILL's own binds (a copy of yours, taken on first run).
- If you use E to swap weapons in ULTRAKILL make sure to change the interact button in risk of rain from E to something else.
- Along with that in mod options under risk of options you can edit your mouse speed, as it's very slow on default.
- Risk of Rain 2 keeps **Interact** (chests, shrines, teleporter), **Equipment**, **Escape** and the scoreboard.
- Damage: one ULTRAKILL damage unit (a revolver shot) is 300% of V1's RoR2 damage stat, so levels and damage items count.
  Kills are Risk of Rain 2's: gold, experience, on-kill items.
- Health: ULTRAKILL's HP is the truth and RoR2's health follows it. RoR2 works out a hit (armor, shield, barrier) and
  ULTRAKILL takes what got through, unless V1 is invincible (a dash, the moment after a hit). RoR2 healing heals V1.
- Parry: punch a monster as its melee hit lands, or punch a projectile as it arrives.

### Your saves

Both games run on **copies** inside `profiles\` (ULTRAKILL's preferences and RoR2's profiles are copied once, the
first time). Nothing is written to your real ULTRAKILL saves or RoR2 profiles, no ULTRAKILL leaderboard or Steam stat
is submitted, and no Steam achievement is sent from bridge runs. Progress made as V1 stays in the copies.

## Settings

In game: **Settings → Mod Options → RorBridge** (or `profiles\ror2\BepInEx\config\dev.v1bridge.ror.cfg`).

| Setting | Default | |
|---|---|---|
| UltrakillHeight | 720 | Height ULTRAKILL renders at. The biggest performance lever: try 540 or 480 on a weak card. |
| UltrakillFpsCap | 120 | ULTRAKILL's frame cap. |
| MouseScale | 1 | Multiplies mouse movement, on top of ULTRAKILL's sensitivity. |
| DamageCoefficient | 3 | RoR2 damage of one ULTRAKILL damage unit, × V1's damage stat. |
| IncomingDamage | 1 | Multiplies what RoR2 does to V1. |
| HideRoR2HealthAndSkills | on | Hide RoR2's health bar and skill icons under ULTRAKILL's HUD. |

ULTRAKILL's own options (field of view, sensitivity, HUD, graphics) are in `profiles\uk\Preferences`.

## If something is wrong

- Logs: `profiles\ror2\BepInEx\LogOutput.log` and `profiles\uk\BepInEx\LogOutput.log`.
- "ULTRAKILL needs Doorstop 4": run `tools\Install-UkDoorstop.ps1`.
- After a game update: run `tools\Build.ps1` again; if RoR2's mods broke, `tools\Setup-Profiles.ps1 -Force` fetches current ones.
- Known limits and open issues are listed in [NOTES.md](NOTES.md).

## For development

`tools\Run-Test.ps1 -Scenario <name>` starts both games unattended, runs scripted checks, saves screenshots and
`results.json` to `test-output\`, closes both games, and verifies that none of the real save files changed.
Scenarios: `handshake`, `survivor`, `picture`, `geometry`, `combat`, `parry`, `runloop`
(add `-ExtraArgs '--v1bridge-stage','golemplains'` to choose the stage). Design: [docs/DESIGN.md](docs/DESIGN.md).

Written with Claude (Anthropic) through Claude Code, directed and play-tested by a person.
