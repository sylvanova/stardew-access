using StardewValley;
using StardewValley.Menus;
using stardew_access.Translation;
using HarmonyLib;
using Microsoft.Xna.Framework.Graphics;

namespace stardew_access.Patches
{
    internal class NumberSelectionMenuPatch : IPatch
    {
        private static bool firstTimeInMenu = true;
        private static string previousValueNPriceText = "";
        private static string previousHoveredButton = "";

        public void Apply(Harmony harmony)
        {
            harmony.Patch(
                original: AccessTools.Method(typeof(NumberSelectionMenu), nameof(NumberSelectionMenu.draw), [typeof(SpriteBatch)]),
                postfix: new HarmonyMethod(typeof(NumberSelectionMenuPatch), nameof(NumberSelectionMenuPatch.DrawPatch))
            );
        }

        private static void DrawPatch(
            NumberSelectionMenu __instance,
            string ___message,
            int ___currentValue,
            int ___price,
            TextBox ___numberSelectedBox
        )
        {
            try
            {
                if (firstTimeInMenu)
                {
                    firstTimeInMenu = false;
                    // The number box stays focused (the game focuses it itself) so the amount
                    // can be typed directly; enter confirms and escape cancels the menu.
                    // Clear the "0" placeholder so typing "50" doesn't produce "050".
                    if (___numberSelectedBox.Text == "0")
                        ___numberSelectedBox.Text = "";
                    MainClass.ScreenReader.TranslateAndSayWithMenuChecker("menu-number_selection-opened_info", true,
                        new { message = ___message, value = ___currentValue });
                    return;
                }

                int totalPrice = (___price <= 0) ? 0 : ___price * ___currentValue;
                string valueNPriceText = Translator.Instance.Translate("menu-number_selection-value_and_price_info",
                    new { value = ___currentValue, price = totalPrice }, TranslationCategory.Menu
                );

                if (TextBoxPatch.IsAnyTextBoxActive)
                {
                    // Typed digits are announced by TextBoxPatch; append the total price when there is one.
                    if (___price > 0 && valueNPriceText != previousValueNPriceText)
                    {
                        previousValueNPriceText = valueNPriceText;
                        MainClass.ScreenReader.Say(valueNPriceText, false);
                    }
                    return;
                }

                string toSpeak = "", hoveredButton = "";
                int x = Game1.getMouseX(true), y = Game1.getMouseY(true); // Mouse x and y position

                if (__instance.okButton != null && __instance.okButton.containsPoint(x, y))
                    hoveredButton = Translator.Instance.Translate("common-ui-ok_button", TranslationCategory.Menu);
                else if (__instance.cancelButton != null && __instance.cancelButton.containsPoint(x, y))
                    hoveredButton = Translator.Instance.Translate("common-ui-cancel_button", TranslationCategory.Menu);
                else if (__instance.leftButton != null && __instance.leftButton.containsPoint(x, y))
                    hoveredButton = Translator.Instance.Translate("menu-number_selection-button-left_button", TranslationCategory.Menu);
                else if (__instance.rightButton != null && __instance.rightButton.containsPoint(x, y))
                    hoveredButton = Translator.Instance.Translate("menu-number_selection-button-right_button", TranslationCategory.Menu);
                else if (GetHoveredDigitPadButton(__instance, x, y) is string digitButton)
                    hoveredButton = digitButton;
                else
                    return; // Skips if no button is hovered, this usually happens when the menu is transitioning or fading in.

                if (valueNPriceText != previousValueNPriceText)
                {
                    previousValueNPriceText = valueNPriceText;
                    toSpeak = $"{toSpeak} {valueNPriceText}";
                }

                if (hoveredButton != previousHoveredButton)
                {
                    previousHoveredButton = hoveredButton;
                    toSpeak = $"{toSpeak} {hoveredButton}";
                }

                MainClass.ScreenReader.SayWithMenuChecker(toSpeak, true);
            }
            catch (Exception e)
            {
                Log.Error($"An error occurred in number selection menu patch:\n{e.Message}\n{e.StackTrace}");
            }
        }

        // DigitEntryMenu (used for example when sending money to another player) additionally
        // shows an on-screen number pad with buttons 1-9, C (clear) and 0.
        private static string? GetHoveredDigitPadButton(NumberSelectionMenu menu, int x, int y)
        {
            if (menu.GetType().Name != "DigitEntryMenu")
                return null;

            if (AccessTools.Field(menu.GetType(), "digits")?.GetValue(menu) is not List<ClickableComponent> digits)
                return null;

            foreach (ClickableComponent digit in digits)
            {
                if (!digit.containsPoint(x, y))
                    continue;

                return digit.name == "c"
                    ? Translator.Instance.Translate("menu-number_selection-button-clear_button", TranslationCategory.Menu)
                    : Translator.Instance.Translate("menu-number_selection-button-digit_button",
                        new { digit = digit.name }, TranslationCategory.Menu);
            }

            return null;
        }

        internal static void Cleanup()
        {
            firstTimeInMenu = true;
            previousValueNPriceText = "";
            previousHoveredButton = "";
        }
    }
}
