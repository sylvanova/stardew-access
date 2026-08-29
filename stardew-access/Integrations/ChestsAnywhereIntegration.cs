using System.Reflection;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using stardew_access.Patches;
using StardewValley;
using StardewValley.Menus;

namespace stardew_access.Integrations;

internal static class ChestsAnywhereIntegration
{
    private const string ModId = "Pathoschild.ChestsAnywhere";
    private const string ChestOverlayTypeName = "Pathoschild.Stardew.ChestsAnywhere.Menus.Overlays.ChestOverlay";

    private static object? _modInstance;
    private static FieldInfo? _currentOverlayField;
    private static FieldInfo? _forMenuInstanceField;
    private static object? _i18nTranslations;
    private static MethodInfo? _i18nGetMethod;
    private static bool _checked;

    private static IClickableMenu? _lastMenu;
    private static object? _lastOverlay;
    private static string? _lastCategory;
    private static string? _lastChest;
    private static string? _pendingCategory;
    private static string? _pendingChest;
    private static object? _pendingInventory;
    private static bool _useNonInterruptNextItem;
    private static int _editNavIndex = -1;
    private static bool _isEditFormOpen;
    private static string? _lastOverlayHoverText;
    private static string? _lastOverlayElementKey;

    private static readonly Dictionary<string, FieldInfo?> FieldCache = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, PropertyInfo?> PropertyCache = new(StringComparer.Ordinal);

    internal static bool TryHandleAnnouncement(IClickableMenu menu, object? currentInventory, bool isChestHovered)
    {
        string? announcement = GetPendingAnnouncement(menu, currentInventory);
        if (string.IsNullOrWhiteSpace(announcement) || !isChestHovered)
        {
            return false;
        }

        MainClass.ScreenReader.SayWithMenuChecker(announcement, true);
        MarkAnnouncementSpoken();
        _useNonInterruptNextItem = true;
        return true;
    }

    internal static void CacheOverlayIfAvailable(IClickableMenu menu)
    {
        if (TryGetOverlay(menu, out object? overlay) && overlay != null)
        {
            _lastOverlay = overlay;
        }
    }

    internal static bool ShouldUseNonInterruptNextItem() => _useNonInterruptNextItem;

    internal static void ClearNonInterruptNextItem() => _useNonInterruptNextItem = false;

    internal static void MarkAnnouncementSpoken()
    {
        if (_pendingCategory != null)
        {
            _lastCategory = _pendingCategory;
        }

        if (_pendingChest != null)
        {
            _lastChest = _pendingChest;
        }

        _pendingCategory = null;
        _pendingChest = null;
        _pendingInventory = null;
    }

    internal static bool TrySpeakOverlayUi(IClickableMenu menu)
    {
        if (!TryGetOverlay(menu, out object? overlay))
        {
            _lastOverlayHoverText = null;
            _lastOverlayElementKey = null;
            return false;
        }

        if (overlay == null)
        {
            _lastOverlayHoverText = null;
            _lastOverlayElementKey = null;
            return false;
        }

        _lastOverlay = overlay;
        if (TryToggleEditForm(overlay))
        {
            return true;
        }

        string? hoverText = GetStringMemberValue(overlay, "HoverText");
        if (!string.IsNullOrWhiteSpace(hoverText))
        {
            if (!string.Equals(hoverText, _lastOverlayHoverText, StringComparison.Ordinal))
            {
                _lastOverlayHoverText = hoverText;
                _lastOverlayElementKey = null;
                MainClass.ScreenReader.SayWithMenuChecker(hoverText, true);
                return true;
            }

            return false;
        }

        _lastOverlayHoverText = null;

        _isEditFormOpen = IsEditFormActive(overlay);
        if (!_isEditFormOpen)
        {
            _lastOverlayElementKey = null;
            return false;
        }

        int x = Game1.getMouseX(true);
        int y = Game1.getMouseY(true);

        if (TryFocusFirstEditElement(overlay))
        {
            return true;
        }

        if (TrySpeakEditElement(overlay, x, y))
        {
            return true;
        }

        _lastOverlayElementKey = null;
        return false;
    }

    private static void Reset()
    {
        _lastMenu = null;
        _lastOverlay = null;
        _lastCategory = null;
        _lastChest = null;
        _pendingCategory = null;
        _pendingChest = null;
        _pendingInventory = null;
        _lastOverlayHoverText = null;
        _lastOverlayElementKey = null;
        _useNonInterruptNextItem = false;
        _editNavIndex = -1;
        _isEditFormOpen = false;
    }

    internal static void NotifyMenuClosed(IClickableMenu menu)
    {
        if (_lastMenu != null && ReferenceEquals(menu, _lastMenu))
        {
            Reset();
        }
    }

    internal static void HandleButtonsChanged(ButtonsChangedEventArgs e)
    {
        if (e is null || !e.Pressed.Any())
        {
            return;
        }

        if (Game1.activeClickableMenu is not IClickableMenu menu)
        {
            return;
        }

        if (!TryGetOverlay(menu, out object? overlay) || overlay == null)
        {
            return;
        }

        if (!IsEditFormActive(overlay))
        {
            return;
        }

        if (IsAnyEditTextBoxSelected(overlay))
        {
            return;
        }

        SuppressMovementButtons(e.Pressed);

        bool upPressed = IsConfiguredButtonPressed(Game1.options.moveUpButton, e.Pressed);
        bool downPressed = IsConfiguredButtonPressed(Game1.options.moveDownButton, e.Pressed);
        if (!upPressed && !downPressed)
        {
            return;
        }

        int direction = upPressed && !downPressed ? -1 : 1;
        TryHandleEditNavigation(overlay, direction);
    }

    private static void SuppressMovementButtons(IEnumerable<SButton> pressed)
    {
        SuppressConfiguredButtons(Game1.options.moveUpButton, pressed);
        SuppressConfiguredButtons(Game1.options.moveDownButton, pressed);
        SuppressConfiguredButtons(Game1.options.moveLeftButton, pressed);
        SuppressConfiguredButtons(Game1.options.moveRightButton, pressed);
    }

    private static void SuppressConfiguredButtons(InputButton[] configuredButtons, IEnumerable<SButton> pressed)
    {
        foreach (InputButton inputButton in configuredButtons)
        {
            SButton button = inputButton.ToSButton();
            if (pressed.Contains(button))
            {
                MainClass.ModHelper?.Input.Suppress(button);
            }
        }
    }

    internal static bool TryHandleEnterPress(ButtonPressedEventArgs e)
    {
        if (e is null)
        {
            return false;
        }

        if (e.Button != SButton.Enter)
        {
            return false;
        }

        if (Game1.activeClickableMenu is not IClickableMenu menu)
        {
            return false;
        }

        if (!TryGetCurrentOverlay(out object? overlay) || overlay == null)
        {
            if (_lastOverlay != null && IsChestOverlayType(_lastOverlay))
            {
                overlay = _lastOverlay;
            }
            else
            {
                return false;
            }
        }

        if (!_isEditFormOpen && !IsEditFormActive(overlay))
        {
            return false;
        }

        if (!IsAnyEditTextBoxSelected(overlay))
        {
            return false;
        }

        DeselectEditTextBoxes(overlay);
        _editNavIndex = -1;
        _lastOverlayElementKey = null;
        _isEditFormOpen = true;
        MainClass.ModHelper?.Input.Suppress(e.Button);
        return true;
    }

    internal static bool TrySuppressPrimaryInfoKeyWhileEditing(ButtonPressedEventArgs e)
    {
        if (e is null)
        {
            return false;
        }

        if (!MainClass.Config.PrimaryInfoKey.JustPressed())
        {
            return false;
        }

        if (!TextBoxPatch.IsAnyTextBoxActive)
        {
            return false;
        }

        if (Game1.activeClickableMenu is not IClickableMenu menu)
        {
            return false;
        }

        if (!TryGetOverlay(menu, out object? overlay) || overlay == null)
        {
            if (_lastOverlay != null && IsChestOverlayType(_lastOverlay))
            {
                overlay = _lastOverlay;
            }
            else
            {
                return false;
            }
        }

        if (!IsEditFormActive(overlay))
        {
            return false;
        }

        if (!IsAnyEditTextBoxSelected(overlay))
        {
            return false;
        }

        MainClass.ModHelper?.Input.Suppress(e.Button);
        return true;
    }

    internal static bool HandleOverlayButtonsChanged(object overlay, ButtonsChangedEventArgs e)
    {
        if (overlay == null || e == null || !e.Pressed.Any())
        {
            return false;
        }

        if (!e.Pressed.Contains(SButton.Enter))
        {
            return false;
        }

        if (!IsEditFormActive(overlay))
        {
            return false;
        }

        if (!IsAnyEditTextBoxSelected(overlay))
        {
            return false;
        }

        DeselectEditTextBoxes(overlay);
        _editNavIndex = -1;
        _lastOverlayElementKey = null;
        _isEditFormOpen = true;
        MainClass.ModHelper?.Input.Suppress(SButton.Enter);
        return true;
    }

    private static void UpdatePending(object overlay)
    {
        object? chest = GetMemberValue(overlay, "Chest");
        string? category = GetStringMemberValue(chest, "DisplayCategory")
            ?? GetStringMemberValue(overlay, "SelectedCategory");
        string? chestName = GetStringMemberValue(chest, "DisplayName");
        object? container = GetMemberValue(chest, "Container");
        object? inventory = GetMemberValue(container, "Inventory");

        if (string.IsNullOrWhiteSpace(category) && string.IsNullOrWhiteSpace(chestName))
        {
            _pendingCategory = null;
            _pendingChest = null;
            _pendingInventory = null;
            return;
        }

        bool categoryChanged = !string.Equals(category, _lastCategory, StringComparison.Ordinal);
        bool chestChanged = !string.Equals(chestName, _lastChest, StringComparison.Ordinal);

        if (categoryChanged || chestChanged)
        {
            _pendingCategory = categoryChanged ? category : null;
            _pendingChest = chestChanged ? chestName : null;
            _pendingInventory = inventory;
        }
        else
        {
            _pendingCategory = null;
            _pendingChest = null;
            _pendingInventory = null;
        }
    }

    private static string? GetPendingAnnouncement(IClickableMenu menu, object? currentInventory)
    {
        if (!TryGetOverlay(menu, out object? overlay))
        {
            return null;
        }
        if (overlay == null)
        {
            return null;
        }

        if (!ReferenceEquals(_lastMenu, menu))
        {
            _lastMenu = menu;
        }
        _lastOverlay = overlay;

        UpdatePending(overlay);

        if (string.IsNullOrWhiteSpace(_pendingCategory) && string.IsNullOrWhiteSpace(_pendingChest))
        {
            return null;
        }

        if (_pendingInventory != null && currentInventory != null && !ReferenceEquals(_pendingInventory, currentInventory))
        {
            return null;
        }

        List<string> parts = [];
        if (!string.IsNullOrWhiteSpace(_pendingCategory))
        {
            parts.Add(_pendingCategory);
        }

        if (!string.IsNullOrWhiteSpace(_pendingChest))
        {
            parts.Add(_pendingChest);
        }

        return parts.Count > 0 ? string.Join(", ", parts) : null;
    }

    private static bool TryGetOverlay(IClickableMenu menu, out object? overlay)
    {
        overlay = null;
        EnsureLoaded();
        if (_modInstance == null || _currentOverlayField == null)
        {
            return false;
        }

        object? currentOverlay = GetPerScreenValue(_currentOverlayField);
        if (currentOverlay == null)
        {
            return false;
        }

        Type overlayType = currentOverlay.GetType();
        if (!string.Equals(overlayType.FullName, ChestOverlayTypeName, StringComparison.Ordinal))
        {
            return false;
        }

        if (_forMenuInstanceField != null)
        {
            if (GetPerScreenValue(_forMenuInstanceField) is IClickableMenu forMenuInstance &&
                !ReferenceEquals(forMenuInstance, menu))
            {
                return false;
            }
        }

        overlay = currentOverlay;
        return true;
    }

    private static bool TryGetCurrentOverlay(out object? overlay)
    {
        overlay = null;
        EnsureLoaded();
        if (_modInstance == null || _currentOverlayField == null)
        {
            return false;
        }

        object? currentOverlay = GetPerScreenValue(_currentOverlayField);
        if (currentOverlay == null)
        {
            return false;
        }

        Type overlayType = currentOverlay.GetType();
        if (!string.Equals(overlayType.FullName, ChestOverlayTypeName, StringComparison.Ordinal))
        {
            return false;
        }

        overlay = currentOverlay;
        _lastOverlay = currentOverlay;
        return true;
    }

    private static bool IsChestOverlayType(object overlay)
        => string.Equals(overlay.GetType().FullName, ChestOverlayTypeName, StringComparison.Ordinal);

    private static void DeselectEditTextBoxes(object overlay)
    {
        SetBoolMemberValue(GetMemberValue(overlay, "EditNameField"), "Selected", false);
        SetBoolMemberValue(GetMemberValue(overlay, "EditCategoryField"), "Selected", false);
        SetBoolMemberValue(GetMemberValue(overlay, "EditOrderField"), "Selected", false);
    }

    private static void EnsureLoaded()
    {
        if (_checked)
        {
            return;
        }

        _checked = true;
        var modInfo = MainClass.ModHelper?.ModRegistry.Get(ModId);
        if (modInfo == null)
        {
            return;
        }

        _modInstance = GetMemberValue(modInfo, "Mod");
        if (_modInstance == null)
        {
            return;
        }
        Type modType = _modInstance.GetType();
        _currentOverlayField = modType.GetField("CurrentOverlay", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        _forMenuInstanceField = modType.GetField("ForMenuInstance", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        Type? i18nType = _modInstance.GetType().Assembly.GetType("Pathoschild.Stardew.ChestsAnywhere.I18n");
        if (i18nType != null)
        {
            _i18nTranslations = GetMemberValue(i18nType, "Translations");
            if (_i18nTranslations != null)
            {
                _i18nGetMethod = _i18nTranslations.GetType()
                    .GetMethods(BindingFlags.Instance | BindingFlags.Public)
                    .FirstOrDefault(method =>
                    {
                        if (!string.Equals(method.Name, "Get", StringComparison.Ordinal)) return false;
                        ParameterInfo[] parameters = method.GetParameters();
                        return parameters.Length >= 1 && parameters[0].ParameterType == typeof(string);
                    });
            }
        }
    }

    private static object? GetPerScreenValue(FieldInfo field)
    {
        object? perScreen = field.GetValue(_modInstance);
        if (perScreen == null)
        {
            return null;
        }

        PropertyInfo? valueProperty = GetPropertyCached(perScreen.GetType(), "Value");
        return valueProperty?.GetValue(perScreen);
    }

    private static object? GetMemberValue(object? instance, string name)
    {
        if (instance == null)
        {
            return null;
        }

        Type type = instance.GetType();
        FieldInfo? field = GetFieldCached(type, name);
        if (field != null)
        {
            return field.GetValue(instance);
        }

        PropertyInfo? prop = GetPropertyCached(type, name);
        return prop?.GetValue(instance);
    }

    private static string? GetStringMemberValue(object? instance, string name)
        => GetMemberValue(instance, name) as string;

    private static bool IsEditFormActive(object overlay)
    {
        object? activeElement = GetMemberValue(overlay, "ActiveElement")
            ?? GetMemberValue(overlay, "ActiveElementImpl");
        return activeElement != null &&
               string.Equals(activeElement.ToString(), "EditForm", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TrySpeakEditElement(object overlay, int x, int y)
    {
        if (TrySpeakForClickable(GetMemberValue(overlay, "EditButton"), x, y, "edit-button", TranslateOrFallback("button.edit-chest", "Edit chest")))
        {
            return true;
        }

        if (TrySpeakForClickable(GetMemberValue(overlay, "EditSaveButtonArea"), x, y, "edit-save", TranslateOrFallback("button.save", "Save")))
        {
            return true;
        }

        if (TrySpeakForClickable(GetMemberValue(overlay, "EditResetButtonArea"), x, y, "edit-reset", TranslateOrFallback("button.reset", "Reset")))
        {
            return true;
        }

        if (TrySpeakForClickable(GetMemberValue(overlay, "EditExitButton"), x, y, "edit-exit", "Close"))
        {
            return true;
        }

        if (TrySpeakForTextBox(GetMemberValue(overlay, "EditNameField"), x, y, "edit-name", TranslateOrFallback("label.name", "Name")))
        {
            return true;
        }

        if (TrySpeakForTextBox(GetMemberValue(overlay, "EditCategoryField"), x, y, "edit-category", TranslateOrFallback("label.category", "Category")))
        {
            return true;
        }

        if (TrySpeakForTextBox(GetMemberValue(overlay, "EditOrderField"), x, y, "edit-order", TranslateOrFallback("label.order", "Order")))
        {
            return true;
        }

        if (TrySpeakForCheckbox(GetMemberValue(overlay, "EditHideChestField"), x, y, "edit-hide"))
        {
            return true;
        }

        if (TrySpeakForDropdown(GetMemberValue(overlay, "EditAutomateStorage"), x, y, "edit-automate-store", TranslateOrFallback("label.automate-store", "Automate store")))
        {
            return true;
        }

        if (TrySpeakForDropdown(GetMemberValue(overlay, "EditAutomateFetch"), x, y, "edit-automate-take", TranslateOrFallback("label.automate-take", "Automate take")))
        {
            return true;
        }

        return false;
    }

    private static bool TryToggleEditForm(object overlay)
    {
        if (TextBoxPatch.IsAnyTextBoxActive || IsAnyEditTextBoxSelected(overlay))
        {
            return false;
        }

        if (!MainClass.Config.PrimaryInfoKey.JustPressed())
        {
            return false;
        }

        _lastOverlay = overlay;
        bool wasEditForm = _isEditFormOpen || IsEditFormActive(overlay);
        if (wasEditForm)
        {
            if (!TryCloseEditForm(overlay))
            {
                return false;
            }

            if (IsEditFormActive(overlay))
            {
                return false;
            }

            _editNavIndex = -1;
            _lastOverlayElementKey = null;
            _isEditFormOpen = false;
            MainClass.ScreenReader.SayWithMenuChecker("Close", true);
            return true;
        }

        if (!TryOpenEditForm(overlay))
        {
            return false;
        }

        if (!IsEditFormActive(overlay))
        {
            return false;
        }

        _editNavIndex = -1;
        _lastOverlayElementKey = null;
        _isEditFormOpen = true;
        MainClass.ScreenReader.SayWithMenuChecker(TranslateOrFallback("button.edit-chest", "Edit chest"), true);
        return true;
    }

    internal static bool TryHandleSimulatedClick()
    {
        if (Game1.activeClickableMenu is not IClickableMenu menu)
        {
            return false;
        }

        if (!TryGetOverlay(menu, out object? overlay) || overlay == null)
        {
            return false;
        }

        bool leftClickPressed = MainClass.Config.LeftClickMainKey.JustPressed()
                                || MainClass.Config.LeftClickAlternateKey.JustPressed();
        if (!leftClickPressed)
        {
            return false;
        }

        bool editActive = _isEditFormOpen || IsEditFormActive(overlay);
        if (!editActive)
        {
            string? hoverText = GetStringMemberValue(overlay, "HoverText");
            if (string.IsNullOrWhiteSpace(hoverText))
            {
                return false;
            }
        }

        int x = Game1.getMouseX(true);
        int y = Game1.getMouseY(true);
        InvokeReceiveLeftClick(overlay, x, y);
        return true;
    }

    private static bool TryOpenEditForm(object overlay)
    {
        _lastOverlay = overlay;
        if (TryInvokeMethod(overlay, "OpenEdit"))
        {
            _isEditFormOpen = true;
            return true;
        }

        object? button = GetMemberValue(overlay, "EditButton");
        if (button is not ClickableComponent cc)
        {
            return false;
        }

        int x = cc.bounds.Center.X;
        int y = cc.bounds.Center.Y;
        Game1.setMousePosition(x, y);
        InvokeReceiveLeftClick(overlay, x, y);
        _isEditFormOpen = true;
        return true;
    }

    private static bool TryCloseEditForm(object overlay)
    {
        _lastOverlay = overlay;
        object? button = GetMemberValue(overlay, "EditExitButton");
        if (button is ClickableComponent cc)
        {
            int x = cc.bounds.Center.X;
            int y = cc.bounds.Center.Y;
            Game1.setMousePosition(x, y);
            InvokeReceiveLeftClick(overlay, x, y);
            _isEditFormOpen = false;
            return true;
        }

        bool closed = TrySetActiveElement(overlay, "Menu");
        if (closed)
        {
            _isEditFormOpen = false;
        }

        return closed;
    }

    private static void InvokeReceiveLeftClick(object overlay, int x, int y)
    {
        try
        {
            MethodInfo? method = overlay.GetType().GetMethod("ReceiveLeftClick", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [typeof(int), typeof(int)], null);
            if (method != null)
            {
                method.Invoke(overlay, [x, y]);
                return;
            }

            method = overlay.GetType().GetMethod("ReceiveLeftClick", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [typeof(int), typeof(int), typeof(bool)], null);
            method?.Invoke(overlay, [x, y, true]);
        }
        catch
        {
            // ignore reflection errors; hover narration still works
        }
    }

    private static bool TryHandleEditNavigation(object overlay, int direction)
    {
        List<(string key, string label, Rectangle bounds)> elements = GetEditElements(overlay);
        if (elements.Count == 0)
        {
            _editNavIndex = -1;
            return false;
        }

        if (_editNavIndex < 0 || _editNavIndex >= elements.Count)
        {
            _editNavIndex = direction < 0 ? 0 : -1;
        }

        _editNavIndex += direction;

        if (_editNavIndex < 0)
        {
            _editNavIndex = elements.Count - 1;
        }
        else if (_editNavIndex >= elements.Count)
        {
            _editNavIndex = 0;
        }

        var target = elements[_editNavIndex];
        Game1.setMousePosition(target.bounds.Center.X, target.bounds.Center.Y);
        SpeakOverlayElement(target.key, target.label);
        return true;
    }

    private static bool TryFocusFirstEditElement(object overlay)
    {
        if (_editNavIndex != -1)
        {
            return false;
        }

        List<(string key, string label, Rectangle bounds)> elements = GetEditElements(overlay);
        if (elements.Count == 0)
        {
            return false;
        }

        _editNavIndex = 0;
        var target = elements[0];
        Game1.setMousePosition(target.bounds.Center.X, target.bounds.Center.Y);
        SpeakOverlayElement(target.key, target.label);
        return true;
    }

    private static List<(string key, string label, Rectangle bounds)> GetEditElements(object overlay)
    {
        List<(string key, string label, Rectangle bounds)> elements = [];

        if (TryGetTextBoxBounds(GetMemberValue(overlay, "EditNameField"), out Rectangle bounds))
        {
            elements.Add(("edit-name", TranslateOrFallback("label.name", "Name"), bounds));
        }

        if (TryGetTextBoxBounds(GetMemberValue(overlay, "EditCategoryField"), out bounds))
        {
            elements.Add(("edit-category", TranslateOrFallback("label.category", "Category"), bounds));
        }

        if (TryGetTextBoxBounds(GetMemberValue(overlay, "EditOrderField"), out bounds))
        {
            elements.Add(("edit-order", TranslateOrFallback("label.order", "Order"), bounds));
        }

        object? hideField = GetMemberValue(overlay, "EditHideChestField");
        if (TryGetCheckboxBounds(hideField, out bounds))
        {
            string hideLabel = hideField != null && GetBoolMemberValue(hideField, "Value")
                ? TranslateOrFallback("label.hide-chest-hidden", "Hide this chest (hidden)")
                : TranslateOrFallback("label.hide-chest", "Hide this chest");
            elements.Add(("edit-hide", hideLabel, bounds));
        }

        if (TryGetDropdownBounds(GetMemberValue(overlay, "EditAutomateStorage"), out bounds))
        {
            elements.Add(("edit-automate-store", TranslateOrFallback("label.automate-store", "Automate store"), bounds));
        }

        if (TryGetDropdownBounds(GetMemberValue(overlay, "EditAutomateFetch"), out bounds))
        {
            elements.Add(("edit-automate-take", TranslateOrFallback("label.automate-take", "Automate take"), bounds));
        }

        if (TryGetClickableBounds(GetMemberValue(overlay, "EditSaveButtonArea"), out bounds))
        {
            elements.Add(("edit-save", TranslateOrFallback("button.save", "Save"), bounds));
        }

        if (TryGetClickableBounds(GetMemberValue(overlay, "EditResetButtonArea"), out bounds))
        {
            elements.Add(("edit-reset", TranslateOrFallback("button.reset", "Reset"), bounds));
        }

        if (TryGetClickableBounds(GetMemberValue(overlay, "EditExitButton"), out bounds))
        {
            elements.Add(("edit-exit", "Close", bounds));
        }

        return elements;
    }

    private static bool TryGetClickableBounds(object? component, out Rectangle bounds)
    {
        bounds = Rectangle.Empty;
        if (component is ClickableComponent cc)
        {
            bounds = cc.bounds;
            return true;
        }

        return false;
    }

    private static bool TryGetTextBoxBounds(object? textBox, out Rectangle bounds)
    {
        bounds = Rectangle.Empty;
        if (textBox == null)
        {
            return false;
        }

        int left = GetIntMemberValue(textBox, "X");
        int top = GetIntMemberValue(textBox, "Y");
        int width = GetIntMemberValue(textBox, "Width");
        int height = GetIntMemberValue(textBox, "Height");
        if (width <= 0 || height <= 0)
        {
            return false;
        }

        bounds = new Rectangle(left, top, width, height);
        return true;
    }

    private static bool TryGetDropdownBounds(object? dropdown, out Rectangle bounds)
    {
        bounds = Rectangle.Empty;
        if (dropdown == null)
        {
            return false;
        }

        object? rect = GetMemberValue(dropdown, "Bounds");
        if (rect is Rectangle boundsValue)
        {
            bounds = boundsValue;
            return true;
        }

        return false;
    }

    private static bool TryGetCheckboxBounds(object? checkbox, out Rectangle bounds)
    {
        bounds = Rectangle.Empty;
        if (checkbox == null)
        {
            return false;
        }

        int left = GetIntMemberValue(checkbox, "X");
        int top = GetIntMemberValue(checkbox, "Y");
        int width = GetIntMemberValue(checkbox, "Width");
        int height = width > 0 ? width : 32;
        if (width <= 0)
        {
            return false;
        }

        bounds = new Rectangle(left, top, width, height);
        return true;
    }

    private static bool IsConfiguredButtonPressed(InputButton[] configuredButtons, IEnumerable<SButton> pressed)
    {
        foreach (InputButton inputButton in configuredButtons)
        {
            SButton button = inputButton.ToSButton();
            if (pressed.Contains(button))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsAnyEditTextBoxSelected(object overlay)
    {
        return IsTextBoxSelected(GetMemberValue(overlay, "EditNameField"))
               || IsTextBoxSelected(GetMemberValue(overlay, "EditCategoryField"))
               || IsTextBoxSelected(GetMemberValue(overlay, "EditOrderField"));
    }

    private static bool IsTextBoxSelected(object? textBox)
    {
        return textBox != null && GetBoolMemberValue(textBox, "Selected");
    }

    private static bool TrySpeakForClickable(object? component, int x, int y, string key, string label)
    {
        if (component is ClickableComponent cc && cc.containsPoint(x, y))
        {
            SpeakOverlayElement(key, label);
            return true;
        }

        return false;
    }

    private static bool TrySpeakForTextBox(object? textBox, int x, int y, string key, string label)
    {
        if (textBox == null)
        {
            return false;
        }

        bool isSelected = GetBoolMemberValue(textBox, "Selected");
        if (isSelected || IsPointInTextBox(textBox, x, y))
        {
            SpeakOverlayElement(key, label);
            return true;
        }

        return false;
    }

    private static bool TrySpeakForDropdown(object? dropdown, int x, int y, string key, string label)
    {
        if (dropdown == null)
        {
            return false;
        }

        object? bounds = GetMemberValue(dropdown, "Bounds");
        if (bounds is Rectangle rect && rect.Contains(x, y))
        {
            SpeakOverlayElement(key, label);
            return true;
        }

        return false;
    }

    private static bool TrySpeakForCheckbox(object? checkbox, int x, int y, string key)
    {
        if (checkbox == null)
        {
            return false;
        }

        int left = GetIntMemberValue(checkbox, "X");
        int top = GetIntMemberValue(checkbox, "Y");
        int width = GetIntMemberValue(checkbox, "Width");
        int height = width > 0 ? width : 32;
        if (new Rectangle(left, top, width, height).Contains(x, y))
        {
            bool value = GetBoolMemberValue(checkbox, "Value");
            string label = value
                ? TranslateOrFallback("label.hide-chest-hidden", "Hide this chest (hidden)")
                : TranslateOrFallback("label.hide-chest", "Hide this chest");
            SpeakOverlayElement(key, label);
            return true;
        }

        return false;
    }

    private static bool IsPointInTextBox(object textBox, int x, int y)
    {
        int left = GetIntMemberValue(textBox, "X");
        int top = GetIntMemberValue(textBox, "Y");
        int width = GetIntMemberValue(textBox, "Width");
        int height = GetIntMemberValue(textBox, "Height");
        return new Rectangle(left, top, width, height).Contains(x, y);
    }

    private static bool SpeakOverlayElement(string key, string label)
    {
        if (string.Equals(_lastOverlayElementKey, key, StringComparison.Ordinal))
        {
            return false;
        }

        _lastOverlayElementKey = key;
        MainClass.ScreenReader.SayWithMenuChecker(label, true);
        return true;
    }

    private static string TranslateOrFallback(string key, string fallback)
        => TryTranslate(key) ?? fallback;

    private static string? TryTranslate(string key)
    {
        if (_i18nTranslations == null || _i18nGetMethod == null)
        {
            return null;
        }

        try
        {
            ParameterInfo[] parameters = _i18nGetMethod.GetParameters();
            object? result = parameters.Length > 1
                ? _i18nGetMethod.Invoke(_i18nTranslations, [key, null])
                : _i18nGetMethod.Invoke(_i18nTranslations, [key]);
            return result as string;
        }
        catch
        {
            return null;
        }
    }

    private static int GetIntMemberValue(object instance, string name)
    {
        object? value = GetMemberValue(instance, name);
        return value == null ? 0 : Convert.ToInt32(value);
    }

    private static bool GetBoolMemberValue(object instance, string name)
    {
        object? value = GetMemberValue(instance, name);
        return value != null && Convert.ToBoolean(value);
    }

    private static bool SetMemberValue(object? instance, string name, object value)
    {
        if (instance == null)
        {
            return false;
        }

        Type type = instance.GetType();
        FieldInfo? field = GetFieldCached(type, name);
        if (field != null)
        {
            field.SetValue(instance, value);
            return true;
        }

        PropertyInfo? prop = GetPropertyCached(type, name);
        if (prop != null && prop.CanWrite)
        {
            prop.SetValue(instance, value);
            return true;
        }

        return false;
    }

    private static void SetBoolMemberValue(object? instance, string name, bool value)
    {
        SetMemberValue(instance, name, value);
    }

    private static bool TryInvokeMethod(object instance, string name)
    {
        try
        {
            MethodInfo? method = instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic);
            if (method == null)
            {
                return false;
            }

            method.Invoke(instance, null);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TrySetActiveElement(object overlay, string elementName)
    {
        try
        {
            PropertyInfo? property = GetPropertyCached(overlay.GetType(), "ActiveElement");
            MethodInfo? setter = property?.GetSetMethod(true);
            if (setter == null || property?.PropertyType == null)
            {
                return false;
            }

            object value = Enum.Parse(property.PropertyType, elementName, ignoreCase: true);
            setter.Invoke(overlay, [value]);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static FieldInfo? GetFieldCached(Type type, string name)
    {
        string key = $"{type.FullName}|{name}";
        if (FieldCache.TryGetValue(key, out FieldInfo? cached))
        {
            return cached;
        }

        FieldInfo? field = GetFieldInHierarchy(type, name);
        FieldCache[key] = field;
        return field;
    }

    private static FieldInfo? GetFieldInHierarchy(Type type, string name)
    {
        while (type != null)
        {
            FieldInfo? field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null)
            {
                return field;
            }

            type = type.BaseType;
        }

        return null;
    }

    private static PropertyInfo? GetPropertyCached(Type type, string name)
    {
        string key = $"{type.FullName}|{name}";
        if (PropertyCache.TryGetValue(key, out PropertyInfo? cached))
        {
            return cached;
        }

        PropertyInfo? prop = GetPropertyInHierarchy(type, name);
        PropertyCache[key] = prop;
        return prop;
    }

    private static PropertyInfo? GetPropertyInHierarchy(Type type, string name)
    {
        while (type != null)
        {
            PropertyInfo? prop = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (prop != null)
            {
                return prop;
            }

            type = type.BaseType;
        }

        return null;
    }
}
