using HarmonyLib;
using StardewValley;

namespace stardew_access.Patches;

internal class Game1Patch : IPatch
{
    public void Apply(Harmony harmony)
    {
        harmony.Patch(
                original: AccessTools.Method(typeof(Game1), nameof(Game1.closeTextEntry)),
                prefix: new HarmonyMethod(typeof(Game1Patch), nameof(Game1Patch.CloseTextEntryPatch))
        );

        harmony.Patch(
                original: AccessTools.Method(typeof(Game1), nameof(Game1.exitActiveMenu)),
                prefix: new HarmonyMethod(typeof(Game1Patch), nameof(Game1Patch.ExitActiveMenuPatch))
        );
    }

    private static bool ExitActiveMenuPatch()
    {
        try
        {
            if (Game1.activeClickableMenu == null) return true;
            Log.Debug($"Game1Patch: Closing {Game1.activeClickableMenu.GetType()} menu, performing cleanup...");
            IClickableMenuPatch.Cleanup(Game1.activeClickableMenu);
        }
        catch (Exception e)
        {
            Log.Error($"An error occurred in exit active menu patch:\n{e.Message}\n{e.StackTrace}");
        }

        return true;
    }

    private static void CloseTextEntryPatch()
    {
        TextBoxPatch.activeTextBoxes = "";
    }
}
