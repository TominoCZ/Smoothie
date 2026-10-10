using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading.Tasks;
using Comfort.Common;
using EFT;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;

namespace Smoothie;

// Bots spawning while you host (or play solo) freeze the game: a group is built in one go,
// and a burst of new bots switches its AI on in the same frame. Both are spread out here,
// only at points where the game already waits, so nothing ever sees a half-built bot.
internal static class BotSpawns
{
    internal static void Enable()
    {
        new WakePatch().Enable();
        new CreatorPatch().Enable();
        new ProfilesPatch().Enable();
    }

    // A new bot waits in PreActive until its weapon is ready, doing nothing else, for an
    // unknown number of frames; then BotOwner.UpdateManual switches its AI on (method_10:
    // brain, SAIN, BigBrain layers). Waiting a frame longer is the same state for longer,
    // so at most one bot wakes per frame instead of a whole wave at once.
    private class WakePatch : ModulePatch
    {
        private static int _wokeFrame = -1;

        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(BotOwner), nameof(BotOwner.UpdateManual));

        [PatchPrefix]
        private static bool Prefix(BotOwner __instance)
        {
            try
            {
                if (__instance.BotState != EBotState.PreActive || !Plugin.On(Plugin.StaggerSpawns)) return true;

                // Not ready yet: vanilla does nothing this frame either, so don't take the slot.
                if (!__instance.WeaponManager.IsReady) return true;
                if (_wokeFrame == Time.frameCount) return false;
                _wokeFrame = Time.frameCount;
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Fail(Plugin.StaggerSpawns, ex);
                return true;
            }
        }
    }

    // BotCreatorClient builds each bot by awaiting its player creator (Fika's or the game's),
    // then creates the BotOwner right after. A group is a loop over that await, so with
    // nothing to wait on, a whole group lands in one frame. Fika itself already waits
    // here, 250 ms or more per bot, whenever another player is connected, so a wait at
    // this point is a path the game, Fika and the mods already handle. Here it is one
    // frame: at most one bot is built per frame.
    private class CreatorPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Constructor(typeof(BotCreatorClient),
                new[] { typeof(IBotGame), typeof(IBotProfileCreator), typeof(Func<GameWorld, Profile, Vector3, Task<LocalPlayer>>) });

        [PatchPostfix]
        private static void Postfix(BotCreatorClient __instance)
        {
            var create = __instance._playerCreator;
            if (create == null) return;
            __instance._playerCreator = (world, profile, position) => OneAtATime(create, world, profile, position);
        }

        private static int _builtFrame = -1;

        private static async Task<LocalPlayer> OneAtATime(Func<GameWorld, Profile, Vector3, Task<LocalPlayer>> create,
            GameWorld world, Profile profile, Vector3 position)
        {
            // Only on the main thread, where a yield means the next frame.
            if (Plugin.On(Plugin.StaggerSpawns) && System.Threading.SynchronizationContext.Current != null)
            {
                while (_builtFrame == Time.frameCount) await Task.Yield();

                // The raid ended while waiting: CreateBot skips a null player.
                if (!Singleton<GameWorld>.Instantiated || Singleton<GameWorld>.Instance != world) return null!;
                _builtFrame = Time.frameCount;
            }

            return await create(world, profile, position);
        }
    }

    // Bot profiles come from the SPT server in batches (a wave, or the game topping up its
    // spare profiles). The reply is parsed in the background, but every profile is then
    // built (its whole inventory as items) on the main thread in one frame: 100-700 ms
    // freezes each time a batch arrives. Here the same request is made and the profiles
    // are built one per frame. Nothing sees them before the whole batch is done, so to the
    // rest of the game this is the same as the server answering a few frames later.
    private class ProfilesPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(ClientBackendSession), nameof(ClientBackendSession.LoadBots));

        [PatchPrefix]
        private static bool Prefix(ClientBackendSession __instance, List<CountTypeBotWave> conditions, ref Task<Profile[]> __result)
        {
            // Only on the main thread, where a yield means the next frame.
            if (!Plugin.On(Plugin.StaggerSpawns) || System.Threading.SynchronizationContext.Current == null) return true;
            __result = OnePerFrame(__instance, conditions);
            return false;
        }

        // The game's LoadBots, with a frame between profiles.
        private static async Task<Profile[]> OnePerFrame(ClientBackendSession session, List<CountTypeBotWave> conditions)
        {
            var descriptors = await session.Send<ProfileDescriptor[]>(new SendRequest
            {
                Url = session._backendUrls.Main + "/client/game/bot/generate",
                Params = new BotGenerateRequestParams<List<CountTypeBotWave>>(conditions),
                Retries = SendRequest.DefaultRetries,
            });

            var profiles = new Profile[descriptors.Length];
            for (var i = 0; i < descriptors.Length; i++)
            {
                if (i > 0) await Task.Yield();
                var watch = Stopwatch.StartNew();
                profiles[i] = new Profile(descriptors[i]);
                if (Diagnostics.Profiling && watch.ElapsedMilliseconds > 20)
                {
                    Plugin.Log.LogInfo($"{DateTime.Now:HH:mm:ss.fff} bot profile {i + 1}/{descriptors.Length} built in {watch.ElapsedMilliseconds} ms");
                }
            }

            return profiles;
        }
    }
}
