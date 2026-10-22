# Smoothie

A client-side performance mod for SPT. Bots stay sharp, your frame rate stays smooth.

Much of what eats your CPU in SPT is work nobody sees: bots animated at full speed 200 m
behind a wall, buildings drawn behind other buildings, level of detail worked out every frame
for objects too far away to draw. Smoothie cuts that waste without changing what bots do or
what you see.

<img width="640" height="360" alt="thumnail" src="https://github.com/user-attachments/assets/2c7bd555-796e-449d-b380-178652a6d9f7" />

## What it does

| | Vanilla | With Smoothie |
|---|---|---|
| Far or unseen bots | animated every frame | fewer updates (down to 15/s on screen at 200 m, 10/s off screen); fighting and audible bots stay smooth |
| Close bots you can't see | full hand and head aiming | skipped until you have a line of sight |
| Small gear on far bots | always drawn | skipped past 60 m (lasers and lights stay) |
| Corpses | ragdoll for 15–30 s | settle after 5 s (left to Rupture when it's installed) |
| Level objects behind nearer ones | drawn, shadows too | not drawn while fully covered, down to parts of big buildings and their decals (checked against your scope's view too) |
| Lamps lighting only rooms you can't see | lit, shadows drawn | skipped until their light may come into view |
| Level of detail for objects behind you or too far to draw | worked out every frame | skipped |
| Huge bot counts when you host | freezes on spawn waves | only as many as fit, gear built a bit per frame |
| Requests to the SPT server | a new connection every time | one connection kept open |
| Distant shadows | the old and the new shadow map read for every pixel, every frame | the old one skipped once the fade to the new one is done (about 0.4 ms less graphics card work at 1440p) |
| Screen effects with nothing to show (blood, death fade, grayscale, distortion) | copy the whole picture every frame | switched off until they have something to show |

Scope zoom counts: through a 4x scope a bot 200 m away is treated like one at 50 m. A few
things the game wastes every frame are fixed too, like the main menu's background video
playing through your whole raid.

## Engine code patch

Smoothie rewrites a few of Unity's per-frame loops in memory (`UnityPlayer.dll`) to do the same
work faster, and reads some of Unity's internal data directly. Nothing on disk changes, and it
only applies to the exact engine build it was made for: Smoothie checks the build id and the
original bytes first, and on any other build these parts switch themselves off and the log says
so. Untick **Faster engine culling loops (code patch)** in the F12 menu to put Unity's code back.
How the patches were found and how to redo them after a game update is in
`src/Smoothie/EnginePatch.cs` and `tools/engine-patches/`.

## Compatibility

- **Client-side only.** Nothing goes on the server.
- **Fika is optional.** Works offline, as a Fika host and as a Fika client.
- **Rupture:** Smoothie's corpse settle time switches itself off when Rupture is installed.
- **AI Limit:** works alongside it; AI Limit switches far bots off, Smoothie makes the rest cheaper.

## Install

Needs SPT `4.1.6`. Extract the zip into your SPT folder, so you end up with:

```
<SPT>/BepInEx/plugins/Smoothie/Smoothie.dll
<SPT>/BepInEx/plugins/Smoothie/SmoothieNative.dll
```

`BepInEx/LogOutput.log` shows `Loading [Smoothie ...]` when it loaded.

## Settings

Everything is in the F12 menu under **Smoothie** and applies straight away. The defaults are the
recommended ones; each setting's description explains what it does. **Show advanced settings**
reveals the sliders and the testing tools, including **Turn off all fixes (A/B test)** to compare
with vanilla and a performance overlay.

The ones you may want to change:

- **Hide objects covered by nearer ones** (on): something coming into view can show up a few
  frames late while you move.
- **Draw level objects grouped by material** (on): moves a little work to the graphics card; if
  the overlay shows GPU wait going up, switch it off.
- **Prevent memory cleanup stutters in raids** (off): pauses memory cleanup again after the
  inventory turns it back on. Only on PCs with enough RAM.

## Building

You need the .NET 10 SDK, gcc and mingw-w64 (for `SmoothieNative.dll`, built by
`src/SmoothieNative/build.sh`), and the path to your SPT folder.

```bash
cp .env.example .env    # set SPT_PATH, then:
./package.sh            # builds Smoothie_<version>.zip

export SPT_PATH=/path/to/SPT                    # MSBuild doesn't read .env
dotnet build -p:SptDeploy=true                  # compile and install into your game
dotnet run --project tests/Smoothie.SelfCheck   # run the checks
```

## Licence

[CC BY-NC-SA 4.0](LICENSE), same as SPT and Fika.
