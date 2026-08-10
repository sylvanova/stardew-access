using HarmonyLib;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewValley;
using StardewValley.Menus;

namespace stardew_access.Patches
{
    internal class ConfirmationDialogMenuPatch : IPatch
    {
        public void Apply(Harmony harmony)
        {
            harmony.Patch(
                original: AccessTools.Method(typeof(ConfirmationDialog), nameof(ConfirmationDialog.draw), [typeof(SpriteBatch)]),
                postfix: new HarmonyMethod(typeof(ConfirmationDialogMenuPatch), nameof(ConfirmationDialogMenuPatch.DrawPatch))
            );

            harmony.Patch(
                original: AccessTools.Method(typeof(ReadyCheckDialog), nameof(ReadyCheckDialog.receiveKeyPress), [typeof(Keys)]),
                postfix: new HarmonyMethod(typeof(ConfirmationDialogMenuPatch), nameof(ConfirmationDialogMenuPatch.ReadyCheckReceiveKeyPressPatch))
            );
        }

        // ReadyCheckDialog ("waiting for players" in multiplayer) overrides receiveKeyPress with an
        // empty body, so the keyboard can never close it; vanilla only supports clicking the cancel
        // button with the mouse. Restore the standard menu-close behavior for it.
        private static void ReadyCheckReceiveKeyPressPatch(ReadyCheckDialog __instance, Keys key)
        {
            try
            {
                if (Game1.options.doesInputListContain(Game1.options.menuButton, key) && __instance.readyToClose())
                {
                    __instance.exitThisMenu();
                }
            }
            catch (Exception e)
            {
                Log.Error($"An error occurred in ready check dialog key press patch:\n{e.Message}\n{e.StackTrace}");
            }
        }

        private static void DrawPatch(ConfirmationDialog __instance, string ___message)
        {
            try
            {
                int x = Game1.getMouseX(true), y = Game1.getMouseY(true);
                string translationKey = "";

                if (__instance.okButton.containsPoint(x, y))
                {
                    translationKey = "menu-confirmation_dialogue-ok_button";
                }
                else if (__instance.cancelButton.containsPoint(x, y))
                {
                    translationKey = Game1.activeClickableMenu is InviteCodeDialog
                        ? "menu-confirmation_dialogue-copy_button"
                        : "menu-confirmation_dialogue-cancel_button";
                }

                if (string.IsNullOrEmpty(translationKey))
                {
                    if (__instance is ReadyCheckDialog readyCheckDialog && readyCheckDialog.isCancelable())
                        MainClass.ScreenReader.TranslateAndSayWithMenuChecker("menu-ready_check-waiting", true,
                            new { dialogue_message = ___message });
                    else
                        MainClass.ScreenReader.SayWithMenuChecker(___message, true);
                }
                else
                    MainClass.ScreenReader.TranslateAndSayWithMenuChecker(translationKey, true, new { dialogue_message = ___message });
            }
            catch (Exception e)
            {
                Log.Error($"An error occurred in confirmation dialogue menu patch:\n{e.Message}\n{e.StackTrace}");
            }
        }
    }
}
