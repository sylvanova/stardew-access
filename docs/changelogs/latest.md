## Changelog

### New Features


### Feature Updates


### Bug Fixes

- Fixed the end-of-day shipping details screen not reading the item breakdown or providing usable keyboard focus. Opening a category now reads its items and prices with focus on the Back button; Ctrl+Enter returns to the selected category on the shipping summary.
- Fixed Object Tracker auto-travel while mounted, including horse-width routing near entrances, preserving the mount across routes, and restoring the normal horse animation and native hoof sounds.
- Reworked mounted route planning to match how the horse actually moves: two-tile-wide passages (bushes, fences, lamp posts, bridges) are now routable instead of failing with "could not find path", open fence gates can be crossed, riding to saved coordinates on a map exit now works, planning no longer freezes the game for seconds when a target is unreachable, and a ride that gets stuck re-plans from the current position instead of giving up.
- The multiplayer "waiting for players" dialog (sleeping, festivals) can now be cancelled with escape; vanilla ignores all key presses there, leaving keyboard users stuck until every player was ready. The dialog also announces the cancel hint while waiting.
- Object Tracker auto-walk now announces "could not find path" when the game cannot compute a route (for example the target tile is blocked by the horse, an NPC or another player) instead of silently not walking; a stalled walk that runs out of retries now announces it stopped instead of silently freezing the feature.
- Fixed auto-walk re-triggering warps every frame during the warp fade in multiplayer, which delayed map crossings by several seconds.
- Made the quest log (journal) usable by keyboard: the cursor now snaps to the first quest when the list opens, opening a quest speaks its full description and objectives, and the detail page's back, collect-reward and cancel-quest buttons are reachable with arrow keys. Vanilla only snaps the cursor in controller mode, which left the menu nearly silent for keyboard users.
- The animal naming screen when buying an animal (for example at Marnie's) now announces the new name after pressing the random name button, matching the behavior of the horse/pet naming dialog. Previously the name changed silently unless the name text box was focused.

### Tile Tracker Changes


### Guides And Docs


### Misc


### Translation Changes


### Development Chores


- On-foot auto walk now routes through clearable obstacles instead of failing: stones, weeds, twigs, artifact spots, crates, mine rocks, closed gates and volcano lava (trees, stumps, logs and boulders optionally). It walks up to each one, names it and the tool it needs, and continues on its own once it is gone. "Could not find path" now says what blocks the way or how far the nearest reachable spot is from the target. New `otplan <x> <y>` console command prints a planned route. New object tracker settings: Path Through Obstacles, Auto Resume After Obstacle, Obstacles: Trees And Boulders, Obstacles In Mines, Max Obstacles On Path.
