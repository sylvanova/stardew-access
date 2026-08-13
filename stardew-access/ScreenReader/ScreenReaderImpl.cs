using CrossSpeak;
using stardew_access.Translation;
using stardew_access.Utils;
using StardewValley.Menus;

namespace stardew_access.ScreenReader;

// TODO Declutter this class
public class ScreenReaderImpl : IScreenReader
{
    public string prevText = "", prevTextTile = "", prevChatText = "", prevMenuText = "";
    private string menuPrefixText = "";
    private string prevMenuPrefixText = "";
    private string menuSuffixText = "";
    private IScreenReadable? prevMenuElement = null;
    private string prevMenuSuffixText = "";
    private string prevMenuElementDescription = "";
    private string menuPrefixNoQueryText = "";
    private string menuSuffixNoQueryText = "";

    // Loop guard for the menu speech channel. All menu narrators share one dedup slot
    // (prevMenuText); when two narration sources alternate for the same on-screen state
    // (e.g. a dedicated menu patch and the generic fallback narrator), each keeps
    // replacing the other's slot value, the dedup never matches, and speech restarts
    // every frame. Track queries spoken recently: a query that keeps recurring within
    // the window is a loop and gets suppressed until the situation changes.
    private const long QueryRecurrenceWindowMs = 1000;
    private sealed class RecurringQuery { public long LastSeenMs; public int Strikes; }
    private readonly Dictionary<string, RecurringQuery> recentMenuQueries = new();

    private bool IsLoopingMenuQuery(string query)
    {
        long now = Environment.TickCount64;
        if (recentMenuQueries.TryGetValue(query, out RecurringQuery? entry))
        {
            if (now - entry.LastSeenMs < QueryRecurrenceWindowMs)
            {
                entry.LastSeenMs = now;
                // The second occurrence within the window is still spoken (deliberate
                // quick re-announcements, e.g. toggling a checkbox twice, stay audible);
                // from the third on it is treated as a loop.
                if (++entry.Strikes >= 2) return true;
            }
            else
            {
                entry.LastSeenMs = now;
                entry.Strikes = 0;
            }
            return false;
        }

        if (recentMenuQueries.Count > 64)
        {
            List<string> expired = new();
            foreach (var pair in recentMenuQueries)
                if (now - pair.Value.LastSeenMs >= QueryRecurrenceWindowMs) expired.Add(pair.Key);
            foreach (string key in expired) recentMenuQueries.Remove(key);
        }
        recentMenuQueries[query] = new RecurringQuery { LastSeenMs = now };
        return false;
    }

    public string PrevTextTile
    {
        get => prevTextTile;
        set => prevTextTile = value;
    }

    public string PrevMenuQueryText
    {
        get => prevMenuText;
        set => prevMenuText = value;
    }

    public string MenuPrefixText
    {
        get => menuPrefixText;
        set => menuPrefixText = value;
    }

    public string MenuSuffixText
    {
        get => menuSuffixText;
        set => menuSuffixText = value;
    }

    public string MenuPrefixNoQueryText
    {
        get => menuPrefixNoQueryText;
        set => menuPrefixNoQueryText = value;
    }

    public string MenuSuffixNoQueryText
    {
        get => menuSuffixNoQueryText;
        set => menuSuffixNoQueryText = value;
    }

    public BoundedQueue<string> SpokenBuffer { get; set;} = new BoundedQueue<string>(size: 20, allowDuplicacy: false);

    public void InitializeScreenReader()
    {
        Log.Info("Initializing CrossSpeak...");
        CrossSpeakManager.Instance.TrySAPI(true);
        if (!CrossSpeakManager.Instance.Initialize())
        {
            Log.Error("None of the supported screen readers is running");
            return;
        }

        string name = CrossSpeakManager.Instance.DetectScreenReader();
        if (name != null)
        {
            Log.Info($"The active screen reader driver is: {name}");
        }
        else
        {
            Log.Info("Screen reader successfully initialized.");
        }
    }

    public void CloseScreenReader() => CrossSpeakManager.Instance.Close();

    public bool Say(string text, bool interrupt, bool excludeFromBuffer = false)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (!MainClass.Config.TTS) return false;
        if (text.Contains('^')) text = text.Replace('^', '\n');

        if (!excludeFromBuffer) SpokenBuffer.Add(text);

        if (!CrossSpeakManager.Instance.Output(text, interrupt))
        {
            Log.Error($"Failed to output text: {text}");
            return false;
        }

#if DEBUG
        Log.Verbose($"Speaking(interrupt: {interrupt}) = {text}");
#endif
        return true;
    }

    public bool TranslateAndSay(string translationKey, bool interrupt, object? translationTokens = null, TranslationCategory translationCategory = TranslationCategory.Default, bool disableTranslationWarnings = false, bool excludeFromBuffer = false)
    {
        if (string.IsNullOrWhiteSpace(translationKey))
            return false;

        return Say(Translator.Instance.Translate(translationKey, translationTokens, translationCategory, disableTranslationWarnings), interrupt, excludeFromBuffer: excludeFromBuffer);
    }

    public bool SayWithChecker(string text, bool interrupt, string? customQuery = null, bool excludeFromBuffer = false)
    {
        customQuery ??= text;

        if (string.IsNullOrWhiteSpace(customQuery))
            return false;

        if (prevText == customQuery)
            return false;

        prevText = customQuery;
        return Say(text, interrupt, excludeFromBuffer: excludeFromBuffer);
    }

    public bool TranslateAndSayWithChecker(string translationKey, bool interrupt, object? translationTokens = null, TranslationCategory translationCategory = TranslationCategory.Default, string? customQuery = null, bool disableTranslationWarnings = false, bool excludeFromBuffer = false)
    {
        if (string.IsNullOrWhiteSpace(translationKey))
            return false;

        return SayWithChecker(Translator.Instance.Translate(translationKey, translationTokens, translationCategory, disableTranslationWarnings), interrupt, customQuery, excludeFromBuffer: excludeFromBuffer);
    }

    public bool SayWithMenuChecker(string text, bool interrupt, string? customQuery = null, bool excludeFromBuffer = false)
    {
        customQuery ??= text;

        if (string.IsNullOrWhiteSpace(customQuery))
            return false;

        if (prevMenuText == customQuery && prevMenuSuffixText == MenuSuffixText && prevMenuPrefixText == MenuPrefixText)
            return false;

        if (IsLoopingMenuQuery(customQuery))
            return false;

        prevMenuText = customQuery;
        prevMenuSuffixText = MenuSuffixText;
        prevMenuPrefixText = MenuPrefixText;
        bool re = Say($"{MenuPrefixNoQueryText}{MenuPrefixText}{text}{MenuSuffixText}{MenuSuffixNoQueryText}", interrupt, excludeFromBuffer: excludeFromBuffer);
        MenuPrefixNoQueryText = "";
        MenuSuffixNoQueryText = "";

        return re;
    }

    public bool TranslateAndSayWithMenuChecker(string translationKey, bool interrupt, object? translationTokens = null, TranslationCategory translationCategory = TranslationCategory.Menu, string? customQuery = null, bool disableTranslationWarnings = false, bool excludeFromBuffer = false)
    {
        if (string.IsNullOrWhiteSpace(translationKey))
            return false;

        return SayWithMenuChecker(Translator.Instance.Translate(translationKey, translationTokens, translationCategory, disableTranslationWarnings), interrupt, customQuery, excludeFromBuffer: excludeFromBuffer);
    }

    public bool SayMenuElement(IScreenReadable element, bool interrupt = true, bool excludeFromBuffer = false)
    {
        string text = element.ScreenReaderText;
        string description = element.ScreenReaderDescription;
        
        if (prevMenuElement != null && System.Object.ReferenceEquals(element, prevMenuElement) && prevMenuText == text && prevMenuSuffixText == MenuSuffixText && prevMenuPrefixText == MenuPrefixText)
            return false;

        if (IsLoopingMenuQuery(text))
            return false;

        prevMenuElement = element;
        // Also update the shared query slot: without this, any other narrator speaking in
        // between resets our dedup and this element would re-announce every frame.
        prevMenuText = text;
        prevMenuSuffixText = MenuSuffixText;
        prevMenuPrefixText = MenuPrefixText;
        bool re = Say($"{MenuPrefixNoQueryText}{MenuPrefixText}{text}\n{(prevMenuElementDescription != description || !System.Object.ReferenceEquals(element, prevMenuElement) ? description : "")}{MenuSuffixText}{MenuSuffixNoQueryText}", interrupt, excludeFromBuffer: excludeFromBuffer);
        MenuPrefixNoQueryText = "";
        MenuSuffixNoQueryText = "";
        prevMenuElementDescription = description;

        return re;
    }

    public bool SayMenuElement(string text, string description = "", bool interrupt = true, string? customQuery = null, bool excludeFromBuffer = false)
    {
        customQuery ??= text;

        if (prevMenuText == customQuery && prevMenuSuffixText == MenuSuffixText && prevMenuPrefixText == MenuPrefixText)
            return false;

        if (IsLoopingMenuQuery(customQuery))
            return false;

        prevMenuText = customQuery;
        prevMenuSuffixText = MenuSuffixText;
        prevMenuPrefixText = MenuPrefixText;
        bool re = Say($"{MenuPrefixNoQueryText}{MenuPrefixText}{text}\n{(prevMenuElementDescription != description ? description : "")}{MenuSuffixText}{MenuSuffixNoQueryText}", interrupt, excludeFromBuffer: excludeFromBuffer);
        MenuPrefixNoQueryText = "";
        MenuSuffixNoQueryText = "";
        prevMenuElementDescription = description;
        
        return re;
    }

    public bool SayWithChatChecker(string text, bool interrupt, bool excludeFromBuffer = false)
    {
        if (prevChatText == text)
            return false;

        prevChatText = text;
        return Say(text, interrupt, excludeFromBuffer: excludeFromBuffer);
    }

    public bool SayWithTileQuery(string text, int x, int y, bool interrupt, bool excludeFromBuffer = false)
    {
        string query = $"{text} x:{x} y:{y}";

        if (prevTextTile == query)
            return false;

        prevTextTile = query;
        return Say(text, interrupt, excludeFromBuffer: excludeFromBuffer);
    }

    public void Cleanup()
    {
        MainClass.ScreenReader.PrevMenuQueryText = "";
        MainClass.ScreenReader.MenuPrefixText = "";
        MainClass.ScreenReader.MenuSuffixText = "";
        prevMenuElementDescription = "";
        recentMenuQueries.Clear();
    }
}
