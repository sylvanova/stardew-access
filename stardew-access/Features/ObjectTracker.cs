using Microsoft.Xna.Framework;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Locations;

namespace stardew_access.Features;
using System.Timers;

using Tracker;
using Utils;
using Translation;
using static Utils.MiscUtils;
using static Utils.InputUtils;
using static Utils.MovementHelpers;
using static Utils.NPCUtils;

internal class ObjectTracker : FeatureBase
{
    private  bool SortByProximity;
    internal TrackedObjects? trackedObjects;
    private  Pathfinder? pathfinder;
    internal string? SelectedCategory;
    internal string? SelectedObjectGroup;
    internal int? SelectedObjectIndex;
    internal Vector2? SelectedCoordinates;
    private Dictionary<string, Dictionary<string, Dictionary<int, (string? name, string? category)>>> favorites =
        new(StringComparer.OrdinalIgnoreCase);
    private const int PressInterval = 500; // Milliseconds
    private readonly Timer lastPressTimer = new(PressInterval);
    private readonly Timer navigationTimer = new(PressInterval);
    private int lastFavoritePressed = 0;
    private int sameFavoritePressed = 0;
    private int navigateToFavorite = -1;
    private int _favoriteStack;
    public int FavoriteStack
    {
        get { return _favoriteStack; }
        set { _favoriteStack = Math.Max(0, value); }
    }
    bool SaveCoordinatesToggle = false;

    private readonly int[] objectCounts = [0, 0, 0, 0, 0, 0, 0];
    private readonly List<Action> updateActions;
    private int currentActionIndex = 0;
    private bool countHasChanged = false;
    private Vector2 lastStepSoundTile = Vector2.Zero;
    // Mounted stall recovery: the watchdog timer (threadpool) only records a request here;
    // the game thread consumes it. The generation counter ties a request to one logical
    // route, so an in-flight callback for a cancelled route can't re-route a newer one.
    // Lock order: Pathfinder's pathfindingLock is always taken OUTSIDE this gate.
    private readonly object mountedReplanGate = new();
    private int mountedRouteGeneration;      // guarded by mountedReplanGate
    private int pendingMountedGeneration = -1; // guarded; -1 = no pending request
    private Vector2 pendingMountedTarget;    // guarded
    private bool pendingMountedGiveUp;       // guarded
    private int mountedReplanAttempts;       // guarded
    private const int MaxMountedReplans = 3;

    // On-foot obstacle route (see Utils/FootPathfinder.cs). The walk is split at the first
    // clearable obstacle; after the leg ends we wait for the player to clear that tile,
    // then replan from wherever they stand. Game thread only.
    private sealed class FootRoute
    {
        public Point Target;
        public bool AllowWarpEnd;
        public bool IgnoreObstacleLimit; // confirmed by a second press: replans skip the limit
        public Obstacle? Pending;       // obstacle the current leg walks up to
        public Point LegEnd;            // last tile of the current leg
        public bool Waiting;            // leg finished, waiting for Pending to disappear
        public int Legs;
    }
    private FootRoute? footRoute;
    /// <summary>
    /// A walk the tracker refused but offers on a second press of the move key: the nearest
    /// standable tile of an unreachable target, or a route with more obstacles than the
    /// configured limit. Any other tracker action or a map change drops it.
    /// </summary>
    private sealed class WalkOffer
    {
        public Point Destination;       // where the confirming press walks to
        public Point Target;            // what the player asked for (tile viewer matches on it)
        public GameLocation Location = null!;
        public bool AllowWarpEnd;
        public bool IgnoreObstacleLimit;
    }
    private WalkOffer? walkOffer;
    // True while the current walk was started from the tile viewer (walk-to-tile key): its
    // end is announced as reached/stopped instead of reading the tracker's selected object.
    private bool tileWalk;
    private const int MaxFootLegs = 60;
    // Walking this far from the obstacle while waiting means the player gave up on it.
    private const int FootWaitAbandonDistance = 6;
    // Set by the pathfinder's stop callback (which the watchdog may fire from its timer
    // thread); consumed in Update so every announcement and state change stays on the game thread.
    private volatile bool footLegArrived;
    // Obstacle announcements are spoken a few ticks after the player turns to face the
    // obstacle, queued behind (not interrupting) the read-tile speech that the turn triggers.
    private int footAnnounceCountdown = -1;
    private const int FootAnnounceDelayTicks = 6;
    // Favorites navigation is requested from a System.Timers callback; run it on the game thread.
    private volatile int pendingFavoriteNavigation = -1;

    /// <summary>Invalidate pending mounted-stall requests and start a new route generation.</summary>
    private void ResetMountedReplanState()
    {
        lock (mountedReplanGate)
        {
            mountedRouteGeneration++;
            pendingMountedGeneration = -1;
            pendingMountedGiveUp = false;
            mountedReplanAttempts = 0;
        }
    }

    private static ObjectTracker? instance;
    public new static ObjectTracker Instance
    {
        get
        {
            instance ??= new ObjectTracker();
            return instance;
        }
    }

    /// <summary>Whether Object Tracker is currently driving an auto-walk route.</summary>
    internal static bool IsAutoWalking => instance?.pathfinder?.IsActive == true;
    
    private enum CycleType
    {
        CATEGORY, OBJECT_GROUP, IN_GROUP
    }

    public ObjectTracker()
    {
        SortByProximity = MainClass.Config.OTSortByProximity;
        lastPressTimer.Elapsed += OnLastPressTimerElapsed;
        lastPressTimer.AutoReset = false; // So it only triggers once per start
        navigationTimer.Elapsed += OnNavigationTimerElapsed;
        navigationTimer.AutoReset = false;
        updateActions =
        [
            () => UpdateAndRunIfChanged(ref objectCounts[0], Game1.currentLocation.debris.Count, () => { Log.Debug("Debris count has changed."); countHasChanged = true; }),
            () => UpdateAndRunIfChanged(ref objectCounts[1], Game1.currentLocation.objects.Count(), () => { Log.Debug("Objects count has changed."); countHasChanged = true; }),
            () => UpdateAndRunIfChanged(ref objectCounts[2], Game1.currentLocation.furniture.Count, () => { Log.Debug("Furniture count has changed."); countHasChanged = true; }),
            () => UpdateAndRunIfChanged(ref objectCounts[3], Game1.currentLocation.resourceClumps.Count, () => { Log.Debug("ResourceClumps count has changed."); countHasChanged = true; }),
            () => UpdateAndRunIfChanged(ref objectCounts[4], Game1.currentLocation.terrainFeatures.Count(), () => { Log.Debug("TerrainFeatures count has changed."); countHasChanged = true; }),
            () => UpdateAndRunIfChanged(ref objectCounts[5], Game1.currentLocation.largeTerrainFeatures.Count, () => { Log.Debug("LargeTerrainFeatures count has changed."); countHasChanged = true; }),
            () => UpdateSpecialAction()
        ];
        LoadFavorites();
    }

    private void OnLastPressTimerElapsed(object? sender, ElapsedEventArgs? e) => FavoriteKeysReset();

    private void OnNavigationTimerElapsed(object? sender, ElapsedEventArgs? e)
    {
        // Timer thread: only record the request. Planning floods the map and replaces
        // player.controller, which must happen on the game thread (see Update).
        if (navigateToFavorite > 0)
        {
            pendingFavoriteNavigation = navigateToFavorite;
            navigateToFavorite = -1;
        }
    }

    public override void Update(object? sender, UpdateTickedEventArgs e)
    {
        // The event with id 13 is the Haley's six heart event, the one at the beach requiring the player to find the bracelet
        // *** Exiting here will cause GridMovement and ObjectTracker functionality to not work during this event, making the bracelet impossible to track ***
        if (!Context.IsPlayerFree && !(Game1.CurrentEvent is not null && Game1.CurrentEvent.id == "13"))
        {
            // Kat: so ... why are we exiting here? _^_ 
            // Shoaib: To stop the object tracker when the player isn't 'free' to move i.e., they are busy in a menu,
            //         in an event or so on. The cancel auto walking when interrupted by a menu logic should've been implemented here.
            
            if (pathfinder != null && pathfinder.IsActive)
            {
                #if DEBUG
                Log.Verbose("ObjectTracker->Update: player isn't \"free\" to move, canceling auto walking.");
                #endif
                CancelFootRoute();
                pathfinder.StopPathfinding();
                Game1.player.completelyStopAnimatingOrDoingAction();
            }
            return;            
        }

        // In multiplayer the world keeps updating during the warp fade, so an active
        // controller pushes the player into the warp again every frame, re-triggering
        // it dozens of times and delaying the crossing. Drop the controller as soon as
        // a warp starts; OnPlayerWarped does the full cleanup after arrival.
        if (Game1.isWarping && pathfinder?.IsActive == true && Game1.player.controller != null)
        {
            Game1.player.controller = null;
        }

        PlayStepSoundWhileAutoWalking();

        ConsumePendingMountedAction();

        int favorite = pendingFavoriteNavigation;
        if (favorite > 0)
        {
            pendingFavoriteNavigation = -1;
            SetFromFavorites(favorite);
            MoveToCurrentlySelectedObject();
        }

        if (footLegArrived)
        {
            footLegArrived = false;
            if (footRoute is { Pending: { } arrivedAt, Waiting: false })
            {
                GetLocationObjects(resetFocus: SortByProximity);
                AnnounceObstacle(footRoute, arrivedAt);
            }
        }

        if (footAnnounceCountdown > 0 && --footAnnounceCountdown == 0)
        {
            footAnnounceCountdown = -1;
            if (footRoute is { Waiting: true, Pending: { } obstacle })
            {
                MainClass.ScreenReader.TranslateAndSay("feature-object_tracker-obstacle_ahead", false,
                    translationTokens: new
                    {
                        name = obstacle.Name,
                        x = obstacle.Tile.X,
                        y = obstacle.Tile.Y,
                        tool = obstacle.ToolName.Length > 0 ? obstacle.ToolName : "none"
                    });
            }
        }

        if (e.IsMultipleOf(10))
            CheckFootRouteObstacle();

        if (e.IsMultipleOf(5))
            Tick();
    }

    /// <summary>Drop any on-foot obstacle route (leg in progress or waiting for a clear).</summary>
    private void CancelFootRoute()
    {
        footRoute = null;
        footAnnounceCountdown = -1;
    }

    /// <summary>
    /// While waiting at an obstacle: once its tile probes clear, replan from the player's
    /// current tile to the original target and walk the next leg (or just report, when
    /// auto-resume is off).
    /// </summary>
    private void CheckFootRouteObstacle()
    {
        if (footRoute is not { Waiting: true, Pending: { } pending })
            return;
        if (pathfinder?.IsActive == true || Game1.player.isRidingHorse())
            return;
        Point here = Game1.player.TilePoint;
        if (Math.Abs(here.X - pending.Tile.X) + Math.Abs(here.Y - pending.Tile.Y) > FootWaitAbandonDistance)
        {
            footRoute = null;
            MainClass.ScreenReader.TranslateAndSay("feature-object_tracker-auto_walk_cancelled", true);
            return;
        }
        if (FootPathfinder.IsTileBlocked(Game1.currentLocation, pending.Tile))
            return;

        FootRoute route = footRoute;
        route.Waiting = false;
        route.Pending = null;
        if (!MainClass.Config.OTAutoResumeAfterObstacle)
        {
            footRoute = null;
            MainClass.ScreenReader.TranslateAndSay("feature-object_tracker-obstacle_cleared", true);
            return;
        }

        MainClass.ScreenReader.TranslateAndSay("feature-object_tracker-obstacle_cleared_continuing", false);
        FootPlan plan = FootPathfinder.PlanTo(Game1.currentLocation, Game1.player.TilePoint, route.Target, route.AllowWarpEnd);
        if (plan.Path == null)
        {
            footRoute = null;
            AnnounceNoFootPath(plan);
            return;
        }
        if (!route.IgnoreObstacleLimit && TooManyObstacles(plan, route.Target, route.AllowWarpEnd))
        {
            footRoute = null;
            return;
        }
        WalkFootLeg(route, plan);
    }

    /// <summary>
    /// Speak and refuse a plan with more clearable obstacles than the configured limit, and
    /// offer the same walk on the next press of the move key (the limit is then skipped for
    /// the whole route).
    /// </summary>
    private bool TooManyObstacles(FootPlan plan, Point target, bool allowWarpEnd)
    {
        int limit = MainClass.Config.OTMaxObstaclesOnPath;
        if (plan.Obstacles.Count <= limit)
            return false;
        walkOffer = new WalkOffer
        {
            Destination = plan.Destination,
            Target = target,
            Location = Game1.currentLocation,
            AllowWarpEnd = allowWarpEnd,
            IgnoreObstacleLimit = true,
        };
        MainClass.ScreenReader.TranslateAndSay("feature-object_tracker-too_many_obstacles", true,
            translationTokens: new { count = plan.Obstacles.Count, limit });
        return true;
    }

    private void AnnounceNoFootPath(FootPlan plan)
    {
        if (plan.BlockedBy != null)
        {
            MainClass.ScreenReader.TranslateAndSay("feature-object_tracker-no_path_blocked", true,
                translationTokens: new { blocker = plan.BlockedBy });
        }
        else if (plan.NearestReachable is Point near && near != plan.Destination)
        {
            var tokens = new
            {
                distance = (int)GetDistance(near.ToVector2(), plan.Destination.ToVector2()),
                direction = GetDirection(plan.Destination.ToVector2(), near.ToVector2())
            };
            walkOffer = new WalkOffer { Destination = near, Target = plan.Destination, Location = Game1.currentLocation };
            MainClass.ScreenReader.TranslateAndSay("feature-object_tracker-no_path_nearest", true, translationTokens: tokens);
        }
        else
        {
            MainClass.ScreenReader.TranslateAndSay("feature-object_tracker-could_not_find_path", true);
        }
    }

    /// <summary>
    /// Walk a confirmed offer: the nearest reachable tile of an unreachable target (through
    /// obstacles like any other route, limit still applies and may offer once more), or a
    /// route with too many obstacles (limit skipped). The destination came from a plan a
    /// moment ago, so a missing path here means the map changed under us and is reported
    /// plainly rather than re-offered.
    /// </summary>
    private void WalkOfferedRoute(WalkOffer offer)
    {
        walkOffer = null;
        FootPlan plan = FootPathfinder.PlanTo(Game1.currentLocation, Game1.player.TilePoint, offer.Destination, offer.AllowWarpEnd);
        if (plan.Path == null)
        {
            MainClass.ScreenReader.TranslateAndSay("feature-object_tracker-could_not_find_path", true);
            return;
        }
        if (!offer.IgnoreObstacleLimit && TooManyObstacles(plan, offer.Target, offer.AllowWarpEnd))
            return;
        if (plan.Path.Count == 0)
        {
            FacePlayerToTargetTile(offer.Target.ToVector2());
            return;
        }
        if (plan.Obstacles.Count > 0)
        {
            MainClass.ScreenReader.TranslateAndSay("feature-object_tracker-path_with_obstacles", false,
                translationTokens: new { tiles = plan.Path.Count, obstacles = FootPathfinder.SummarizeObstacles(plan.Obstacles) });
        }
        WalkFootLeg(new FootRoute { Target = plan.Destination, AllowWarpEnd = offer.AllowWarpEnd, IgnoreObstacleLimit = offer.IgnoreObstacleLimit }, plan);
    }

    /// <summary>
    /// Walk the plan up to (not onto) its first obstacle, facing it on arrival; with no
    /// obstacle, walk the whole plan. A plan whose very first tile is the obstacle just
    /// announces it and waits.
    /// </summary>
    private void WalkFootLeg(FootRoute route, FootPlan plan)
    {
        Farmer player = Game1.player;
        footRoute = route;
        route.Legs++;
        if (route.Legs > MaxFootLegs)
        {
            Log.Debug($"Foot route to {route.Target} exceeded {MaxFootLegs} legs; giving up.");
            footRoute = null;
            MainClass.ScreenReader.TranslateAndSay("feature-object_tracker-could_not_find_path", true);
            return;
        }

        Obstacle? first = plan.Obstacles.Count > 0 ? plan.Obstacles[0] : null;
        List<Point> nodes = [.. plan.Path!];
        int legLength = first == null ? nodes.Count : nodes.IndexOf(first.Tile);
        Point legEnd = legLength > 0 ? nodes[legLength - 1] : player.TilePoint;
        Stack<Point> leg = new();
        for (int i = legLength - 1; i >= 0; i--)
            leg.Push(nodes[i]);

        int facing = -1;
        if (first != null)
        {
            int dx = first.Tile.X - legEnd.X, dy = first.Tile.Y - legEnd.Y;
            facing = dy < 0 ? 0 : dx > 0 ? 1 : dy > 0 ? 2 : 3;
        }

        route.Pending = first;
        route.LegEnd = legEnd;
        route.Waiting = false;

        if (leg.Count == 0)
        {
            // Already standing next to the obstacle (or at the destination).
            if (first != null)
            {
                player.faceDirection(facing);
                AnnounceObstacle(route, first);
            }
            else
            {
                FacePlayerToTargetTile(route.Target.ToVector2());
                footRoute = null;
                ReadCurrentlySelectedObject();
            }
            return;
        }

        ResetMountedReplanState();
        pathfinder?.Dispose();
        pathfinder = new(RetryPathfinding, StopPathfinding);
        pathfinder.StartPathfinding(player, Game1.currentLocation, legEnd, leg, facing);
    }

    /// <summary>Enter the waiting state; the speech itself is scheduled from Update.</summary>
    private void AnnounceObstacle(FootRoute route, Obstacle obstacle)
    {
        route.Waiting = true;
        route.Pending = obstacle;
        footAnnounceCountdown = FootAnnounceDelayTicks;
    }

    /// <summary>
    /// Handle a mounted-stall request recorded by the watchdog timer: re-plan from the
    /// player's current position, or stop with feedback when out of attempts. Game thread
    /// only — the vanilla controller and tracker state must never be touched from the timer.
    /// </summary>
    private void ConsumePendingMountedAction()
    {
        Vector2? replanTarget = null;
        bool giveUp = false;
        lock (mountedReplanGate)
        {
            if (pendingMountedGeneration == -1)
                return;
            bool current = pendingMountedGeneration == mountedRouteGeneration;
            if (current)
            {
                giveUp = pendingMountedGiveUp;
                if (!giveUp)
                {
                    replanTarget = pendingMountedTarget;
                    mountedReplanAttempts++;
                }
            }
            // Stale requests (from a route that was since stopped or replaced) are dropped.
            pendingMountedGeneration = -1;
            pendingMountedGiveUp = false;
            if (!current)
                return;
        }

        if (pathfinder == null || !pathfinder.IsActive || !Game1.player.isRidingHorse())
            return;

        if (giveUp)
        {
            MainClass.ScreenReader.Say("Target unreachable, stopping.", true);
            pathfinder.StopPathfinding();
            return;
        }

        Point targetTile = replanTarget!.Value.ToPoint();
        Stack<Point>? path = FindMountedPath(Game1.currentLocation, Game1.player.TilePoint, targetTile, allowWarpEnd: true);
        if (path == null || path.Count == 0)
        {
            pathfinder.StopPathfinding();
            MainClass.ScreenReader.TranslateAndSay("feature-object_tracker-could_not_find_path", true);
            return;
        }

        Log.Debug($"Mounted route stalled; replanned from {Game1.player.TilePoint} to {targetTile} (attempt {mountedReplanAttempts} of {MaxMountedReplans}).");
        pathfinder.StartPathfinding(Game1.player, Game1.currentLocation, targetTile, path);
    }

    /// <summary>
    /// Plays one terrain footstep for every tile actually moved on foot.
    /// </summary>
    private void PlayStepSoundWhileAutoWalking()
    {
        if (pathfinder == null || !pathfinder.IsActive) return;

        // Extra guard against the stale-controller teleport (see OnPlayerWarped), in
        // case the warp event is missed.
        if (Game1.player.controller != null && Game1.player.controller.location != Game1.currentLocation)
        {
            Game1.player.controller = null;
            pathfinder.StopPathfinding();
            return;
        }

        // Mounted auto-walk uses Horse.OnMountFootstep through the vanilla animation.
        if (Game1.player.isRidingHorse()) return;

        Vector2 currentTile = Game1.player.Tile;
        if (currentTile == lastStepSoundTile) return;

        lastStepSoundTile = currentTile;
        Game1.currentLocation.playTerrainSound(currentTile);
    }

    public override bool OnButtonPressed(object? sender, ButtonPressedEventArgs e)
    {
        base.OnButtonPressed(sender, e);
        bool cancelAutoWalkingPressed = MainClass.Config.OTCancelAutoWalking.JustPressed();

        if (footRoute is { Waiting: true } && (pathfinder == null || !pathfinder.IsActive))
        {
            if (cancelAutoWalkingPressed)
            {
                CancelFootRoute();
                MainClass.ScreenReader.TranslateAndSay("feature-object_tracker-auto_walk_cancelled", true);
                MainClass.ModHelper!.Input.Suppress(e.Button);
                return true;
            }
            return false;
        }

        if (pathfinder != null && pathfinder.IsActive)
        {
            if (cancelAutoWalkingPressed)
            {
                #if DEBUG
                Log.Verbose("ObjectTracker->HandleKeys: cancel auto walking pressed, canceling auto walking for object tracker.");
                #endif
                CancelFootRoute();
                pathfinder.StopPathfinding();
                MainClass.ModHelper!.Input.Suppress(e.Button);
                return true;
                
            }
            else if (IsAnyMovementKeyPressed())
            {
                #if DEBUG
                Log.Verbose("ObjectTracker->HandleKeys: movement key pressed, canceling auto walking for object tracker.");
                #endif
                CancelFootRoute();
                pathfinder.StopPathfinding();
                MainClass.ModHelper!.Input.Suppress(e.Button);
                return true;
            }
            else if (IsUseToolKeyActive())
            {
                #if DEBUG
                Log.Verbose("ObjectTracker->HandleKeys: use tool button pressed, canceling auto walking for object tracker.");
                #endif
                CancelFootRoute();
                pathfinder.StopPathfinding();
                MainClass.ModHelper!.Input.Suppress(e.Button);
                Game1.pressUseToolButton();
                return true;
            }
            else if (IsDoActionKeyActive())
            {
                #if DEBUG
                Log.Verbose("ObjectTracker->HandleKeys: action button pressed, canceling auto walking for object tracker.");
                #endif
                CancelFootRoute();
                pathfinder.StopPathfinding();
                MainClass.ModHelper!.Input.Suppress(e.Button);
                Game1.pressActionButton(Game1.input.GetKeyboardState(), Game1.input.GetMouseState(),
                    Game1.input.GetGamePadState());
                return true;
            }
        }
        return false;
    }

    public override void OnButtonsChanged(object? sender, ButtonsChangedEventArgs e)
    {
        base.OnButtonsChanged(sender, e);
        
        if (!Context.IsPlayerFree)
            return;
        Instance.HandleKeys(sender, e);
    }

    public override void OnPlayerWarped(object? sender, WarpedEventArgs e)
    {
        // A route belongs to the location where it was calculated. Clear it before
        // refreshing the tracker so it can't move the player in the new map.
        CancelFootRoute();
        if (pathfinder?.IsActive == true)
            pathfinder.StopPathfinding();

        // reset the objects being tracked
        GetLocationObjects(resetFocus: true);
        // reset favorites stack to the first stack for new location.
        FavoriteStack = 0;
    }

    private void UpdateSpecialAction()
    {
        // Early exit if no location
        if (Game1.currentLocation == null) return;
        // Handle any special event refresh logic here
        if (Game1.currentLocation!.currentEvent != null)
        {
            switch(Game1.currentLocation!.currentEvent!.id)
            {
                case "festival_spring13":
                    UpdateAndRunIfChanged(ref objectCounts[6], Game1.currentLocation.currentEvent.festivalProps.Count, () => { Log.Debug("Eggs count has changed."); countHasChanged = true; });
                    return;
            }
        }
        else
        {
            switch (Game1.currentLocation)
            {
                case  IslandHut islandHut:
                    UpdateAndRunIfChanged(ref objectCounts[6], islandHut.treeHitLocal ? 1 : 0, () => { Log.Debug("Potted tree state has changed."); countHasChanged = true; });
                    return;
            }
        }
    }

    public void Tick()
    {
        if (!MainClass.Config.OTAutoRefreshing || Game1.currentLocation == null) return;

        if (updateActions.Count == 0)
        {
            Log.Error("No update actions to run.");
            return;
        }

        // Cycle through the actions without wrapping around
        var (action, edgeOfList) = MiscUtils.Cycle(updateActions, ref currentActionIndex, wrapAround: false);

        // If we've reached the end of the cycle
        if (edgeOfList)
        {
            // If a change was detected, refresh the objects
            if (countHasChanged)
            {
                Log.Debug("Refreshing ObjectTracker; changes detected.");
                GetLocationObjects(resetFocus: SortByProximity);
                countHasChanged = false;  // Reset the flag for the next cycle
            }

            // Manually reset the index to 0 for the next iteration
            currentActionIndex = 0;

            // Return without running the action
            return;
        }

        // Run the selected action
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Log.Error($"Error in RunUpdateAction: {ex.Message}");
        }
    }

    private bool RetryPathfinding(int attemptNumber, int maxRetries, Vector2? lastTargetedTile)
    {
        // Mounted routes stall on geometry, not just on NPCs; ghosting doesn't help there.
        // Record a request for the game thread instead — the watchdog timer runs on a
        // threadpool thread, where neither player.controller nor tracker state may be
        // touched. Even the give-up is deferred, so always return true here (returning
        // false would make the timer stop the route from its own thread).
        if (Game1.player.isRidingHorse())
        {
            lock (mountedReplanGate)
            {
                pendingMountedGiveUp = mountedReplanAttempts >= MaxMountedReplans || lastTargetedTile == null;
                if (!pendingMountedGiveUp)
                    pendingMountedTarget = lastTargetedTile!.Value;
                pendingMountedGeneration = mountedRouteGeneration;
            }
            return true;
        }
        if (IsFocusValid() && attemptNumber < maxRetries)
        {
            if (trackedObjects != null && trackedObjects.GetObjects().TryGetValue("characters", out var characters))
            {
                foreach (var kvp in characters)
                {
                    // NPCs don't usually have the same name so the list should only really contain one item.
                    foreach (var kvp2 in kvp.Value)
                    {
                        NPC? character = kvp2.character;
                        GhostNPC(character, sameTile: true);
                    }
                }
            }
            return true;
        }
        GetLocationObjects(resetFocus: true);
        return false;
    }

    private void StopPathfinding(Vector2? lastTargetedTile)
    {
        ResetMountedReplanState();
        FixCharacterMovement();

        // A leg of an obstacle route ended. Only a stop ON the leg's last tile counts as
        // arrival; a stall or stop anywhere else drops the route (the explicit cancel
        // sites clear footRoute before calling us, so those never get here with one).
        FootRoute? route = footRoute;
        if (route is { Pending: { } pending, Waiting: false })
        {
            if (Game1.player.TilePoint == route.LegEnd && !Game1.player.isRidingHorse())
            {
                pathfinder?.Dispose();
                footLegArrived = true; // announced from Update, on the game thread
                return;
            }
            footRoute = null;
        }
        else if (route != null)
        {
            footRoute = null; // final leg done
        }

        if (lastTargetedTile != null) FacePlayerToTargetTile(lastTargetedTile.Value);
        if (tileWalk)
        {
            bool arrived = lastTargetedTile != null && Game1.player.TilePoint == lastTargetedTile.Value.ToPoint();
            MainClass.ScreenReader.TranslateAndSay(arrived ? "feature-tile_viewer-reached" : "feature-tile_viewer-stopped_moving", true);
        }
        else
        {
            ReadCurrentlySelectedObject();
        }
        GetLocationObjects(resetFocus: SortByProximity);
        pathfinder?.Dispose();
    }

    // TODO Check what this does
    private bool IsFocusValid()
    {
        if (SelectedCategory != null && SelectedObjectGroup != null)
            return trackedObjects?.GetObjects().ContainsKey(SelectedCategory) == true && trackedObjects.GetObjects()[SelectedCategory].ContainsKey(SelectedObjectGroup);
        return false;
    }

    // TODO Check what this does
    private bool IsValidSelection()
    {
        return trackedObjects != null && SelectedCategory != null && SelectedObjectGroup != null;
    }

    private SpecialObject? GetCurrentlySelectedObject()
    {
        return SelectedCategory != null && SelectedObjectGroup != null && SelectedObjectIndex != null
               && trackedObjects?.GetObjects()?.TryGetValue(SelectedCategory, out var categoryObjects) == true
            ? categoryObjects.TryGetValue(SelectedObjectGroup, out var selectedObjectGroup)
                ? selectedObjectGroup[SelectedObjectIndex ?? throw new ArgumentNullException("SelectedObjectIndex cannot be null.")]
                : null
            : null;
    }

    private void ReadCurrentlySelectedObject(bool readTileOnly = false)
    {
        if (!IsValidSelection())
            return;

        Farmer player = Game1.player;
        SpecialObject? sObject = GetCurrentlySelectedObject();

        if (sObject != null)
        {
            Vector2 playerTile = player.Tile;
            Vector2? sObjectTile = sObject?.TileLocation;
            if (sObjectTile != null)
            {
                string direction = GetDirection(playerTile, sObjectTile.Value);
                string distance = GetDistance(playerTile, sObjectTile).ToString();
                if (SelectedCoordinates != null)
                {
                    object? translationTokens = new
                    {
                        coordinates_x = SelectedCoordinates.Value.X.ToString(),
                        coordinates_y = SelectedCoordinates.Value.Y.ToString(),
                        only_tile = readTileOnly ? 1 : 0,
                        player_x = (int)playerTile.X,
                        player_y = (int)playerTile.Y,
                        direction,
                        distance
                    };
                } else {
                    object? translationTokens = new
                    {
                        object_name = SelectedObjectGroup ??
                                      Translator.Instance.Translate("feature-object_tracker-no_selected_object"),
                        only_tile = readTileOnly ? 1 : 0,
                        object_x = (int)sObjectTile.Value.X,
                        object_y = (int)sObjectTile.Value.Y,
                        player_x = (int)playerTile.X,
                        player_y = (int)playerTile.Y,
                        direction,
                        distance
                    };
                    MainClass.ScreenReader.TranslateAndSay(SelectedCoordinates != null ? "feature-object_tracker-read_selected_coordinates" : "feature-object_tracker-read_selected_object", true,
                        translationTokens: translationTokens);
                }
            }
        }
    }

    private void SetFocusToFirstObject(bool resetCategory = false)
    {
        var objects = trackedObjects?.GetObjects();
        if (objects == null || !objects.Any())
        {
            MainClass.ScreenReader.TranslateAndSay("feature-object_tracker-no_objects_found", true);
            return;
        }

        if (!objects.Keys.Any())
        {
            MainClass.ScreenReader.TranslateAndSay("feature-object_tracker-no_categories_found", true);
            return;
        }

        // If SelectedCategory is unset or resetCategory is true, set SelectedCategory to the first available category
        if (string.IsNullOrEmpty(SelectedCategory) || resetCategory || !objects.ContainsKey(SelectedCategory))
        {
            SelectedCategory = objects.Keys.First();
            SelectedObjectGroup = null;
        }

        // Set SelectedObject to the first item in the current category
        if (SelectedCategory != null && objects.TryGetValue(SelectedCategory, out var catObjects) && catObjects.Any())
        {
            SelectedObjectGroup = catObjects.Keys.First();
            SelectedObjectIndex = 0;
        }
        else
        {
            SelectedObjectGroup = null;
            SelectedObjectIndex = null;
        }

        string outputCategory = SelectedCategory ?? "No Category";
        string outputObjectGroup = SelectedObjectGroup ?? "No Object Group";
        Log.Debug($"Category: {outputCategory} | Object Group: {outputObjectGroup} | Index: {SelectedObjectIndex}");
    }

    internal void GetLocationObjects(bool resetFocus = true)
    {
        TrackedObjects tObjects = new();
        tObjects.FindObjectsInArea(SortByProximity);
        trackedObjects = tObjects;

        var objects = trackedObjects.GetObjects();

        if (!resetFocus && SelectedCategory != null && !objects.ContainsKey(SelectedCategory))
        {
            resetFocus = true;
        }

        if (resetFocus)
        {
            SetFocusToFirstObject();
        }

        if (trackedObjects == null || SelectedCategory == null || SelectedObjectGroup == null || SelectedObjectIndex == null)
        {
            return;
        }

        if (!objects.TryGetValue(SelectedCategory, out var categoryObjects) || !categoryObjects.ContainsKey(SelectedObjectGroup))
        {
            SetFocusToFirstObject(false);
        }
    }

    private void Cycle(CycleType cycleType, bool back = false, bool wrapAround = false)
    {
        walkOffer = null;
        if (!IsValidSelection())
            return;

        var objects = trackedObjects?.GetObjects();
        string suffixText = string.Empty;
        string endOfList = Translator.Instance.Translate("feature-object_tracker-end_of_list");
        string startOfList = Translator.Instance.Translate("feature-object_tracker-start_of_list");
        string noObject = Translator.Instance.Translate("feature-object_tracker-no_object");
        string noCategory = Translator.Instance.Translate("feature-object_tracker-no_category");
        SpecialObject? selectedObject = null;

        void CycleHelper(ref string? selectedItem, string[] items)
        {
            if (selectedItem is null) return;
            
            int index = Array.IndexOf(items, selectedItem);
            if (index == -1)
            {
                SetFocusToFirstObject(cycleType == CycleType.CATEGORY);
                index = 0; // Reset index to 0 after setting focus to first object
            }

            var (selected, edgeOfList) = MiscUtils.Cycle(items, ref index, back, wrapAround);
            selectedItem = selected;
            suffixText = edgeOfList ? (wrapAround ? (back ? endOfList : startOfList) : (back ? startOfList : endOfList)) : string.Empty;
        }

        switch (cycleType)
        {
            case CycleType.CATEGORY:
                string[] categories = objects?.Keys.ToArray() ?? [];
                CycleHelper(ref SelectedCategory, categories);
                SetFocusToFirstObject(false);
                break;
            case CycleType.OBJECT_GROUP:
                string[] objectKeys = SelectedCategory != null && objects?.ContainsKey(SelectedCategory) == true
                    ? [.. objects[SelectedCategory].Keys]
                    : [];
                CycleHelper(ref SelectedObjectGroup, objectKeys);
                SelectedObjectIndex = 0;
                break;
            case CycleType.IN_GROUP:
                // TODO Organize
                var groupItems = SelectedCategory != null
                                 && SelectedObjectGroup != null
                                 && objects?.ContainsKey(SelectedCategory) == true
                                 && objects[SelectedCategory]?.ContainsKey(SelectedObjectGroup) == true
                    ? objects[SelectedCategory][SelectedObjectGroup]
                    : [];
                var index = SelectedObjectIndex ?? 0;
                var (selected, edgeOfList) = MiscUtils.Cycle(groupItems, ref index, back, wrapAround);
                selectedObject = selected;
                SelectedObjectIndex = index;
                suffixText = edgeOfList
                    ? (wrapAround ? (back ? endOfList : startOfList) : (back ? startOfList : endOfList))
                    : string.Empty;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(cycleType), cycleType, null);
        }

        suffixText = suffixText.Length > 0 ? ", " + suffixText : string.Empty;
        string groupInfo = cycleType == CycleType.OBJECT_GROUP
            ? SelectedObjectGroup != null && objects != null && SelectedCategory != null &&
              objects[SelectedCategory][SelectedObjectGroup].Count > 1
                ? ", " + Translator.Instance.Translate(
                    translationKey: "feature-object_tracker-group_info",
                    tokens: new { count = objects[SelectedCategory][SelectedObjectGroup].Count }
                )
                : ""
            : "";
        string objectInGroup = cycleType == CycleType.IN_GROUP && SelectedObjectGroup != null && selectedObject != null
            ? Translator.Instance.Translate("feature-object_tracker-read_selected_object_in_group",
                tokens: new
                {
                    object_x = selectedObject.TileLocation.X,
                    object_y = selectedObject.TileLocation.Y,
                    read_position = string.IsNullOrEmpty(suffixText) && SelectedObjectGroup != null &&
                                    objects != null && SelectedCategory != null
                        ? 1
                        : 0,
                    pos_in_group = (SelectedObjectIndex ?? 0) + 1,
                    count = SelectedObjectGroup != null && objects != null && SelectedCategory != null
                        ? objects[SelectedCategory][SelectedObjectGroup].Count
                        : 0,
                })
            : "";
        
        string spokenText = cycleType switch
        {
            CycleType.CATEGORY => $"{SelectedCategory ?? noCategory}, {SelectedObjectGroup ?? noObject}" + suffixText,
            CycleType.OBJECT_GROUP => $"{SelectedObjectGroup ?? noObject}" + groupInfo + suffixText,
            CycleType.IN_GROUP => selectedObject == null
                ? $"{SelectedObjectGroup ?? noObject}"
                : objectInGroup + suffixText,
            _ => throw new ArgumentOutOfRangeException(nameof(cycleType), cycleType, null)
        };

        MainClass.ScreenReader.Say(spokenText, true);
    }

    internal void HandleKeys(object? sender, ButtonsChangedEventArgs e)
    {
        bool cycleUpCategoryPressed = MainClass.Config.OTCycleUpCategory.JustPressed();
        bool cycleDownCategoryPressed = MainClass.Config.OTCycleDownCategory.JustPressed();
        bool cycleUpInGroupPressed = MainClass.Config.OTCycleUpInGroup.JustPressed();
        bool cycleDownInGroupPressed = MainClass.Config.OTCycleDownInGroup.JustPressed();
        bool cycleUpObjectGroupPressed = MainClass.Config.OTCycleUpObjectGroup.JustPressed();
        bool cycleDownObjectGroupPressed = MainClass.Config.OTCycleDownObjectGroup.JustPressed();
        bool readSelectedObjectPressed = MainClass.Config.OTReadSelectedObject.JustPressed();
        bool switchSortingModePressed = MainClass.Config.OTSwitchSortingMode.JustPressed();
        bool moveToSelectedObjectPressed = MainClass.Config.OTMoveToSelectedObject.JustPressed();
        bool readSelectedObjectTileLocationPressed = MainClass.Config.OTReadSelectedObjectTileLocation.JustPressed();
        
        int favoriteKeyJustPressed = 0;

        if (MainClass.Config.OTFavorite1.JustPressed()) favoriteKeyJustPressed = 1;
        else if (MainClass.Config.OTFavorite2.JustPressed()) favoriteKeyJustPressed = 2;
        else if (MainClass.Config.OTFavorite3.JustPressed()) favoriteKeyJustPressed = 3;
        else if (MainClass.Config.OTFavorite4.JustPressed()) favoriteKeyJustPressed = 4;
        else if (MainClass.Config.OTFavorite5.JustPressed()) favoriteKeyJustPressed = 5;
        else if (MainClass.Config.OTFavorite6.JustPressed()) favoriteKeyJustPressed = 6;
        else if (MainClass.Config.OTFavorite7.JustPressed()) favoriteKeyJustPressed = 7;
        else if (MainClass.Config.OTFavorite8.JustPressed()) favoriteKeyJustPressed = 8;
        else if (MainClass.Config.OTFavorite9.JustPressed()) favoriteKeyJustPressed = 9;
        else if (MainClass.Config.OTFavorite10.JustPressed()) favoriteKeyJustPressed = 10;
        else if (MainClass.Config.OTFavoriteDecreaseStack.JustPressed()) favoriteKeyJustPressed = 11;
        else if (MainClass.Config.OTFavoriteIncreaseStack.JustPressed()) favoriteKeyJustPressed = 12;
        else if (MainClass.Config.OTFavoriteSaveCoordinatesToggle.JustPressed()) favoriteKeyJustPressed = 13;
        else if (MainClass.Config.OTFavoriteSaveDefault.JustPressed()) favoriteKeyJustPressed = 14;

        if (favoriteKeyJustPressed > 0)
        {
            HandleFavorite(favoriteKeyJustPressed);
            foreach(var button in e.Pressed)
                MainClass.ModHelper!.Input.Suppress(button);
        }
        else 
        {
            if (e.Pressed.Any()) FavoriteKeysReset();
            if (cycleUpCategoryPressed)
            {
                Cycle(cycleType: CycleType.CATEGORY, back: true, wrapAround: MainClass.Config?.OTWrapLists ?? false);
            }
            else if (cycleDownCategoryPressed)
            {
                Cycle(cycleType: CycleType.CATEGORY, wrapAround: MainClass.Config?.OTWrapLists ?? false);
            }
            else if (cycleUpInGroupPressed)
            {
                Cycle(cycleType: CycleType.IN_GROUP, back: true, wrapAround: MainClass.Config?.OTWrapLists ?? false);
            }
            else if (cycleDownInGroupPressed)
            {
                Cycle(cycleType: CycleType.IN_GROUP, wrapAround: MainClass.Config?.OTWrapLists ?? false);
            }
            else if (cycleUpObjectGroupPressed)
            {
                Cycle(cycleType: CycleType.OBJECT_GROUP, back: true, wrapAround: MainClass.Config?.OTWrapLists ?? false);
            }
            else if (cycleDownObjectGroupPressed)
            {
                Cycle(cycleType: CycleType.OBJECT_GROUP, wrapAround: MainClass.Config?.OTWrapLists ?? false);
            }

            if (readSelectedObjectPressed || moveToSelectedObjectPressed || readSelectedObjectTileLocationPressed || switchSortingModePressed)
            {
                if (switchSortingModePressed)
                {
                    SortByProximity = !SortByProximity;
                    MainClass.ScreenReader.TranslateAndSay("feature-object_tracker-sort_by_proximity", true,
                        translationTokens: new { is_enabled = SortByProximity ? 1 : 0 });
                }
                GetLocationObjects(resetFocus: false);
                if (readSelectedObjectPressed)
                {
                    ReadCurrentlySelectedObject();
                }
                if (moveToSelectedObjectPressed)
                {
                    MoveToCurrentlySelectedObject();
                }
                if (readSelectedObjectTileLocationPressed)
                {
                    ReadCurrentlySelectedObject(readTileOnly: true);
                }
            }
        }
    }

    private void MoveToCurrentlySelectedObject()
    {
        Log.Debug("Attempt pathfinding.");
        if (walkOffer == null && IsFocusValid())
        {
            ReadCurrentlySelectedObject();
        }
        tileWalk = false;
        StartMove(GetCurrentlySelectedObject());
    }

    /// <summary>
    /// Auto walk to an exact tile (tile viewer's walk-to-tile key). Shares the tracker's whole
    /// walk: obstacle legs, footsteps, cancel keys, mounted planning and the nearest-reachable
    /// offer. Pressing the key again on the tile that was just reported unreachable confirms
    /// the offer; any other tile is a new destination.
    /// </summary>
    internal void WalkToTile(Point tile)
    {
        if (walkOffer is { } offer && offer.Target != tile)
            walkOffer = null;
        if (walkOffer == null)
            SelectedCoordinates = tile.ToVector2();
        tileWalk = true;
        StartMove(null);
    }

    private void StartMove(SpecialObject? sObject)
    {
        Farmer player = Game1.player;
        Vector2? sObjectTile = (sObject != null) ? sObject.TileLocation : (Vector2?)null;

        if (player.isRidingHorse())
        {
            MoveToCurrentlySelectedObjectMounted(player, sObject, sObjectTile);
            return;
        }

        if (MainClass.Config.OTPathThroughObstacles)
        {
            MoveToCurrentlySelectedObjectThroughObstacles(player, sObject, sObjectTile);
            return;
        }

        Vector2? closestTile = SelectedCoordinates ?? (sObject is not null ? (sObject.PathfindingOverride != null ? GetClosestTilePath((Vector2)sObject.PathfindingOverride) : GetClosestTilePath(sObjectTile)) : null);
        SelectedCoordinates = null;

        if (closestTile != null)
        {
            MainClass.ScreenReader.TranslateAndSay("feature-object_tracker-moving_to", true,
                translationTokens: new
                {
                    object_x = (int)closestTile.Value.X,
                    object_y = (int)closestTile.Value.Y
                });
            pathfinder?.Dispose();
            pathfinder = new(RetryPathfinding, StopPathfinding);
            pathfinder.StartPathfinding(player, Game1.currentLocation, closestTile.Value.ToPoint());
        }
        else
        {
            MainClass.ScreenReader.TranslateAndSay("feature-object_tracker-could_not_find_path", true);
        }
    }

    /// <summary>On-foot walk using the obstacle-aware planner (see FootPathfinder).</summary>
    private void MoveToCurrentlySelectedObjectThroughObstacles(Farmer player, SpecialObject? sObject, Vector2? sObjectTile)
    {
        CancelFootRoute();
        if (pathfinder is { IsActive: true })
        {
            // A new destination replaces the walk in progress, quietly (the stop callback
            // would read out the old target). Every branch below either starts a new
            // controller or expects none.
            pathfinder.Dispose();
            pathfinder = null;
            player.controller = null;
            FixCharacterMovement();
        }
        if (walkOffer is { } offer)
        {
            walkOffer = null;
            // A favorite pressed in between means a new target, not a confirmation.
            if (SelectedCoordinates == null && ReferenceEquals(offer.Location, Game1.currentLocation))
            {
                WalkOfferedRoute(offer);
                return;
            }
        }
        FootPlan plan;
        bool allowWarpEnd = false;
        if (SelectedCoordinates is Vector2 selectedCoordinates)
        {
            allowWarpEnd = true;
            plan = FootPathfinder.PlanTo(Game1.currentLocation, player.TilePoint, selectedCoordinates.ToPoint(), allowWarpEnd: true);
        }
        else
        {
            Vector2? target = sObject?.PathfindingOverride ?? sObjectTile;
            if (target == null)
            {
                SelectedCoordinates = null;
                MainClass.ScreenReader.TranslateAndSay("feature-object_tracker-could_not_find_path", true);
                return;
            }
            plan = FootPathfinder.PlanNextTo(Game1.currentLocation, player.TilePoint, target.Value.ToPoint());
        }
        SelectedCoordinates = null;

        if (plan.Path == null)
        {
            AnnounceNoFootPath(plan);
            return;
        }

        if (TooManyObstacles(plan, plan.Destination, allowWarpEnd))
            return;

        if (plan.Path.Count == 0)
        {
            FacePlayerToTargetTile(plan.Destination.ToVector2());
            if (tileWalk)
                MainClass.ScreenReader.TranslateAndSay("feature-tile_viewer-reached", true);
            else
                ReadCurrentlySelectedObject();
            return;
        }

        if (plan.Obstacles.Count == 0)
        {
            MainClass.ScreenReader.TranslateAndSay("feature-object_tracker-moving_to", true,
                translationTokens: new { object_x = plan.Destination.X, object_y = plan.Destination.Y });
        }
        else
        {
            MainClass.ScreenReader.TranslateAndSay("feature-object_tracker-path_with_obstacles", true,
                translationTokens: new { tiles = plan.Path.Count, obstacles = FootPathfinder.SummarizeObstacles(plan.Obstacles) });
        }

        FootRoute route = new() { Target = plan.Destination, AllowWarpEnd = allowWarpEnd };
        WalkFootLeg(route, plan);
    }

    private void MoveToCurrentlySelectedObjectMounted(Farmer player, SpecialObject? sObject, Vector2? sObjectTile)
    {
        Vector2? closestTile;
        Stack<Point>? mountedPath;

        if (SelectedCoordinates is Vector2 selectedCoordinates)
        {
            closestTile = selectedCoordinates;
            // Explicit coordinates may sit on a map exit; riding onto the warp is the point.
            mountedPath = FindMountedPath(Game1.currentLocation, player.TilePoint, selectedCoordinates.ToPoint(), allowWarpEnd: true);
        }
        else
        {
            Vector2? target = sObject?.PathfindingOverride ?? sObjectTile;
            (closestTile, mountedPath) = GetClosestMountedTilePath(target);
        }
        SelectedCoordinates = null;

        if (closestTile == null || mountedPath == null)
        {
            MainClass.ScreenReader.TranslateAndSay("feature-object_tracker-could_not_find_path", true);
            return;
        }

        if (mountedPath.Count == 0)
        {
            FacePlayerToTargetTile(closestTile.Value);
            ReadCurrentlySelectedObject();
            return;
        }

        MainClass.ScreenReader.TranslateAndSay("feature-object_tracker-moving_to", true,
            translationTokens: new
            {
                object_x = (int)closestTile.Value.X,
                object_y = (int)closestTile.Value.Y
            });
        ResetMountedReplanState();
        pathfinder?.Dispose();
        pathfinder = new(RetryPathfinding, StopPathfinding);
        pathfinder.StartPathfinding(player, Game1.currentLocation, closestTile.Value.ToPoint(), mountedPath);
    }

    public void SaveToFavorites(int hotkey)
    {
        string location = Game1.currentLocation.currentEvent is not null ? Game1.currentLocation.currentEvent.FestivalName : Game1.currentLocation.NameOrUniqueName;
        string currentSaveFileName = MainClass.GetCurrentSaveFileName();
        if (!favorites.ContainsKey(currentSaveFileName))
        {
            favorites[currentSaveFileName] = [];
        }
        if (!favorites[currentSaveFileName].ContainsKey(location))
        {
            favorites[currentSaveFileName][location] = [];
        }

        if (SaveCoordinatesToggle)
        {
            favorites[MainClass.GetCurrentSaveFileName()][location][hotkey] = (Vector2ToString(CurrentPlayer.FacingTile), "coordinates");
        } else {
            favorites[MainClass.GetCurrentSaveFileName()][location][hotkey] = (SelectedObjectGroup, SelectedCategory);
        }
        SaveFavorites();
    }

    public (string?, string?) GetFromFavorites(int hotkey)
    {
        string location = Game1.currentLocation.currentEvent is not null ? Game1.currentLocation.currentEvent.FestivalName : Game1.currentLocation.NameOrUniqueName;
        if (!favorites.TryGetValue(MainClass.GetCurrentSaveFileName(), out var _saveFileFavorites) || _saveFileFavorites != null)
        {
            LoadDefaultFavorites();
            SaveFavorites();
        }
        if (favorites.TryGetValue(MainClass.GetCurrentSaveFileName(), out var saveFileFavorites) && saveFileFavorites != null)
        {
            if (saveFileFavorites.TryGetValue(location, out var locationFavorites) && locationFavorites.TryGetValue(hotkey, out var value))
            {
                return value;
            }
        }

        return (null, null);
    }

    public void SetFromFavorites(int hotkey)
    {
        var (obj, category) = GetFromFavorites(hotkey);
        if (category != null && obj != null)
        {
            SelectedCategory = category;
            SelectedCoordinates = StringToVector2(obj);
            SelectedObjectGroup = obj;
            SelectedObjectIndex = 0;
        }
    }

    public void DeleteFavorite(int favoriteNumber)
    {
        string currentLocation = Game1.currentLocation.currentEvent is not null ? Game1.currentLocation.currentEvent.FestivalName : Game1.currentLocation.NameOrUniqueName;

        // Try to get the sub-dictionary for the current location
        if (favorites.TryGetValue(MainClass.GetCurrentSaveFileName(), out var saveFileFavorites) && saveFileFavorites != null)
        {
            if (saveFileFavorites.TryGetValue(currentLocation, out var locationFavorites))
            {
                // Remove the favorite entry if it exists
                locationFavorites.Remove(favoriteNumber);
                if (locationFavorites.Count == 0)
                {
                    // If empty, remove the location from the favorites
                    favorites.Remove(currentLocation);
                }
                SaveFavorites();
            }
        }
    }

    private void FavoriteKeysReset()
    {
        lastFavoritePressed = 0;
        sameFavoritePressed = -1;
        lastPressTimer.Stop();
        SelectedCoordinates = null;
    }

    private void HandleFavorite(int favKeyNum)
    {
        if (lastFavoritePressed == favKeyNum)
        {
            sameFavoritePressed++;
        }
        else
        {
            sameFavoritePressed = 1;
            lastFavoritePressed = favKeyNum;
            lastPressTimer.Stop();
            lastPressTimer.Start();
        }

        if (favKeyNum > 10)
        {
            switch (favKeyNum)
            {
                case 11:
                    FavoriteStack--;
                    MainClass.ScreenReader.TranslateAndSay("feature-object_tracker-read_favorite_stack", true,
                        new {
                            stack_number = FavoriteStack + 1
                        }
                    );
                    break;
                case 12:
                    FavoriteStack++;
                    MainClass.ScreenReader.TranslateAndSay("feature-object_tracker-read_favorite_stack", true,
                        new {
                            stack_number = FavoriteStack + 1
                        }
                    );
                    break;
                case 13:
                    SaveCoordinatesToggle = !SaveCoordinatesToggle;
                    if (!SaveCoordinatesToggle)
                    {
                        SetFocusToFirstObject(true);
                    }
                    MainClass.ScreenReader.TranslateAndSay("feature-object_tracker-save_coordinates_toggle", true,
                            translationTokens: new { is_enabled = SaveCoordinatesToggle ? 1 : 0 });
                    break;
                case 14:
                    if (sameFavoritePressed <= 1)
                        SetAsDefaultFavorites();
                    else
                        ClearDefaultFavorites();
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(favKeyNum), favKeyNum, "The favorite key number cannot be greater than 12.");
            }
        } else {

            int favorite_number = favKeyNum + (FavoriteStack * 10);
            string? targetObject, targetCategory;
            (targetObject, targetCategory) = GetFromFavorites(favorite_number);
            bool isFavoriteSet = targetObject != null && targetCategory != null;
            // Logic for handling single, double, or triple presses
            if (sameFavoritePressed == 1)
            {
                // Handle single press
                if (isFavoriteSet)
                {
                    MainClass.ScreenReader.TranslateAndSay("feature-object_tracker-read_favorite", true,
                        new
                        {
                            favorite_number,
                            target_object = targetObject,
                            target_category = targetCategory
                        }
                    );
                }
                else
                {
                    MainClass.ScreenReader.TranslateAndSay("feature-object_tracker-favorite_unset", true,
                        new
                        {
                            favorite_number
                        }
                    );
                }
                lastPressTimer.Start();
            }
            else if (sameFavoritePressed == 2)
            {
                if (isFavoriteSet)
                {
                    // start a timer that will begin movement in `PressInterval` ms, allowing time for 3rd press to cancel it.
                    navigateToFavorite = favorite_number;
                    navigationTimer.Start();
                }
                else
                {
                    // this slot is unset; save current tracker target here
                    // Only if `SelectedObject` and `SelectedCategory` are both not null
                    if (SelectedObjectGroup != null && SelectedCategory != null)
                    {
                        SaveToFavorites(favorite_number);
                        if (SaveCoordinatesToggle)
                        {
                            MainClass.ScreenReader.TranslateAndSay("feature-object_tracker-favorite_save_coordinates", true,
                                new
                                {
                                    coordinates = Vector2ToString(CurrentPlayer.FacingTile),
                                    location_name = Game1.currentLocation.currentEvent is not null ? Game1.currentLocation.currentEvent.FestivalName : Game1.currentLocation.NameOrUniqueName,
                                    favorite_number
                                }
                            );
                        } else {
                            MainClass.ScreenReader.TranslateAndSay("feature-object_tracker-favorite_save", true,
                                new
                                {
                                    selected_object = SelectedObjectGroup,
                                    selected_category = SelectedCategory,
                                    location_name = Game1.currentLocation.currentEvent is not null ? Game1.currentLocation.currentEvent.FestivalName : Game1.currentLocation.NameOrUniqueName,
                                    favorite_number
                                }
                            );
                        }
                    }
                    else
                    {
                        MainClass.ScreenReader.TranslateAndSay("feature-object_tracker-no_destination_selected", true);
                    }
                }
            }
            else if (sameFavoritePressed >= 3)
            {
                navigationTimer.Stop();
                navigateToFavorite = -1;
                DeleteFavorite(favorite_number);
                MainClass.ScreenReader.TranslateAndSay("feature-object_tracker-favorite_cleared", true,
                    new
                    {
                        location_name = Game1.currentLocation.currentEvent is not null ? Game1.currentLocation.currentEvent.FestivalName : Game1.currentLocation.NameOrUniqueName,
                        favorite_number
                    }
                );
            }
        }
    }

    internal void LoadFavorites()
    {
        try
        {
            if (JsonLoader.TryLoadJsonFile("favorites.json", out JToken? jsonToken, "assets/TileData") && jsonToken is not null)
            {
                try
                {
                    favorites = jsonToken.ToObject<Dictionary<string, Dictionary<string, Dictionary<int, (string?, string?)>>>>() ?? [];
                }
                catch (JsonSerializationException)
                {
                    // Attempt to load in the old format
                    var oldFormatFavorites = jsonToken.ToObject<Dictionary<string, Dictionary<int, (string?, string?)>>>();
                    if (oldFormatFavorites != null)
                    {
                        favorites = new Dictionary<string, Dictionary<string, Dictionary<int, (string?, string?)>>>
                        {
                            [""] = oldFormatFavorites
                        };
                        Log.Alert("Loaded and converted favorites from the old format to the new format.");
                        LoadDefaultFavorites();
                        SaveFavorites();
                    }
                    else
                    {
                        favorites = [];
                    }
                }

                if (!favorites.TryGetValue(MainClass.GetCurrentSaveFileName(), out var saveFileFavorites) && favorites.TryGetValue("", out var defaultFavorites) && defaultFavorites != null)
                {
                    LoadDefaultFavorites();
                }
            }
            else
            {
                throw new FileNotFoundException("Could not load favorites.json or the file is empty.");
            }
        }
        catch (Exception ex)
        {
            Log.Warn(ex.Message);
            favorites = [];
        }
    }

    private void LoadDefaultFavorites()
    {
        if (favorites.TryGetValue("", out var defaultFavorites) && defaultFavorites != null)
        {
            favorites[MainClass.GetCurrentSaveFileName()] = defaultFavorites;
        }
    }
    
    private void SetAsDefaultFavorites()
    {
        if (favorites.TryGetValue(MainClass.GetCurrentSaveFileName(), out var saveFileFavorites) && saveFileFavorites != null)
        {
            favorites[""] = saveFileFavorites;
            MainClass.ScreenReader.TranslateAndSay("feature-object_tracker-favorite_set_as_default", true);
            SaveFavorites();
        }
    }
    
    private void ClearDefaultFavorites()
    {
        favorites.Remove("");
        MainClass.ScreenReader.TranslateAndSay("feature-object_tracker-favorite_default_cleared", true);
    }

    internal void SaveFavorites()
    {
        #if DEBUG
        Log.Verbose("Saving favorites");
        #endif
        JsonLoader.SaveJsonFile("favorites.json", favorites, "assets/TileData");
    }
    
    private static string Vector2ToString(Vector2 coordinates)
    {
        return $"{coordinates.X}, {coordinates.Y}";
    }   

    private static Vector2? StringToVector2(string? input)
    {
        if (string.IsNullOrEmpty(input))
            return null;
        string[] coordinates = input.Split(',');
        if (coordinates.Length != 2)
            return null;
        if (float.TryParse(coordinates[0].Trim(), out float x) && float.TryParse(coordinates[1].Trim(), out float y))
        {
            return new Vector2(x, y);
        }
        return null;
    }

}
