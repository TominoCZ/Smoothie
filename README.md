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
| Corpses | ragdoll for 15–30 s | settle after 5 s (left to Rupture when it's installed) |
| Buildings, walls and furniture behind nearer ones | drawn, shadows too | not drawn past 3 m when fully covered, shadows included (checked against your scope's view too) |
| Lamps and area lights lighting only rooms you can't see | light and shadows drawn | skipped until any of their light may come into view |
| Level of detail for objects behind you | worked out every frame | skipped (big maps) |
| Huge bot counts when you host | hundreds of spawn attempts every 15 s, freezes | only as many as fit, gear built a bit per frame |
| Requests to the SPT server | a new connection every time | one connection kept open |

Your scope zoom counts too: through a 4x scope a bot 200 m away is treated like one at 50 m,
so whatever you're looking at stays smooth.

On top of that, a few things the game wastes every frame are fixed: a coroutine started for
every bot every frame, dead bots' brains still being updated, the main menu's background
video playing through your whole raid, and muzzle smoke lighting recalculated for every gun.

## What's new in 1.0.6

- One more engine loop patched: every frame Unity worked out the position, size and speed of
  every door's navigation obstacle for path-following agents ("NavMesh agents") that Tarkov
  doesn't use: bots find their path a different way, and none of the popular AI mods (SAIN,
  Questing Bots, Looting Bots, ORBIT, BigBrain, Waypoints) add such agents. On Streets that was
  about 5% of the main thread's time, now under 1%. Doors still block bot paths exactly as
  before; that part is left alone. If a mod ever adds such an agent, Unity's own code runs
  again. Part of **Faster engine culling loops (code patch)**.

## What's new in 1.0.5

- Hiding covered objects costs far less on your main thread. Turning around no longer causes
  spikes (from up to 4.5 ms of its own down to about 1 ms), the check runs on several CPU cores, and
  objects the game isn't drawing anyway aren't checked at all.
- Area lights (the game's glowing panels, signs and windows) behind walls are skipped like lamps.
  Indoors on Streets that took one spot from ~150 to 170-200 fps.
- **Smoothie now patches Unity's engine code** (see below): two of Unity's culling loops are
  rewritten in memory to do the same work faster, and level of detail is skipped for objects
  behind you. Only for the current game build; on any other build these switch themselves off.
- Far buildings' stand-in meshes are hidden too when covered.
- The covered-object check runs in native code with SIMD (`SmoothieNative.dll`, next to
  `Smoothie.dll`), several times faster than before; that matters most on CPUs with fewer cores.

## Engine code patch

Since 1.0.5 Smoothie changes a little of Unity's own engine code while the game runs. Most mods
only change the game's C# scripts; this goes one level deeper, into `UnityPlayer.dll`, the
engine itself. In short:

- **What:** two loops Unity runs every frame over every object on the map (on Streets over
  200,000) are replaced in memory with versions that do the same work faster: the scan after
  culling checks 16 objects at a time instead of one, and the level-of-detail loop skips the
  groups Smoothie has set aside. A third skips Unity's per-frame update of every door's
  navigation obstacle for path-following agents, which the game doesn't use (bots find their way
  differently); if any such agent exists, Unity's own code runs as before. Unity's results are the
  same; nothing you see changes.
- **Nothing on disk changes.** The patch lives in the running game's memory only. Close the game
  and it's gone; the game files are never touched.
- **Only for the exact engine build it was made for.** Smoothie checks the build id of
  `UnityPlayer.dll` and the exact original bytes at each spot before touching anything. After a
  game update, or on any other build, the patch (and the other features that read Unity's
  internals) switch themselves off and the log says so, until Smoothie is updated.
- **Safe instructions only.** The new code uses only instructions every 64-bit CPU has (SSE2),
  and it's written between frames, never while Unity could be running those loops.
- **Easy to turn off.** Untick **Faster engine culling loops (code patch)** in the F12 menu and
  Unity's original code goes back in immediately; **Turn off all fixes (A/B test)** does the
  same.

Some of Smoothie's other features read and write Unity's internal data directly too (for speed),
under the same build check. Smoothie also ships a small native library, `SmoothieNative.dll`, for
the covered-object check. Its source and everything about how the patch was found and how to
redo it after a game update are in this repository (`src/Smoothie/EnginePatch.cs`,
`tools/engine-patches/`, `src/SmoothieNative/`).

Antivirus software rarely cares about this, but code that changes another module's code in
memory is the kind of thing heuristics look at. If yours complains, the source is all here.

## Compatibility

- **Client-side only.** Nothing goes on the server.
- **Patches Unity's engine code in memory** on the current game build only; see
  [Engine code patch](#engine-code-patch).
- **Fika is optional.** Smoothie works offline, as a Fika host and as a Fika client, and doesn't
  touch any of Fika's code. On a Fika client, hidden bots keep their aiming.
- **Works with Rupture.** Rupture keeps corpses reacting to hits, so Smoothie's corpse settle
  time switches itself off when Rupture is installed.
- **Works with AI Limit.** AI Limit switches far bots off completely; Smoothie makes the ones
  still running cheaper. Use both if you like.

## Install

Needs SPT `4.1.6`.

Extract the zip into your SPT folder, so you end up with:

```
<SPT>/BepInEx/plugins/Smoothie/Smoothie.dll
<SPT>/BepInEx/plugins/Smoothie/SmoothieNative.dll
```

To check it loaded, look for `Loading [Smoothie 1.0.5]` in `BepInEx/LogOutput.log`. As a Fika
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
| Corpses stop moving after | 5 s | vanilla is 15 s (30 s in Fika). Off automatically with Rupture installed |
| Remove small CPU waste in game code | on | the per-frame waste listed above, plus bots the game failed to spawn no longer stay in memory until you quit |
| Muzzle flashes of others cast no shadows | on | other players' and bots' flashes still light things up, just without shadows; that more than doubled the shadow work in fights. Your own gun is untouched |
| Hide objects covered by nearer ones (experimental) | on | level objects aren't drawn while something nearer completely covers them. Something coming into view can show up a few frames late, only while you move; the faster you move, the earlier it's drawn. Doors, loot, players and anything that moves are never hidden |
| Hide covered objects only beyond (m) | 3 m | everything nearer stays drawn; 1–10 m |
| Covered objects also stop casting shadows | on | covered objects aren't drawn into shadow maps either, nor the game's separate shadow-only meshes when covered. Off: their shadows stay, always right but costs those draws |
| Skip lamps lighting only covered areas | on | a lamp or area light whose whole lit area is behind nearer things lights nothing you can see, so it stops lighting and drawing its shadows until any of that area may come into view. The sun and moon are left alone |
| Skip LOD work for objects you cannot see | on | Unity stops working out the level of detail of level objects out of view and beyond shadow range, and of those the game switched off. Nothing you see changes. Current game build only |
| Faster engine culling loops (code patch) | on | rewrites three of Unity's per-frame loops (two culling, one navigation) in memory (nothing on disk) to do the same work faster. Nothing you see changes. Current game build only; off puts Unity's code back |
| Draw level objects grouped by material | on | fewer material switches for the CPU, a little more work for the graphics card; if the overlay shows GPU wait going up, switch it off |
| Prevent memory cleanup stutters in raids | off | opening your inventory turns memory cleanup back on for the rest of the raid, which stutters every minute or two; this lets it finish and then pauses it again. Only on PCs with enough RAM for the game to pause it in the first place (25 GB by default) |
| Keep server connection open (faster menus) | on | one open connection to the SPT server instead of a new one per request |
| **4. Overlay and logs** (advanced) | | all off by default |
| Turn off all fixes (A/B test) | off | runs the game as if Smoothie weren't there, without touching your settings, so you can compare. Always starts off |
| Show performance overlay | off | shows what each frame is spent on, with a graph per part |
| Log stutters and their causes | off | writes every stutter and what caused it to the log |
| Log time per script (slow) | off | names the script behind each stutter; costs frame time while on |
| Show text stats panel | off | frame timings, memory and bot counts |
| Show hiding cells and hidden objects (debug) | off | outlines level objects within 150 m: red hidden, yellow drawn because something behind it shows, green kept drawn because you're moving, blue the lit area of a skipped lamp. While aiming: magenta shows in the scope's view, cyan kept next to it, orange the scope has no depth of its own |
| Show the depth image hiding uses | off | the small depth image the hiding checks against, plus the scope's while aiming |

Settings were renamed in 1.0.1, so if you're coming from 1.0.0 they reset to the defaults once.

## Building

You need the .NET 10 SDK, gcc and mingw-w64 (for `SmoothieNative.dll`, the depth checks in C
with SSE2, built by `src/SmoothieNative/build.sh`), and the path to your SPT folder (the one with
`EscapeFromTarkov.exe` and `BepInEx/`). Without the native DLL next to `Smoothie.dll` the plugin
falls back to its own C# depth checks.

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
