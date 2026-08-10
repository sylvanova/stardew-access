using HarmonyLib;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Characters;
using stardew_access.Features;

namespace stardew_access.Patches;

/// <summary>
/// Keeps vanilla horse animation alive while Object Tracker supplies movement through
/// a PathFindController instead of a held direction key.
/// </summary>
internal sealed class MountedAutoWalkAnimationPatch : IPatch
{
    private sealed record AnimationState(
        Horse Horse,
        List<FarmerSprite.AnimationFrame> Frames,
        int AnimationIndex,
        float Timer,
        int CurrentFrame,
        int OldFrame
    );

    public void Apply(Harmony harmony)
    {
        harmony.Patch(
            original: AccessTools.Method(typeof(Farmer), nameof(Farmer.Halt)),
            prefix: new HarmonyMethod(typeof(MountedAutoWalkAnimationPatch), nameof(HaltPrefix)),
            postfix: new HarmonyMethod(typeof(MountedAutoWalkAnimationPatch), nameof(HaltPostfix))
        );
        harmony.Patch(
            original: AccessTools.Method(typeof(Horse), nameof(Horse.update), [typeof(GameTime), typeof(GameLocation)]),
            prefix: new HarmonyMethod(typeof(MountedAutoWalkAnimationPatch), nameof(HorseUpdatePrefix)),
            postfix: new HarmonyMethod(typeof(MountedAutoWalkAnimationPatch), nameof(HorseUpdatePostfix))
        );
    }

    private static void HaltPrefix(Farmer __instance, out AnimationState? __state)
    {
        __state = null;
        if (!ObjectTracker.IsAutoWalking || !__instance.IsLocalPlayer
            || __instance.controller?.pathToEndPoint == null || __instance.mount is not Horse horse
            || horse.Sprite.CurrentAnimation == null)
        {
            return;
        }

        AnimatedSprite sprite = horse.Sprite;
        __state = new AnimationState(
            horse,
            [.. sprite.CurrentAnimation],
            sprite.currentAnimationIndex,
            sprite.timer,
            sprite.currentFrame,
            sprite.oldFrame
        );
    }

    private static void HaltPostfix(Farmer __instance, AnimationState? __state)
    {
        if (__state == null || !ObjectTracker.IsAutoWalking || __instance.mount != __state.Horse)
            return;

        AnimatedSprite sprite = __state.Horse.Sprite;
        sprite.CurrentAnimation = __state.Frames;
        sprite.currentAnimationIndex = __state.AnimationIndex;
        sprite.timer = __state.Timer;
        sprite.currentFrame = __state.CurrentFrame;
        sprite.oldFrame = __state.OldFrame;
        sprite.UpdateSourceRect();
    }

    private static void HorseUpdatePrefix(Horse __instance, out int? __state)
    {
        __state = null;
        Farmer? rider = __instance.rider;
        if (!ObjectTracker.IsAutoWalking || rider == null || !rider.IsLocalPlayer
            || rider.controller?.pathToEndPoint == null || rider.movementDirections.Count != 0)
        {
            return;
        }

        __state = rider.FacingDirection;
        rider.movementDirections.Add(__state.Value);
    }

    private static void HorseUpdatePostfix(Horse __instance, int? __state)
    {
        if (__state.HasValue)
            __instance.rider?.movementDirections.Remove(__state.Value);
    }
}
