using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using EFT;
using EFT.Bots;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;

namespace Smoothie;

// Bot profiles come from the server in batches, and each one is turned into a Profile (its
// whole inventory as items) on the main thread when the reply lands. Horde raids were seen
// sending 150 requests in 6 s: every profile got built in the next few frames, 5 fps for
// 10 s. Here the profiles of every request share one time budget per frame. Same request,
// same Profile constructor, same main thread as the game's LoadBots; nothing sees a profile
// before its whole batch is built, so to the game this is the server answering later.
internal static class ProfileBudget
{
    private const double BudgetMs = 3.0;
    private static readonly long BudgetTicks = (long)(BudgetMs * Stopwatch.Frequency / 1000.0);

    // "Load new bots on another CPU core": one batch at a time, never racing itself.
    private static readonly SemaphoreSlim Worker = new(1, 1);

    private static int _frame = -1;
    private static long _spent;

    // Running average of one profile's build time, so a frame only starts one that fits.
    private static long _averageTicks;

    // For the cost overlay: profiles still waiting to be built.
    internal static int Waiting;

    internal static void Enable()
    {
        new LoadBotsPatch().Enable();
        new SpawnCheckPatch().Enable();
        new JobBudgetPatch().Enable();
    }

    // The first profile of a frame always goes, so one slow profile can't stall the queue;
    // after that, only one expected to fit in what's left.
    private static bool HasBudget()
    {
        if (_frame != Time.frameCount)
        {
            _frame = Time.frameCount;
            _spent = 0;
        }

        return _spent == 0 || _spent + _averageTicks <= BudgetTicks;
    }

    // Other mods hook game code with Harmony. If any of them patched what building a profile
    // runs through (the profile and inventory, ItemFactory, any item class), their code would
    // run on the worker too; then the worker isn't used. Not checked once at startup: mods can
    // load after this one or patch later (at raid start, say). Every 10 s at most, though, as
    // it walks every patched method of every mod.
    private static readonly HashSet<string> Reported = new();
    private static bool _noModHooks;
    private static float _hooksCheckedAt = -100f;

    private static bool NoModHooks()
    {
        if (Time.unscaledTime - _hooksCheckedAt < 10f) return _noModHooks;
        _hooksCheckedAt = Time.unscaledTime;
        return _noModHooks = ScanModHooks();
    }

    private static bool ScanModHooks()
    {
        foreach (var method in Harmony.GetAllPatchedMethods())
        {
            var type = method.DeclaringType;
            if (type == null || !(type == typeof(Profile) || type == typeof(InventoryDescriptor) || type == typeof(ItemFactory)
                                  || typeof(EFT.InventoryLogic.Item).IsAssignableFrom(type)))
            {
                continue;
            }

            var owners = Harmony.GetPatchInfo(method)?.Owners;
            if (owners == null || owners.Count == 0) continue;
            var what = $"{string.Join(", ", owners)} patches {type.Name}.{method.Name}";
            if (Reported.Add(what)) Plugin.Log.LogInfo($"bot profiles stay on the main thread: {what}");
            return false;
        }

        return true;
    }

    // The game's LoadBots, with the profiles built under the shared budget.
    private static async Task<Profile[]> Load(ClientBackendSession session, List<CountTypeBotWave> conditions)
    {
        // The spawner counts bots whose profiles are loading (ProfilesLoadingProcess), but the
        // game's own bot client never adds to it: a bot waiting for its profile counted as a
        // free slot, and every spawn check (every 15 s) started the same bots again. Counted
        // here while the request is out and its profiles are being built.
        BotCreationData.ProfilesLoadingProcess++;
        try
        {
            return await LoadCounted(session, conditions);
        }
        finally
        {
            // The game resets it to 0 for a new raid; a request from the last one ending late
            // mustn't take it below that.
            if (BotCreationData.ProfilesLoadingProcess > 0) BotCreationData.ProfilesLoadingProcess--;
        }
    }

    private static async Task<Profile[]> LoadCounted(ClientBackendSession session, List<CountTypeBotWave> conditions)
    {
        var descriptors = await session.Send<ProfileDescriptor[]>(new SendRequest
        {
            Url = session._backendUrls.Main + "/client/game/bot/generate",
            Params = new BotGenerateRequestParams<List<CountTypeBotWave>>(conditions),
            Retries = SendRequest.DefaultRetries,
        });

        Diagnostics.NoteProfilesReceived(descriptors.Length);

        // Experimental: build the batch on a worker. Item creation mostly works on the new
        // objects it makes, but not every item class and mod hook is known to be safe off
        // the main thread. Anything that throws there turns the worker off for the session
        // (and logs what threw); that batch is then built below as usual.
        if (Plugin.On(Plugin.ProfilesOffThread) && NoModHooks())
        {
            Waiting += descriptors.Length;
            await Worker.WaitAsync();
            try
            {
                return await Task.Run(() => descriptors.Select(d => new Profile(d)).ToArray());
            }
            catch (Exception ex)
            {
                Plugin.Fail(Plugin.ProfilesOffThread, ex);
            }
            finally
            {
                Worker.Release();
                Waiting -= descriptors.Length;
            }
        }

        var profiles = new Profile[descriptors.Length];
        Waiting += descriptors.Length;
        try
        {
            for (var i = 0; i < descriptors.Length; i++)
            {
                while (Plugin.On(Plugin.SpreadProfiles) && !HasBudget()) await Task.Yield();
                var start = Stopwatch.GetTimestamp();
                profiles[i] = new Profile(descriptors[i]);
                var took = Stopwatch.GetTimestamp() - start;
                _spent += took;
                _averageTicks += (took - _averageTicks) / 8;
                Waiting--;
            }
        }
        catch
        {
            Waiting = 0;
            throw;
        }

        return profiles;
    }

    // The game's spawn check (NonWavesSpawnScenario, every 15 s) fills up to the location's bot
    // count, not the spawner's own limit (BotSpawner.MaxBots). With Horde mode's ~300 it started
    // 285 bots per check, each asking the server for profiles, when the spawner would only let
    // a handful in and delay the rest. Capped here at the room the spawner has, plus a few so
    // a freed slot still refills from its delay list right away, as without this.
    private const int SpawnBuffer = 10;
    private static (int, int, int, int) _lastSpawnCheck;

    // The raid's own setting, on the host (the only place the spawn check runs): the menu's
    // "no bots" sets it, and so does Fika's No AI before the raid is created.
    private static bool NoBotsRaid() =>
        TarkovApplication.Exist(out var app) && app.CurrentRaidSettings is { } raid
        && (raid.BotSettings.BotAmount == EBotAmount.NoBots || raid.WavesSettings.BotAmount == EBotAmount.NoBots);

    private class SpawnCheckPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(NonWaveGroupScenario), nameof(NonWaveGroupScenario.TrySpawn));

        [PatchPrefix]
        private static void Prefix(ref int botFreeSlotsCount, BotsController botsController)
        {
            try
            {
                var spawner = botsController?.BotSpawner;
                if (spawner == null) return;
                var wanted = botFreeSlotsCount;
                if (Plugin.On(Plugin.CapSpawnAttempts) && NoBotsRaid())
                {
                    // A raid set to no bots (the menu's "no bots", Fika's No AI) still runs the
                    // spawn check, sized by the map rather than the raid, and its bot limit is 0,
                    // which the spawner reads as "no limit": it kept asking the server for a
                    // map's worth of bot profiles every check.
                    botFreeSlotsCount = 0;
                }
                else if (Plugin.On(Plugin.CapSpawnAttempts) && spawner.MaxBots > 0)
                {
                    var room = spawner.MaxBots - spawner.AllBotsWithDelayed + SpawnBuffer;
                    botFreeSlotsCount = Math.Max(0, Math.Min(botFreeSlotsCount, room));
                }

                // Only when something changed: otherwise a line every 15 s for the whole raid.
                var state = (wanted, botFreeSlotsCount, spawner.MaxBots, spawner.AliveAndLoadingBotsCount);
                if (Diagnostics.Logging && state != _lastSpawnCheck)
                {
                    _lastSpawnCheck = state;
                    Plugin.Log.LogInfo($"{DateTime.Now:HH:mm:ss.fff} spawn check: wants {wanted} more, trying {botFreeSlotsCount} | spawner limit {spawner.MaxBots}, " +
                                       $"in raid {spawner.AliveAndLoadingBotsCount}, waiting for profiles {BotCreationData.ProfilesLoadingProcess}, delayed {spawner.AllBotsWithDelayed - spawner.AliveAndLoadingBotsCount - BotCreationData.ProfilesLoadingProcess}");
                }
            }
            catch (Exception ex)
            {
                Plugin.Fail(Plugin.CapSpawnAttempts, ex);
            }
        }
    }

    // The game's job scheduler (where much of a new bot's setup runs) works through its queue
    // until the frame has taken FrameTicks, at most half of that per pass. FrameTicks comes
    // from the frame rate limit, but "no limit" (in raid, with the limit switched off) is
    // ignored, so the menu's limit stayed: at 60 that's 8 ms of queued work per frame, and
    // every spawn wave dragged frames down to about 60 fps. With no limit, the display's
    // refresh rate is used instead: about the same work per second, spread over more frames.
    private class JobBudgetPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(Diz.Jobs.JobScheduler), nameof(Diz.Jobs.JobScheduler.SetTargetFrameRate));

        private static double _loggedHz;

        [PatchPostfix]
        private static void Postfix(Diz.Jobs.JobScheduler __instance, int frameRate)
        {
            try
            {
                if (frameRate > 0 || !Plugin.On(Plugin.SpreadProfiles)) return;
                var hz = Screen.currentResolution.refreshRateRatio.value;
                if (hz < 30.0) return;
                __instance.FrameTicks = TimeSpan.FromMilliseconds(1000.0 / hz).Ticks;
                // The game sets it again and again; say so once per refresh rate.
                if (Diagnostics.Logging && Math.Abs(hz - _loggedHz) > 0.5)
                {
                    _loggedHz = hz;
                    Plugin.Log.LogInfo($"job scheduler: no frame limit, budget from the display ({hz:0} Hz) instead of the menu's limit");
                }
            }
            catch (Exception ex)
            {
                Plugin.Fail(Plugin.SpreadProfiles, ex);
            }
        }
    }

    private class LoadBotsPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(ClientBackendSession), nameof(ClientBackendSession.LoadBots));

        [PatchPrefix]
        private static bool Prefix(ClientBackendSession __instance, List<CountTypeBotWave> conditions, ref Task<Profile[]> __result)
        {
            try
            {
                // Taken over for the budget, and for counting the request as a loading bot.
                if (!Plugin.On(Plugin.SpreadProfiles) && !Plugin.On(Plugin.CapSpawnAttempts)) return true;
                // Only on the main thread, where a yield means the next frame.
                if (SynchronizationContext.Current == null) return true;
                __result = Load(__instance, conditions);
                return false;
            }
            catch (Exception ex)
            {
                Plugin.Fail(Plugin.SpreadProfiles, ex);
                return true;
            }
        }
    }
}
