# Smoothie

A client-side performance mod for SPT. Bots stay sharp, your frame rate stays smooth.

Tarkov spends most of your CPU on bots: a full-speed animator for a scav 200 m behind a
wall, hand IK for someone in the next room, a whole squad built in a single frame. Smoothie
cuts that waste without changing what bots do. AI, sounds and bullets work as before.

## What it does

| | Vanilla | Smoothie |
|---|---|---|
| Bots far away or outside your field of view | animate every frame | **lower rate**: on screen it slides down to 15 Hz at 200 m, off screen 10 Hz |
| Bots in a fight (with you or each other) | every frame | **every frame**, untouched |
| Bots within hearing range | every frame | at least **30 Hz**, so every footstep lands |
| Close bots you can't see (behind walls, behind you) | full hand/head IK | **no IK** until a sightline opens |
| Small gear on distant bots (sights, pouches, patches) | always drawn | **skipped** beyond 60 m; lasers and lights stay |
| Weapon of a bot the game has culled | still drawn | **hidden** with the body |
| Corpse ragdolls | simulate 15–30 s | settle after **5 s** |
| Bot spawns and wake-ups when hosting | whole wave in one frame | **one per frame** |
| Bot profiles arriving from the server | all built in one frame | **one per frame** |
| SPT's in-raid memory cleanup | cut short, heap keeps growing | **finishes**; the GC then stays out of your fights |
| Backend requests | new TLS connection each time | **one kept-alive connection** |

Your scope zoom is part of the distance: through a 4x scope, a bot 200 m away counts as
about 50 m, so whatever you're looking at stays smooth.

A few vanilla per-frame wastes are fixed too: a coroutine started for every bot every frame,
dead bots' brains kept in the AI update list, and muzzle-smoke lighting recomputed once per
gun per frame.

## Compatibility

- **Client-side only.** Nothing goes on the server.
- **Fika: optional.** It works offline, as a Fika host and as a Fika client. When Fika is
  installed, Smoothie patches exactly one Fika method (to skip IK for hidden bots on a
  client). If a Fika update changes that method, Smoothie leaves it alone and logs it.
  Everything else keeps working.
- **AI Limit: stack them.** AI Limit switches far bots off entirely; Smoothie makes the
  ones still running cheaper. They don't touch each other.

## Install

Requires SPT `4.1.6`.

Extract the release zip into your SPT folder. That puts `Smoothie.dll` here:

```
<SPT>/BepInEx/plugins/Smoothie/
```

`BepInEx/LogOutput.log` shows `Loading [Smoothie 1.0.0]` at startup. In raid as a Fika
client it also logs:

```
[Info   :  Smoothie] ObservedPlayer has its own LateUpdate, throttling it from Smoothie's
```

## Configuring

Everything is in the F12 menu under **Smoothie**, one toggle per feature, applied live.

| Setting | Default | |
|---|---|---|
| Throttle bot animators | on | the animation rate limit for far and off-screen bots |
| Full-rate distance | 40 m | closer than this, bots animate every frame |
| Lowest-rate distance | 200 m | from here on, the lowest on-screen rate |
| Lowest on-screen rate | 15 Hz | for visible bots at the far end |
| Off-screen rate | 10 Hz | for bots outside your view |
| Footstep rate / range | 30 Hz / 60 m | minimum rate for bots you could hear |
| Hide culled bot weapons | on | |
| Skip IK for hidden bots | on | |
| Simplify distant bots / distance | on / 60 m | |
| Corpse settle time | 5 s | vanilla is 15 s (30 s for Fika corpses) |
| Hot-path fixes | on | |
| Stagger bot spawns | on | only matters when bots run on your PC |
| Keep backend connection open | on | |
| Keep GC off in raid | on | |

**Diagnostics** (all off by default): an on-screen stats overlay, a script and engine-stage
profiler, and a frame-drop logger that writes snapshots to the BepInEx log. The profiler
costs frame time while it's on, so don't judge your fps with it enabled.

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
