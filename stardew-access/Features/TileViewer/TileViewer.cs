using Microsoft.Xna.Framework;
using xTile;
using stardew_access.Patches;
using stardew_access.Utils;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;
using StardewValley.Pathfinding;

namespace stardew_access.Features;

/// <summary>
/// Allows browsing of the map and snapping mouse to tiles with the arrow keys
/// </summary>
internal class TileViewer : FeatureBase
{

    //None of these positions take viewport into account; other functions are responsible later
    private Vector2 _viewingOffset = Vector2.Zero;
    private Vector2 _relativeOffsetLockPosition = Vector2.Zero;
    private Boolean _relativeOffsetLock = false;
    private Vector2 _prevPlayerPosition = Vector2.Zero, _prevFacing = Vector2.Zero;
    private bool _upKeyFlag = true;
    private bool _rightKeyFlag = true;
    private bool _downKeyFlag = true;
    private bool _leftKeyFlag = true;

    private static TileViewer? instance;
    public new static TileViewer Instance
    {
        get
        {
            instance ??= new TileViewer();
            return instance;
        }
    }


    private static Vector2 PlayerFacingVector
    {
        get
        {
            return Game1.player.FacingDirection switch
            {
                0 => new Vector2(0, -Game1.tileSize),
                1 => new Vector2(Game1.tileSize, 0),
                2 => new Vector2(0, Game1.tileSize),
                3 => new Vector2(-Game1.tileSize, 0),
                _ => Vector2.Zero,
            };
        }
    }

    private static Vector2 PlayerPosition
    {
        get
        {
            int x = Game1.player.GetBoundingBox().Center.X;
            int y = Game1.player.GetBoundingBox().Center.Y;
            return new Vector2(x, y);
        }
    }

    /// <summary>
    /// Return the position of the tile cursor in pixels from the upper-left corner of the map.
    /// </summary>
    /// <returns>Vector2</returns>
    public Vector2 GetTileCursorPosition()
    {
        if (IsInMenuBuilderViewport())
        {
            return MouseOverrideForTileViewer.MousePosition!.Value;
        }
        Vector2 target = PlayerPosition;
        if (_relativeOffsetLock)
        {
            target += _relativeOffsetLockPosition;
        }
        else
        {
            target += PlayerFacingVector + _viewingOffset;
        }
        return target;
    }

    /// <summary>
    /// Return the tile at the position of the tile cursor.
    /// </summary>
    /// <returns>Vector2</returns>
    public Vector2 GetViewingTile()
    {
        Vector2 position = GetTileCursorPosition();
        return new Vector2((int)(position.X / Game1.tileSize), (int)(position.Y / Game1.tileSize));
    }

    internal static bool IsInMenuBuilderViewport() => Game1.activeClickableMenu switch
    {
        CarpenterMenu => CarpenterMenuPatch.isOnFarm,
        PurchaseAnimalsMenu => PurchaseAnimalsMenuPatch.isOnFarm && !PurchaseAnimalsMenuPatch.isNamingAnimal,
        AnimalQueryMenu => AnimalQueryMenuPatch.isOnFarm,
        _ => false
    };

    /// <summary>
    /// Handle keyboard input related to the tile viewer.
    /// </summary>
    public override bool OnButtonPressed(object? sender, ButtonPressedEventArgs e)
    {
        // Exit if in a menu
        if (!IsInMenuBuilderViewport() && Game1.activeClickableMenu != null)
        {
            return false;
        }

        if (Game1.activeClickableMenu == null && MainClass.Config.ToggleRelativeCursorLockKey.JustPressed())
        {
            _relativeOffsetLock = !_relativeOffsetLock;
            if (_relativeOffsetLock)
            {
                _relativeOffsetLockPosition = PlayerFacingVector + _viewingOffset;
            }
            else
            {
                _relativeOffsetLockPosition = Vector2.Zero;
            }

            MainClass.ScreenReader.TranslateAndSay("feature-tile_viewer-relative_cursor_lock_info", true, new
            {
                is_enabled = _relativeOffsetLock ? 1 : 0
            });
        }
        else if (MainClass.Config.TileCursorPreciseUpKey.JustPressed())
        {
            CursorMoveInput(new Vector2(0, -MainClass.Config.TileCursorPreciseMovementDistance), true);
        }
        else if (MainClass.Config.TileCursorPreciseRightKey.JustPressed())
        {
            CursorMoveInput(new Vector2(MainClass.Config.TileCursorPreciseMovementDistance, 0), true);
        }
        else if (MainClass.Config.TileCursorPreciseDownKey.JustPressed())
        {
            CursorMoveInput(new Vector2(0, MainClass.Config.TileCursorPreciseMovementDistance), true);
        }
        else if (MainClass.Config.TileCursorPreciseLeftKey.JustPressed())
        {
            CursorMoveInput(new Vector2(-MainClass.Config.TileCursorPreciseMovementDistance, 0), true);
        }
        else if (MainClass.Config.AutoWalkToTileKey.JustPressed() && Context.IsPlayerFree)
        {
            // The object tracker owns auto walking (obstacle legs, footsteps, cancel keys,
            // nearest-reachable offer); walk-to-tile just feeds it the cursor tile.
            ObjectTracker.Instance.WalkToTile(GetViewingTile().ToPoint());
        }
        else if (Game1.activeClickableMenu == null && MainClass.Config.OpenTileInfoMenuKey.JustPressed() && Context.IsPlayerFree)
        {
            Game1.activeClickableMenu = new TileInfoMenu((int)GetViewingTile().X, (int)GetViewingTile().Y);
        }

        return false;
    }

    // For currently unimplemented report panning of map in CarpenterMenu
    internal static int GetDirectionAsInt(int x, int y)
    {
        // Compute the direction code based on the signs of x and y
        return (Math.Sign(x), Math.Sign(y)) switch
        {
            (0, -1) => 1, // North
            (0, 1) => 2,  // South
            (1, 0) => 3,  // East
            (-1, 0) => 4, // West
            (1, -1) => 5, // Northeast
            (-1, -1) => 6, // Northwest
            (1, 1) => 7,  // Southeast
            (-1, 1) => 8, // Southwest
            _ => -1       // Invalid direction or no movement
        };
    }

    private void CursorMoveInput(Vector2 delta, Boolean precise = false)
    {
        bool isInMenuBuilderViewport = IsInMenuBuilderViewport();
        if (isInMenuBuilderViewport)
        {
            int deltaX = (int)delta.X, deltaY = (int)delta.Y;
            int currentX = Game1.getMouseX(ui_scale: false), currentY = Game1.getMouseY(ui_scale: false);
            int newX = currentX + deltaX, newY = currentY + deltaY;
            int beforeViewportX = Game1.viewport.X, beforeViewportY = Game1.viewport.Y;

            // Pan the screen if at edge
            if (newX < 32)
            {
                Game1.panScreen(-64, 0);
                int diffVewportX = Math.Abs(Game1.viewport.X - beforeViewportX);
                newX = diffVewportX != 64 && diffVewportX != 0
                          ? newX - deltaX - 64 - diffVewportX
                          : newX - deltaX;
            }
            else if (newX - Game1.viewport.Width >= -32)
            {
                Game1.panScreen(64, 0);
                int diffVewportX = Math.Abs(Game1.viewport.X - beforeViewportX);
                newX = diffVewportX != 64 && diffVewportX != 0
                          ? newX - deltaX + 64 - diffVewportX
                          : newX - deltaX;
            }
            if (newY < 32)
            {
                Game1.panScreen(0, -64);
                int diffVewportY = Math.Abs(Game1.viewport.Y - beforeViewportY);
                newY = diffVewportY != 64 && diffVewportY != 0
                          ? newY - deltaY - 64 - diffVewportY
                          : newY - deltaY;
            }
            else if (newY - Game1.viewport.Height >= -32)
            {
                Game1.panScreen(0, 64);
                int diffVewportY = Math.Abs(Game1.viewport.Y - beforeViewportY);
                newY = diffVewportY != 64 && diffVewportY != 0
                          ? newY - deltaY + 64 - diffVewportY
                          : newY - deltaY;
            }

            MouseOverrideForTileViewer.MousePosition = new(newX, newY);
        }
        else if (!TryMoveTileView(delta)) return;
        Vector2 position = isInMenuBuilderViewport
            ? MouseOverrideForTileViewer.MousePosition!.Value + new Vector2(Game1.viewport.X, Game1.viewport.Y)
            : GetTileCursorPosition();
        string name = TileInfo.GetNameAtTileWithBlockedOrEmptyIndication(!isInMenuBuilderViewport ? GetViewingTile() : position / 64);

        MainClass.ScreenReader.Say(precise
            ? $"{name}, {position.X}, {position.Y}"
            : $"{name}, {(int)(position.X / Game1.tileSize)}, {(int)(position.Y / Game1.tileSize)}", true);
    }

    private bool TryMoveTileView(Vector2 delta)
    {
        Vector2 dest = GetTileCursorPosition() + delta;
        if (!IsPositionOnMap(dest)) return false;
        if ((MainClass.Config.LimitTileCursorToScreen && Utility.isOnScreen(dest, 0)) || !MainClass.Config.LimitTileCursorToScreen)
        {
            if (_relativeOffsetLock)
            {
                _relativeOffsetLockPosition += delta;
            }
            else
            {
                _viewingOffset += delta;
            }
            return true;
        }
        return false;
    }

    private void SnapMouseToPlayer()
    {
        Vector2 cursorPosition = GetTileCursorPosition();
        if (AllowMouseSnap(cursorPosition))
        {
            // Must account for viewport here
            int newX = (int)cursorPosition.X - Game1.viewport.X, newY = (int)cursorPosition.Y - Game1.viewport.Y;
            Game1.setMousePosition(newX, newY);
        }
    }

    /// <summary>
    /// Handle tile viewer logic.
    /// </summary>
    public override void Update(object? sender, UpdateTickedEventArgs e)
    {
        if ((Game1.activeClickableMenu == null && Context.IsPlayerFree) || IsInMenuBuilderViewport())
        {
            if (MainClass.Config.TileCursorUpKey.IsDown() && _upKeyFlag)
            {
                CursorMoveInput(new Vector2(0, -64));
                _upKeyFlag = false;
                Task.Delay(200).ContinueWith(_ => { _upKeyFlag = true; });
            }
            else if (MainClass.Config.TileCursorRightKey.IsDown() && _rightKeyFlag)
            {
                CursorMoveInput(new Vector2(64, 0));
                _rightKeyFlag = false;
                Task.Delay(200).ContinueWith(_ => { _rightKeyFlag = true; });
            }
            else if (MainClass.Config.TileCursorDownKey.IsDown() && _downKeyFlag)
            {
                CursorMoveInput(new Vector2(0, 64));
                _downKeyFlag = false;
                Task.Delay(200).ContinueWith(_ => { _downKeyFlag = true; });
            }
            else if (MainClass.Config.TileCursorLeftKey.IsDown() && _leftKeyFlag)
            {
                CursorMoveInput(new Vector2(-64, 0));
                _leftKeyFlag = false;
                Task.Delay(200).ContinueWith(_ => { _leftKeyFlag = true; });
            }
        }

        if (!Context.IsPlayerFree) return;

        //Reset the viewing cursor to the player when they turn or move. This will not reset the locked offset relative cursor position.
        if (_prevFacing != PlayerFacingVector || _prevPlayerPosition != PlayerPosition)
        {
            _viewingOffset = Vector2.Zero;
        }
        _prevFacing = PlayerFacingVector;
        _prevPlayerPosition = PlayerPosition;
        if (MainClass.Config.SnapMouse)
            SnapMouseToPlayer();

    }

    private static bool AllowMouseSnap(Vector2 point)
    {
        // Prevent snapping if any menu is open or if window loses focus
        if (!Game1.game1.HasKeyboardFocus() && Game1.activeClickableMenu != null && !IsInMenuBuilderViewport()) return false;

        // Utility.isOnScreen treats a vector as a pixel position, not a tile position
        if (!Utility.isOnScreen(point, 0)) return false;

        //prevent mousing over the toolbar or any other UI component with the tile cursor
        foreach (IClickableMenu menu in Game1.onScreenMenus)
        {
            //must account for viewport here
            if (menu.isWithinBounds((int)point.X - Game1.viewport.X, (int)point.Y - Game1.viewport.Y)) return false;
        }
        return true;
    }

    private static bool IsPositionOnMap(Vector2 position)
    {
        var currentLocation = Game1.currentLocation;
        // Check whether the position is a warp point, if so then return true, sometimes warp points are 1 tile off the map for example in coops and barns
        if (DoorUtils.IsWarpAtTile(((int)(position.X / Game1.tileSize), (int)(position.Y / Game1.tileSize)), currentLocation)) return true;

        //position does not take viewport into account since the entire map needs to be checked.
        Map map = currentLocation.map;
        if (position.X < 0 || position.X > map.Layers[0].DisplayWidth) return false;
        if (position.Y < 0 || position.Y > map.Layers[0].DisplayHeight) return false;
        return true;
    }
}
