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
            return false;
        }

        MethodInfo? receiveButtonsChanged = AccessTools.Method(overlayType, "ReceiveButtonsChanged");
        if (receiveButtonsChanged == null)
        {
            return false;
        }

        harmony.Patch(
            original: receiveButtonsChanged,
            prefix: new HarmonyMethod(typeof(ChestsAnywhereOverlayPatch), nameof(ReceiveButtonsChangedPatch))
        );
        _applied = true;
        return true;
    }

    private static bool ReceiveButtonsChangedPatch(object __instance, object? sender, ButtonsChangedEventArgs e)
    {
        return !ChestsAnywhereIntegration.HandleOverlayButtonsChanged(__instance, e);
    }
}
