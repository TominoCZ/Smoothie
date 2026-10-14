# Smoothie

A client-side performance mod for SPT. Bots stay sharp, your frame rate stays smooth.

Most of what eats your CPU in Tarkov is bots. The game animates a scav 200 m behind a wall
at full speed and works out hand and head positions for someone in the next room you can't
even see. Smoothie cuts that waste without changing what bots actually do: AI, sounds and
bullets all work like before.

## What it does

| | Vanilla | With Smoothie |
|---|---|---|
| Bots far away or off screen | animated every frame | fewer updates: down to 15 per second on screen at 200 m, 10 off screen |
| Bots in a fight | every frame | every frame while you can see them, at least 30 per second when you can't |
| Bots you can hear | every frame | at least 30 per second, so footsteps stay on time |
| Close bots you can't see (behind walls, behind you) | full hand and head aiming | skipped until you have a line of sight |
| Small gear on far bots (sights, pouches, patches) | always drawn | skipped past 60 m; lasers and lights stay |
| Gun of a bot the game already hid | still drawn | hidden with the bot |
| Corpses | ragdoll for 15–30 s | settle after 5 s |
| Buildings, walls and furniture behind nearer ones | drawn | not drawn past 10 m when fully covered (checked against your scope's view too) |
| Huge bot counts when you host | hundreds of spawn attempts every 15 s, freezes | only as many as fit, gear built a bit per frame |
| Requests to the SPT server | a new connection every time | one connection kept open |

Your scope zoom counts too: through a 4x scope a bot 200 m away is treated like one at 50 m,
so whatever you're looking at stays smooth.

On top of that, a few things the game wastes every frame are fixed: a coroutine started for
every bot every frame, dead bots' brains still being updated, the main menu's background
video playing through your whole raid, and muzzle smoke lighting recalculated for every gun.

## What's new in 1.0.3

- Hiding covered objects is cheaper and reacts faster. The time it costs per frame is about
  halved and the worst spikes are gone.
- Raising a scope no longer flashes hidden objects back on, and more stays hidden while aiming.
- Hidden objects behind you stay hidden while you walk and come back the moment you turn
  around.

## Compatibility

- **Client-side only.** Nothing goes on the server.
- **Fika is optional.** Smoothie works offline, as a Fika host and as a Fika client, and doesn't
  touch any of Fika's code. On a Fika client, hidden bots keep their aiming.
- **Works with AI Limit.** AI Limit switches far bots off completely; Smoothie makes the ones
  still running cheaper. Use both if you like.

## Install

Needs SPT `4.1.6`.

Extract the zip into your SPT folder, so you end up with:

```
<SPT>/BepInEx/plugins/Smoothie/Smoothie.dll
```

To check it loaded, look for `Loading [Smoothie 1.0.3]` in `BepInEx/LogOutput.log`. As a Fika
client you'll also see this once you're in a raid:

```
[Info   :  Smoothie] ObservedPlayer has its own LateUpdate, throttling it from Smoothie's
```

## Settings

Everything is in the F12 menu under **Smoothie** and applies straight away. The defaults are
what I'd recommend. Tick **Show advanced settings** to see the sliders and the testing tools.

| Setting | Default | What it does |
|---|---|---|
| **1. Bots** | | |
| Fewer animation updates for far or unseen bots | on | far and unseen bots animate less often; close, fighting and audible bots stay smooth |
| Skip arm and head aiming for unseen bots nearby | on | close bots behind you or behind walls skip hand, head and gun positioning |
| Hide tiny gear details on far-away bots | on | small gear isn't drawn on distant bots |
| Hide guns of bots that are not drawn | on | the gun disappears along with its bot |
| *advanced:* distances, update rates, hearing distance, "Update far bots on all CPU cores" | 60 m · 40/200 m · 15/10/30 per s · 60 m · on | fine-tuning for the above |
| **2. Bot spawning (when you host)** | | only matters when bots run on your PC |
| Limit spawn attempts to free bot slots | on | stops the game trying to spawn hundreds of bots at once (Horde mode), or any bots in a raid set to no bots |
| Load new bots a bit at a time | on | builds new bots' gear over several frames instead of freezing; spawn waves no longer drag raids down to 60 fps when your frame limit is off |
| *advanced:* Load new bots on another CPU core (experimental) | off | builds that gear on another core instead |
| **3. Other fixes** | | |
| Corpses stop moving after | 5 s | vanilla is 15 s (30 s in Fika) |
| Remove small CPU waste in game code | on | the per-frame waste listed above, plus bots the game failed to spawn no longer stay in memory until you quit |
| Muzzle flashes of others cast no shadows | on | other players' and bots' flashes still light things up, just without shadows; that more than doubled the shadow work in fights. Your own gun is untouched |
| Hide objects covered by nearer ones (experimental) | on | level objects 10 m+ away aren't drawn while something nearer completely covers them. Their shadows stay. Something coming into view can show up a few frames late, only while you move. Doors, loot, players and anything that moves are never hidden |
| Prevent memory cleanup stutters in raids | off | opening your inventory turns memory cleanup back on for the rest of the raid, which stutters every minute or two; this lets it finish and then pauses it again. Only on PCs with enough RAM for the game to pause it in the first place (25 GB by default) |
| Keep server connection open (faster menus) | on | one open connection to the SPT server instead of a new one per request |
| **4. Overlay and logs** (advanced) | | all off by default |
| Turn off all fixes (A/B test) | off | runs the game as if Smoothie weren't there, without touching your settings, so you can compare. Always starts off |
| Show performance overlay | off | shows what each frame is spent on, with a graph per part |
| Log stutters and their causes | off | writes every stutter and what caused it to the log |
| Log time per script (slow) | off | names the script behind each stutter; costs frame time while on |
| Show text stats panel | off | frame timings, memory and bot counts |
| Show hiding cells and hidden objects (debug) | off | outlines level objects within 150 m: red hidden, yellow drawn because something behind it shows. While aiming: magenta shows in the scope's view, cyan kept next to it, orange the scope has no depth of its own |
| Show the depth image hiding uses | off | the small depth image the hiding checks against, plus the scope's while aiming |

Settings were renamed in 1.0.1, so if you're coming from 1.0.0 they reset to the defaults once.

## Building

You need the .NET 10 SDK and the path to your SPT folder (the one with `EscapeFromTarkov.exe`
and `BepInEx/`).

For the release zip, put the path in `.env`:

```bash
cp .env.example .env    # then set SPT_PATH
./package.sh            # builds Smoothie_<version>.zip
```

Everything else needs it as an environment variable, since MSBuild doesn't read `.env`:

```bash
export SPT_PATH=/path/to/SPT

dotnet build                                    # compile
dotnet build -p:SptDeploy=true                  # compile and install into your game
dotnet run --project tests/Smoothie.SelfCheck   # run the checks
```

## Licence

[CC BY-NC-SA 4.0](LICENSE), same as SPT and Fika.
