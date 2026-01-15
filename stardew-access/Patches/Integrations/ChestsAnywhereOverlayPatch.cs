using System.Reflection;
using HarmonyLib;
using StardewModdingAPI.Events;
using stardew_access.Integrations;

namespace stardew_access.Patches;

internal class ChestsAnywhereOverlayPatch : IPatch
{
    private static bool _applied;

    public void Apply(Harmony harmony)
    {
        TryApply(harmony);
    }

    internal static bool TryApply(Harmony harmony)
    {
        if (_applied)
        {
            return true;
        }

        Type? overlayType = AccessTools.TypeByName("Pathoschild.Stardew.ChestsAnywhere.Menus.Overlays.BaseChestOverlay");
        if (overlayType == null)
        {
            Log.Debug("[ChestsAnywhere] Overlay patch not applied: type not found.");
            return false;
        }

        MethodInfo? receiveButtonsChanged = AccessTools.Method(overlayType, "ReceiveButtonsChanged");
        if (receiveButtonsChanged == null)
        {
            Log.Debug("[ChestsAnywhere] Overlay patch not applied: ReceiveButtonsChanged not found.");
            return false;
        }

        harmony.Patch(
            original: receiveButtonsChanged,
            prefix: new HarmonyMethod(typeof(ChestsAnywhereOverlayPatch), nameof(ReceiveButtonsChangedPatch))
        );
        _applied = true;
        Log.Debug("[ChestsAnywhere] Overlay patch applied.");
        return true;
    }

    private static bool ReceiveButtonsChangedPatch(object __instance, object? sender, ButtonsChangedEventArgs e)
    {
        return !ChestsAnywhereIntegration.HandleOverlayButtonsChanged(__instance, e);
    }
}
