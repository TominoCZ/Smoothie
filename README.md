# Smoothie

A client-side performance mod for SPT. Bots stay sharp, your frame rate stays smooth.

Tarkov spends most of your CPU on bots: a full-speed animator for a scav 200 m behind a
wall, and hand IK for someone in the next room. Smoothie cuts that waste without
changing what bots do. AI, sounds and bullets work as before.

## What it does

| | Vanilla | Smoothie |
|---|---|---|
| Bots far away or outside your field of view | animate every frame | **lower rate**: on screen it slides down to 15 Hz at 200 m, off screen 10 Hz |
| Bots in a fight (with you or each other) | every frame | **every frame** in view, at least **30 Hz** out of view |
| Bots within hearing range | every frame | at least **30 Hz**, so every footstep lands |
| Close bots you can't see (behind walls, behind you) | full hand/head IK | **no IK** until a sightline opens |
| Small gear on distant bots (sights, pouches, patches) | always drawn | **skipped** beyond 60 m; lasers and lights stay |
| Weapon of a bot the game has culled | still drawn | **hidden** with the body |
| Corpse ragdolls | simulate 15–30 s | settle after **5 s** |
| Horde-sized bot counts when you host | hundreds of spawn attempts every 15 s, freezes | **only as many as fit**, gear built a little per frame |
| Backend requests | new TLS connection each time | **one kept-alive connection** |

Your scope zoom is part of the distance: through a 4x scope, a bot 200 m away counts as
about 50 m, so whatever you're looking at stays smooth.

A few vanilla per-frame wastes are fixed too: a coroutine started for every bot every frame,
dead bots' brains kept in the AI update list, and muzzle-smoke lighting recomputed once per
gun per frame.

## Compatibility

- **Client-side only.** Nothing goes on the server.
- **Fika: optional.** It works offline, as a Fika host and as a Fika client. Smoothie
  doesn't change any of Fika's code; with the performance overlay on, it only times Fika's
  observed-bot pass. Hidden bots on a Fika client keep their IK.
  Everything else keeps working.
- **AI Limit: stack them.** AI Limit switches far bots off entirely; Smoothie makes the
  ones still running cheaper. They don't touch each other.

## Install

Requires SPT `4.1.6`.

Extract the release zip into your SPT folder. That puts `Smoothie.dll` here:

```
<SPT>/BepInEx/plugins/Smoothie/
```

`BepInEx/LogOutput.log` shows `Loading [Smoothie 1.0.1]` at startup. In raid as a Fika
client it also logs:

```
[Info   :  Smoothie] ObservedPlayer has its own LateUpdate, throttling it from Smoothie's
```

## Configuring

Everything is in the F12 menu under **Smoothie**, applied live. The defaults are what most
people want. Tick **Show advanced settings** in the F12 menu to see the fine-tuning sliders
and experimental options.

| Setting | Default | What it does |
|---|---|---|
| **1. Bots** | | |
| Fewer animation updates for far or unseen bots | on | far-away and unseen bots get fewer animation updates; close, fighting and audible bots stay smooth |
| Skip arm and head aiming for unseen bots nearby | on | close bots behind you or behind walls skip hand/head/gun positioning |
| Hide tiny gear details on far-away bots | on | tiny gear details aren't drawn on distant bots |
| Hide guns of bots that are not drawn | on | the gun disappears along with the bot it belongs to |
| *advanced:* simplify distance, animation distances and rates, hearing distance | 60 m · 40/200 m · 15/10/30 per s · 60 m | fine-tuning for the above |
| **2. Bot spawning (when you host)** | | only matters when bots run on your PC |
| Limit spawn attempts to free bot slots | on | stops the game trying to spawn hundreds of bots at once (Horde mode) |
| Load new bots a bit at a time | on | builds new bots' gear a little per frame instead of freezing; with the frame rate limit off, sizes the game's work queue by the display's refresh rate instead of the menu's limit (spawn waves no longer pull raids down to 60 fps) |
| *advanced:* Load new bots on another CPU core (experimental) | off | experimental: does that on another CPU core |
| **3. Other fixes** | | |
| Corpses stop moving after | 5 s | vanilla 15 s (30 s in Fika) |
| Remove small CPU waste in game code | on | removes per-frame waste in the game's own code; pauses the main menu's video background while it's hidden (it kept playing through raids, 2-3 CPU cores under Proton); stops every bot the game tried to spawn from staying in memory until the game is closed |
| Muzzle flashes of others cast no shadows | on | other players' and bots' muzzle flashes light things up without casting shadows; shadow work more than doubled in fights. Your own gun is untouched |
| Prevent memory cleanup stutters in raids | off | SPT turns memory cleanup back on when you open your inventory and leaves it on, which stutters every minute or two; this lets that cleanup finish, then pauses it again (one more runs if 4 GB of garbage piles up). Does nothing on PCs below the game's own RAM limit for pausing cleanup (25 GB by default), and gives up for the raid if something else switches cleanup back on right after every pause (inventory opens don't count) |
| Keep server connection open (faster menus) | on | one kept-open connection to the SPT server |
| **4. Overlay and logs** (advanced) | | all off by default, shown with "Show advanced settings" |
| Turn off all fixes (A/B test) | off | runs the game as without Smoothie, leaving your settings alone; overlay and logs keep working. Switched on mid-raid, the bot spawning fixes stay on for that raid. Always starts off |
| Show performance overlay | off | what each frame is spent on, with a graph per part; toggle a setting and watch it change |
| Log stutters and their causes | off | writes each stutter and its cause to the BepInEx log |
| Log time per script (slow) | off | names the script behind each stutter in the log; costs frame time while on |
| Show text stats panel | off | a text panel with frame timings, memory and bot counts |

Settings were renamed in 1.0.1, so values saved by 1.0.0 reset to these defaults once.

## Building

Needs the .NET 10 SDK and the path to your SPT folder (the one with `EscapeFromTarkov.exe`
and `BepInEx/`).

`package.sh` picks the path up from `.env`:

```bash
cp .env.example .env    # then edit SPT_PATH
./package.sh            # builds the release zip
```

Everything else needs it as a real environment variable, since MSBuild can't read `.env`:

```bash
export SPT_PATH=/path/to/SPT

dotnet build                                    # compile
dotnet build -p:SptDeploy=true                  # install into your game
dotnet run --project tests/Smoothie.SelfCheck   # run the checks
```

## Licence

[CC BY-NC-SA 4.0](LICENSE), matching SPT and Fika.
