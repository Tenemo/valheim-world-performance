# World Performance

Valheim slows down where you spend the most time: a big base full of buildings, tames, chests and crops. World Performance cuts the work the game does that you would never notice, so large bases run smoother, loading screens end sooner and the periodic freezes go away. Requires only BepInEx.

In testing at four large bases, the game ran 48 to 62% more frames a second with World Performance, and 55 to 94% more alongside the 47 other mods of our pack, with the slowest 1% of frames gaining as much. Preparing the next frame early comes on top of that wherever the processor holds the frame rate back.

## What it doesn't change

World Performance doesn't change how the game plays. Creatures, fights, crafting, cooking, smelting and growth timers, fuel, building support and damage work as in the vanilla game, and the area simulated around you stays the same size. Saves stay ordinary Valheim saves, so you can remove the mod at any time.

The only visible differences are small: crops and other small objects more than 60 m away cast no sun shadows, which don't show at that distance, signs more than 60 m away show no writing, which is too small to read there, and above 50 FPS the loose mist outside the Mistlands is as thin as at 50 FPS. The game also starts without the Coffee Stain and Iron Gate logos: the Valheim logo and its loading bar show at once.

## What it changes

- **Big bases draw faster.** Stable building pieces, including the Ashlands fortress walls, bird nests and beehives, trees, rocks, bushes and crops, planted ones included, draw from merged meshes; doors, fires, lights and anything moving draw as usual. Writing on signs more than 60 m away isn't drawn, which takes about 0.4 ms off each frame within sight of a building with dozens of signs. When the processor limits the frame rate, the game starts preparing the next frame while the last one is still being drawn, and steps back by itself when the graphics card, vsync or a frame limit sets the pace.
- **Your base stops doing work nobody sees.** Tames standing still rest instead of being simulated every physics step, for 16% less physics work at a base with 60 tames, and update every fourth step until they move or something happens to them, for up to 0.5 ms less in each step. Resting doors and gates stop animating until they move. Unseen effects, inaudible looping sounds, empty or unlit cooking slots and weather that has passed pause until they show again. Dropped items lying still rest their sparkles while out of view, about 1.1 ms of each frame at a base with around 500 loose items, and loose items another player's game looks after take no work in each physics step until something changes them. Health bars behind you are no longer shown and hidden again every frame. Loaded ships and carts stop rebuilding their cargo every second, which removes two 2 to 4 ms hitches a second near them.
- **Biomes with heavy scenery run lighter.** The hundreds of vine segments in the Ashlands ruins stop updating while nothing about them changes, about 0.9 ms of each frame there. The Mistlands mist emitter runs at most 10 times a second, cutting its cost from 0.4 to 0.16 ms of each frame; the mist there looks the same.
- **Building and inventory feel snappier.** Finding where the piece in your hands snaps took about 1.3 ms of each frame near many pieces and now takes a fraction of that. ComfortMe's This room panel, comfort chip and build menu badges redraw only when they change, about 0.5 ms of each frame with the hammer out, and its build menu plates are no longer hidden again every frame while none shows, about 0.6 ms of each frame. The build menu no longer slows down each time you open it, and at a large base it opens in about a quarter less time. With AzuCraftyBoxes, the open build menu and crafting panels count the materials in your chests again only when a chest in reach or its contents change: at a base with 32 chests in reach, each refresh of the open build menu takes about 9 ms instead of 15. With Treadwell installed, placing a piece takes about 5 ms less and taking out the hammer about 19 ms less, since Treadwell now looks for its road brushes only among pieces that can be one. The open inventory redraws only where it changes, including Epic Loot's rarity backgrounds, BetterUI's stars and durability colours and Quick Stack Store's favourite borders, and the armour value, requirement amounts, key hints and floating damage numbers stop being rebuilt every frame.
- **Fewer freezes.** Frames over 100 ms while travelling fall by about five sixths with our pack, and to none without other mods. Bases with many Better Beehives hives no longer freeze for a second or more every 20 minutes when the hives make honey. Memory cleanup no longer freezes the game for seconds on a busy computer, and runs less often: the game keeps an additional 256 MB, mods' config file watchers make about 1.3 MB less garbage a second, and Epic Loot's animation speed effects cause about a fifth fewer cleanup hitches at a large base. AzuClock's first appearance no longer freezes the game, and the hourly asset cleanup waits for a loading screen or the game menu, for up to one additional hour.
- **New places appear without a stutter.** A ruin's stones and other location scenery, dungeon interiors, grass after terrain changes and the distant terrain build over a few frames instead of in one long frame, and what you leave behind, such as a base when you set out, is removed over about a second. Terrain tiles build in about half the time. Chests full of items load their contents in a third of the time or less when you arrive near them, and so do the items you carry when you log in.
- **Loading screens end sooner.** The game starts about 4 seconds sooner. With Progression Fix 1.22.2 or newer, compatible updates keep the startup improvement without a matching World Performance release. Arriving at a large base takes about 3 seconds less, and 9 to 14 seconds less alongside a large mod pack, loading into a world about 2.5 seconds less, and joining a server again skips several seconds of map and world generation and about 1.5 seconds of reading Drop That's drop tables and waiting for the server. Portal trips end about a second sooner, and joining at your base or respawning at a bed about 2 seconds sooner, once the server confirms it has sent everything nearby, a nearby dungeon is built before the screen lifts, and finished terrain is kept so each zone is built once.
- **Smoother multiplayer.** Steam update windows are sized to each connection, including when other mods wrap a player's connection as they join. An object whose owner left or moved away goes to the player whose connection can carry it best.
- **Many smaller savings.** Maps pack in the background for automatic saves; navigation links stay stable during travel and new ones take neighbouring engine records, so creature navigation upkeep stays quick through long sessions; grounded and settled building support, ground heights, water updates and Drop That synchronization data are reused; terrain paint, plant checks, cooking models, floating objects, swimming checks and network packages do less repeated work; FastLink stops querying the server list while you play; FedoHud makes its beehive and crop icons only while they are turned on; BetterUI's enemy health numbers, NoSmokeStayLit's fires and cooking slots, AzuCraftyBoxes' chest loading and TameRidingOverhaul's tames skip repeated work; and unused labels, inventory references and container registrations are released.

## Smooth Motion on NVIDIA cards

On an RTX 40 or 50 series card, NVIDIA's Smooth Motion makes Valheim run much smoother, and it has worked great in our games. It adds a generated frame between each pair the game draws, so you see about twice the frame rate. This suits Valheim especially well: at a big base the processor, not the graphics card, limits the frame rate. Even an RTX 4090 runs only 50 to 68 FPS there and sits idle for much of each frame, so it has the time to make the extra frames without slowing the game down.

In the NVIDIA app, under Graphics > Program settings > Valheim, set:

- **Smooth Motion**: On
- **Vertical sync**: On
- **Max frame rate**: 3 below your monitor's refresh rate, such as 141 FPS at 144 Hz

Vsync and the cap keep the frames even and stop them from tearing.

## For server hosts

World Performance is optional on a dedicated server, but it helps there too:

- What players change or build, such as chest contents and signs, is included in the next world save, which a restart could otherwise lose.
- A world save pauses the server for about a tenth of the time, the save after a slept night for about half, and a player leaving no longer pauses it for about a fifth of a second.
- Newly explored land is built faster: about 3 ms less per terrain area and 30 to 80 ms less per crypt or Dvergr town.
- The hourly asset cleanup waits until nobody has been connected for a minute, for up to 12 hours.
- The server's upload is shared between its players.

### Server settings

Settings are in `BepInEx/config/tenemo.WorldPerformance.cfg`, created when the server first starts. Restart after changing them.

`[Dedicated server] Garbage fixes = true` adds allocation reductions for world updates, active-area checks, package values, collisions and supported Better Networking compression. The other fixes stay active with this option off.

`[Network] Server send rate` sets the KiB/s allowance per connection, from 64 to 4096, and `[Network] Upload budget` the KiB/s for all players together, from 256 to 16384. Each player receives the smaller of the two, so the budget divided by the number of players caps a player's rate as more join. Keep the budget within the server's upload capacity: Steam does not measure it, and a combined rate above it fills the router's queue and raises everyone's ping. Existing Progression Fix installations retain their previous per-connection value when this setting first moves here.

With Network Performance System installed, it finds the rate for each player itself and picks which player simulates each object, so the two network settings and the ownership order are left to it; each player's update window still follows the rate it sets. Mods that send their configuration or a saved character when a player joins are told what the connection holds as the game's own window would have left it, so a distant player is not disconnected while waiting for one.

`[Network] Connection log` writes `network-log/<date>_<time>/connections.csv`, `bosses.csv` and `players.csv` beside the server's saves. `connections.csv` has a row for each player every five seconds: the delay before what the server sends now reaches them, their ping, packet loss, actual and allowed rates, the bytes waiting for them, the zone they are in, the creatures and bosses they simulate, and the server's own frame times and object count. `out_kib_s` near `send_rate_kib_s` with rising `ping_ms` or falling `quality_local` means the budget is above the real upload.

Players who run World Performance 0.11.0 or later also send how smoothly their game ran in those five seconds, which fills the row's `client_` columns: frame count, average, median, 95th and 99th percentile and longest frame time, frames over 33 and 50 ms, physics steps a frame, the share of each frame spent waiting for the graphics card, vsync or a frame limit, which stays empty where the game can't measure it, and the frame limit and vsync in force. Frames behind a loading screen or the menu, or with the game in the background, are counted apart in `client_excluded_frames`. The server also records each of these players' machine and graphics settings in `players.csv`, when they join and again whenever they change: processor and its logical processor count, memory, graphics card, its memory and graphics API, resolution, window mode, refresh rate, vsync, frame limit, quality preset, each graphics setting and their World Performance version. Nothing that names the person or their computer is sent. Older versions and players without the mod leave these empty, and a server without it asks for nothing.

## Installation

Install **WorldPerformance** from [Hexium](https://valheim.hexium.gg) through Gale; r2modman cannot read Hexium. For a manual installation, install BepInEx and copy this package's `plugins` contents into `BepInEx/plugins/WorldPerformance`.

Install it on each player's client that should benefit. It can be used without installing it on the server. Servers that restrict client mods must allow the plugin ID `tenemo.WorldPerformance`.

If upgrading from a manually installed private copy, remove its old `Tenemo.WorldPerformance.dll`, `local-features-only` and `spatial-renderer` files before installing the package so the game loads only one copy with the full feature set.

## Compatibility

This is an early release. Results depend on the world, hardware and other mods. Tests use an isolated client and do not establish the frame-rate improvement on your own PC. Long sessions, frame pacing and multiplayer timing still need real play feedback.

Merged buildings and scenery use up to an eighth of the graphics card's memory, about 600 MB at a large base; beyond that the farther cells draw as usual. They need Direct3D 11, the game's default on Windows; with another graphics API the game draws everything itself. Preparing the next frame early needs it too.

All other mods are optional. Better Networking compression supports versions 1.2.0 and 1.3.1 with compression on and per-packet logging off. Faster chest and inventory loading supports Epic Loot 0.15.6 and ItemStacksItemWeights 1.1.1. Container, inventory and menu cleanup supports Quick Stack 1.4.16, AzuCraftyBoxes 1.8.28, MultiUserChest 0.6.2, BetterUI 2.5.13/2.5.14, ItemStacksItemWeights 1.1.1, FastLink 1.5.2, Drop That 3.1.7 and Epic Loot 0.15.4 to 0.15.6. The honey search supports Better Beehives 1.3.1, the build menu and room panel savings ComfortMe 1.3.1, and the icon saving FedoHud 1.1.2. The enemy health numbers support BetterUI 2.5.14, the fire and cooking savings NoSmokeStayLit 2.4.4, the chest loading and chest counts AzuCraftyBoxes 1.8.28, the tame savings TameRidingOverhaul 0.1.20 and the placement saving Treadwell 1.0.1. Swimming checks support WieldEquipmentWhileSwimming 1.1.4, and the font search for the clock's wind arrows supports AzuClock 1.1.1. Unsupported integrations report their reason and leave that mod's own behavior active.

Progression Fix 1.11.0 requires World Performance. With older Progression Fix versions, transferred features keep their existing owner so the same fix does not run twice. World Performance does not require Progression Fix or EpicLoot.

## Removal

Remove the mod through your mod manager, or remove its folder from `BepInEx/plugins` while the game is closed.

## Report a problem

Report bugs and ideas on [GitHub](https://github.com/Tenemo/valheim-world-performance/issues). Include the mod's version and your `BepInEx/LogOutput.log`.

## Credits

Built with AI assistance. Release notes are in `CHANGELOG.md`.
