using HarmonyLib;
using Microsoft.Xna.Framework.Graphics;
using stardew_access.Translation;
using StardewValley;
using StardewValley.Menus;
using StardewValley.Quests;
using StardewValley.SpecialOrders;
using StardewValley.SpecialOrders.Objectives;

namespace stardew_access.Patches
{
    // a.k.a. Journal Menu
    internal class QuestLogPatch : IPatch
    {
        internal static bool isNarratingQuestInfo = false;
        internal static bool firstTimeInIndividualQuest = true;
        internal static bool firstTimeInList = true;

        // Vanilla only snaps the cursor in this menu when SnappyMenus (controller mode) is on,
        // so for keyboard users the cursor stays wherever it was and nothing gets hovered,
        // leaving the whole menu silent. Snap it ourselves.
        private static void SnapToComponent(QuestLog menu, int componentId)
        {
            if (menu.allClickableComponents == null)
                menu.populateClickableComponentList();

            var component = menu.getComponentWithID(componentId);
            if (component == null)
                return;

            menu.currentlySnappedComponent = component;
            menu.snapCursorToCurrentSnappedComponent();
        }

        public void Apply(Harmony harmony)
        {
            harmony.Patch(
                original: AccessTools.Method(typeof(QuestLog), nameof(QuestLog.draw), [typeof(SpriteBatch)]),
                postfix: new HarmonyMethod(typeof(QuestLogPatch), nameof(QuestLogPatch.DrawPatch))
            );
        }

        private static void DrawPatch(QuestLog __instance, int ___questPage, List<List<IQuest>> ___pages, int ___currentPage, IQuest ____shownQuest, List<string> ____objectiveText)
        {
            try
            {
                int x = Game1.getMouseX(true), y = Game1.getMouseY(true); // Mouse x and y position

                if (___questPage == -1)
                {
                    NarrateQuestList(__instance, ___pages, ___currentPage, x, y);
                }
                else
                {
                    NarrateIndividualQuest(__instance, ___currentPage, ____shownQuest, ____objectiveText, x, y);
                }
            }
            catch (Exception e)
            {
                Log.Error($"An error occurred in quest log menu patch:\n{e.Message}\n{e.StackTrace}");
            }
        }

        private static void NarrateQuestList(QuestLog __instance, List<List<IQuest>> ___pages, int ___currentPage, int x, int y)
        {
            string translationKey = "";
            object? translationTokens = null;

            if (firstTimeInList || !firstTimeInIndividualQuest)
            {
                // Menu just opened, or we returned from a quest's detail page: put the
                // cursor on the first quest so it is announced and arrow keys work from there.
                firstTimeInList = false;
                if (!firstTimeInIndividualQuest) firstTimeInIndividualQuest = true;
                if (___pages.Count > 0 && ___pages[___currentPage].Count > 0)
                {
                    SnapToComponent(__instance, 0);
                    return; // narrate the now-hovered quest next frame
                }
            }

            if (__instance.backButton != null && __instance.backButton.visible && __instance.backButton.containsPoint(x, y))
                translationKey = "common-ui-previous_page_button";
            else if (__instance.forwardButton != null && __instance.forwardButton.visible && __instance.forwardButton.containsPoint(x, y))
                translationKey = "common-ui-next_page_button";
            else if (__instance.upperRightCloseButton != null && __instance.upperRightCloseButton.visible && __instance.upperRightCloseButton.containsPoint(x, y))
                translationKey = "common-ui-close_menu_button";
            else
            {
                for (int i = 0; i < __instance.questLogButtons.Count; i++)
                {
                    if (___pages.Count <= 0 || ___pages[___currentPage].Count <= i)
                        continue;

                    if (!__instance.questLogButtons[i].containsPoint(x, y))
                        continue;

                    translationTokens = new
                    {
                        name = ___pages[___currentPage][i].GetName(),
                        days_left = ___pages[___currentPage][i].GetDaysLeft(),
                        is_completed = ___pages[___currentPage][i].ShouldDisplayAsComplete() ? 1 : 0
                    };

                    translationKey = "menu-quest_log-quest_brief";
                    break;
                }
            }

            MainClass.ScreenReader.TranslateAndSayWithMenuChecker(translationKey, true, translationTokens);
        }

        private static void NarrateIndividualQuest(QuestLog __instance, int ___currentPage, IQuest ____shownQuest, List<string> ____objectiveText, int x, int y)
        {
            if (____shownQuest == null)  return;

            bool isPrimaryInfoKeyPressed = MainClass.Config.PrimaryInfoKey.JustPressed();
            bool containsReward = __instance.HasReward() || __instance.HasMoneyReward();
            string description = ____shownQuest.GetDescription();
            string translationKey = "";

            bool justOpenedQuestDetail = firstTimeInIndividualQuest;
            if (firstTimeInIndividualQuest || (isPrimaryInfoKeyPressed && !isNarratingQuestInfo))
            {
                firstTimeInIndividualQuest = false;

                List<string> objectivesList = [];
                for (int j = 0; !____shownQuest.ShouldDisplayAsComplete() && j < ____objectiveText.Count; j++)
                {
                    string objective_info = ____objectiveText[j];
                    if (____shownQuest is SpecialOrder order)
                    {
                        OrderObjective order_objective = order.objectives[j];
                        if (order_objective.GetMaxCount() > 1 && order_objective.ShouldShowProgress())
                            objective_info = $"{order_objective.GetCount()}/{order_objective.GetMaxCount()} {objective_info}";
                    }

                    objectivesList.Add($"{j + 1}: {objective_info}");
                }

                object translationTokens = new
                {
                    is_completed = ____shownQuest.ShouldDisplayAsComplete() ? 1 : 0,
                    name = ____shownQuest.GetName(),
                    description = ____shownQuest.GetDescription(),
                    objectives_list = string.Join(", ", objectivesList),
                    days_left = ____shownQuest.GetDaysLeft(),
                    has_received_money = __instance.HasMoneyReward() ? 1 : 0,
                    received_money = ____shownQuest.GetMoneyReward(),
                };

                MainClass.ScreenReader.MenuPrefixNoQueryText = $"{Translator.Instance.Translate("menu-quest_log-quest_detail", translationTokens, TranslationCategory.Menu)}\n";
                MainClass.ScreenReader.PrevMenuQueryText = "";
                isNarratingQuestInfo = true;
                Task.Delay(200).ContinueWith(_ => { isNarratingQuestInfo = false; });

                if (justOpenedQuestDetail)
                {
                    // Mirror what vanilla does under SnappyMenus when a quest is opened:
                    // wire the detail-page neighbors and park the cursor on the back button,
                    // so the queued details above get spoken with it next frame and arrow
                    // keys reach the reward/cancel buttons.
                    if (__instance.allClickableComponents == null)
                        __instance.populateClickableComponentList();
                    var backComponent = __instance.getComponentWithID(102);
                    if (backComponent != null)
                    {
                        backComponent.rightNeighborID = -7777;
                        backComponent.downNeighborID = __instance.HasMoneyReward() ? 103 : (____shownQuest.CanBeCancelled() ? 104 : -1);
                        __instance.currentlySnappedComponent = backComponent;
                        __instance.snapCursorToCurrentSnappedComponent();
                        return; // next frame speaks the queued details together with the back button
                    }
                }
            }

            if (__instance.backButton != null && __instance.backButton.visible && __instance.backButton.containsPoint(x, y))
                translationKey = (___currentPage > 0) ? "common-ui-previous_page_button" : "common-ui-back_button";
            else if (__instance.forwardButton != null && __instance.forwardButton.visible && __instance.forwardButton.containsPoint(x, y))
                translationKey = "common-ui-next_page_button";
            else if (__instance.cancelQuestButton != null && __instance.cancelQuestButton.visible && __instance.cancelQuestButton.containsPoint(x, y))
                translationKey = "menu-quest_log-cancel_quest_button";
            else if (__instance.upperRightCloseButton != null && __instance.upperRightCloseButton.visible && __instance.upperRightCloseButton.containsPoint(x, y))
                translationKey = "common-ui-close_menu_button";
            else if (containsReward && __instance.rewardBox.containsPoint(x, y))
                translationKey = "menu-quest_log-reward_button";

            if (!string.IsNullOrEmpty(translationKey))
                MainClass.ScreenReader.TranslateAndSayWithMenuChecker(translationKey, true);
            else if (!string.IsNullOrEmpty(MainClass.ScreenReader.MenuPrefixNoQueryText))
                // Nothing is hovered for the queued quest details to piggyback on
                // (e.g. the back button couldn't be snapped to); speak them directly
                // so the description is never lost.
                MainClass.ScreenReader.SayWithMenuChecker(" ", true, customQuery: "quest-details");
        }

        internal static void Cleanup()
        {
            isNarratingQuestInfo = false;
            firstTimeInIndividualQuest = true;
            firstTimeInList = true;
        }
    }
}
