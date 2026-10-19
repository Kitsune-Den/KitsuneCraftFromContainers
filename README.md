# Kitsune Craft From Containers

Crafting uses ingredients from containers near you, as well as your backpack
and toolbelt. Built for **7 Days to Die 3.3**, which removed the
`ITileEntityLootable` interface that older craft-from-containers mods were
compiled against.

Install on the **server and every client**. EAC must be off.

## What it does

- The recipe list, ingredient counts, craft-count spinner, HUD recipe tracker
  and the craft button all count items in containers within range (30 blocks
  by default, `settings.txt`).
- When you craft, it takes from your backpack first, then your toolbelt, then
  the containers.
- Repairs and upgrades use the same containers (`repair` in `settings.txt`):
  - repairing and upgrading blocks with a stone axe, hammer or nail gun
  - repairing tools, weapons and armor with repair kits
  - repairing vehicles and drones from their radial menu

  They take from your toolbelt first, then your backpack, then the
  containers. A block upgrade can now also use a few from each, where vanilla
  needs the whole amount in one place.
- Only crafting and repairs see the containers. Ammo counts, quests, traders
  and lockpicks still read your real inventory.
- Crafting windows with their own input grid (the forge) are unchanged.

## Which containers

Each of these can be switched off in `settings.txt`, except player-placed
storage:

| Source | What counts | Setting |
|---|---|---|
| Player-placed storage | Chests, crates, gun safes, and cabinets or fridges you placed | always on |
| World storage | A POI's cabinets, fridges, desks and so on, inside a land claim you own or are allied to, once someone has opened them | `claimed` |
| Workstations | Output slots of forges, campfires, workbenches, chem stations and cement mixers | `workstations` |
| Collectors | Output slots of apiaries, chicken coops and dew collectors | `collectors` |
| Vehicles and drones | Their storage, unless it is locked and you aren't the owner or on its allowed list | `vehicles` |

## What it won't touch

- Storage, vehicles and drones locked against you.
- Slots you locked inside a box.
- Storage whose label contains `nocraft`.
- World loot outside your claim, loot nobody has opened yet, and anything
  that vanishes when closed (trash piles, bags).
- Workstation input, fuel and tool slots, and collector mods and fuel.
- The container you have open right now.
- Items with quality or with mods attached.

## Multiplayer

The crafter's client never writes a container itself. It asks the server to
take the items. The server removes them from the real container and forwards
the removal to anyone who has it open, so their copy doesn't put the items
back when they close it.

The server's `settings.txt` decides. When a client joins, the server sends its
range and which sources it allows. The client uses the smaller range and only
the sources both sides turn on, so it never crafts from something the server
would refuse. `repair` works the same way, except a server older than 0.3.0
doesn't send it, and then repairs are on: they ask for items the same way
crafting does, so any version of the server can serve them.

Vehicles and drones work differently, because the game doesn't keep other
players' copies of a vehicle's storage up to date. While your crafting window
(or a tracked recipe) is counting and a vehicle or drone you can use is in
range, your client asks the server what's in it about once a second, and
counts that.

A client with the mod on a server without it just crafts from the backpack.
A 0.2.0 or later client on a 0.1.0 server uses player-placed storage only, at
its own range.

## Building

```
dotnet build -c Release
```

The project builds against the 3.3 client install at
`G:\SteamLibrary\steamapps\common\7 Days To Die`. Override with
`-p:GameDir=...`. The DLL lands in `KitsuneCraftFromContainers\`, which is the
folder to drop into `Mods`.

Without a game install (CI, or if you don't own the game) it builds against
`refs/` instead. Those are stripped reference assemblies of the 3.3 client:
the public API only, with every method body removed, so they hold no game
code and are never loaded by the game. Regenerate them with
`bash tools/strip-refs.sh` when the target game version changes. It needs
`dotnet tool install --global JetBrains.Refasmer.CliTool`.

CI (`.github/workflows/build.yml`) builds every push and pull request to
`main` against `refs/` and uploads the mod folder as an artifact. Release
DLLs are still built locally against the real game.
