namespace stardew_access.Features;

using StardewModdingAPI.Events;
using System.Text.RegularExpressions;
using Translation;
using StardewValley;
using Utils;
using StardewModdingAPI;

internal class GameStateNarrator : FeatureBase
{
    private static Item? currentSlotItem;
    private static Item? previousSlotItem;

    private static GameLocation? currentLocation;
    private static GameLocation? previousLocation;

    private static StardewValley.Characters.Horse? previousMount;

    // Per-message dedup state: a single "last message" slot cannot cope with two or more
    // messages being (re-)added at the same time — they keep displacing each other in the
    // slot and every displacement re-triggers speech (endless interrupt loop).
    private static readonly Dictionary<string, DateTime> recentHudMessageTexts = new();
    private static readonly HashSet<HUDMessage> handledHudMessages = new(ReferenceEqualityComparer.Instance);
    private static bool isNarratingHudMessage = false;

    private static GameStateNarrator? instance;

    /// <summary>
    /// Stores the last 9 spoken hud messages.
    /// </summary>
    public static BoundedQueue<string> HudMessagesBuffer = new(size: 9, allowDuplicacy: true);

    public new static GameStateNarrator Instance
    {
        get
        {
            instance ??= new GameStateNarrator();
            return instance;
        }
    }

    public override void Update(object? sender, UpdateTickedEventArgs e)
    {
        RunHudMessageNarration();

        NarrateMountState();

        if (!Context.IsPlayerFree) return;

        NarrateCurrentSlot();
        NarrateCurrentLocation();

        static async void RunHudMessageNarration()
        {
            if (!isNarratingHudMessage)
            {
                isNarratingHudMessage = true;
                NarrateHudMessages();
                await Task.Delay(300);
                isNarratingHudMessage = false;
            }
        }
    }

    /// <summary>
    /// Narrates the currently selected slot item when changing the selected slot.
    /// </summary>
    public static void NarrateCurrentSlot()
    {
        try
        {
            currentSlotItem = Game1.player.CurrentItem;

            if (currentSlotItem == null)
                return;

            if (previousSlotItem == currentSlotItem)
                return;

            previousSlotItem = currentSlotItem;
            MainClass.ScreenReader.Say(
                Translator.Instance.Translate("feature-speak_selected_slot_item_name",
                    new { slot_item_name = currentSlotItem.DisplayName }),
                true
            );
        }
        catch (Exception e)
        {
            Log.Error($"An error occurred in narrating the current slot item:\n{e.Message}\n{e.StackTrace}");
        }
    }


    /// <summary>
    /// Announces mounting and dismounting the horse, whatever the cause
    /// (manual, auto walk, warps, action button), so the player always knows
    /// whether they are riding.
    /// </summary>
    public static void NarrateMountState()
    {
        try
        {
            if (!Context.IsWorldReady)
            {
                previousMount = null;
                return;
            }

            StardewValley.Characters.Horse? currentMount = Game1.player?.mount;
            if (currentMount == previousMount)
                return;

            StardewValley.Characters.Horse? dismountedFrom = previousMount;
            previousMount = currentMount;

            if (currentMount != null)
            {
                MainClass.ScreenReader.TranslateAndSay("feature-mount_state-mounted", true,
                    new
                    {
                        name = GetMountName(currentMount),
                        is_grid_active = MainClass.Config.GridMovementActive ? 1 : 0,
                    });
            }
            else
            {
                MainClass.ScreenReader.TranslateAndSay("feature-mount_state-dismounted", true,
                    new { name = GetMountName(dismountedFrom) });
            }
        }
        catch (Exception e)
        {
            Log.Error($"An error occurred in narrating the mount state:\n{e.Message}\n{e.StackTrace}");
        }
    }

    private static string GetMountName(StardewValley.Characters.Horse? mount)
    {
        string? name = mount?.displayName;
        return string.IsNullOrWhiteSpace(name)
            ? Translator.Instance.Translate("feature-mount_state-default_mount_name")
            : name;
    }

    /// <summary>
    /// Narrates the current location name when moving to a new location.
    /// </summary>
    public static void NarrateCurrentLocation()
    {
        try
        {
            currentLocation = Game1.currentLocation;

            if (currentLocation == null)
                return;

            if (previousLocation == currentLocation)
                return;

            previousLocation = currentLocation;
            MainClass.ScreenReader.Say(
                Translator.Instance.Translate("feature-speak_location_name",
                    new { location_name = currentLocation.GetParentLocation() is Farm ? currentLocation.Name : currentLocation.DisplayName }),
                true
            );
        }
        catch (Exception e)
        {
            Log.Error($"An error occurred in narrating the current location:\n{e.Message}\n{e.StackTrace}");
        }
    }

    /// <summary>
    /// Narrates the HUD messages.
    /// </summary>
    public static void NarrateHudMessages()
    {
        try
        {
            if (Game1.hudMessages.Count <= 0)
            {
                if (handledHudMessages.Count > 0) handledHudMessages.Clear();
                return;
            }

            var now = DateTime.Now;
            TimeSpan duplicateWindow = TimeSpan.FromMilliseconds(MainClass.Config.HudDuplicateMessageTimeout);
            List<string>? newTexts = null;

            // Each message object is handled at most once; its text is spoken at most once
            // per duplicate window even if the game re-adds it as a new object every tick.
            foreach (HUDMessage message in Game1.hudMessages)
            {
                if (message is null || !handledHudMessages.Add(message)) continue;
                string toSpeak = message.message ?? "";
                if (string.IsNullOrWhiteSpace(toSpeak)) continue;
                string normalized = Regex.Replace(toSpeak, "[0-9]+", "").Trim();
                if (recentHudMessageTexts.TryGetValue(normalized, out DateTime spokenAt) && (now - spokenAt) < duplicateWindow)
                    continue;
                recentHudMessageTexts[normalized] = now;
                (newTexts ??= new List<string>()).Add(toSpeak);
                HudMessagesBuffer.Add(toSpeak);
            }

            handledHudMessages.RemoveWhere(m => !Game1.hudMessages.Contains(m));
            if (recentHudMessageTexts.Count > 32)
            {
                List<string> expired = new();
                foreach (var pair in recentHudMessageTexts)
                    if ((now - pair.Value) >= duplicateWindow) expired.Add(pair.Key);
                foreach (string key in expired) recentHudMessageTexts.Remove(key);
            }

            if (newTexts is not null)
                MainClass.ScreenReader.Say(string.Join(", ", newTexts), true);
        }
        catch (Exception e)
        {
            Log.Error($"An error occurred in narrating the hud messages:\n{e.Message}\n{e.StackTrace}");
        }
    }
}
