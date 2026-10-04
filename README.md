# Kitsune Craft From Containers

Crafting uses ingredients from your own storage boxes nearby, as well as your
backpack and toolbelt. Built for **7 Days to Die 3.3**, which removed the
`ITileEntityLootable` interface that older craft-from-containers mods were
compiled against.

Install on the **server and every client**. EAC must be off.

## What it does

- The recipe list, ingredient counts, craft-count spinner, HUD recipe tracker
  and the craft button all count items in player-placed storage within range
  (15 blocks by default, `settings.txt`).
- When you craft, it takes from your backpack first, then your toolbelt, then
  the boxes.
- Only the crafting UI sees the boxes. Ammo counts, quests, traders, repairs
  and lockpicks still read your real inventory.
- Crafting windows with their own input grid (the forge) are unchanged.

## What it won't touch

- Boxes locked against you (secure storage you don't have the code for).
- Slots you locked inside a box.
- Boxes whose label contains `nocraft`.
- World loot containers. Only player-placed storage counts.
- Items with quality or with mods attached.

## Multiplayer

The crafter's client never writes a box itself. It asks the server to take the
items. The server removes them from the real box and forwards the removal to
anyone who has that box open, so their copy doesn't put the items back when
they close it. A client only uses boxes after the server confirms it runs the
mod. A client with the mod on a server without it just crafts from the
backpack.

## Building

```
dotnet build -c Release
```

The project builds against the 3.3 client install at
`G:\SteamLibrary\steamapps\common\7 Days To Die`. Override with
`-p:GameDir=...`. The DLL lands in `KitsuneCraftFromContainers\`, which is the
folder to drop into `Mods`.
