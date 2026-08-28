using Microsoft.Xna.Framework;
using stardew_access.Translation;
using StardewValley;
using StardewValley.BellsAndWhistles;
using StardewValley.Locations;
using StardewValley.Objects;
using StardewValley.TerrainFeatures;
using StardewValley.Tools;

namespace stardew_access.Utils;

/// <summary>What sits on a blocked tile, and whether the player can clear it.</summary>
internal enum ObstacleKind
{
    Clear,
    Wall,
    Weeds,
    Stone,
    Twig,
    ArtifactSpot,
    Container,
    MineRock,
    Boulder,
    Meteorite,
    Stump,
    HollowLog,
    GiantCrop,
    Tree,
    Lava,
    ClosedGate,
    Bridge,
}

/// <summary>A single tile on a planned route that the player must clear before walking on.</summary>
internal sealed record Obstacle(Point Tile, ObstacleKind Kind, string Name, string ToolName);

/// <summary>Result of an on-foot plan.</summary>
internal sealed class FootPlan
{
    /// <summary>The tile the route ends on (the requested goal, or the ring tile chosen next to it).</summary>
    public Point Destination;
    /// <summary>Route from the tile after the start up to and including the destination. Null when unreachable.</summary>
    public Stack<Point>? Path;
    /// <summary>Obstacles on the route, in walking order.</summary>
    public List<Obstacle> Obstacles = [];
    /// <summary>When unreachable: the reached tile closest to the goal, if any.</summary>
    public Point? NearestReachable;
    /// <summary>When unreachable: the reason for the tile at the goal itself, if it is a known unclearable obstacle.</summary>
    public string? BlockedBy;
}

/// <summary>
/// On-foot route planner for the object tracker. Unlike the vanilla PathFindController
/// A* (which the tracker used before), a blocked tile is not automatically a wall: stones,
/// weeds, twigs, mine rocks, crates, closed gates, lava and (optionally) big clumps and
/// trees are "clearable obstacles" with a higher step cost, so the plan goes through them
/// and reports each one. The caller walks up to the first obstacle, waits for the player to
/// remove it, then replans. Probes are side-effect-free like the mounted planner's.
/// </summary>
internal static class FootPathfinder
{
    private static readonly int[] DeltaX = [0, 1, 0, -1];
    private static readonly int[] DeltaY = [-1, 0, 1, 0];

    private const int CostClear = 1;
    private const int CostLight = 6;   // one or two swings
    private const int CostLava = 8;
    private const int CostMineRock = 12;
    private const int CostHeavy = 20;  // stump, log, boulder, meteorite
    private const int CostTree = 30;
    private const int WarpEndPenalty = 3;
    private const int MaxFloodNodes = 40000;

    internal sealed class TileInfo
    {
        public ObstacleKind Kind;
        public bool Clearable;
        public int Cost;
        public string Name = "";
        public string ToolName = "";
        public bool IsWarp;
        public bool Walkable => Kind == ObstacleKind.Clear || Kind == ObstacleKind.Bridge || Clearable;
    }

    internal sealed class Options
    {
        public bool AllowTreesAndBoulders;
        public bool ObstaclesInMines = true;
        public bool AllowWarpEnd;
    }

    private static Options CurrentOptions(bool allowWarpEnd) => new()
    {
        AllowTreesAndBoulders = MainClass.Config.OTAllowTreesAndBoulders,
        ObstaclesInMines = MainClass.Config.OTObstaclesInMines,
        AllowWarpEnd = allowWarpEnd,
    };

    // --- Tile probing ---------------------------------------------------------------

    /// <summary>Vanilla-style whole-tile probe (PathFindController.findPath uses the same 62x62 box).</summary>
    internal static bool IsTileBlocked(GameLocation location, Point tile)
    {
        Rectangle box = new(tile.X * Game1.tileSize + 1, tile.Y * Game1.tileSize + 1, 62, 62);
        return location.isCollidingPosition(box, Game1.viewport, isFarmer: true, 0, glider: false,
            character: null, pathfinding: true, projectile: false,
            ignoreCharacterRequirement: true, skipCollisionEffects: true);
    }

    private static string ToolName(string qualifiedId)
        => ItemRegistry.GetDataOrErrorItem(qualifiedId).DisplayName;

    private static string T(string key) => Translator.Instance.Translate(key);

    private static int BestToolLevel<TTool>() where TTool : Tool
    {
        int best = -1;
        foreach (Item? item in Game1.player.Items)
        {
            if (item is TTool tool && tool.UpgradeLevel > best)
                best = tool.UpgradeLevel;
        }
        return best;
    }

    private static void Set(TileInfo info, ObstacleKind kind, bool clearable, int cost, string name, string toolName)
    {
        info.Kind = kind;
        info.Clearable = clearable;
        info.Cost = cost;
        info.Name = name;
        info.ToolName = toolName;
    }

    /// <summary>Classify one tile: clear, a clearable obstacle (with tool), or a wall.</summary>
    internal static TileInfo Classify(GameLocation location, Point tile, Options options, int mapWidth, int mapHeight)
    {
        TileInfo info = new();
        if (tile.X < 0 || tile.Y < 0 || tile.X >= mapWidth || tile.Y >= mapHeight)
        {
            Set(info, ObstacleKind.Wall, false, 0, "", "");
            return info;
        }

        info.IsWarp = DoorUtils.IsWarpAtTile((tile.X, tile.Y), location);

        if (!IsTileBlocked(location, tile))
        {
            Set(info, ObstacleKind.Clear, false, CostClear, "", "");
            return info;
        }

        bool obstaclesAllowed = options.ObstaclesInMines || location is not MineShaft;
        Vector2 tileVector = new(tile.X, tile.Y);

        // 1. Placed / spawned objects.
        if (location.objects.TryGetValue(tileVector, out StardewValley.Object? obj) && !obj.isPassable())
        {
            if (obj is Fence fence)
            {
                if (fence.isGate.Value)
                    Set(info, ObstacleKind.ClosedGate, true, CostLight, T("feature-object_tracker-obstacle-closed_gate"), "");
                else
                    Set(info, ObstacleKind.Wall, false, 0, obj.DisplayName, "");
                return info;
            }
            if (!obstaclesAllowed)
            {
                Set(info, ObstacleKind.Wall, false, 0, obj.DisplayName, "");
                return info;
            }
            if (obj.IsWeeds())
                Set(info, ObstacleKind.Weeds, true, CostLight, obj.DisplayName, T("feature-object_tracker-obstacle-any_tool"));
            else if (obj.IsBreakableStone())
                Set(info, ObstacleKind.Stone, true, CostLight, obj.DisplayName, ToolName("(T)Pickaxe"));
            else if (obj.IsTwig())
                Set(info, ObstacleKind.Twig, true, CostLight, obj.DisplayName, ToolName("(T)Axe"));
            else if (obj.QualifiedItemId == "(O)590" || obj.QualifiedItemId == "(O)SeedSpot")
                Set(info, ObstacleKind.ArtifactSpot, true, CostLight, obj.DisplayName, ToolName("(T)Hoe"));
            else if (obj is BreakableContainer)
                Set(info, ObstacleKind.Container, true, CostLight, obj.DisplayName, T("feature-object_tracker-obstacle-any_tool"));
            else
                Set(info, ObstacleKind.Wall, false, 0, obj.DisplayName, "");
            return info;
        }

        // 2. Resource clumps (boulders, stumps, logs, mine rocks, giant crops).
        foreach (ResourceClump clump in location.resourceClumps)
        {
            if (!clump.occupiesTile(tile.X, tile.Y))
                continue;
            ClassifyClump(info, clump, options, obstaclesAllowed);
            return info;
        }

        // 3. Trees.
        if (location.terrainFeatures.TryGetValue(tileVector, out TerrainFeature? feature))
        {
            if (feature is Tree or FruitTree)
            {
                string name = T("feature-object_tracker-obstacle-tree");
                if (options.AllowTreesAndBoulders && obstaclesAllowed)
                    Set(info, ObstacleKind.Tree, true, CostTree, name, ToolName("(T)Axe"));
                else
                    Set(info, ObstacleKind.Wall, false, 0, name, "");
                return info;
            }
        }

        // 4. Volcano lava (cooled lava gets a Passable tile property and probes as clear).
        if (location is VolcanoDungeon volcano && volcano.isWaterTile(tile.X, tile.Y) && !volcano.IsCooledLava(tile.X, tile.Y))
        {
            string name = T("feature-object_tracker-obstacle-lava");
            // The caldera (level 5) can't be cooled: the game refuses the watering can there.
            if (volcano.level.Value != 5 && obstaclesAllowed)
                Set(info, ObstacleKind.Lava, true, CostLava, name, ToolName("(T)WateringCan"));
            else
                Set(info, ObstacleKind.Wall, false, 0, name, "");
            return info;
        }

        Set(info, ObstacleKind.Wall, false, 0, "", "");
        return info;
    }

    private static void ClassifyClump(TileInfo info, ResourceClump clump, Options options, bool obstaclesAllowed)
    {
        int index = clump.parentSheetIndex.Value;
        string pickaxe = ToolName("(T)Pickaxe");
        string axe = ToolName("(T)Axe");

        if (clump is GiantCrop)
        {
            string name = T("feature-object_tracker-obstacle-giant_crop");
            if (options.AllowTreesAndBoulders && obstaclesAllowed)
                Set(info, ObstacleKind.GiantCrop, true, CostTree, name, axe);
            else
                Set(info, ObstacleKind.Wall, false, 0, name, "");
            return;
        }

        switch (index)
        {
            case ResourceClump.mineRock1Index:
            case ResourceClump.mineRock2Index:
            case ResourceClump.mineRock3Index:
            case ResourceClump.mineRock4Index:
                if (obstaclesAllowed)
                    Set(info, ObstacleKind.MineRock, true, CostMineRock, T("feature-object_tracker-obstacle-mine_rock"), pickaxe);
                else
                    Set(info, ObstacleKind.Wall, false, 0, T("feature-object_tracker-obstacle-mine_rock"), "");
                return;
            case ResourceClump.stumpIndex:
                Heavy(info, ObstacleKind.Stump, T("feature-object_tracker-obstacle-stump"), axe, BestToolLevel<Axe>() >= 1, options, obstaclesAllowed);
                return;
            case ResourceClump.hollowLogIndex:
                Heavy(info, ObstacleKind.HollowLog, T("feature-object_tracker-obstacle-hollow_log"), axe, BestToolLevel<Axe>() >= 2, options, obstaclesAllowed);
                return;
            case ResourceClump.boulderIndex:
                Heavy(info, ObstacleKind.Boulder, T("feature-object_tracker-obstacle-boulder"), pickaxe, BestToolLevel<Pickaxe>() >= 2, options, obstaclesAllowed);
                return;
            case ResourceClump.meteoriteIndex:
            case ResourceClump.quarryBoulderIndex:
                Heavy(info, ObstacleKind.Meteorite, T("feature-object_tracker-obstacle-meteorite"), pickaxe, BestToolLevel<Pickaxe>() >= 3, options, obstaclesAllowed);
                return;
            default:
                Set(info, ObstacleKind.Wall, false, 0, T("feature-object_tracker-obstacle-clump"), "");
                return;
        }
    }

    private static void Heavy(TileInfo info, ObstacleKind kind, string name, string tool, bool toolGoodEnough, Options options, bool obstaclesAllowed)
    {
        if (options.AllowTreesAndBoulders && obstaclesAllowed && toolGoodEnough)
            Set(info, kind, true, CostHeavy, name, tool);
        else
            Set(info, ObstacleKind.Wall, false, 0, name, toolGoodEnough ? "" : tool);
    }

    // --- Suspension bridges (Ginger Island north) ----------------------------------

    private sealed class BridgeMap
    {
        public readonly HashSet<Point> Span = [];
        public readonly HashSet<Point> Entrances = [];
        public bool Any => Span.Count > 0;
    }

    private static BridgeMap CollectBridges(GameLocation location)
    {
        BridgeMap map = new();
        if (location is not IslandNorth north)
            return map;
        foreach (SuspensionBridge bridge in north.suspensionBridges)
        {
            Rectangle bounds = bridge.bridgeBounds;
            for (int x = bounds.X / 64; x < bounds.Right / 64; x++)
                map.Span.Add(new Point(x, bounds.Y / 64));
            foreach (Rectangle entrance in bridge.bridgeEntrances)
                map.Entrances.Add(new Point(entrance.X / 64, entrance.Y / 64));
        }
        return map;
    }

    /// <summary>
    /// Bridge planks are only walkable sideways, and only entered from an entrance tile or a
    /// neighbouring plank: the game attaches <c>Farmer.bridge</c> when the box is fully inside
    /// an entrance tile and then relaxes collision along the span while <c>onBridge</c>.
    /// </summary>
    private static bool BridgeEdgeAllowed(BridgeMap bridges, Point from, Point to, bool vertical)
    {
        bool fromSpan = bridges.Span.Contains(from);
        bool toSpan = bridges.Span.Contains(to);
        if (!fromSpan && !toSpan)
            return true;
        if (vertical)
            return false;
        if (toSpan)
            return fromSpan || bridges.Entrances.Contains(from);
        // Leaving the span: only onto a plank or an entrance.
        return bridges.Entrances.Contains(to);
    }

    // --- Flood ----------------------------------------------------------------------

    private sealed class Flood
    {
        public readonly Dictionary<Point, int> Cost = [];
        public readonly Dictionary<Point, Point> CameFrom = [];
        public readonly Dictionary<Point, TileInfo> Tiles = [];
        public bool BudgetExhausted;
    }

    /// <summary>
    /// One bounded Dijkstra flood from the player over weighted tiles. Warp tiles are entered
    /// (so they can be a destination) but never expanded (walking across one mid-route would
    /// teleport the player).
    /// </summary>
    private static Flood RunFlood(GameLocation location, Point start, Options options, BridgeMap bridges, Point? goal)
    {
        int mapWidth = location.map.Layers[0].LayerWidth;
        int mapHeight = location.map.Layers[0].LayerHeight;
        int budget = Math.Min(MaxFloodNodes, Math.Max(12000, mapWidth * mapHeight * 2));

        Flood flood = new();
        TileInfo InfoAt(Point tile)
        {
            if (!flood.Tiles.TryGetValue(tile, out TileInfo? info))
            {
                info = Classify(location, tile, options, mapWidth, mapHeight);
                if (bridges.Span.Contains(tile))
                    Set(info, ObstacleKind.Bridge, false, CostClear, "", "");
                flood.Tiles[tile] = info;
            }
            return info;
        }

        // The player is standing on the start tile legally even when it probes as blocked
        // (e.g. a wide box edge); never plan it as a wall.
        TileInfo startInfo = InfoAt(start);
        if (!startInfo.Walkable)
            Set(startInfo, ObstacleKind.Clear, false, CostClear, "", "");
        if (goal is Point requestedGoal && options.AllowWarpEnd && DoorUtils.IsWarpAtTile((requestedGoal.X, requestedGoal.Y), location))
        {
            // Vanilla warps can sit one tile off-map (Town -> BusStop at x = -1); accept them as
            // the endpoint regardless of bounds, like the mounted planner does.
            TileInfo goalInfo = new() { IsWarp = true };
            Set(goalInfo, ObstacleKind.Clear, false, CostClear, "", "");
            flood.Tiles[requestedGoal] = goalInfo;
        }

        PriorityQueue<Point, int> open = new();
        flood.Cost[start] = 0;
        open.Enqueue(start, 0);
        HashSet<Point> closed = [];
        int visited = 0;

        while (open.Count > 0)
        {
            Point current = open.Dequeue();
            if (!closed.Add(current))
                continue;
            if (visited++ >= budget)
            {
                flood.BudgetExhausted = true;
                break;
            }
            if (goal is Point g && current == g)
                break;
            if (InfoAt(current).IsWarp && current != start)
                continue;

            for (int direction = 0; direction < 4; direction++)
            {
                Point next = new(current.X + DeltaX[direction], current.Y + DeltaY[direction]);
                bool vertical = DeltaY[direction] != 0;
                if (closed.Contains(next))
                    continue;
                TileInfo nextInfo = InfoAt(next);
                if (!nextInfo.Walkable)
                    continue;
                if (bridges.Any && !BridgeEdgeAllowed(bridges, current, next, vertical))
                    continue;
                if (nextInfo.IsWarp && !(options.AllowWarpEnd && goal == next) && goal != null)
                    continue; // direct plan: warps are never a via, and only the explicit goal may be one
                int nextCost = flood.Cost[current] + nextInfo.Cost + (nextInfo.IsWarp ? WarpEndPenalty : 0);
                if (flood.Cost.TryGetValue(next, out int known) && known <= nextCost)
                    continue;
                flood.Cost[next] = nextCost;
                flood.CameFrom[next] = current;
                open.Enqueue(next, nextCost);
            }
        }

        if (flood.BudgetExhausted)
            Log.Debug($"FootPathfinder: flood budget ({budget}) exhausted in {location.NameOrUniqueName}; distant targets may read as unreachable.");
        return flood;
    }

    private static FootPlan BuildPlan(Flood flood, Point start, Point destination)
    {
        FootPlan plan = new() { Destination = destination, Path = new Stack<Point>() };
        List<Point> nodes = [];
        for (Point node = destination; node != start; node = flood.CameFrom[node])
            nodes.Add(node);
        nodes.Reverse();
        foreach (Point node in nodes)
        {
            TileInfo info = flood.Tiles[node];
            if (info.Clearable)
                plan.Obstacles.Add(new Obstacle(node, info.Kind, info.Name, info.ToolName));
        }
        for (int i = nodes.Count - 1; i >= 0; i--)
            plan.Path.Push(nodes[i]);
        return plan;
    }

    private static void FillUnreachable(FootPlan plan, Flood flood, Point goal)
    {
        if (flood.Tiles.TryGetValue(goal, out TileInfo? goalInfo) && goalInfo.Kind == ObstacleKind.Wall && goalInfo.Name.Length > 0)
            plan.BlockedBy = goalInfo.ToolName.Length > 0
                ? Translator.Instance.Translate("feature-object_tracker-obstacle-needs_better_tool", new { name = goalInfo.Name, tool = goalInfo.ToolName })
                : goalInfo.Name;

        int bestDistance = int.MaxValue;
        Point? best = null;
        foreach ((Point tile, int _) in flood.Cost)
        {
            int distance = Math.Abs(tile.X - goal.X) + Math.Abs(tile.Y - goal.Y);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = tile;
            }
        }
        plan.NearestReachable = best;
    }

    // --- Public entry points -------------------------------------------------------

    /// <summary>Plan to an exact tile (coordinates favorite, console command).</summary>
    internal static FootPlan PlanTo(GameLocation location, Point start, Point goal, bool allowWarpEnd)
    {
        Options options = CurrentOptions(allowWarpEnd);
        BridgeMap bridges = CollectBridges(location);
        if (start == goal)
            return new FootPlan { Destination = goal, Path = new Stack<Point>() };

        Flood flood = RunFlood(location, start, options, bridges, goal);
        if (flood.Cost.ContainsKey(goal))
            return BuildPlan(flood, start, goal);

        FootPlan plan = new() { Destination = goal };
        FillUnreachable(plan, flood, goal);
        return plan;
    }

    /// <summary>
    /// Plan to the best tile next to a tracked object: orthogonal neighbours first (the player
    /// can interact from there), diagonals as a fallback, cheapest route wins within a stage.
    /// Mirrors the old GetClosestTilePath choice order.
    /// </summary>
    internal static FootPlan PlanNextTo(GameLocation location, Point start, Point target)
    {
        Options options = CurrentOptions(allowWarpEnd: false);
        BridgeMap bridges = CollectBridges(location);
        Flood flood = RunFlood(location, start, options, bridges, goal: null);

        Point[][] stages =
        [
            [new(0, -1), new(1, 0), new(0, 1), new(-1, 0)],
            [new(-1, -1), new(1, -1), new(1, 1), new(-1, 1)],
        ];
        foreach (Point[] stage in stages)
        {
            Point? best = null;
            int bestCost = int.MaxValue;
            foreach (Point offset in stage)
            {
                Point candidate = new(target.X + offset.X, target.Y + offset.Y);
                if (flood.Cost.TryGetValue(candidate, out int cost) && cost < bestCost)
                {
                    best = candidate;
                    bestCost = cost;
                }
            }
            if (best is Point found)
                return BuildPlan(flood, start, found);
        }

        FootPlan plan = new() { Destination = target };
        FillUnreachable(plan, flood, target);
        return plan;
    }

    /// <summary>Human-readable obstacle summary, e.g. "Stone 2, Weeds 1".</summary>
    internal static string SummarizeObstacles(List<Obstacle> obstacles)
    {
        List<string> parts = [];
        foreach (var group in obstacles.GroupBy(o => o.Name))
            parts.Add($"{group.Key} {group.Count()}");
        return string.Join(", ", parts);
    }
}
