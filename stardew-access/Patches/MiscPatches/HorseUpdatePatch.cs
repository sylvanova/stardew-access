using HarmonyLib;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Characters;
using stardew_access.Features;

namespace stardew_access.Patches
{
    /// <summary>
    /// While auto walking, PathFindController briefly clears the farmer's movement
    /// directions at every tile checkpoint. Horse.update reads that single empty tick
    /// as "rider stopped" and restarts its walk animation, so the riding animation -
    /// and the real hoof sounds attached to its frames - never gets to play. This
    /// patch keeps the direction visible to the horse during active auto walking
    /// (and removes it again right after, so the game's own movement code never
    /// sees the injected value).
    /// </summary>
    internal class HorseUpdatePatch : IPatch
    {
        private static bool injectedDirection;

        public void Apply(Harmony harmony)
        {
            harmony.Patch(
                original: AccessTools.Method(typeof(Horse), nameof(Horse.update), [typeof(GameTime), typeof(GameLocation)]),
                prefix: new HarmonyMethod(typeof(HorseUpdatePatch), nameof(UpdatePrefix)),
                postfix: new HarmonyMethod(typeof(HorseUpdatePatch), nameof(UpdatePostfix))
            );
        }

        private static void UpdatePrefix(Horse __instance)
        {
            try
            {
                injectedDirection = false;
                Farmer? rider = __instance.rider;
                if (rider == null || !rider.IsLocalPlayer) return;
                if (!ObjectTracker.IsAutoWalking || rider.controller?.pathToEndPoint == null) return;

                if (rider.movementDirections.Count == 0)
                {
                    rider.movementDirections.Add(rider.FacingDirection);
                    injectedDirection = true;
                }
            }
            catch (Exception e)
            {
                Log.Error($"An error occurred in HorseUpdatePatch prefix:\n{e.Message}\n{e.StackTrace}");
            }
        }

        private static void UpdatePostfix(Horse __instance)
        {
            try
            {
                if (injectedDirection)
                {
                    __instance.rider?.movementDirections.Remove(__instance.rider.FacingDirection);
                    injectedDirection = false;
                }
            }
            catch (Exception e)
            {
                Log.Error($"An error occurred in HorseUpdatePatch postfix:\n{e.Message}\n{e.StackTrace}");
            }
        }
    }
}
