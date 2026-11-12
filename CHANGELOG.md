# Changelog

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
