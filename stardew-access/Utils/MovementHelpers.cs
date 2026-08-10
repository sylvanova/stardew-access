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

        /// <summary>
        /// Plays one step sound for the given tile: hoof sounds while riding
        /// (same terrain mapping as the game's Horse.OnMountFootstep), normal
        /// terrain footsteps otherwise.
        /// </summary>
        internal static void PlayStepSound(GameLocation location, Vector2 tile)
        {
            if (Game1.player.isRidingHorse())
            {
                string? stepType = location.doesTileHaveProperty((int)tile.X, (int)tile.Y, "Type", "Back");
                string sound = stepType switch
                {
                    "Stone" => "stoneStep",
                    "Wood" => "woodyStep",
                    _ => "thudStep",
                };
                location.localSound(sound, tile);
            }
            else
            {
                location.playTerrainSound(tile);
            }
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
            // Intentionally NOT dismounting here: the horse follows the rider during
            // pathfinding (Horse.SyncPositionToRider), so auto walk works while mounted.
        }

        /// <summary>
        /// A* pathfinding for the mounted player. The game's own pathfinding only checks
        /// single tiles, but the horse's bounding box spans the standing tile plus its
        /// east neighbor, so paths that are valid on foot stall the horse. This search
        /// requires that clearance for every step, producing routes the horse can
        /// actually follow.
        /// </summary>
        internal static Stack<Point>? FindHorsePath(GameLocation location, Point start, Point end, int limit = 10000)
        {
            if (start == end) return null;

            Dictionary<Point, bool> passableCache = [];
            bool IsTilePassable(int x, int y)
            {
                Point p = new(x, y);
                if (!passableCache.TryGetValue(p, out bool passable))
                {
                    // Warp tiles count as walls: auto walk must never drag the mounted
                    // player through a map exit (a surviving controller then teleports
                    // them; see ObjectTracker.OnPlayerWarped). The ride stops next to
                    // the exit and the player crosses it themselves by riding on.
                    passable = !DoorUtils.IsWarpAtTile((x, y), location)
                        && !location.isCollidingPosition(
                            new Rectangle(x * 64 + 1, y * 64 + 1, 62, 62),
                            Game1.viewport, isFarmer: true, 0, glider: false, Game1.player, pathfinding: true);
                    passableCache[p] = passable;
                }
                return passable;
            }
            // The horse's box covers the standing tile and the one east of it.
            bool HasHorseClearance(int x, int y) => IsTilePassable(x, y) && IsTilePassable(x + 1, y);

            if (!HasHorseClearance(end.X, end.Y)) return null;

            int width = location.map.Layers[0].LayerWidth;
            int height = location.map.Layers[0].LayerHeight;
            int[] dx = [0, 1, 0, -1];
            int[] dy = [-1, 0, 1, 0];

            // Costs are scaled by 10 so a small penalty for changing direction can be
            // added: straighter paths keep the horse's riding animation (and its real
            // hoof sounds) running instead of restarting at every zig-zag turn.
            const int stepCost = 10;
            const int turnPenalty = 2;

            PriorityQueue<Point, int> openList = new();
            Dictionary<Point, Point> cameFrom = [];
            Dictionary<Point, int> directionTo = [];
            Dictionary<Point, int> costSoFar = new() { [start] = 0 };
            openList.Enqueue(start, stepCost * (Math.Abs(end.X - start.X) + Math.Abs(end.Y - start.Y)));
            int visited = 0;

            while (openList.Count > 0 && visited++ < limit)
            {
                Point current = openList.Dequeue();
                if (current == end)
                {
                    Stack<Point> path = new();
                    for (Point p = end; p != start; p = cameFrom[p])
                        path.Push(p);
                    return path;
                }

                directionTo.TryGetValue(current, out int currentDirection);
                for (int i = 0; i < 4; i++)
                {
                    Point next = new(current.X + dx[i], current.Y + dy[i]);
                    if (next.X < 0 || next.Y < 0 || next.X >= width || next.Y >= height) continue;
                    // (The start tile itself is never re-entered thanks to the cost check.)
                    if (!HasHorseClearance(next.X, next.Y)) continue;

                    int newCost = costSoFar[current] + stepCost
                        + ((current != start && i != currentDirection) ? turnPenalty : 0);
                    if (costSoFar.TryGetValue(next, out int existing) && existing <= newCost) continue;
                    costSoFar[next] = newCost;
                    cameFrom[next] = current;
                    directionTo[next] = i;
                    openList.Enqueue(next, newCost + stepCost * (Math.Abs(end.X - next.X) + Math.Abs(end.Y - next.Y)));
                }
            }

            return null;
        }

        /// <summary>
        /// Mounted variant of <see cref="GetClosestTilePath"/>: picks the closest tile
        /// around the target that the horse can reach and stand on, and returns the
        /// horse-viable path to it.
        /// </summary>
        internal static (Vector2? tile, Stack<Point>? path) GetClosestHorseTilePath(Vector2? tilePosition)
        {
            if (tilePosition == null) return (null, null);

            GameLocation location = Game1.currentLocation;
            Point start = Game1.player.TilePoint;

            foreach (var stage in Stages)
            {
                Vector2? bestTile = null;
                Stack<Point>? bestPath = null;

                foreach (var offset in stage)
                {
                    Vector2 candidate = tilePosition.Value + offset;
                    Stack<Point>? path = FindHorsePath(location, start, candidate.ToPoint());
                    if (path != null && (bestPath == null || path.Count < bestPath.Count))
                    {
                        bestTile = candidate;
                        bestPath = path;
                    }
                }

                if (bestPath != null) return (bestTile, bestPath);
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