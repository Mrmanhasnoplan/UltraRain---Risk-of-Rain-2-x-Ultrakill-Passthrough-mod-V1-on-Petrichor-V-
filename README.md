# UltraRain: V1 on Petrichor V

A passthrough mod for Risk of Rain 2 and ULTRAKILL, allowing you to play as V1 on Petrichor V. All weapons are in the
game and work, along with conduction and the Whiplash. Items also affect V1, although some items such as Soldier's
Syringe don't. ULTRAKILL's sound and Risk of Rain 2's jump pads now work (bouncy mushrooms have not been tested yet).
This mod will receive further updates like multiplayer in future.

It runs the real ULTRAKILL beside Risk of Rain 2 and draws it into RoR2's window: ULTRAKILL's own movement, weapons,
arms, HUD, style meter and sounds. The RoR2 stage is ULTRAKILL's level and RoR2's monsters are its targets. Nothing of
ULTRAKILL is reimplemented. (In the code and scripts the project is called "V1 Bridge".)

> **Early build.** It has only been run on one PC, on three stages, with the game versions of October 2026. Setup is a
> PowerShell script that builds the mod against your own games. Known gaps: big bosses have rough hit boxes, moving
> platforms and map-edge walls are not solid for V1, and gasoline on the ground is not drawn. The full list is in
> [NOTES.md](NOTES.md).

The idea and several techniques come from [Ultracraft](https://github.com/alfr0762/ultracraft) (MIT), which does the
same for Minecraft. Licence: MIT ([LICENSE](LICENSE), [third-party notices](THIRD-PARTY-NOTICES.md)).

You need to own **ULTRAKILL** and **Risk of Rain 2** on Steam. Windows only.

Multiplayer, in part: the **host** can play V1 with friends who play ordinary survivors. Every player needs the mod,
and the same version of it. A **joining** player can play V1 too (new, tested only between two games on one PC):
moving, shooting, punching, parrying, the Whiplash, blast knockback, coins catching shots, being hurt, healing and
dying all go through the host.
Friends see V1 as a blue winged figure the mod builds and animates itself (it runs, leans and falls; it does not show
slides, dashes or punches as such), turned the way V1 looks. V1's attacks are drawn for them in a simple form: a line for each shot, a ball of light
for an explosion, a small light for each rocket, nail, coin or the Whiplash's hook in the air. A friend who does not own ULTRAKILL can install the mod too,
from a download that has the `prebuilt` folder, to join such a game; playing V1 needs ULTRAKILL. More than one V1 in
a game has not been tried.
No game files are in this repository; both plugins are compiled against your own installs.

## Install

Extract the ZIP to a folder with a short path (for example `C:\V1Bridge`), then double-click **`Install.cmd`**
(or run `powershell -ExecutionPolicy Bypass -File tools\Install.ps1`).

It finds both games through Steam and downloads BepInEx, R2API and Risk of Options from Thunderstore/GitHub into
`profiles\`. The two plugins are taken ready-built from `prebuilt\` when that folder is there, so nothing is compiled
and no .NET is needed. Otherwise (or with `Install.cmd -Build`) they are built against your own games: with an
installed .NET SDK if there is one, else with a copy of the .NET 8 SDK fetched as a plain ZIP into `.tools\dotnet`
(about 270 MB, no installer, nothing added to Windows). Your games stay as they are, with one exception:

- **ULTRAKILL's folder** gets Doorstop 4 (`winhttp.dll`, `doorstop_config.ini`, `.doorstop_version`), because the older
  Doorstop 3 does not load on ULTRAKILL's Unity version. The replaced files are backed up to
  `backups\ULTRAKILL-doorstop\`, the new config is **disabled by default** (a normal launch stays unmodded), and
  `tools\Install-UkDoorstop.ps1 -Restore` undoes it.

Risk of Rain 2's folder is not changed (it needs the Doorstop proxy r2modman or BepInExPack leaves next to the exe).
Your r2modman profiles are not touched.

## Play

Double-click **`Play.cmd`** (or run `powershell -ExecutionPolicy Bypass -File tools\Play.ps1`). Steam has to be running.

Singleplayer → pick **V1** (the last survivor) → wait a few seconds while ULTRAKILL loads in the background → start.
Picking any other survivor is plain Risk of Rain 2. ULTRAKILL closes when Risk of Rain 2 does.

V1's model in character select is read out of your own ULTRAKILL while it runs (nothing of it ships with the mod). The
very first time you pick V1 a placeholder figure stands there for about ten seconds until ULTRAKILL has loaded; after
that a copy kept in `profiles\ror2\cache` shows at once.

- Keys and mouse are ULTRAKILL's, with ULTRAKILL's own binds (a copy of yours, taken on first run).
- If you use E to swap weapons in ULTRAKILL make sure to change the interact button in risk of rain from E to something else.
- Along with that in mod options under risk of options you can edit your mouse speed (MouseScale, 2 by default) if
  turning feels too slow or too fast.
- Risk of Rain 2 keeps **Interact** (chests, shrines, teleporter), **Equipment**, **Ping** (middle mouse by default),
  **Escape** and the scoreboard. Whatever those are bound to is not passed on to ULTRAKILL.
- Damage: one ULTRAKILL damage unit (a revolver shot) is 300% of V1's RoR2 damage stat, so levels and damage items count.
  Kills are Risk of Rain 2's: gold, experience, on-kill items.
- Health: ULTRAKILL's HP is the truth and RoR2's health follows it. RoR2 works out a hit (armor, shield, barrier) and
  ULTRAKILL takes what got through, unless V1 is invincible (a dash, the moment after a hit). RoR2 healing heals V1.
- Parry: punch a monster as its melee hit lands, or punch a projectile as it arrives. The Sharpshooter's ricochet shot
  blows up projectiles it passes through, and a coin in the way of a monster's hitscan shot sends it into the nearest monster.
- Shields (Personal Shield Generator, Transcendence) are part of V1's 100 HP: hits take the shield first, blood heals
  health only, and the shield comes back the Risk of Rain 2 way, out of combat. V1 has no regeneration of its own.
- The Sandbox's spawner arm is no longer in the 6th weapon slot: that slot is empty.

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
| MouseScale | 2 | Multiplies mouse movement, on top of ULTRAKILL's sensitivity. |
| DamageCoefficient | 3 | RoR2 damage of one ULTRAKILL damage unit, × V1's damage stat. |
| IncomingDamage | 1 | Multiplies what RoR2 does to V1. |
| V1Health | 90 | V1's base health in RoR2 (grows 30% of this per level). V1's real health is ULTRAKILL's 100; a higher number makes each RoR2 hit cost a smaller share of it. |
| HideRoR2HealthAndSkills | on | Hide RoR2's health bar and skill icons under ULTRAKILL's HUD. |

ULTRAKILL's own options (field of view, sensitivity, HUD, graphics) are in `profiles\uk\Preferences`.

## If something is wrong

- Logs: `profiles\ror2\BepInEx\LogOutput.log` and `profiles\uk\BepInEx\LogOutput.log`.
- "ULTRAKILL needs Doorstop 4": run `tools\Install-UkDoorstop.ps1`.
- "Risk of Rain 2 has no Doorstop proxy": Risk of Rain 2 has never been modded on this PC; install BepInExPack once with r2modman.
- After a game update: run `Install.cmd -Build` (or `tools\Build.ps1`) again; if RoR2's mods broke, `tools\Setup-Profiles.ps1 -Force` fetches current ones.
- Known limits and open issues are listed in [NOTES.md](NOTES.md).

## For development

`tools\Run-Test.ps1 -Scenario <name>` starts both games unattended, runs scripted checks, saves screenshots and
`results.json` to `test-output\`, closes both games, and verifies that none of the real save files changed.
Scenarios: `handshake`, `survivor`, `picture`, `geometry`, `combat`, `parry`, `runloop` and more (the list is in
`src\RorBridge\TestMode.cs`)
(add `-ExtraArgs '--v1bridge-stage','golemplains'` to choose the stage). Design: [docs/DESIGN.md](docs/DESIGN.md).

Written with Claude (Anthropic) through Claude Code, directed and play-tested by a person.
