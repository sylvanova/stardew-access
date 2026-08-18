using StardewValley;
using StardewValley.Pathfinding;
using StardewValley.Tools;
using Microsoft.Xna.Framework;
using stardew_access.Translation;

namespace stardew_access.Utils
{
    public static class MovementHelpers
    {
        private static readonly List<Vector2>[] Stages =
        [
            [ // directly adjacent
                new Vector2(0, -1), // top
                new Vector2(1, 0), // right
                new Vector2(0, 1), // bottom
                new Vector2(-1, 0), // left
            ],
            [ // diagonally adjacent
                new Vector2(-1, -1), // top left
                new Vector2(1, -1), // top right
                new Vector2(1, 1), // bottom right
                new Vector2(-1, 1), // bottom left
            ]
        ];

        internal static void CenterPlayer()
        {
            Game1.player.Position = Vector2.Divide(Game1.player.Position, Game1.tileSize) * Game1.tileSize;
        }

        internal static void FacePlayerToTargetTile(Vector2 targetTile)
        {
            var player = Game1.player;
            string faceDirection = GetDirectionTranslationKey(player.Tile, targetTile);
            switch (faceDirection)
            {
                case "direction-north":
                    player.faceDirection(0);
                    break;
                case "direction-east":
                    player.faceDirection(1);
                    break;
                case "direction-south":
                    player.faceDirection(2);
                    break;
                case "direction-west":
                    player.faceDirection(3);
                    break;
            }
        }

        internal static void FixCharacterMovement()
        {
            //ripped from the debug cm command
            Game1.player.isEating = false;
            Game1.player.CanMove = true;
            Game1.player.UsingTool = false;
            Game1.player.usingSlingshot = false;
            Game1.player.FarmerSprite.PauseForSingleAnimation = false;
            if (Game1.player.CurrentTool is FishingRod fishingRod)
                fishingRod.isFishing = false;
        }

        // --- Mounted pathfinding ---
        //
        // The mounted collision box (the horse's) is 96px wide: 1.5 tiles. Centering it on
        // a tile overhangs both horizontal neighbours, which rejects 2-tile corridors the
        // game itself rides through fine (PathFindController only steers the box CENTER
        // into each node tile, and Farmer.MovePositionImpl's corner assist strafes around
        // one-sided obstructions). So passability is tracked per lateral PLACEMENT: the box
        // center at 16, 32 or 48px within the tile. Sliding between valid placements inside
        // one tile is always safe (the swept area is the union of the end boxes), so the
        // search stays tile-level; but a vertical step needs a placement valid in BOTH tiles,
        // otherwise the plan can demand a sideways shift the engine has no room to perform.

        private static readonly int[] PhaseOffsets = [16, 32, 48];
        private const byte AnyPhase = 0b0111;
        // Open fence gate: crossable vertically only, via Horse.squeezeForGate (the squeeze
        // shrinks the box just for north/south facing, and triggers from Farmer.collideWith
        // during controller-driven movement too).
        private const byte GateBit = 0b1000;

        /// <summary>
        /// The mounted collision box size, reconstructed from the horse sprite so that a
        /// squeeze frame at plan time (Horse.GetBoundingBox shrinks mid-gate) can't produce
        /// an over-permissive probe.
        /// </summary>
        private static (int width, int height) GetMountedBoxSize()
        {
            var mount = Game1.player.mount;
            if (mount?.Sprite != null)
            {
                int spriteWidth = mount.forceOneTileWide.Value ? 16 : mount.Sprite.SpriteWidth;
                return (spriteWidth * 4 * 3 / 4, 32);
            }
            Rectangle box = Game1.player.GetBoundingBox();
            return (box.Width, box.Height);
        }

        private static bool IsOpenGate(GameLocation location, Point tile)
        {
            return location.objects.TryGetValue(new Vector2(tile.X, tile.Y), out var obj)
                && obj is StardewValley.Fence fence && fence.isGate.Value && fence.isPassable();
        }

        /// <summary>
        /// Which lateral placements of the mounted box are collision-free on this tile.
        /// Probes are side-effect-free: character is null (with ignoreCharacterRequirement)
        /// so planning can't trigger gate squeezes, animal pushes or TemporaryPassableTiles
        /// mutations; pathfinding=true skips NPCs, and animals are ignored entirely — they
        /// move, get pushed at ride time, or trigger a replan.
        /// </summary>
        private static byte GetPhaseMask(GameLocation location, Point tile, int boxWidth, int boxHeight,
            int mapWidth, int mapHeight, Dictionary<Point, byte> cache)
        {
            if (cache.TryGetValue(tile, out byte mask))
                return mask;

            if (tile.X < 0 || tile.Y < 0 || tile.X >= mapWidth || tile.Y >= mapHeight)
            {
                cache[tile] = 0;
                return 0;
            }

            mask = 0;
            // A warp mid-route would teleport the player; warps are only ever endpoints
            // (handled explicitly by the planners below).
            if (!DoorUtils.IsWarpAtTile((tile.X, tile.Y), location))
            {
                for (int i = 0; i < PhaseOffsets.Length; i++)
                {
                    Rectangle box = new(
                        tile.X * Game1.tileSize + PhaseOffsets[i] - boxWidth / 2,
                        tile.Y * Game1.tileSize + Game1.tileSize / 2 - boxHeight / 2,
                        boxWidth,
                        boxHeight
                    );
                    if (!location.isCollidingPosition(box, Game1.viewport, isFarmer: true, 0, glider: false,
                            character: null, pathfinding: true, projectile: false,
                            ignoreCharacterRequirement: true, skipCollisionEffects: true))
                    {
                        mask |= (byte)(1 << i);
                    }
                }
                if (IsOpenGate(location, tile))
                    mask |= GateBit;
            }

            cache[tile] = mask;
            return mask;
        }

        /// <summary>Whether the horse can step from one tile to an adjacent one.</summary>
        private static bool IsEdgePassable(byte fromMask, byte toMask, bool vertical)
        {
            if (toMask == 0 || fromMask == 0)
                return false;
            if (!vertical)
                // Horizontal motion sweeps the box along the row; both end placements being
                // clear covers the whole swept area. Gates never open sideways for a horse.
                return (fromMask & AnyPhase) != 0 && (toMask & AnyPhase) != 0;
            // Vertical: need a placement valid in both tiles, or a gate squeeze on either side.
            if ((fromMask & toMask & AnyPhase) != 0)
                return true;
            return ((fromMask | toMask) & GateBit) != 0;
        }

        private static readonly int[] DeltaX = [0, 1, 0, -1];
        private static readonly int[] DeltaY = [-1, 0, 1, 0];

        /// <summary>
        /// A* to an exact tile using the mounted collision box.
        /// <paramref name="allowWarpEnd"/> lets an explicitly requested warp tile (a map
        /// exit favorite) be the final node; the engine fires the warp on contact.
        /// </summary>
        internal static Stack<Point>? FindMountedPath(GameLocation location, Point start, Point end,
            bool allowWarpEnd = false, int limit = 12000)
        {
            if (start == end)
                return new Stack<Point>();

            (int boxWidth, int boxHeight) = GetMountedBoxSize();
            int mapWidth = location.map.Layers[0].LayerWidth;
            int mapHeight = location.map.Layers[0].LayerHeight;
            Dictionary<Point, byte> phaseCache = [];

            byte MaskAt(Point tile) => GetPhaseMask(location, tile, boxWidth, boxHeight, mapWidth, mapHeight, phaseCache);

            if (allowWarpEnd && end.X >= 0 && end.Y >= 0 && end.X < mapWidth && end.Y < mapHeight
                && DoorUtils.IsWarpAtTile((end.X, end.Y), location))
            {
                // Ride onto the exit; MovePositionImpl checks warps before collision.
                phaseCache[end] = AnyPhase;
            }

            if (MaskAt(end) == 0)
                return null;

            // The horse is standing on the start tile legally, but possibly at a lateral
            // position between our probe placements; never let the start tile plan as a wall.
            byte startMask = MaskAt(start);
            if ((startMask & AnyPhase) == 0)
                phaseCache[start] = (byte)(startMask | AnyPhase);

            PriorityQueue<Point, int> open = new();
            Dictionary<Point, Point> cameFrom = [];
            Dictionary<Point, int> cost = new() { [start] = 0 };
            open.Enqueue(start, Math.Abs(end.X - start.X) + Math.Abs(end.Y - start.Y));
            int visited = 0;

            while (open.Count > 0 && visited++ < limit)
            {
                Point current = open.Dequeue();
                if (current == end)
                {
                    Stack<Point> path = new();
                    for (Point node = end; node != start; node = cameFrom[node])
                        path.Push(node);
                    return path;
                }

                byte currentMask = MaskAt(current);
                for (int direction = 0; direction < 4; direction++)
                {
                    Point next = new(current.X + DeltaX[direction], current.Y + DeltaY[direction]);
                    if (!IsEdgePassable(currentMask, MaskAt(next), vertical: DeltaY[direction] != 0))
                        continue;

                    int nextCost = cost[current] + 1;
                    if (cost.TryGetValue(next, out int existingCost) && existingCost <= nextCost)
                        continue;

                    cost[next] = nextCost;
                    cameFrom[next] = current;
                    int distance = Math.Abs(end.X - next.X) + Math.Abs(end.Y - next.Y);
                    open.Enqueue(next, nextCost + distance);
                }
            }

            if (open.Count > 0)
                Log.Debug($"FindMountedPath: node budget ({limit}) exhausted before reaching {end}; treating as unreachable.");
            return null;
        }

        /// <summary>
        /// Find the nearest reachable place around a tracked target for a mounted player.
        /// One breadth-first flood from the player feeds every ring lookup, instead of a
        /// separate full search per candidate tile.
        /// </summary>
        internal static (Vector2? tile, Stack<Point>? path) GetClosestMountedTilePath(Vector2? tilePosition)
        {
            if (tilePosition == null)
                return (null, null);

            GameLocation location = Game1.currentLocation;
            Point start = Game1.player.TilePoint;
            Point target = tilePosition.Value.ToPoint();

            (int boxWidth, int boxHeight) = GetMountedBoxSize();
            int mapWidth = location.map.Layers[0].LayerWidth;
            int mapHeight = location.map.Layers[0].LayerHeight;
            Dictionary<Point, byte> phaseCache = [];

            byte MaskAt(Point tile) => GetPhaseMask(location, tile, boxWidth, boxHeight, mapWidth, mapHeight, phaseCache);

            byte startMask = MaskAt(start);
            if ((startMask & AnyPhase) == 0)
                phaseCache[start] = (byte)(startMask | AnyPhase);

            const int FloodLimit = 12000;
            Queue<Point> frontier = new();
            Dictionary<Point, Point> cameFrom = [];
            Dictionary<Point, int> distance = new() { [start] = 0 };
            frontier.Enqueue(start);
            int visited = 0;

            while (frontier.Count > 0 && visited++ < FloodLimit)
            {
                Point current = frontier.Dequeue();
                byte currentMask = MaskAt(current);
                for (int direction = 0; direction < 4; direction++)
                {
                    Point next = new(current.X + DeltaX[direction], current.Y + DeltaY[direction]);
                    if (distance.ContainsKey(next))
                        continue;
                    if (!IsEdgePassable(currentMask, MaskAt(next), vertical: DeltaY[direction] != 0))
                        continue;
                    distance[next] = distance[current] + 1;
                    cameFrom[next] = current;
                    frontier.Enqueue(next);
                }
            }
            if (frontier.Count > 0)
                Log.Debug($"GetClosestMountedTilePath: flood budget ({FloodLimit}) exhausted; distant targets may read as unreachable.");

            for (int radius = 1; radius <= 6; radius++)
            {
                Point? bestTile = null;
                int bestDistance = int.MaxValue;

                for (int offsetX = -radius; offsetX <= radius; offsetX++)
                {
                    for (int offsetY = -radius; offsetY <= radius; offsetY++)
                    {
                        if (Math.Abs(offsetX) != radius && Math.Abs(offsetY) != radius)
                            continue;

                        Point candidate = new(target.X + offsetX, target.Y + offsetY);
                        if (distance.TryGetValue(candidate, out int candidateDistance) && candidateDistance < bestDistance)
                        {
                            bestTile = candidate;
                            bestDistance = candidateDistance;
                        }
                    }
                }

                if (bestTile is Point found)
                {
                    Stack<Point> path = new();
                    for (Point node = found; node != start; node = cameFrom[node])
                        path.Push(node);
                    return (found.ToVector2(), path);
                }
            }

            return (null, null);
        }

        private static Vector2? GetClosestNavigableTile(List<Vector2> tiles, Vector2? tilePosition, Vector2 playerLocation)
        {
            if (tilePosition == null) return null;
            Vector2? closestTile = null;
            double? closestTileDistance = null;

            foreach (var tile in tiles)
            {
                Vector2 tileLocation = tilePosition.Value + tile;
                PathFindController controller = new(Game1.player, Game1.currentLocation, tileLocation.ToPoint(), -1); //***** , eraseOldPathController: true);

                if (controller.pathToEndPoint != null)
                {
                    int tileDistance = controller.pathToEndPoint.Count;
                    double distanceToObject = GetDistance(tileLocation, playerLocation);

                    if (closestTileDistance == null || tileDistance <= closestTileDistance && distanceToObject <= closestTileDistance)
                    {
                        closestTile = tileLocation;
                        closestTileDistance = tileDistance;
                    }
                }
            }

            return closestTile;
        }

        internal static Vector2? GetClosestTilePath(Vector2? tilePosition)
        {
            if (tilePosition == null) return null;

            Vector2 playerLocation = Game1.player.Tile;

            foreach (var stage in Stages)
            {
                Vector2? closestTile = GetClosestNavigableTile(stage, tilePosition, playerLocation);
                if (closestTile != null)
                {
                    return closestTile;
                }
            }

            return null;
        }
        
        internal static string GetDirection(Vector2 start, Vector2 end)
        {
            return Translator.Instance.Translate(GetDirectionTranslationKey(start, end));
        }

        internal static string GetDirectionTranslationKey(Vector2 start, Vector2 end)
        {
            double tan_Pi_div_8 = Math.Sqrt(2.0) - 1.0;
            double dx = end.X - start.X;
            double dy = start.Y - end.Y;

            if (Math.Abs(dx) > Math.Abs(dy)) {
                if (Math.Abs(dy / dx) <= tan_Pi_div_8) {
                    return dx > 0 ? "direction-east" : "direction-west";
                } else if (dx > 0) {
                    return dy > 0 ? "direction-north_east" : "direction-south_east";
                } else {
                    return dy > 0 ? "direction-north_west" : "direction-south_west";
                }
            } else if (Math.Abs(dy) > 0) {
                if (Math.Abs(dx / dy) <= tan_Pi_div_8) {
                    return dy > 0 ? "direction-north" : "direction-south";
                } else if (dy > 0) {
                    return dx > 0 ? "direction-north_east" : "direction-north_west";
                } else {
                    return dx > 0 ? "direction-south_east" : "direction-south_west";
                }
            } else {
                return "direction-current_tile";
            }
        }

        internal static double GetDistance(Vector2? player, Vector2? point)
        {
            if (player == null)
            {
                string message = point == null ? "Both player and point must not be null." : "Player must not be null.";
                throw new ArgumentNullException(nameof(player), message);
            }
            else if (point == null)
            {
                throw new ArgumentNullException(nameof(point), "Point must not be null.");
            }

            double value = Math.Sqrt(Math.Pow((point.Value.X - player.Value.X), 2) + Math.Pow((point.Value.Y - player.Value.Y), 2));
            return Math.Round(value);
        }

        // Add other methods related to player movement here
    }
}
