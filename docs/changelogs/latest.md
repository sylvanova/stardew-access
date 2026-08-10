## Changelog

### New Features


### Feature Updates


### Bug Fixes

- Fixed Object Tracker auto-travel while mounted, including horse-width routing near entrances, preserving the mount across routes, and restoring the normal horse animation and native hoof sounds.
- The multiplayer "waiting for players" dialog (sleeping, festivals) can now be cancelled with escape; vanilla ignores all key presses there, leaving keyboard users stuck until every player was ready. The dialog also announces the cancel hint while waiting.
- Object Tracker auto-walk now announces "could not find path" when the game cannot compute a route (for example the target tile is blocked by the horse, an NPC or another player) instead of silently not walking; a stalled walk that runs out of retries now announces it stopped instead of silently freezing the feature.
- Fixed auto-walk re-triggering warps every frame during the warp fade in multiplayer, which delayed map crossings by several seconds.

### Tile Tracker Changes


### Guides And Docs


### Misc


### Translation Changes


### Development Chores


