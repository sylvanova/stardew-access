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
    private const int MaxFloodNodes = 40000;

    internal sealed class ProbeInfo
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

    /// <summary>
    /// Vanilla-style whole-tile probe: the same 62x62 box and the same arguments as
    /// PathFindController.findPath uses for the player. The real farmer is passed on purpose:
    /// isCollidingPosition derives isFarmer from the character (a null character probes as an
    /// NPC, with different Buildings-layer and event rules), and Farmer.collideWith only has
    /// side effects while riding, which this on-foot planner never is. pathfinding=true skips
    /// characters, skipCollisionEffects blocks the remaining reactions.
    /// </summary>
    internal static bool IsTileBlocked(GameLocation location, Point tile)
    {
        Rectangle box = new(tile.X * Game1.tileSize + 1, tile.Y * Game1.tileSize + 1, 62, 62);
        Farmer? probe = Game1.player.isRidingHorse() ? null : Game1.player;
        return location.isCollidingPosition(box, Game1.viewport, isFarmer: true, 0, glider: false,
            character: probe, pathfinding: true, projectile: false,
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

    private static void Set(ProbeInfo info, ObstacleKind kind, bool clearable, int cost, string name, string toolName)
    {
        info.Kind = kind;
        info.Clearable = clearable;
        info.Cost = cost;
        info.Name = name;
        info.ToolName = toolName;
    }

    /// <summary>Classify one tile: clear, a clearable obstacle (with tool), or a wall.</summary>
    internal static ProbeInfo Classify(GameLocation location, Point tile, Options options, int mapWidth, int mapHeight)
    {
        ProbeInfo info = new();
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

    private static void ClassifyClump(ProbeInfo info, ResourceClump clump, Options options, bool obstaclesAllowed)
    {
        int index = clump.parentSheetIndex.Value;
        // Same names the tile viewer speaks for these clumps (assets/TileData map).
        string name = stardew_access.Utils.TileInfo.GetResourceClumpName(index);

        if (clump is GiantCrop)
        {
            Heavy(info, ObstacleKind.GiantCrop, name, "(T)Axe", BestToolLevel<Axe>(), 0, options, obstaclesAllowed);
            return;
        }

        switch (index)
        {
            case ResourceClump.mineRock1Index:
            case ResourceClump.mineRock2Index:
            case ResourceClump.mineRock3Index:
            case ResourceClump.mineRock4Index:
                if (obstaclesAllowed)
                    Set(info, ObstacleKind.MineRock, true, CostMineRock, name, ToolName("(T)Pickaxe"));
                else
                    Set(info, ObstacleKind.Wall, false, 0, name, "");
                return;
            case ResourceClump.stumpIndex:
                Heavy(info, ObstacleKind.Stump, name, "(T)Axe", BestToolLevel<Axe>(), 1, options, obstaclesAllowed);
                return;
            case ResourceClump.hollowLogIndex:
                Heavy(info, ObstacleKind.HollowLog, name, "(T)Axe", BestToolLevel<Axe>(), 2, options, obstaclesAllowed);
                return;
            case ResourceClump.boulderIndex:
                Heavy(info, ObstacleKind.Boulder, name, "(T)Pickaxe", BestToolLevel<Pickaxe>(), 2, options, obstaclesAllowed);
                return;
            case ResourceClump.meteoriteIndex:
            case ResourceClump.quarryBoulderIndex:
                Heavy(info, ObstacleKind.Meteorite, name, "(T)Pickaxe", BestToolLevel<Pickaxe>(), 3, options, obstaclesAllowed);
                return;
            default:
                Set(info, ObstacleKind.Wall, false, 0, name, "");
                return;
        }
    }

    /// <summary>
    /// Heavy clumps are clearable only with the option on and a tool that can break them
    /// (levels from ResourceClump.performToolAction). When they stay a wall, the tool text
    /// explains why: missing tool, or tool too weak; nothing when the option is simply off.
    /// </summary>
    private static void Heavy(ProbeInfo info, ObstacleKind kind, string name, string toolId, int bestLevel, int requiredLevel,
        Options options, bool obstaclesAllowed)
    {
        bool optionOn = options.AllowTreesAndBoulders && obstaclesAllowed;
        string tool = ToolName(toolId);
        if (optionOn && bestLevel >= requiredLevel)
        {
            Set(info, kind, true, CostHeavy, name, tool);
            return;
        }
        string why = "";
        if (optionOn)
            why = bestLevel < 0
                ? Translator.Instance.Translate("feature-object_tracker-obstacle-needs_tool", new { tool })
                : Translator.Instance.Translate("feature-object_tracker-obstacle-needs_better_tool", new { tool });
        Set(info, ObstacleKind.Wall, false, 0, name, why);
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
    /// One step next to or across a suspension bridge, mirroring the game: the bridge attaches
    /// (<c>Farmer.bridge</c>) only when the box sits inside an entrance tile; from there the
    /// step onto the span ignores collision and from then on the game only keeps the box
    /// inside the bridge row, so a walker on the planks moves sideways until an entrance. A
    /// span tile that is open ground anyway (the Island North volcano path crosses the rope
    /// bridge's row) is ordinary ground when reached any other way, with the bridge detached.
    /// Entrance tiles themselves are ordinary ground: the row rule never applies to a box
    /// standing on them.
    /// </summary>
    /// <returns>Whether the step is legal, with <paramref name="onArrival"/> set when it lands on a plank with the bridge attached.</returns>
    private static bool BridgeStep(BridgeMap bridges, Node from, Point to, bool vertical, ProbeInfo toInfo, out bool onArrival)
    {
        onArrival = false;
        bool toSpan = bridges.Span.Contains(to);
        if (from.OnBridge)
        {
            if (vertical || !(toSpan || bridges.Entrances.Contains(to)))
                return false;
            onArrival = toSpan;
            return true;
        }
        if (toSpan && !vertical && bridges.Entrances.Contains(from.Tile))
        {
            onArrival = true;
            return true;
        }
        return toInfo.Walkable;
    }

    // --- Flood ----------------------------------------------------------------------

    /// <summary>A flood node: the tile, and whether the suspension bridge is attached there.</summary>
    private readonly record struct Node(Point Tile, bool OnBridge);

    private sealed class Flood
    {
        /// <summary>Cheapest cost per tile over both bridge states.</summary>
        public readonly Dictionary<Point, int> Cost = [];
        /// <summary>The node that achieved <see cref="Cost"/> for a tile.</summary>
        public readonly Dictionary<Point, Node> Best = [];
        public readonly Dictionary<Node, int> NodeCost = [];
        public readonly Dictionary<Node, Node> CameFrom = [];
        public readonly Dictionary<Point, ProbeInfo> Tiles = [];
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
        ProbeInfo InfoAt(Point tile)
        {
            if (!flood.Tiles.TryGetValue(tile, out ProbeInfo? info))
            {
                info = Classify(location, tile, options, mapWidth, mapHeight);
                flood.Tiles[tile] = info;
            }
            return info;
        }

        // The player is standing on the start tile legally even when it probes as blocked
        // (e.g. a wide box edge); never plan it as a wall.
        ProbeInfo startInfo = InfoAt(start);
        if (!startInfo.Walkable)
            Set(startInfo, ObstacleKind.Clear, false, CostClear, "", "");
        if (goal is Point requestedGoal && options.AllowWarpEnd && DoorUtils.IsWarpAtTile((requestedGoal.X, requestedGoal.Y), location))
        {
            // Vanilla warps can sit one tile off-map (Town -> BusStop at x = -1); accept them as
            // the endpoint regardless of bounds, like the mounted planner does.
            ProbeInfo goalInfo = new() { IsWarp = true };
            Set(goalInfo, ObstacleKind.Clear, false, CostClear, "", "");
            flood.Tiles[requestedGoal] = goalInfo;
        }

        // Standing on a plank mid-bridge, the game already has the bridge attached.
        Node startNode = new(start, bridges.Span.Contains(start) && Game1.player.onBridge.Value);
        PriorityQueue<Node, int> open = new();
        flood.Cost[start] = 0;
        flood.Best[start] = startNode;
        flood.NodeCost[startNode] = 0;
        open.Enqueue(startNode, 0);
        HashSet<Node> closed = [];
        int visited = 0;

        while (open.Count > 0)
        {
            Node current = open.Dequeue();
            if (!closed.Add(current))
                continue;
            if (visited++ >= budget)
            {
                flood.BudgetExhausted = true;
                break;
            }
            if (goal is Point g && current.Tile == g)
                break;
            if (InfoAt(current.Tile).IsWarp && current.Tile != start)
                continue;

            for (int direction = 0; direction < 4; direction++)
            {
                Point next = new(current.Tile.X + DeltaX[direction], current.Tile.Y + DeltaY[direction]);
                bool vertical = DeltaY[direction] != 0;
                ProbeInfo nextInfo = InfoAt(next);
                bool onArrival = false;
                if (bridges.Any)
                {
                    if (!BridgeStep(bridges, current, next, vertical, nextInfo, out onArrival))
                        continue;
                }
                else if (!nextInfo.Walkable)
                    continue;
                Node nextNode = new(next, onArrival);
                if (closed.Contains(nextNode))
                    continue;
                // Warps are never a via, and only an explicitly allowed goal may be one; a
                // ring tile next to an object must never be a map exit either.
                if (nextInfo.IsWarp && !(options.AllowWarpEnd && goal == next))
                    continue;
                // A plank crossed with the bridge attached costs a plain step whatever sits under it.
                int stepCost = nextInfo.Walkable ? nextInfo.Cost : CostClear;
                int nextCost = flood.NodeCost[current] + stepCost;
                if (flood.NodeCost.TryGetValue(nextNode, out int known) && known <= nextCost)
                    continue;
                flood.NodeCost[nextNode] = nextCost;
                flood.CameFrom[nextNode] = current;
                if (!flood.Cost.TryGetValue(next, out int tileKnown) || nextCost < tileKnown)
                {
                    flood.Cost[next] = nextCost;
                    flood.Best[next] = nextNode;
                }
                open.Enqueue(nextNode, nextCost);
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
        for (Node node = flood.Best[destination]; node.Tile != start; node = flood.CameFrom[node])
            nodes.Add(node.Tile);
        nodes.Reverse();
        foreach (Point node in nodes)
        {
            ProbeInfo info = flood.Tiles[node];
            if (info.Clearable)
                plan.Obstacles.Add(new Obstacle(node, info.Kind, info.Name, info.ToolName));
        }
        for (int i = nodes.Count - 1; i >= 0; i--)
            plan.Path.Push(nodes[i]);
        return plan;
    }

    /// <summary>
    /// Explain an unreachable goal. <paramref name="goalIsDestination"/>: the goal tile itself
    /// was requested (so what sits on it is the blocker); for a tracked object the goal is the
    /// object's own tile and naming it would be nonsense. The nearest reachable tile is one the
    /// player can actually stand on: no obstacle, no warp.
    /// </summary>
    private static void FillUnreachable(FootPlan plan, Flood flood, Point goal, bool goalIsDestination)
    {
        if (goalIsDestination && flood.Tiles.TryGetValue(goal, out ProbeInfo? goalInfo)
            && goalInfo.Kind == ObstacleKind.Wall && goalInfo.Name.Length > 0)
        {
            plan.BlockedBy = goalInfo.ToolName.Length > 0 ? $"{goalInfo.Name}, {goalInfo.ToolName}" : goalInfo.Name;
        }

        int bestDistance = int.MaxValue;
        Point? best = null;
        foreach ((Point tile, int _) in flood.Cost)
        {
            ProbeInfo info = flood.Tiles[tile];
            if (info.IsWarp || (info.Kind != ObstacleKind.Clear && info.Kind != ObstacleKind.Bridge))
                continue;
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
        FillUnreachable(plan, flood, goal, goalIsDestination: true);
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
        FillUnreachable(plan, flood, target, goalIsDestination: false);
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
