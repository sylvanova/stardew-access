using stardew_access.Tiles;
using stardew_access.Translation;
using stardew_access.Utils;
using StardewValley;

namespace stardew_access.Commands;

public class OtherCommands
{
    // TODO: add Refresh functionality to `AccessibleTileManager and restore this
    /*helper.ConsoleCommands.Add("refst", "Refresh static tiles", (string command, string[] args) =>
    {
        StaticTiles.LoadTilesFiles();
        StaticTiles.SetupTilesDicts();

        Log.Info("Static tiles refreshed!");
    });*/

    /// <summary>Plan an on-foot route to a tile and print it, without moving the player.</summary>
    public static void PlanFootRoute_otplan(string[] args, bool fromChatBox = false)
    {
        void Out(string text)
        {
            if (fromChatBox) Game1.chatBox.addInfoMessage(text);
            else Log.Info(text);
        }

        if (Game1.currentLocation == null || Game1.player == null)
        {
            Out("otplan: no location loaded.");
            return;
        }
        if (args.Length < 2 || !int.TryParse(args[0], out int x) || !int.TryParse(args[1], out int y))
        {
            Out("Usage: otplan <x> <y>");
            return;
        }

        var start = Game1.player.TilePoint;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var plan = FootPathfinder.PlanTo(Game1.currentLocation, start, new Microsoft.Xna.Framework.Point(x, y), allowWarpEnd: true);
        watch.Stop();

        if (plan.Path == null)
        {
            Out($"otplan: no route from {start.X},{start.Y} to {x},{y} ({watch.ElapsedMilliseconds} ms)."
                + (plan.BlockedBy != null ? $" Blocked by: {plan.BlockedBy}." : "")
                + (plan.NearestReachable is { } near ? $" Nearest reachable: {near.X},{near.Y}." : ""));
            return;
        }

        Out($"otplan: {plan.Path.Count} steps from {start.X},{start.Y} to {x},{y}, {plan.Obstacles.Count} obstacles ({watch.ElapsedMilliseconds} ms).");
        Out("  route: " + string.Join(" ", plan.Path.Select(p => $"{p.X},{p.Y}")));
        foreach (var obstacle in plan.Obstacles)
            Out($"  obstacle at {obstacle.Tile.X},{obstacle.Tile.Y}: {obstacle.Kind} \"{obstacle.Name}\" tool: {obstacle.ToolName}");
    }

    public static void RefreshScreenReader_refsr(string[] args, bool fromChatBox = false)
    {
        MainClass.ScreenReader.InitializeScreenReader();

        string text = Translator.Instance.Translate("commands-other-refresh_screen_reader",
            translationCategory: TranslationCategory.CustomCommands);

        if (fromChatBox) Game1.chatBox.addInfoMessage(text);
        else Log.Info(text);
    }

    public static void RefreshModConfig_refmc(string[] args, bool fromChatBox = false)
    {
        MainClass.Config = MainClass.ModHelper!.ReadConfig<ModConfig>();

        string text = Translator.Instance.Translate("commands-other-refresh_mod_config",
            translationCategory: TranslationCategory.CustomCommands);

        if (fromChatBox) Game1.chatBox.addInfoMessage(text);
        else Log.Info(text);
    }

    public static void RefreshUserTiles_refut(string[] args, bool fromChatBox = false)
    {
        AccessibleTileManager.Instance.LoadTileData();

        string text = Translator.Instance.Translate("commands-other-refresh_user_tiles",
            translationCategory: TranslationCategory.CustomCommands);

        if (fromChatBox) Game1.chatBox.addInfoMessage(text);
        else Log.Info(text);
    }

    public static void HnsPercentage_hnspercent(string[] args, bool fromChatBox = false)
    {
        MainClass.Config.HealthNStaminaInPercentage = !MainClass.Config.HealthNStaminaInPercentage;
        MainClass.ModHelper!.WriteConfig(MainClass.Config);

        string text = Translator.Instance.Translate("commands-other-hns_percentage_toggle",
            new { is_enabled = MainClass.Config.HealthNStaminaInPercentage ? 1 : 0 },
            translationCategory: TranslationCategory.CustomCommands);

        if (fromChatBox) Game1.chatBox.addInfoMessage(text);
        else Log.Info(text);
    }

    public static void SnapMouse(string[] args, bool fromChatBox = false)
    {
        MainClass.Config.SnapMouse = !MainClass.Config.SnapMouse;
        MainClass.ModHelper!.WriteConfig(MainClass.Config);

        string text = Translator.Instance.Translate("commands-other-snap_mouse_toggle",
            new { is_enabled = MainClass.Config.SnapMouse ? 1 : 0 },
            translationCategory: TranslationCategory.CustomCommands);

        if (fromChatBox) Game1.chatBox.addInfoMessage(text);
        else Log.Info(text);
    }

    public static void Warning(string[] args, bool fromChatBox = false)
    {
        MainClass.Config.Warning = !MainClass.Config.Warning;
        MainClass.ModHelper!.WriteConfig(MainClass.Config);

        string text = Translator.Instance.Translate("commands-other-warnings_toggle",
            new { is_enabled = MainClass.Config.Warning ? 1 : 0 },
            translationCategory: TranslationCategory.CustomCommands);

        if (fromChatBox) Game1.chatBox.addInfoMessage(text);
        else Log.Info(text);
    }

    public static void Tts(string[] args, bool fromChatBox = false)
    {
        MainClass.Config.TTS = !MainClass.Config.TTS;
        MainClass.ModHelper!.WriteConfig(MainClass.Config);

        string text = Translator.Instance.Translate("commands-other-tts_toggle",
            new { is_enabled = MainClass.Config.TTS ? 1 : 0 },
            translationCategory: TranslationCategory.CustomCommands);

        if (fromChatBox) Game1.chatBox.addInfoMessage(text);
        else Log.Info(text);
    }

    public static void RepeatLastText_rlt(string[] args, bool fromChatBox = false)
    {
        if (int.TryParse(args[0], out int index))
        {
#if DEBUG
            Log.Verbose($"OtherCommands->RepeatLastText: Repeating the {index}th from last");
#endif
            MainClass.ScreenReader.Say(MainClass.ScreenReader.SpokenBuffer[^index], true, excludeFromBuffer: true);
        }
        else
        {
            string text = "Unable to parse the index provided.";
            if (fromChatBox) Game1.chatBox.addInfoMessage(text);
            else Log.Info(text);
        }
    }
}
