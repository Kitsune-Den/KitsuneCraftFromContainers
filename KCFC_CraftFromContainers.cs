using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using Platform;
using UnityEngine;

// KitsuneCraftFromContainers — crafting counts and uses ingredients from
// containers near you, not just your backpack and toolbelt. Built for
// 7 Days to Die 3.3, where ITileEntityLootable is gone and a box's contents
// live in TEFeatureStorage.ItemGrid.
//
// Scope. Only crafting, repairs, reloading, refueling and trader purchases
// see the containers. XUiM_PlayerInventory's count / has / remove methods are
// extended ONLY while one of the crafting entry points below, item repair,
// the ammo radial or a purchase is on the stack (KCFC_Scope), so quests,
// selling and lockpicks keep reading the real inventory. Crafting windows
// with their own input grid (the forge) are left alone: vanilla removes from
// that grid, never the backpack.
//
// Block repair and upgrade, vehicle and drone repair, reloading and
// refueling read the toolbelt (Inventory) and bag directly instead. While
// one of those is on the stack (KCFC_ToolScope), the local player's toolbelt
// counts the containers too (and the bag, where vanilla checks it on its
// own), and a toolbelt removal that comes up short takes the rest from the
// bag, then the containers.
//
// Which containers (KCFC_Source), all within `range` blocks (settings.txt):
//   - Player-placed storage. Skipped: boxes locked against you, slots you
//     locked inside a box, and boxes whose label contains "nocraft".
//   - World storage (a POI's cabinets, fridges, ...) inside a land claim you
//     own or are allied to, once someone has opened it. Never trash piles or
//     anything else that vanishes when closed.
//   - Workstation output slots (forge, campfire, workbench, ...). Never their
//     input, fuel or tool slots.
//   - Collector output slots (apiary, chicken coop, dew collector), skipping
//     the slot that is still filling.
//   - Vehicle and drone storage that isn't locked against you.
// With `party`, storage, vehicles and drones locked against you count when
// their owner is in your party. With `claimonly`, block containers count only
// inside a land claim you own or are allied to.
// Only plain stackable items are taken, never anything with quality or mods.
//
// Multiplayer. In 3.3 a client edits a container by sending its WHOLE
// contents to the server (NetPackageTileEntity, last writer wins). Storage
// and workstation windows ignore server updates while open and write their
// copy back on close, so writing our own copy would duplicate items whenever
// someone else had it open. Instead:
//
//   1. The crafter's client takes the items from its local copy WITHOUT
//      syncing it, and sends "kcfc_take <x> <y> <z> <item type> <count>" to
//      the server inside a NetPackageConsoleCmdServer.
//   2. The server checks the request, takes the items from the real
//      container, and forwards the same line to every player who has that
//      container open, inside a NetPackageConsoleCmdClient.
//   3. Those players' clients take the items from the copy they are looking
//      at, so what they write back on close already reflects the craft.
//      Collector windows follow the server's copy live, so they need no
//      forward.
//
// Vehicles and drones are entities, and a client's edit to their bag is never
// replicated to other clients, so a client can't count one from its own copy.
// Instead it asks: "kcfc_peek <seq>" while crafting, and the server answers
// "kcfc_bags <seq> <entity id>=<type>:<count>,... ..." for the ones in range.
// A take is "kcfc_take_entity <entity id> <item type> <count>", handled like
// the one above. Replies to peeks sent before our last take are dropped, so a
// stale reply can't put back items we just used. The server never sends a bag
// itself: that replaces the bag an open window is editing.
//
// The server decides what may be used: on "kcfc_hello" it answers with
// "kcfc_config range=.. workstations=.. collectors=.. claimed=.. vehicles=..
// repair=.. reload=.. refuel=.. trader=.. party=.. claimonly=.." and then
// "kcfc_ok". A client uses the smaller range, only the sources (and `party`)
// both sides allow, and `claimonly` if either side sets it, so a take the
// client makes is one the server will accept. A server that answers
// "kcfc_ok" without a config (0.1.0) gets player-placed storage only, at the
// client's own range. Repairs, reloads, refuels and purchases send the same
// take lines as crafting, so a server that doesn't mention them allows them.
//
// Like KitsuneSharedMagazines, all of this rides vanilla packages (no custom
// NetPackage IDs), and the kcfc_ lines are swallowed before the console sees
// them. A client with the mod on a server without it just crafts from the
// backpack as normal.

public class KitsuneCraftFromContainersInit : IModApi
{
    private static bool inited;

    public void InitMod(Mod _modInstance)
    {
        if (inited) return;
        inited = true;
        KCFC_Settings.Load(_modInstance.Path);
        Log.Out("[KitsuneCraftFromContainers] Applying patches (" + KCFC_Settings.Local.ToLine() + ")");
        new Harmony(GetType().ToString()).PatchAll(Assembly.GetExecutingAssembly());
    }
}

// What crafting may use. One copy comes from settings.txt; on a client a
// second one comes from the server, and the two are combined (Within).
public class KCFC_Config
{
    public int Range = 30;
    public bool Workstations = true;
    public bool Collectors = true;
    public bool Claimed = true;
    public bool Vehicles = true;
    // Not sources but uses: repairs and upgrades, reloading, refueling. They
    // take with the same lines as crafting, so every server version can
    // serve them.
    public bool Repair = true;
    public bool Reload = true;
    public bool Refuel = true;
    public bool Trader = true;
    // Locked storage, vehicles and drones also open to the owner's party.
    // A source-like permission: a server that doesn't know it refuses it.
    public bool Party = false;
    // A restriction, not a permission: block containers count only inside a
    // claim you own or are allied to. Either side asking for it is enough.
    public bool ClaimOnly = false;

    // What a server without a config line (0.1.0) supports.
    public static KCFC_Config Legacy(int range)
    {
        return new KCFC_Config { Range = range, Workstations = false, Collectors = false, Claimed = false, Vehicles = false,
            Repair = true, Reload = true, Refuel = true, Trader = true, Party = false, ClaimOnly = false };
    }

    public KCFC_Config Within(KCFC_Config other)
    {
        return new KCFC_Config
        {
            Range = Math.Min(Range, other.Range),
            Workstations = Workstations && other.Workstations,
            Collectors = Collectors && other.Collectors,
            Claimed = Claimed && other.Claimed,
            Vehicles = Vehicles && other.Vehicles,
            Repair = Repair && other.Repair,
            Reload = Reload && other.Reload,
            Refuel = Refuel && other.Refuel,
            Trader = Trader && other.Trader,
            Party = Party && other.Party,
            ClaimOnly = ClaimOnly || other.ClaimOnly,
        };
    }

    // Returns false if the key is not one of ours.
    public bool Set(string key, string value)
    {
        switch (key)
        {
            case "range":
                if (int.TryParse(value, out int range)) Range = Mathf.Clamp(range, 1, 64);
                return true;
            case "workstations":
                Workstations = ParseBool(value, Workstations);
                return true;
            case "collectors":
                Collectors = ParseBool(value, Collectors);
                return true;
            case "claimed":
                Claimed = ParseBool(value, Claimed);
                return true;
            case "vehicles":
                Vehicles = ParseBool(value, Vehicles);
                return true;
            case "repair":
                Repair = ParseBool(value, Repair);
                return true;
            case "reload":
                Reload = ParseBool(value, Reload);
                return true;
            case "refuel":
                Refuel = ParseBool(value, Refuel);
                return true;
            case "trader":
                Trader = ParseBool(value, Trader);
                return true;
            case "party":
                Party = ParseBool(value, Party);
                return true;
            case "claimonly":
                ClaimOnly = ParseBool(value, ClaimOnly);
                return true;
        }
        return false;
    }

    public string ToLine()
    {
        return "range=" + Range + " workstations=" + (Workstations ? 1 : 0) + " collectors=" + (Collectors ? 1 : 0)
            + " claimed=" + (Claimed ? 1 : 0) + " vehicles=" + (Vehicles ? 1 : 0) + " repair=" + (Repair ? 1 : 0)
            + " reload=" + (Reload ? 1 : 0) + " refuel=" + (Refuel ? 1 : 0) + " trader=" + (Trader ? 1 : 0)
            + " party=" + (Party ? 1 : 0) + " claimonly=" + (ClaimOnly ? 1 : 0);
    }

    // Reads the "key=value key=value" part of a kcfc_config line. Unknown
    // keys are ignored, so a newer server can add some; a source the server
    // doesn't mention is one it doesn't know, so it starts off.
    public static KCFC_Config FromLine(string pairs)
    {
        KCFC_Config config = Legacy(new KCFC_Config().Range);
        foreach (string pair in pairs.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            if (eq > 0) config.Set(pair.Substring(0, eq).ToLowerInvariant(), pair.Substring(eq + 1));
        }
        return config;
    }

    private static bool ParseBool(string value, bool fallback)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "1":
            case "true":
            case "yes":
            case "on":
                return true;
            case "0":
            case "false":
            case "no":
            case "off":
                return false;
        }
        return fallback;
    }
}

public static class KCFC_Settings
{
    public static readonly KCFC_Config Local = new KCFC_Config();

    // The rules this side crafts by: its own settings on the server and in
    // single player, its own settings within the server's on a client.
    public static KCFC_Config Effective
    {
        get
        {
            ConnectionManager connections = SingletonMonoBehaviour<ConnectionManager>.Instance;
            if (connections == null || connections.IsServer) return Local;
            return Local.Within(KCFC_Net.ServerConfig ?? KCFC_Config.Legacy(Local.Range));
        }
    }

    public static void Load(string modPath)
    {
        try
        {
            string file = Path.Combine(modPath, "settings.txt");
            if (!File.Exists(file)) return;
            foreach (string raw in File.ReadAllLines(file))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                if (!Local.Set(key, line.Substring(eq + 1).Trim()))
                {
                    Log.Warning("[KitsuneCraftFromContainers] settings.txt: unknown setting '" + key + "'");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("[KitsuneCraftFromContainers] reading settings.txt failed, using defaults: " + ex.Message);
        }
    }
}
