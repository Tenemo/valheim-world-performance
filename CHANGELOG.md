# Changelog

## 1.0.11

- Morkhalla's rooms load and unload across several frames, so arriving at or leaving one no longer stalls the game for a fifth of a second.

## 1.0.10

- The first automatic save after joining no longer pauses the game to pack your map.
- Locations loaded across several frames set themselves up exactly as the game's own copies do, also when you log out while one is loading.

## 1.0.9

- Returning to edited terrain reuses its unchanged ground and collision meshes.
- Large supported locations load across multiple frames, reducing the hitch when travelling past them.

## 1.0.8

- A teleport, portal trip or respawn ends once your destination's buildings and terrain have arrived and been created, also at a busy base where another player keeps fires burning, where it used to wait the full 8 seconds, and at a high frame rate it no longer ends while the area is still being created.
- An item taken off an item stand, or food off a cooking station, at a large base disappears at once instead of staying drawn, and a tree that Star Level System resizes is drawn at its new size.
- Water keeps its usual draw distance, the pieces of something that breaks keep their look until they're gone, and far torch flames keep their size.
- A resting animal that is pushed moves as far as it should, and one that has attacked rests again.
- Turning FedoHud's beehive or crop icons on in game gives the hives and crops already loaded their icons at once.
- Something that stopped being saved with the world no longer comes back after a server restart.
- A bounty ledger save that fails, as while another program holds the file, is retried until it is written, and stopping, logging out or quitting waits for it at most 15 seconds.
- A config file being written while Epic Loot or Equipment and Quick Slots checks it is no longer read half-written.

## 1.0.7

- An object removed by another mod, or together with something it was attached to, no longer stops the world around you from unloading for the rest of the session; before, everything you passed stayed loaded and frames grew up to three times longer.
- A short teleport beside such an object, as through a dungeon door, no longer keeps its loading screen up until you leave the world.
- With `[Network] Connection log` on, the server also records each boss altar offering in `altars.csv` and which player's game created each boss in `bosses.csv`.

## 1.0.6

- With Progression Fix, a Quick stack key or mouse button chosen in Settings, Keyboard & Mouse works again; since 1.0.4 only P did.

## 1.0.5

- Saving your character with Steam Cloud on, every 10 minutes and when you log out, no longer freezes the game for 0.1-0.5 s.
- Big fights cost less: a floating damage number shows in about a fifth of the time, and an enemy's weakness hit effect starts in about half.
- Finishing a material conversion at the enchanting table takes about a third less time in that frame.
- Opening the build menu at a large base makes about 3 MB less garbage, so later frames stutter less.
- Places full of rooted creatures, such as a Black Forest landing of tentaroots, take up to 0.4 ms less of each physics step.

## 1.0.4

- Travelling past locations and dungeons and into large bases holds far fewer long frames. A location or dungeon you leave is let go at the next loading screen or open menu instead of freezing a frame for 35-52 ms, keeping up to 64 of them, about 100 MB more memory. Objects you leave behind are removed over several frames, so the worst of those frames fell from up to 41 ms to 15 ms.
- Grass around you builds in about half the time while you move.
- Staff of Fracturing's splinter impacts and enemies' weakness hits cost far less, so the longest frames of a Fracturing barrage are less than half as long.
- Turning the camera quickly at a large base takes up to about 1.2 ms less of each frame.
- Many smaller savings in buildings, creatures, sounds, fires and other mods' per-frame work.

## 1.0.3

- Large bases take a few hundred fewer draw calls and up to 1.9 ms less render-thread time a frame: merged building and scenery meshes with nothing to draw at the moment are skipped.

## 1.0.2

- Long sessions no longer pile up memory: the material and mesh copies that rocks, creatures, coloured pieces, weapon swing trails and Star Level System's creature colours make now go with them, and water and areas you have left are let go, instead of all waiting for the hourly cleanup.
- With AzuCraftyBoxes and Epic Loot, the enchanting table opens in about 46 ms instead of 130 ms in a large base, and Convert Materials in about 25 ms instead of up to 300 ms.
- Creatures' pathfinding upkeep in a large base takes about a third less time.

## 1.0.1

- The mod page gives one short point per improvement, lists the long-session fixes, recommends TenemoPack and asks for problem reports in its Hexium threads.

## 1.0.0

- First stable release, with no changes in play since 0.12.0.

## 0.12.0

- Joining a server without crossplay by its address takes about 7 seconds less from the second join on: the game no longer searches PlayFab for a crossplay server it has already joined through Steam.
- With Drop That, a server no longer stops for up to 4 seconds when a player joins.
- Epic Loot's and Equipment and Quick Slots' config file checks and Epic Loot's bounty ledger saves no longer freeze the game or a server for seconds while the disk is busy.

## 0.11.3

- Faster startup stays available across compatible Progression Fix updates without another World Performance update.

## 0.11.2

- Keeps the faster game startup with Progression Fix 1.22.1.

## 0.11.1

- Keeps the faster game startup with Progression Fix 1.22.0.

## 0.11.0

- The game starts about 4 seconds sooner, and without the Coffee Stain and Iron Gate logos: the Valheim logo and its loading bar show at once.
- Joining a server you joined before skips about 3 seconds of world generation, and with Drop That installed a join takes about 1.5 seconds less.
- The loading screen of a join or a bed respawn ends once the server has sent everything nearby, about 2 seconds sooner at a large base.
- Chests full of items load their contents in a third of the time or less when you arrive near them, and so do the items you carry when you log in.
- With AzuCraftyBoxes, the open build menu and crafting panels count the materials in your chests again only when a chest changes: at a large base, each refresh of the open build menu takes about 9 ms instead of 15.
- Creatures' navigation upkeep no longer slows down over a long session, so busy bases have fewer long frames.
- Writing on signs more than 60 m away is no longer drawn, which takes about 0.4 ms off each frame within sight of a building with dozens of signs. It comes back as you approach.
- On a dedicated server, the pause at the save after a slept night is about half as long.
- On a dedicated server, the connection log now records how each player's game runs: their frame times every five seconds, and their machine and graphics settings when they join. Nothing that names the person or their computer is sent.

## 0.10.0

- Dropped items lying still rest their sparkle while it is out of view, about 1.1 ms less of each frame at a base with around 500 loose items in testing. Sparkles in view play as before.
- Planted bushes and ripe barley now draw as merged scenery, about 0.3 ms less of each frame over a large farm in testing. They look the same.
- Loose items that another player's game looks after, or nobody's, take no work in each physics step until something changes them, about 0.4 ms less in each step at a large base on a slower PC in testing.
- The build menu opens in about a quarter less time at a large base, and with Treadwell installed placing a piece takes about 5 ms less.
- Health bars behind you are no longer shown and hidden again every frame, which more than halves the health bar work near many tames.
- Empty cooking and oven slots and weather effects that aren't playing are skipped when the game works out what to draw, about 0.2 ms less of each frame at a large base in testing.
- On a dedicated server, the connection log's creature and boss columns now count what each player's game simulates; they were always 0.

## 0.9.2

- Bird nests and beehives now draw as merged buildings too, which cut 350 to 511 separate draws from a large base's views in testing. They look the same.
- Checking whether a settled building piece is still supported is quicker, about 0.04 ms of each frame at a large stone base in testing, with the same results.
- BetterUI's enemy health numbers, NoSmokeStayLit's fires and cooking slots, AzuCraftyBoxes' chest loading and TameRidingOverhaul's tames do half their earlier work at a large base, with the same results.
- The README explains how to turn on NVIDIA's Smooth Motion, which makes Valheim run much smoother on RTX 40 and 50 series cards.

## 0.9.1

- The Charred fortress in the Ashlands draws faster: its walls, wall tops, corners and spike walls now draw as merged buildings, for 1 to 1.7 ms less of each frame there in testing. They look the same.
- ComfortMe's build menu and FedoHud's beehive and crop icons get their speedups from World Performance, so they also work without Progression Fix.

## 0.9.0

- Building near many pieces runs faster: finding where the piece in your hands snaps took about 1.3 ms of each frame at a large base in testing and now takes a fraction of that.
- ComfortMe's This room panel and comfort chip redraw only when they change, saving about 0.5 ms of each frame at a large base with the hammer out in testing.
- Vines stop updating every frame while nothing about them changes: the hundreds of vine segments in the Ashlands ruins took about 0.9 ms of each frame there in testing.
- Above 50 FPS, the Mistlands mist emitter runs 10 times a second instead of every fifth frame, which cut its cost from 0.4 to 0.16 ms of each frame inside the Mistlands in testing. The mist there looks the same; outside the Mistlands the loose mist is as thin as at 50 FPS.
- Epic Loot's animation speed effects no longer make garbage for every character that isn't attacking or staggering, for about a fifth fewer garbage collection hitches at a large base in testing.

## 0.8.0

- Large bases with many tames run faster again: tames standing still now update every fourth physics step until they move or something happens to them, for up to 0.5 ms less work in each step at a base with about 60 tames in testing.
- Empty or unlit cooking stations, ovens and grills, and weather that has passed, no longer keep their effects and sizzles running unseen, saving about 0.4 ms of each frame at a large base in testing.
- Loaded ships and carts no longer rebuild their cargo every second when nothing in it changed, which removes two 2 to 4 ms hitches a second near them.

## 0.7.0

- Large bases with many tames run faster: creatures standing still now rest in the physics engine instead of being simulated every step. At a base with about 60 tames, each physics step took 16% less work in testing.
- Bases with many Better Beehives hives no longer freeze for a second or more every 20 minutes when the hives make honey.
- Clicking in the crafting and inventory panels no longer stutters: each click took 17–55 ms before and now takes well under 1 ms.
- Fewer freezes of several seconds while your PC is busy with other programs.

## 0.6.1

- A feature that can't start no longer stops the features after it from starting.
- A torch or fire disappearing near a base just as its merged buildings update no longer turns merged building rendering off until the next start, which cost frame rate.

## 0.6.0

- While the processor limits the frame rate, the game starts preparing the next frame as the last one is being drawn, for 32 to 55% more frames at large bases in testing. It steps back by itself when the graphics card, vsync or a frame limit sets the pace. It needs Direct3D 11; otherwise the game runs as before.
- Keeping creature pathfinding up to date while you travel takes about a third of the time, because it holds half as many links.

## 0.5.1

- Boss altars summon again after you or another player walk or sail up to them, instead of doing nothing until everyone there logs in again next to them.

## 0.5.0

- Crops and other small objects more than 60 m away cost less to draw: they no longer cast shadows, which don't show at that distance.
- The first join after starting the game no longer freezes for up to 2.5 seconds when AzuClock's wind arrows first appear: the search for a font with the arrows now happens behind the loading screen.

## 0.4.0

- Draws stable building pieces and natural objects such as trees, rocks, bushes and crops from merged meshes, for 14 to 32% more frames at a large base in testing. It uses up to an eighth of the graphics card's memory, about 600 MB at a large base, and needs Direct3D 11; otherwise the game draws them as before.
- Portal trips end about a second sooner, and a nearby dungeon finishes loading behind the loading screen instead of stuttering into view. The shorter trips need World Performance on the server too.
- Arriving at a large base takes about 3 seconds less and loading into a world about 2.5 seconds less, and joining a server again skips several seconds of map generation.
- Ruins and houses, dungeon interiors, grass after terrain changes, the base you leave behind and the distant terrain now appear or go away over a few frames instead of in one long frame.
- Terrain builds in about half the time.
- Torches more than 30 m away cost less to draw and look the same.
- Pauses for garbage collection come less often: mods' config file watchers make about 1.3 MB less garbage a second, and arriving at or leaving a large base copies less memory.
- The build menu no longer slows down each time you open it, and floating damage numbers, the armour value, requirement amounts and the build piece's name are no longer redrawn every frame.
- Swimming checks are cheaper with WieldEquipmentWhileSwimming 1.1.4.
- On a dedicated server, a world save pauses the server for about a tenth of the time and keeps what players changed since the last save, such as chest contents and signs, which a restart could lose. A player leaving no longer pauses the server for about a fifth of a second, the hourly asset cleanup waits until nobody has been connected for a minute, and newly explored land generates faster.
- Leaves out the research code and five shader bundles that never ran in a player's game.

## 0.3.3

- Supports EpicLoot 0.15.6.

## 0.3.2

- No longer logs an error at every shutdown when Better Networking and Network Performance System are also installed.

## 0.3.1

- Published on Hexium, without the water library that only ran in testing.

## 0.3.0

- Object-update windows now follow the connection on servers where other mods wrap a player's connection as they join, which previously left every window at the game's size.
- The dedicated server divides a configured upload budget among its players, so more players no longer multiply the rate it tries to send.
- A rate change now reaches players who are already connected.
- The dedicated server records each player's connection, the creatures they simulate and its own frame times in a network log beside the saves.
- An object whose owner left or moved away goes to the player whose connection can carry it best, instead of the first one in range.
- Where Network Performance System is installed, it sets the rate each player is sent at and picks which player simulates each object; update windows stay here and follow the rate it chooses.
- A player is no longer disconnected while another mod sends them its configuration or their saved character, which happened when more of their updates were waiting than the game's window would have left.

## 0.2.0

- Includes navigation, building support, terrain painting, loading and map-save optimizations previously supplied by Progression Fix.
- Reduces work for plants, cooking models and remote floating objects.
- Releases unused labels and inventory/container references, reuses Drop That synchronization data and stops FastLink menu queries while playing.
- Adapts Steam update windows to each connection and retains the server send-rate setting.
- Dedicated servers automatically skip unused rendering and character previews.
- Existing Progression Fix installations retain one owner for each transferred fix.

## 0.1.0

- First public release.
- Reduces work for unseen looping effects, inaudible sounds, resting doors and objects, water and display settings.
- Packs changed maps in the background for automatic saves and holds hourly asset cleanup for a natural pause.
- Spreads terrain drawing updates after new locations appear and reduces garbage collections and network allocations.
- Supports optional Better Networking 1.2.0 and 1.3.1 compression.
- Experimental building rendering remains off by default.
