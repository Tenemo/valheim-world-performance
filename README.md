# World Performance

Valheim slows down where you spend the most time: a big base full of buildings, tames, chests and crops. World Performance cuts the work the game does that you would never notice, so large bases run smoother, loading screens end sooner, the periodic freezes go away and long sessions no longer slow down. Requires only BepInEx.

- **Much higher frame rates.** In testing at four large bases, the game ran 48 to 62% more frames a second with World Performance, and 55 to 94%(!) more alongside the 47 other mods of our pack, with the slowest 1% of frames gaining as much. Preparing the next frame early comes on top of that wherever the processor holds the frame rate back.
- **Much less stutter.** With our pack, the game hitched up to 97%(!) less often at large bases, and travelling had about a tenth as many long hitches. Without other mods, travelling had none.
- **Long sessions stay fast.** Without it, the game slows down the longer you play: the build menu takes longer to open each time, creatures' pathfinding gets heavier, and some mods keep every chest and inventory you've ever been near in memory. World Performance fixes all of these, so long sessions no longer slow the game down (I can't guarantee some other mods you add don't introduce their own issues, though!).

## What it doesn't change

World Performance doesn't change how the game plays. Creatures, fights, crafting, cooking, smelting and growth timers, fuel, building support and damage work as in the vanilla game, and the area simulated around you stays the same size. Saves stay ordinary Valheim saves, so you can remove the mod at any time.

The only visible differences are small: crops and other small objects more than 60 m away cast no sun shadows, which don't show at that distance, signs more than 60 m away show no writing, which is too small to read there, and above 50 FPS the loose mist outside the Mistlands is as thin as at 50 FPS. The game also starts without the Coffee Stain and Iron Gate logos: the Valheim logo and its loading bar show at once.

## What it changes

- **Big bases draw faster.** Buildings, trees, rocks, bushes and crops that don't move are drawn in large batches instead of piece by piece. Doors, fires, lights and anything moving draw as usual.
- **The next frame starts sooner.** When your processor holds the frame rate back, the game starts the next frame while the last one is still being drawn.
- **Far signs skip their writing.** Signs more than 60 m away, too small to read, show their writing again as you approach.
- **Tames rest when they stand still.** About 16% less physics work at a base with 60 tames.
- **Idle things stop working.** Doors that aren't moving, effects you can't see, sounds you can't hear, empty cooking slots, passed weather and health bars behind you pause until they matter again.
- **Loose items cost less.** Dropped items lying still stop sparkling while out of view, and items another player's game looks after cost nothing until they move.
- **Ships and carts stop hitching.** Loaded ships and carts no longer rebuild their cargo every second.
- **Ashlands and Mistlands run lighter.** Ashlands ruin vines stop updating while nothing touches them, and the Mistlands mist costs less than half as much and looks the same.
- **Building is snappier.** Snapping pieces into place costs a fraction of what it did, and the build menu opens about a quarter faster at a large base. With Treadwell, placing pieces and taking out the hammer are faster too.
- **ComfortMe and AzuCraftyBoxes are lighter.** ComfortMe's panels and badges redraw only when they change, and AzuCraftyBoxes recounts your chests only when one changes.
- **The inventory is snappier.** The open inventory redraws only what changed, including Epic Loot's, BetterUI's and Quick Stack Store's markings.
- **No more honey freezes.** Bases with many Better Beehives hives no longer freeze for a second or more every 20 minutes.
- **Fewer memory cleanup hitches.** Memory cleanup runs less often and no longer freezes the game for seconds on a busy computer.
- **Saves and cleanups don't interrupt play.** Automatic saves hitch for about 17 ms instead of 65, and the hourly asset cleanup waits for a loading screen or the menu.
- **No freezes from a busy disk.** Epic Loot's and Equipment and Quick Slots' file checks and saves no longer stall the game while the disk is busy.
- **No freeze when AzuClock first appears.**
- **New places appear without a stutter.** Ruins, dungeons, regrown grass and distant terrain build over a few frames instead of one long one, terrain builds in about half the time, and what you leave behind is cleared over about a second.
- **Chests load faster.** Chests full of items load in a third of the time or less when you arrive, and so does your inventory when you log in.
- **The game starts about 4 seconds sooner.**
- **Loading screens end sooner.** Arriving at a large base takes about 3 seconds less, or 9 to 14 with a large mod pack. Portal trips end about a second sooner, and joining at your base or respawning at a bed about 2 seconds sooner.
- **Rejoining a server is faster.** A server you've joined before loads several seconds faster, and about 7 seconds faster still if it has no crossplay.
- **Smoother multiplayer.** Each player's connection gets as much as it can carry, and objects whose owner left go to the player best able to look after them.
- **Long sessions stay fast.** The build menu no longer slows down with each opening, creatures' pathfinding no longer gets heavier over a session, and chests, inventories, icons and labels that Quick Stack, AzuCraftyBoxes, MultiUserChest, BetterUI and the game kept for the whole session are let go once they're gone.
- **Many smaller savings.** Building support, ground heights, water, terrain paint, plants, cooking, floating objects, swimming, network data, FastLink, FedoHud, BetterUI, NoSmokeStayLit and TameRidingOverhaul all do less repeated work.

## Recommended mod pack

World Performance was built and tested inside [TenemoPack](https://valheim.hexium.gg/mods/Tenemo/TenemoPack), the mod pack of our long-running private server, and that's where it helps most.

- **The figures above come from it.** "Our pack" means TenemoPack: 46 mods with their settings, World Performance included.
- **It speeds up the pack's other mods too.** Epic Loot, AzuCraftyBoxes, ComfortMe, BetterUI, Better Beehives, Treadwell, Quick Stack Store, Drop That, FedoHud and others each do less repeated work, checked against the exact versions the pack installs. With a different version of one of those mods, only that mod's saving turns off.
- **It's a whole game setup.** Hard combat against star-level creatures, EpicLoot loot that follows the biomes, building, crafting, storage and sailing helpers, and the settings that tie them together, all installed in one go through Gale.
- **Everyone on a server needs the same pack version.** Some of its mods refuse players on another version.

World Performance also works on its own: it needs only BepInEx and works with any other mods.

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

[TenemoPack](https://valheim.hexium.gg/mods/Tenemo/TenemoPack) installs it through Gale together with the mods and settings it was developed with.

If upgrading from a manually installed private copy, remove its old `Tenemo.WorldPerformance.dll`, `local-features-only` and `spatial-renderer` files before installing the package so the game loads only one copy with the full feature set.

## Compatibility

Results depend on the world, hardware and other mods. Tests use an isolated client and do not establish the frame-rate improvement on your own PC. Long sessions, frame pacing and multiplayer timing still need real play feedback.

Merged buildings and scenery use up to an eighth of the graphics card's memory, about 600 MB at a large base; beyond that the farther cells draw as usual. They need Direct3D 11, the game's default on Windows; with another graphics API the game draws everything itself. Preparing the next frame early needs it too.

All other mods are optional. Better Networking compression supports versions 1.2.0 and 1.3.1 with compression on and per-packet logging off. Faster chest and inventory loading supports Epic Loot 0.15.6 and ItemStacksItemWeights 1.1.1. Container, inventory and menu cleanup supports Quick Stack 1.4.16, AzuCraftyBoxes 1.8.28, MultiUserChest 0.6.2, BetterUI 2.5.13/2.5.14, ItemStacksItemWeights 1.1.1, FastLink 1.5.2, Drop That 3.1.7 and Epic Loot 0.15.4 to 0.15.6. The honey search supports Better Beehives 1.3.1, the build menu and room panel savings ComfortMe 1.3.1, and the icon saving FedoHud 1.1.2. The enemy health numbers support BetterUI 2.5.14, the fire and cooking savings NoSmokeStayLit 2.4.4, the chest loading and chest counts AzuCraftyBoxes 1.8.28, the tame savings TameRidingOverhaul 0.1.20 and the placement saving Treadwell 1.0.1. Swimming checks support WieldEquipmentWhileSwimming 1.1.4, and the font search for the clock's wind arrows supports AzuClock 1.1.1. The background config file checks support Epic Loot 0.15.6 and Equipment and Quick Slots 3.2.1, and the background bounty ledger saves Epic Loot 0.15.6. Unsupported integrations report their reason and leave that mod's own behavior active.

Progression Fix 1.11.0 requires World Performance. With older Progression Fix versions, transferred features keep their existing owner so the same fix does not run twice. World Performance does not require Progression Fix or EpicLoot.

## Removal

Remove the mod through your mod manager, or remove its folder from `BepInEx/plugins` while the game is closed.

## Report a problem

Report bugs and ideas on [GitHub](https://github.com/Tenemo/valheim-world-performance/issues), or message `tenemo` on Discord. Include the mod's version and your `BepInEx/LogOutput.log`.

## Credits

Built with AI assistance. Release notes are in `CHANGELOG.md`.
