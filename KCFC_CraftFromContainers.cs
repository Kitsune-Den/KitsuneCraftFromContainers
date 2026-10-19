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

// Depth counter, not a bool: the craft action's hasItems runs inside OnActivated.
public static class KCFC_Scope
{
    private static int depth;

    public static bool Active => depth > 0;

    public static bool Enter(XUiController windowController)
    {
        if (!KCFC_Containers.UsesPlayerInventory(windowController)) return false;
        depth++;
        return true;
    }

    public static bool EnterAlways()
    {
        depth++;
        return true;
    }

    public static void Exit(bool entered)
    {
        if (entered && depth > 0) depth--;
    }

    // For a step inside the scope that must see the real inventory.
    public static int Suspend()
    {
        int was = depth;
        depth = 0;
        return was;
    }

    public static void Resume(int was) => depth = was;
}

// The toolbelt-and-bag counterpart of KCFC_Scope, for the repairs, reloads
// and refuels that skip XUiM_PlayerInventory. Only ever the local player:
// remote players' run on their own machines.
public static class KCFC_ToolScope
{
    private static int depth;
    private static EntityPlayerLocal player;

    // The toolbelt's count includes the bag, and a short removal takes from
    // the bag before the containers. Off where vanilla adds the bag itself.
    public static bool WithBag { get; private set; }

    // A removal the bag and containers can't fully cover takes nothing more.
    public static bool AllOrNothing { get; private set; }

    // Repairs check the toolbelt and bag separately and need the whole amount.
    public static bool EnterRepair(Entity entity) => Enter(entity, KCFC_Settings.Effective.Repair, true, true);

    // Refuel checks the toolbelt, then the bag only if it got nothing; a
    // partial fill is fine.
    public static bool EnterRefuel(Entity entity) => Enter(entity, KCFC_Settings.Effective.Refuel, true, false);

    // Reload adds the bag and toolbelt itself; a partial reload is fine.
    public static bool EnterReload(Entity entity) => Enter(entity, KCFC_Settings.Effective.Reload, false, false);

    public static bool IsLocalPlayer(Entity entity)
    {
        return entity is EntityPlayerLocal local && local == GameManager.Instance?.World?.GetPrimaryPlayer();
    }

    // A nested scope keeps the outer one's rules.
    private static bool Enter(Entity entity, bool enabled, bool withBag, bool allOrNothing)
    {
        if (!enabled || !IsLocalPlayer(entity)) return false;
        EntityPlayerLocal local = (EntityPlayerLocal)entity;
        if (depth > 0 && local != player) return false;
        if (depth == 0)
        {
            player = local;
            WithBag = withBag;
            AllOrNothing = allOrNothing;
        }
        depth++;
        return true;
    }

    public static void Exit(bool entered)
    {
        if (!entered || depth <= 0) return;
        if (--depth == 0) player = null;
    }

    public struct Saved
    {
        public int Depth;
        public EntityPlayerLocal Player;
        public bool WithBag;
        public bool AllOrNothing;
    }

    // For a step inside a scope that must see the real toolbelt. Anything
    // that enters a scope meanwhile (a HUD refresh) starts from scratch.
    public static Saved Suspend()
    {
        Saved saved = new Saved { Depth = depth, Player = player, WithBag = WithBag, AllOrNothing = AllOrNothing };
        depth = 0;
        player = null;
        return saved;
    }

    public static void Resume(Saved saved)
    {
        depth = saved.Depth;
        player = saved.Player;
        WithBag = saved.WithBag;
        AllOrNothing = saved.AllOrNothing;
    }

    // The player whose toolbelt this is, if it is extended right now.
    public static EntityPlayerLocal Owner(Inventory toolbelt)
    {
        return depth > 0 && player != null && ReferenceEquals(player.inventory, toolbelt) ? player : null;
    }
}

// ---- Container sources ----

// One container crafting can draw from. Wraps the tile entity or entity so
// the rest of the mod doesn't care whether it is a box, a workstation, a
// collector or a vehicle.
public abstract class KCFC_Source
{
    // Where range is measured to.
    public abstract Vector3 Center { get; }

    // The request the crafter sends the server (and the server forwards to
    // viewers) for a take from this container.
    public abstract string TakeLine(int itemType, int count);

    // What LockManager records when someone has this container open.
    public abstract ILockTarget LockTarget { get; }

    // The local player has it open (their window is the authority on it).
    public abstract bool IsUserAccessing { get; }

    public abstract int Length { get; }

    // The stack in slot i, or null if crafting must leave that slot alone.
    public abstract ItemStack Slot(int i);

    // The type-specific checks: ownership, locks, labels, which sources the
    // rules allow. Distance is checked in Usable.
    protected abstract bool Allowed(PlatformUserIdentifierAbs user, KCFC_Config rules);

    // Takes up to `count` of a plain item. `sync` sends the container out to
    // everyone (the server's own copy); without it only this copy changes.
    public abstract int TakeFrom(int itemType, int count, bool sync);

    // Server: whether players with it open need the take forwarded (their
    // window would otherwise write the items back on close).
    public virtual bool ForwardsToViewers => true;

    // Client: someone else crafted from this container and the server
    // forwarded the take because we have it open.
    public abstract void TakeAsViewer(int itemType, int count);

    // The checks both sides agree on. The server re-runs them against its own
    // copy before it takes anything.
    public bool Usable(PlatformUserIdentifierAbs user, Vector3 from, float range, KCFC_Config rules)
    {
        if (!Allowed(user, rules)) return false;
        return (Center - from).sqrMagnitude <= range * range;
    }

    public static KCFC_Source For(TileEntity te)
    {
        if (te == null) return null;
        if (te is TileEntityWorkstation workstation) return new KCFC_WorkstationSource(workstation);
        if (te is TileEntityCollector collector) return new KCFC_CollectorSource(collector);
        if (te.TryGetSelfOrFeature<TEFeatureStorage>(out TEFeatureStorage storage)) return new KCFC_StorageSource(storage);
        return null;
    }

    public static KCFC_Source At(World world, Vector3i pos)
    {
        return For(world.GetTileEntity(pos));
    }

    // Takes from an array of stacks in place. `skip` marks slots to leave
    // alone; touched slots are added to `touched` when it is given.
    protected static int TakeFromArray(ItemStack[] stacks, int itemType, int count, Func<int, bool> skip, List<int> touched)
    {
        int removed = 0;
        for (int i = 0; i < stacks.Length && removed < count; i++)
        {
            if (skip != null && skip(i)) continue;
            ItemStack stack = stacks[i];
            if (!KCFC_Containers.Eligible(stack) || stack.itemValue.type != itemType) continue;
            int n = Math.Min(stack.count, count - removed);
            stack.count -= n;
            if (stack.count <= 0) stacks[i] = ItemStack.Empty;
            removed += n;
            touched?.Add(i);
        }
        return removed;
    }

    // Takes from a grid in place, skipping locked slots. ChangeCount empties a
    // slot through ItemStack.Clear(), which raises no change event, so with
    // `raiseEvents` we raise one per touched slot ourselves: that refreshes an
    // open window and sends a box out (SetModified). Without it the grid
    // raises nothing, so this copy changes quietly.
    protected static int TakeFromGrid(ItemStackGrid grid, int itemType, int count, bool raiseEvents)
    {
        int removed = 0;
        List<int> touched = raiseEvents ? new List<int>() : null;
        bool wasSuppressed = grid.suppressChanged;
        if (!raiseEvents) grid.suppressChanged = true;
        try
        {
            for (int i = 0; i < grid.Length && removed < count; i++)
            {
                if (grid.IsLocked(i)) continue;
                ItemStack stack = grid.GetItem(i);
                if (!KCFC_Containers.Eligible(stack) || stack.itemValue.type != itemType) continue;
                int changed = -grid.ChangeCount(i, -(count - removed));
                if (changed <= 0) continue;
                removed += changed;
                touched?.Add(i);
            }
        }
        finally
        {
            grid.suppressChanged = wasSuppressed;
        }
        if (touched != null)
        {
            for (int i = 0; i < touched.Count; i++) grid.OnSlotChanged(touched[i]);
        }
        return removed;
    }

    // Whether the user and the owner are in the same party right now (both
    // online). The server knows every party and a client knows its own, so
    // the crafter's client and the server answer alike.
    protected static bool InParty(PlatformUserIdentifierAbs user, PlatformUserIdentifierAbs owner)
    {
        if (user == null || owner == null) return false;
        World world = GameManager.Instance?.World;
        PersistentPlayerList players = GameManager.Instance?.persistentPlayers;
        PersistentPlayerData userData = players?.GetPlayerData(user);
        PersistentPlayerData ownerData = players?.GetPlayerData(owner);
        if (world == null || userData == null || ownerData == null || ownerData.EntityId == -1) return false;
        Party party = (world.GetEntity(userData.EntityId) as EntityPlayer)?.Party;
        return party != null && party.ContainsMember(ownerData.EntityId);
    }

    // World containers count only inside a claim the user owns or is
    // allied to.
    protected static bool InClaim(PlatformUserIdentifierAbs user, Vector3i pos)
    {
        if (user == null) return false;
        World world = GameManager.Instance?.World;
        PersistentPlayerData player = GameManager.Instance?.persistentPlayers?.GetPlayerData(user);
        if (world == null || player == null) return false;
        EnumLandClaimOwner owner = world.GetLandClaimOwner(pos, player);
        return owner == EnumLandClaimOwner.Self || owner == EnumLandClaimOwner.Ally;
    }
}

// A container that is a block, addressed by its position.
public abstract class KCFC_BlockSource : KCFC_Source
{
    public abstract Vector3i Pos { get; }

    public override Vector3 Center => Pos.ToVector3() + new Vector3(0.5f, 0.5f, 0.5f);

    public override string TakeLine(int itemType, int count)
    {
        Vector3i pos = Pos;
        return KCFC_Net.TakeVerb + " " + pos.x + " " + pos.y + " " + pos.z + " " + itemType + " " + count;
    }
}

public class KCFC_StorageSource : KCFC_BlockSource
{
    private const string OptOutLabel = "nocraft";

    private readonly TEFeatureStorage storage;

    public KCFC_StorageSource(TEFeatureStorage storage)
    {
        this.storage = storage;
    }

    public override Vector3i Pos => storage.ToWorldPos();
    public override ILockTarget LockTarget => storage;
    public override bool IsUserAccessing => storage.IsUserAccessing();
    public override int Length => storage.ItemGrid.Length;

    public override ItemStack Slot(int i)
    {
        ItemStackGrid grid = storage.ItemGrid;
        return grid.IsLocked(i) ? null : grid.GetItem(i);
    }

    protected override bool Allowed(PlatformUserIdentifierAbs user, KCFC_Config rules)
    {
        TileEntityComposite parent = storage.Parent;
        if (parent == null) return false;

        TEFeatureLockable lockable = storage.lockFeature;
        if (lockable != null && lockable.IsLocked() && (user == null || !lockable.IsUserAllowed(user))
            && !(rules.Party && InParty(user, lockable.GetOwner())))
        {
            return false;
        }

        string label = parent.GetFeature<TEFeatureSignable>()?.displayText;
        if (!string.IsNullOrEmpty(label) && label.IndexOf(OptOutLabel, StringComparison.OrdinalIgnoreCase) >= 0) return false;

        if (parent.PlayerPlaced) return !rules.ClaimOnly || InClaim(user, Pos);

        // World loot. Untouched containers haven't rolled their loot yet, and
        // ones that vanish on close (trash piles, bags) aren't storage.
        if (!rules.Claimed || !storage.ItemGrid.Touched) return false;
        LootContainer loot = string.IsNullOrEmpty(storage.lootListName) ? null : LootContainer.GetLootContainer(storage.lootListName);
        if (loot != null && loot.destroyOnClose != LootContainer.DestroyOnClose.False) return false;
        return InClaim(user, Pos);
    }

    // The grid's change event is what sends a box out, so syncing is raising it.
    public override int TakeFrom(int itemType, int count, bool sync)
    {
        return TakeFromGrid(storage.ItemGrid, itemType, count, raiseEvents: sync);
    }

    public override void TakeAsViewer(int itemType, int count)
    {
        if (storage.IsUserAccessing()) TakeFrom(itemType, count, sync: true);
    }
}

// Output slots only. While the workstation window is open its output grid
// holds the live stacks and writes them back on close, so whoever has it open
// takes from the grid instead of the tile entity.
public class KCFC_WorkstationSource : KCFC_BlockSource
{
    private readonly TileEntityWorkstation workstation;

    public KCFC_WorkstationSource(TileEntityWorkstation workstation)
    {
        this.workstation = workstation;
    }

    public override Vector3i Pos => workstation.ToWorldPos();
    public override ILockTarget LockTarget => workstation;
    public override bool IsUserAccessing => workstation.IsUserAccessing();
    public override int Length => workstation.Output.Length;

    public override ItemStack Slot(int i) => workstation.Output[i];

    protected override bool Allowed(PlatformUserIdentifierAbs user, KCFC_Config rules)
    {
        if (!rules.Workstations) return false;
        if (rules.ClaimOnly) return (workstation.IsPlayerPlaced || rules.Claimed) && InClaim(user, Pos);
        return workstation.IsPlayerPlaced || (rules.Claimed && InClaim(user, Pos));
    }

    public override int TakeFrom(int itemType, int count, bool sync)
    {
        int removed;
        XUiC_WorkstationOutputGrid grid = OpenGrid();
        if (grid != null)
        {
            ItemStack[] slots = grid.GetSlots();
            removed = TakeFromArray(slots, itemType, count, null, null);
            if (removed > 0)
            {
                grid.SetSlots(slots);
                // Writes the slots through to the tile entity.
                grid.UpdateData(slots);
            }
        }
        else
        {
            removed = TakeFromArray(workstation.Output, itemType, count, null, null);
        }
        if (sync && removed > 0) workstation.SetModified();
        return removed;
    }

    public override void TakeAsViewer(int itemType, int count)
    {
        // The window sends its slots on close; no need to send them now.
        if (OpenGrid() != null) TakeFrom(itemType, count, sync: false);
    }

    private XUiC_WorkstationOutputGrid OpenGrid()
    {
        XUiC_WorkstationOutputGrid grid = GameManager.Instance?.World?.GetPrimaryPlayer()?.PlayerUI?.xui?.CurrentWorkstationOutputGrid;
        return grid != null && grid.WorkstationData?.TileEntity == workstation ? grid : null;
    }
}

// Output slots only, minus the one still filling. The collector window
// listens to the tile entity and follows the server's copy while open, so
// no forward to viewers is needed.
public class KCFC_CollectorSource : KCFC_BlockSource
{
    private readonly TileEntityCollector collector;

    public KCFC_CollectorSource(TileEntityCollector collector)
    {
        this.collector = collector;
    }

    public override Vector3i Pos => collector.ToWorldPos();
    public override ILockTarget LockTarget => collector;
    public override bool IsUserAccessing => collector.IsUserAccessing();
    public override int Length => collector.Items.Length;
    public override bool ForwardsToViewers => false;

    public override ItemStack Slot(int i) => Skip(i) ? null : collector.Items[i];

    protected override bool Allowed(PlatformUserIdentifierAbs user, KCFC_Config rules)
    {
        return rules.Collectors && (!rules.ClaimOnly || InClaim(user, Pos));
    }

    public override int TakeFrom(int itemType, int count, bool sync)
    {
        int removed = TakeFromArray(collector.Items, itemType, count, Skip, null);
        if (removed > 0)
        {
            // Refreshes a window open on this machine (the host's).
            collector.NotifyListeners();
            if (sync) collector.SetModified();
        }
        return removed;
    }

    public override void TakeAsViewer(int itemType, int count)
    {
    }

    private bool Skip(int i) => collector.IsCurrentStack(i);
}

// A vehicle's or drone's bag. On the server (and in single player) this is
// the live bag. On a client it is what the server last reported (KCFC_Bags),
// one stack per item type, since our own copy of the bag goes stale.
public class KCFC_EntitySource : KCFC_Source
{
    private readonly Entity entity;
    private readonly ItemStack[] reported;

    private KCFC_EntitySource(Entity entity, ItemStack[] reported)
    {
        this.entity = entity;
        this.reported = reported;
    }

    public static bool HasStorage(Entity entity)
    {
        return (entity is EntityVehicle || entity is EntityDrone) && entity.bag?.ItemGrid != null;
    }

    // The bag as this machine holds it.
    public static KCFC_EntitySource Live(Entity entity)
    {
        return HasStorage(entity) ? new KCFC_EntitySource(entity, null) : null;
    }

    public static KCFC_EntitySource Reported(Entity entity, ItemStack[] reported)
    {
        return HasStorage(entity) && reported != null ? new KCFC_EntitySource(entity, reported) : null;
    }

    public int EntityId => entity.entityId;
    public override Vector3 Center => entity.position;
    public override ILockTarget LockTarget => entity;
    // Storage and the service window both lock channel 0.
    public override bool IsUserAccessing => LockManager.Instance.IsLockedByLocalPlayer(entity, 0);
    public override int Length => reported?.Length ?? Grid.Length;

    private ItemStackGrid Grid => entity.bag.ItemGrid;

    public override string TakeLine(int itemType, int count)
    {
        return KCFC_Net.TakeEntityVerb + " " + entity.entityId + " " + itemType + " " + count;
    }

    public override ItemStack Slot(int i)
    {
        if (reported != null) return reported[i];
        return Grid.IsLocked(i) ? null : Grid.GetItem(i);
    }

    // Vanilla's own storage check: anyone may open it unless it is locked and
    // they are neither the owner nor on its allowed list.
    protected override bool Allowed(PlatformUserIdentifierAbs user, KCFC_Config rules)
    {
        if (!rules.Vehicles) return false;
        if (entity is EntityVehicle vehicle)
        {
            // Not IsUserAllowed: it asks about the local player, which on a
            // dedicated server is nobody.
            return !vehicle.isLocked
                || (user != null && (vehicle.IsOwner(user) || vehicle.vehicle.AllowedUsers.Contains(user)))
                || (rules.Party && InParty(user, vehicle.vehicle.OwnerId));
        }
        if (entity is EntityDrone drone)
        {
            return !drone.IsLocked() || (user != null && drone.IsUserAllowed(user))
                || (rules.Party && InParty(user, drone.OwnerID));
        }
        return false;
    }

    // There is nothing to send: a client's bag edit goes to the server, and
    // the server's own edit must not go out (see the header). Raising the
    // grid's events refreshes a window open on this machine, and an open
    // window then sends its bag itself.
    public override int TakeFrom(int itemType, int count, bool sync)
    {
        if (reported != null) return TakeFromArray(reported, itemType, count, null, null);
        return TakeFromGrid(Grid, itemType, count, raiseEvents: sync);
    }

    public override void TakeAsViewer(int itemType, int count)
    {
        if (reported == null && IsUserAccessing) TakeFromGrid(Grid, itemType, count, raiseEvents: true);
    }
}

// Vehicle and drone contents as the server reports them to a client.
public static class KCFC_Bags
{
    public const string PeekVerb = "kcfc_peek";
    public const string BagsVerb = "kcfc_bags";

    private const float PeekSeconds = 1f;

    private static readonly Dictionary<int, ItemStack[]> reported = new Dictionary<int, ItemStack[]>();
    private static readonly List<Entity> found = new List<Entity>();
    private static World reportedWorld;
    private static string lastPayload;
    private static int peekSeq;
    // Replies to peeks up to this one were sent before our last take.
    private static int staleUpTo;
    private static float lastPeekAt = -100f;

    // Vehicles and drones near `from`, whatever their state.
    public static List<Entity> Around(World world, Vector3 from, float range)
    {
        found.Clear();
        Bounds bounds = new Bounds(from, new Vector3(range * 2f, range * 2f, range * 2f));
        world.GetEntitiesInBounds(typeof(EntityVehicle), bounds, found);
        world.GetEntitiesInBounds(typeof(EntityDrone), bounds, found);
        return found;
    }

    // Client: what the server last said is in this entity, or null.
    public static ItemStack[] Get(World world, int entityId)
    {
        if (world != reportedWorld) return null;
        return reported.TryGetValue(entityId, out ItemStack[] stacks) ? stacks : null;
    }

    // Client: ask again if the last answer is a second old. Called while the
    // crafting UI is counting and a usable vehicle or drone is in range.
    public static void Peek()
    {
        float now = Time.realtimeSinceStartup;
        if (now - lastPeekAt < PeekSeconds) return;
        lastPeekAt = now;
        peekSeq++;
        SingletonMonoBehaviour<ConnectionManager>.Instance.SendToServer(
            NetPackageManager.GetPackage<NetPackageConsoleCmdServer>().Setup(PeekVerb + " " + peekSeq));
    }

    // Client: nothing in range any more.
    public static void Forget()
    {
        reported.Clear();
        lastPayload = null;
    }

    // Client: we just took from a reported bag. Answers already on their way
    // describe the bag before the take, so drop them.
    public static void OnTake()
    {
        staleUpTo = peekSeq;
        lastPayload = null;
        lastPeekAt = -100f;
    }

    // Server: the reply to "kcfc_peek <seq>" from this player.
    public static string Describe(World world, ClientInfo sender, string seq)
    {
        string line = BagsVerb + " " + seq;
        KCFC_Config rules = KCFC_Settings.Local;
        EntityPlayer player = world.GetEntity(sender.entityId) as EntityPlayer;
        if (player == null || !rules.Vehicles) return line;

        Dictionary<int, int> totals = new Dictionary<int, int>();
        foreach (Entity entity in Around(world, player.position, rules.Range))
        {
            KCFC_EntitySource source = KCFC_EntitySource.Live(entity);
            if (source == null || !source.Usable(sender.InternalId, player.position, rules.Range, rules)) continue;
            totals.Clear();
            for (int i = 0; i < source.Length; i++)
            {
                ItemStack stack = source.Slot(i);
                if (!KCFC_Containers.Eligible(stack)) continue;
                totals.TryGetValue(stack.itemValue.type, out int sum);
                totals[stack.itemValue.type] = sum + stack.count;
            }
            if (totals.Count == 0) continue;
            line += " " + source.EntityId + "=";
            bool first = true;
            foreach (KeyValuePair<int, int> total in totals)
            {
                line += (first ? "" : ",") + total.Key + ":" + total.Value;
                first = false;
            }
        }
        return line;
    }

    // Client: a "kcfc_bags" reply. Returns true if what we know changed.
    public static bool OnReply(World world, string line)
    {
        string[] parts = line.Split(new[] { ' ' }, 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !int.TryParse(parts[1], out int seq) || seq <= staleUpTo) return false;
        string payload = parts.Length > 2 ? parts[2] : "";
        if (world == reportedWorld && payload == lastPayload) return false;

        reported.Clear();
        reportedWorld = world;
        lastPayload = payload;
        foreach (string entry in payload.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = entry.IndexOf('=');
            if (eq <= 0 || !int.TryParse(entry.Substring(0, eq), out int entityId)) continue;
            List<ItemStack> stacks = new List<ItemStack>();
            foreach (string item in entry.Substring(eq + 1).Split(','))
            {
                int colon = item.IndexOf(':');
                if (colon <= 0
                    || !int.TryParse(item.Substring(0, colon), out int type) || type <= 0
                    || !int.TryParse(item.Substring(colon + 1), out int count) || count <= 0)
                {
                    continue;
                }
                stacks.Add(new ItemStack(new ItemValue(type), count));
            }
            reported[entityId] = stacks.ToArray();
        }
        return true;
    }
}

public static class KCFC_Containers
{
    private const float CacheSeconds = 1f;

    private static readonly List<KCFC_Source> nearby = new List<KCFC_Source>();
    private static readonly List<KCFC_Source> none = new List<KCFC_Source>();
    // Totals per item type over `nearby`, filled as recipes ask for them and
    // dropped whenever the list is rebuilt or we take something.
    private static readonly Dictionary<int, int> counts = new Dictionary<int, int>();
    private static World cacheWorld;
    private static float cacheAt = -100f;

    // A crafting window feeds from the backpack unless it has an input grid
    // of its own (the forge). Mirrors vanilla's own GetChildByType checks.
    public static bool UsesPlayerInventory(XUiController windowController)
    {
        return windowController != null && windowController.GetChildByType<XUiC_WorkstationInputGrid>() == null;
    }

    public static bool Eligible(ItemStack stack)
    {
        if (stack == null || stack.IsEmpty() || stack.count <= 0) return false;
        ItemValue value = stack.itemValue;
        return value != null && !value.HasQuality && !(value.HasModSlots && value.HasMods());
    }

    // Containers in range of the local player. The list is cached for a
    // second; the contents are read live from each container every time.
    public static List<KCFC_Source> Nearby()
    {
        if (!KCFC_Net.Ready) return none;
        World world = GameManager.Instance?.World;
        EntityPlayerLocal player = world?.GetPrimaryPlayer();
        if (player == null) return none;

        float now = Time.realtimeSinceStartup;
        if (world == cacheWorld && now - cacheAt < CacheSeconds) return nearby;
        cacheWorld = world;
        cacheAt = now;
        nearby.Clear();
        counts.Clear();

        KCFC_Config rules = KCFC_Settings.Effective;
        int range = rules.Range;
        Vector3 from = player.position;
        Vector3i at = World.worldToBlockPos(from);
        PlatformUserIdentifierAbs user = PlatformManager.InternalLocalUserIdentifier;

        for (int cx = World.toChunkXZ(at.x - range); cx <= World.toChunkXZ(at.x + range); cx++)
        {
            for (int cz = World.toChunkXZ(at.z - range); cz <= World.toChunkXZ(at.z + range); cz++)
            {
                if (!(world.GetChunkSync(cx, cz) is Chunk chunk)) continue;
                List<TileEntity> entities = chunk.GetTileEntities()?.list;
                if (entities == null) continue;
                for (int i = 0; i < entities.Count; i++)
                {
                    KCFC_Source source = KCFC_Source.For(entities[i]);
                    // The container open in front of you is yours to manage by hand.
                    if (source == null || source.IsUserAccessing) continue;
                    if (source.Usable(user, from, range, rules)) nearby.Add(source);
                }
            }
        }
        if (rules.Vehicles) AddEntities(world, from, range, user, rules);
        return nearby;
    }

    // Forces the next Nearby() to rebuild, e.g. when the server reports new
    // vehicle contents.
    public static void Invalidate()
    {
        cacheAt = -100f;
        counts.Clear();
    }

    // Vehicles and drones. The server reads their live bags; a client uses
    // what the server last reported and keeps asking while any are in range.
    private static void AddEntities(World world, Vector3 from, int range, PlatformUserIdentifierAbs user, KCFC_Config rules)
    {
        bool isServer = SingletonMonoBehaviour<ConnectionManager>.Instance.IsServer;
        bool any = false;
        foreach (Entity entity in KCFC_Bags.Around(world, from, range))
        {
            KCFC_EntitySource live = KCFC_EntitySource.Live(entity);
            if (live == null || live.IsUserAccessing || !live.Usable(user, from, range, rules)) continue;
            if (isServer)
            {
                nearby.Add(live);
                continue;
            }
            any = true;
            KCFC_EntitySource reported = KCFC_EntitySource.Reported(entity, KCFC_Bags.Get(world, entity.entityId));
            if (reported != null) nearby.Add(reported);
        }
        if (isServer) return;
        if (any) KCFC_Bags.Peek();
        else KCFC_Bags.Forget();
    }

    public static int Count(int itemType)
    {
        List<KCFC_Source> sources = Nearby();
        if (counts.TryGetValue(itemType, out int total)) return total;
        for (int s = 0; s < sources.Count; s++)
        {
            KCFC_Source source = sources[s];
            for (int i = 0; i < source.Length; i++)
            {
                ItemStack stack = source.Slot(i);
                if (Eligible(stack) && stack.itemValue.type == itemType) total += stack.count;
            }
        }
        counts[itemType] = total;
        return total;
    }

    public static void AppendStacks(List<ItemStack> into)
    {
        if (into == null) return;
        List<KCFC_Source> sources = Nearby();
        for (int s = 0; s < sources.Count; s++)
        {
            KCFC_Source source = sources[s];
            for (int i = 0; i < source.Length; i++)
            {
                ItemStack stack = source.Slot(i);
                if (Eligible(stack)) into.Add(stack.Clone());
            }
        }
    }

    // The crafter's side of a removal. Returns how many were taken.
    public static int Take(ItemValue itemValue, int count)
    {
        bool isServer = SingletonMonoBehaviour<ConnectionManager>.Instance.IsServer;
        int taken = 0;
        List<KCFC_Source> sources = Nearby();
        for (int s = 0; s < sources.Count && taken < count; s++)
        {
            KCFC_Source source = sources[s];
            // A remote client must not sync its copy (see the header); the
            // server's own copy IS the real container, so the host syncs normally.
            int got = source.TakeFrom(itemValue.type, count - taken, sync: isServer);
            if (got <= 0) continue;
            taken += got;
            if (isServer)
            {
                KCFC_Net.ForwardToViewers(source, itemValue.type, got, GameManager.Instance.World.GetPrimaryPlayerId());
            }
            else
            {
                KCFC_Net.SendTake(source, itemValue.type, got);
                if (source is KCFC_EntitySource) KCFC_Bags.OnTake();
            }
        }
        if (taken > 0) counts.Clear();
        return taken;
    }
}

public static class KCFC_Net
{
    public const string HelloVerb = "kcfc_hello";
    public const string ConfigVerb = "kcfc_config";
    public const string OkVerb = "kcfc_ok";
    public const string TakeVerb = "kcfc_take";
    public const string TakeEntityVerb = "kcfc_take_entity";

    private const int MaxHellos = 3;
    private const float HelloRetrySeconds = 10f;
    // Slack over the server's own range for player movement between the
    // client's check and the request arriving.
    private const float ServerRangeSlack = 4f;
    private const int MaxTakePerRequest = 100000;

    private static World helloWorld;
    private static bool serverHasMod;
    private static KCFC_Config serverConfig;
    private static int hellosSent;
    private static float lastHelloAt = -100f;

    // What the server allows, or null if it hasn't said (yet, or ever: 0.1.0).
    public static KCFC_Config ServerConfig => serverConfig;

    // True once containers may be used: always on the server / single
    // player, and on a client once the server has said it runs the mod.
    public static bool Ready
    {
        get
        {
            ConnectionManager connections = SingletonMonoBehaviour<ConnectionManager>.Instance;
            World world = GameManager.Instance?.World;
            if (connections == null || world == null) return false;
            if (connections.IsServer) return true;

            if (world != helloWorld)
            {
                helloWorld = world;
                serverHasMod = false;
                serverConfig = null;
                hellosSent = 0;
                lastHelloAt = -100f;
            }
            if (serverHasMod) return true;

            float now = Time.realtimeSinceStartup;
            if (hellosSent < MaxHellos && now - lastHelloAt > HelloRetrySeconds && world.GetPrimaryPlayer() != null)
            {
                hellosSent++;
                lastHelloAt = now;
                connections.SendToServer(NetPackageManager.GetPackage<NetPackageConsoleCmdServer>().Setup(HelloVerb));
            }
            return false;
        }
    }

    public static void SendTake(KCFC_Source source, int itemType, int count)
    {
        SingletonMonoBehaviour<ConnectionManager>.Instance.SendToServer(
            NetPackageManager.GetPackage<NetPackageConsoleCmdServer>().Setup(source.TakeLine(itemType, count)));
    }

    private static void SendLine(ClientInfo client, string line)
    {
        client.SendPackage(NetPackageManager.GetPackage<NetPackageConsoleCmdClient>().Setup(line, true));
    }

    // Server: a client line. Returns false if it is not ours.
    public static bool OnServerLine(World world, ClientInfo sender, string cmd)
    {
        if (cmd == HelloVerb)
        {
            if (sender == null) return true;
            // Config first: packages to one client arrive in order, so the
            // client knows the rules before it starts using containers. A
            // 0.1.0 client ignores the config line.
            SendLine(sender, ConfigVerb + " " + KCFC_Settings.Local.ToLine());
            SendLine(sender, OkVerb);
            return true;
        }
        if (cmd.StartsWith(KCFC_Bags.PeekVerb + " ", StringComparison.Ordinal))
        {
            // The sequence number is echoed back, so only accept a number.
            if (sender != null && int.TryParse(cmd.Substring(KCFC_Bags.PeekVerb.Length + 1), out int seq))
            {
                SendLine(sender, KCFC_Bags.Describe(world, sender, seq.ToString()));
            }
            return true;
        }
        if (!IsTake(cmd)) return false;
        if (sender == null) return true;

        if (!ParseTake(world, cmd, out KCFC_Source source, out string where, out int itemType, out int count)
            || count > MaxTakePerRequest)
        {
            Log.Warning("[KitsuneCraftFromContainers] Ignored a malformed take from " + sender.playerName);
            return true;
        }

        KCFC_Config rules = KCFC_Settings.Local;
        EntityPlayer player = world.GetEntity(sender.entityId) as EntityPlayer;
        if (player == null || source == null
            || !source.Usable(sender.InternalId, player.position, rules.Range + ServerRangeSlack, rules))
        {
            Log.Warning("[KitsuneCraftFromContainers] Refused a take by " + sender.playerName + " from " + where
                + " (no usable container there for them)");
            return true;
        }

        int got = source.TakeFrom(itemType, count, sync: true);
        if (got < count)
        {
            // The client's copy was ahead of the container. Rare (two people
            // pulling the same items at once); the crafter keeps the difference.
            Log.Warning("[KitsuneCraftFromContainers] " + sender.playerName + " crafted with " + count + " of item "
                + itemType + " from " + where + " but only " + got + " were there");
        }
        if (got > 0) ForwardToViewers(source, itemType, got, sender.entityId);
        return true;
    }

    // Server: tell everyone looking into this container (except the crafter,
    // and except the host, whose window shows the real container) to drop the
    // items from the copy they will write back.
    public static void ForwardToViewers(KCFC_Source source, int itemType, int count, int crafterEntityId)
    {
        if (!source.ForwardsToViewers) return;
        ConnectionManager connections = SingletonMonoBehaviour<ConnectionManager>.Instance;
        int hostId = GameManager.Instance.World.GetPrimaryPlayerId();
        string line = null;
        foreach (int viewer in Viewers(source.LockTarget))
        {
            if (viewer == crafterEntityId || viewer == hostId) continue;
            ClientInfo client = connections?.Clients?.ForEntityId(viewer);
            if (client == null) continue;
            line = line ?? source.TakeLine(itemType, count);
            SendLine(client, line);
        }
    }

    private static List<int> Viewers(ILockTarget target)
    {
        List<int> ids = new List<int>();
        LockManager locks = LockManager.Instance;
        foreach (LockManager.LockEntry entry in locks.singleLocks.Values)
        {
            if (ReferenceEquals(entry.Target, target) && locks.singleLocks.TryGetByValue(entry, out int id) && !ids.Contains(id))
            {
                ids.Add(id);
            }
        }
        foreach (LockManager.LockEntry entry in locks.sharedLocks.Values)
        {
            if (!ReferenceEquals(entry.Target, target) || !locks.sharedLocks.TryGetByValue(entry, out IReadOnlyCollection<int> holders)) continue;
            foreach (int id in holders)
            {
                if (!ids.Contains(id)) ids.Add(id);
            }
        }
        return ids;
    }

    // Client: a server line. Returns false if it is not ours.
    public static bool OnClientLine(World world, string line)
    {
        if (line.StartsWith(ConfigVerb + " ", StringComparison.Ordinal))
        {
            serverConfig = KCFC_Config.FromLine(line.Substring(ConfigVerb.Length + 1));
            helloWorld = world;
            return true;
        }
        if (line == OkVerb)
        {
            serverHasMod = true;
            helloWorld = world;
            Log.Out("[KitsuneCraftFromContainers] Server runs the mod; crafting can use nearby containers ("
                + KCFC_Settings.Effective.ToLine() + ")");
            // Wake the crafting UI so an already-open recipe list recounts.
            XUiM_PlayerInventory inventory = world.GetPrimaryPlayer()?.PlayerUI?.xui?.PlayerInventory;
            inventory?.dispatchBackpackItemsChanged();
            return true;
        }
        if (line.StartsWith(KCFC_Bags.BagsVerb + " ", StringComparison.Ordinal))
        {
            if (KCFC_Bags.OnReply(world, line))
            {
                // New vehicle contents: recount, and wake an open recipe list.
                KCFC_Containers.Invalidate();
                world.GetPrimaryPlayer()?.PlayerUI?.xui?.PlayerInventory?.dispatchBackpackItemsChanged();
            }
            return true;
        }
        if (!IsTake(line)) return false;
        if (!ParseTake(world, line, out KCFC_Source source, out _, out int itemType, out int count)) return true;

        // Someone else crafted from the container we have open. Our window
        // ignores the server's copy until we close it, so apply the take here too.
        source?.TakeAsViewer(itemType, count);
        return true;
    }

    private static bool IsTake(string line)
    {
        return line.StartsWith(TakeVerb + " ", StringComparison.Ordinal)
            || line.StartsWith(TakeEntityVerb + " ", StringComparison.Ordinal);
    }

    // Reads either take line. `source` is the container it names as this
    // machine holds it, or null if nothing is there; `where` names it for
    // the log. Returns false if the line is malformed.
    private static bool ParseTake(World world, string line, out KCFC_Source source, out string where, out int itemType, out int count)
    {
        source = null;
        where = null;
        itemType = 0;
        count = 0;
        string[] parts = line.Split(' ');
        if (parts[0] == TakeEntityVerb)
        {
            if (parts.Length != 4 || !int.TryParse(parts[1], out int entityId)
                || !int.TryParse(parts[2], out itemType) || !int.TryParse(parts[3], out count))
            {
                return false;
            }
            source = KCFC_EntitySource.Live(world.GetEntity(entityId));
            where = "entity " + entityId;
        }
        else
        {
            if (parts.Length != 6
                || !int.TryParse(parts[1], out int x) || !int.TryParse(parts[2], out int y) || !int.TryParse(parts[3], out int z)
                || !int.TryParse(parts[4], out itemType) || !int.TryParse(parts[5], out count))
            {
                return false;
            }
            Vector3i pos = new Vector3i(x, y, z);
            source = KCFC_Source.At(world, pos);
            where = pos.ToString();
        }
        return itemType > 0 && count > 0;
    }
}

// ---- Network inboxes ----

// Runs before ConnectionManager.ServerConsoleCommand, so kcfc_ lines never
// reach the console, its permission check or its log.
[HarmonyPatch(typeof(NetPackageConsoleCmdServer))]
[HarmonyPatch("ProcessPackage")]
public class KCFC_ServerInboxPatch
{
    public static bool Prefix(NetPackageConsoleCmdServer __instance, World _world)
    {
        string cmd = __instance.cmd;
        if (cmd == null || !cmd.StartsWith("kcfc_", StringComparison.Ordinal)) return true;
        try
        {
            if (_world != null) KCFC_Net.OnServerLine(_world, __instance.Sender, cmd);
        }
        catch (Exception ex)
        {
            Log.Error("[KitsuneCraftFromContainers] handling '" + cmd + "' failed: " + ex);
        }
        return false;
    }
}

[HarmonyPatch(typeof(NetPackageConsoleCmdClient))]
[HarmonyPatch("ProcessPackage")]
public class KCFC_ClientInboxPatch
{
    public static bool Prefix(NetPackageConsoleCmdClient __instance, World _world)
    {
        List<string> lines = __instance.lines;
        if (!__instance.bExecute || lines == null || lines.Count == 0 || lines[0] == null
            || !lines[0].StartsWith("kcfc_", StringComparison.Ordinal))
        {
            return true;
        }
        try
        {
            if (_world != null) KCFC_Net.OnClientLine(_world, lines[0]);
        }
        catch (Exception ex)
        {
            Log.Error("[KitsuneCraftFromContainers] handling '" + lines[0] + "' failed: " + ex);
        }
        return false;
    }
}

// Say hello as soon as the local player exists, not on the first craft, so
// the recipe list already includes the containers the first time it opens.
[HarmonyPatch(typeof(GameManager))]
[HarmonyPatch("Update")]
public class KCFC_HelloPatch
{
    private static float nextCheckAt;

    public static void Postfix()
    {
        float now = Time.realtimeSinceStartup;
        if (now < nextCheckAt) return;
        nextCheckAt = now + 1f;
        if (GameManager.IsDedicatedServer) return;
        _ = KCFC_Net.Ready;
    }
}

// ---- Crafting scope ----

[HarmonyPatch(typeof(ItemActionEntryCraft))]
[HarmonyPatch("OnActivated")]
public class KCFC_CraftActivatedScope
{
    public static void Prefix(ItemActionEntryCraft __instance, out bool __state)
    {
        __state = KCFC_Scope.Enter(__instance.craftCountControl?.WindowGroup?.Controller);
    }

    public static void Finalizer(bool __state) => KCFC_Scope.Exit(__state);
}

[HarmonyPatch(typeof(ItemActionEntryCraft))]
[HarmonyPatch("hasItems")]
public class KCFC_CraftHasItemsScope
{
    public static void Prefix(ItemActionEntryCraft __instance, out bool __state)
    {
        __state = KCFC_Scope.Enter(__instance.craftCountControl?.WindowGroup?.Controller);
    }

    public static void Finalizer(bool __state) => KCFC_Scope.Exit(__state);
}

[HarmonyPatch(typeof(XUiC_RecipeCraftCount))]
[HarmonyPatch("calcMaxCraftable")]
public class KCFC_CraftCountScope
{
    public static void Prefix(XUiC_RecipeCraftCount __instance, out bool __state)
    {
        __state = KCFC_Scope.Enter(__instance.WindowGroup?.Controller);
    }

    public static void Finalizer(bool __state) => KCFC_Scope.Exit(__state);
}

[HarmonyPatch(typeof(XUiC_IngredientEntry))]
[HarmonyPatch("GetBindingValueInternal")]
public class KCFC_IngredientEntryScope
{
    public static void Prefix(XUiC_IngredientEntry __instance, out bool __state)
    {
        __state = KCFC_Scope.Enter(__instance.WindowGroup?.Controller);
    }

    public static void Finalizer(bool __state) => KCFC_Scope.Exit(__state);
}

// The HUD recipe tracker: display only, it never removes anything.
[HarmonyPatch(typeof(XUiC_RecipeTrackerIngredientEntry))]
[HarmonyPatch("Ingredient", MethodType.Setter)]
public class KCFC_TrackerScope
{
    public static void Prefix(out bool __state)
    {
        __state = KCFC_Scope.EnterAlways();
    }

    public static void Finalizer(bool __state) => KCFC_Scope.Exit(__state);
}

// The recipe list builds its own stack list (backpack + toolbelt) and passes
// it in, so it gets the container stacks added directly.
[HarmonyPatch(typeof(XUiC_RecipeList))]
[HarmonyPatch("BuildRecipeInfosList")]
public class KCFC_RecipeListPatch
{
    public static void Prefix(XUiC_RecipeList __instance, List<ItemStack> _items)
    {
        try
        {
            if (KCFC_Containers.UsesPlayerInventory(__instance.WindowGroup?.Controller))
            {
                KCFC_Containers.AppendStacks(_items);
            }
        }
        catch (Exception ex)
        {
            Log.Error("[KitsuneCraftFromContainers] adding containers to the recipe list failed: " + ex);
        }
    }
}

// ---- Player inventory, extended while in scope ----

[HarmonyPatch(typeof(XUiM_PlayerInventory))]
[HarmonyPatch("GetItemCount", new[] { typeof(ItemValue) })]
public class KCFC_CountByValuePatch
{
    public static void Postfix(ItemValue _itemValue, ref int __result)
    {
        if (KCFC_Scope.Active && _itemValue != null && !_itemValue.HasQuality)
        {
            __result += KCFC_Containers.Count(_itemValue.type);
        }
    }
}

[HarmonyPatch(typeof(XUiM_PlayerInventory))]
[HarmonyPatch("GetItemCount", new[] { typeof(int) })]
public class KCFC_CountByIdPatch
{
    public static void Postfix(int _itemId, ref int __result)
    {
        if (KCFC_Scope.Active && !new ItemValue(_itemId).HasQuality)
        {
            __result += KCFC_Containers.Count(_itemId);
        }
    }
}

[HarmonyPatch(typeof(XUiM_PlayerInventory))]
[HarmonyPatch("GetAllItemStacks")]
public class KCFC_AllStacksPatch
{
    public static void Postfix(List<ItemStack> __result)
    {
        if (KCFC_Scope.Active) KCFC_Containers.AppendStacks(__result);
    }
}

// Vanilla HasItems counts backpack + toolbelt directly; in scope it counts
// through GetItemCount, which then includes the containers.
[HarmonyPatch(typeof(XUiM_PlayerInventory))]
[HarmonyPatch("HasItems")]
public class KCFC_HasItemsPatch
{
    public static bool Prefix(XUiM_PlayerInventory __instance, IList<ItemStack> _itemStacks, int _multiplier, ref bool __result)
    {
        if (!KCFC_Scope.Active) return true;
        __result = true;
        for (int i = 0; i < _itemStacks.Count; i++)
        {
            if (_itemStacks[i].count * _multiplier - __instance.GetItemCount(_itemStacks[i].itemValue) > 0)
            {
                __result = false;
                break;
            }
        }
        return false;
    }
}

// Vanilla RemoveItems, with the containers as a third source after backpack
// and toolbelt. Kept faithful to vanilla, including checking HasItems at a
// multiplier of 1 before removing count * multiplier.
[HarmonyPatch(typeof(XUiM_PlayerInventory))]
[HarmonyPatch("RemoveItems")]
public class KCFC_RemoveItemsPatch
{
    public static bool Prefix(XUiM_PlayerInventory __instance, IList<ItemStack> _itemStacks, int _multiplier, IList<ItemStack> _removedItems)
    {
        if (!KCFC_Scope.Active) return true;
        if (!__instance.HasItems(_itemStacks)) return false;

        for (int i = 0; i < _itemStacks.Count; i++)
        {
            ItemValue itemValue = _itemStacks[i].itemValue;
            int num = _itemStacks[i].count * _multiplier;
            num -= __instance.backpack.DecItem(itemValue, num, _ignoreModdedItems: true, _removedItems);
            if (num > 0)
            {
                num -= __instance.toolbelt.DecItem(itemValue, num, _ignoreModdedItems: true, _removedItems);
            }
            if (num > 0 && !itemValue.HasQuality)
            {
                int got = KCFC_Containers.Take(itemValue, num);
                // Recorded like DecItem does, so a cancelled craft refunds them.
                if (got > 0) _removedItems?.Add(new ItemStack(itemValue.Clone(), got));
            }
        }
        __instance.dispatchBackpackItemsChanged();
        __instance.dispatchToolbeltItemsChanged();
        return false;
    }
}

// ---- Repairs and upgrades ----

// Repairing a tool, weapon or armor piece with a repair kit. It counts and
// removes the kits through XUiM_PlayerInventory, like a craft.
[HarmonyPatch(typeof(ItemActionEntryRepair))]
public class KCFC_ItemRepairScope
{
    public static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(ItemActionEntryRepair), "RefreshEnabled");
        yield return AccessTools.Method(typeof(ItemActionEntryRepair), "OnActivated");
    }

    public static void Prefix(out bool __state)
    {
        __state = KCFC_Settings.Effective.Repair && KCFC_Scope.EnterAlways();
    }

    public static void Finalizer(bool __state) => KCFC_Scope.Exit(__state);
}

// Block repair and upgrade (stone axe, hammer, nail gun). The first argument
// of each is the holding entity's inventory data.
[HarmonyPatch(typeof(ItemActionRepair))]
public class KCFC_BlockRepairScope
{
    public static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(ItemActionRepair), "canRemoveRequiredItem");
        yield return AccessTools.Method(typeof(ItemActionRepair), "removeRequiredItem");
        yield return AccessTools.Method(typeof(ItemActionRepair), "CanRemoveRequiredResource");
        yield return AccessTools.Method(typeof(ItemActionRepair), "RemoveRequiredResource");
    }

    public static void Prefix(object[] __args, out bool __state)
    {
        __state = KCFC_ToolScope.EnterRepair((__args[0] as ItemInventoryData)?.holdingEntity);
    }

    public static void Finalizer(bool __state) => KCFC_ToolScope.Exit(__state);
}

// The vehicle radial's repair, which uses one repair kit.
[HarmonyPatch(typeof(XUiM_Vehicle))]
[HarmonyPatch("RepairVehicle")]
public class KCFC_VehicleRepairScope
{
    public static void Prefix(XUi _xui, out bool __state)
    {
        __state = KCFC_ToolScope.EnterRepair(_xui?.playerUI?.entityPlayer);
    }

    public static void Finalizer(bool __state) => KCFC_ToolScope.Exit(__state);
}

// The drone radial's repair, which uses one repair kit.
[HarmonyPatch(typeof(EntityDrone))]
[HarmonyPatch("DoRepairAction")]
public class KCFC_DroneRepairScope
{
    public static void Prefix(LocalPlayerUI playerUI, out bool __state)
    {
        __state = KCFC_ToolScope.EnterRepair(playerUI?.entityPlayer);
    }

    public static void Finalizer(bool __state) => KCFC_ToolScope.Exit(__state);
}

// ---- Reloading ----

// Guns, bows and launchers (ItemActionRanged and its subclasses). Each of
// these reads the holder's toolbelt directly; the first argument is either
// the holder or the action data.
[HarmonyPatch]
public class KCFC_ReloadScope
{
    public static IEnumerable<MethodBase> TargetMethods()
    {
        Type ranged = typeof(ItemActionRanged);
        yield return AccessTools.Method(ranged, "CanReload");
        yield return AccessTools.Method(ranged, "CompleteReload");
        yield return AccessTools.Method(ranged, "GetReloadFlags",
            new[] { typeof(ItemValue), typeof(EntityAlive), typeof(ItemActionRanged.ItemActionDataRanged) });
        yield return AccessTools.Method(ranged, "CycleAmmoType");
        yield return AccessTools.Method(ranged, "SwapAmmoType");
        // How many projectiles a launcher shows loaded.
        yield return AccessTools.Method(typeof(AnimatorRangedReloadState), "GetAmmoCount");
    }

    public static void Prefix(MethodBase __originalMethod, object[] __args, out bool __state)
    {
        object first = __originalMethod.Name == "GetReloadFlags" ? __args[1] : __args[0];
        Entity holder = first is ItemActionData data ? data.invData?.holdingEntity : first as Entity;
        __state = KCFC_ToolScope.EnterReload(holder);
    }

    public static void Finalizer(bool __state) => KCFC_ToolScope.Exit(__state);
}

// Unloading on an ammo swap decides between bag and toolbelt by where that
// ammo already is; containers mustn't make the toolbelt look like the place.
[HarmonyPatch(typeof(ItemActionRanged))]
[HarmonyPatch("removeCurrentlyLoadedAmmunition")]
public class KCFC_UnloadPause
{
    public static void Prefix(out KCFC_ToolScope.Saved __state) => __state = KCFC_ToolScope.Suspend();

    public static void Finalizer(KCFC_ToolScope.Saved __state) => KCFC_ToolScope.Resume(__state);
}

// The reserve ammo count next to the magazine on the HUD. It can refresh in
// the middle of a repair (the toolbelt changed), so it sets the outer scope
// aside rather than counting by a repair's rules.
[HarmonyPatch(typeof(XUiC_HUDStatBar))]
[HarmonyPatch("updateActiveItemAmmo")]
public class KCFC_HudAmmoScope
{
    public static void Prefix(EntityPlayerLocal ___localPlayer, out KCFC_ToolScope.Saved __state)
    {
        __state = KCFC_ToolScope.Suspend();
        KCFC_ToolScope.EnterReload(___localPlayer);
    }

    public static void Finalizer(KCFC_ToolScope.Saved __state) => KCFC_ToolScope.Resume(__state);
}

// The ammo type radial (hold reload). It counts through XUiM_PlayerInventory.
[HarmonyPatch(typeof(ItemActionAttack))]
[HarmonyPatch("SetupRadial")]
public class KCFC_AmmoRadialScope
{
    public static void Prefix(out bool __state)
    {
        __state = KCFC_Settings.Effective.Reload && KCFC_Scope.EnterAlways();
    }

    public static void Finalizer(bool __state) => KCFC_Scope.Exit(__state);
}

// ---- Refueling ----

// Whether the vehicle radial offers "refuel". Vanilla looks for a fuel stack
// in the bag or toolbelt slot by slot, so this one adds the containers here.
[HarmonyPatch(typeof(EntityVehicle))]
[HarmonyPatch("hasGasCan")]
public class KCFC_HasFuelPatch
{
    public static void Postfix(EntityVehicle __instance, EntityAlive _ea, ref bool __result)
    {
        if (__result || !KCFC_Settings.Effective.Refuel || !KCFC_ToolScope.IsLocalPlayer(_ea)) return;
        string fuelItem = __instance.GetVehicle()?.GetFuelItem();
        if (string.IsNullOrEmpty(fuelItem)) return;
        ItemValue fuel = ItemClass.GetItem(fuelItem);
        if (fuel != null && fuel.type > 0 && !fuel.HasQuality) __result = KCFC_Containers.Count(fuel.type) > 0;
    }
}

[HarmonyPatch(typeof(EntityVehicle))]
[HarmonyPatch("takeFuel")]
public class KCFC_RefuelScope
{
    public static void Prefix(EntityAlive _entityFocusing, out bool __state)
    {
        __state = KCFC_ToolScope.EnterRefuel(_entityFocusing);
    }

    public static void Finalizer(bool __state) => KCFC_ToolScope.Exit(__state);
}

// ---- Trader purchases ----

// Buying from a trader or someone else's vending machine. The buy button and
// the payment count and remove currency through XUiM_PlayerInventory. Your
// own vending machine costs nothing, so it is left alone.
[HarmonyPatch(typeof(ItemActionEntryPurchase))]
public class KCFC_PurchaseScope
{
    public static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(ItemActionEntryPurchase), "RefreshEnabled");
        yield return AccessTools.Method(typeof(ItemActionEntryPurchase), "OnActivated");
    }

    public static void Prefix(ItemActionEntryPurchase __instance, out bool __state)
    {
        __state = !__instance.isOwner && KCFC_Settings.Effective.Trader && KCFC_Scope.EnterAlways();
    }

    public static void Finalizer(bool __state) => KCFC_Scope.Exit(__state);
}

// Before a purchase, vanilla checks the bought items will fit once the
// payment is gone, counting slots the payment frees. Only currency paid from
// the backpack and toolbelt frees one there (they are paid from first), so in
// scope it checks against that much, and against the real inventory.
[HarmonyPatch(typeof(XUiM_PlayerInventory))]
[HarmonyPatch("CanSwapItems")]
public class KCFC_CanSwapPatch
{
    public static void Prefix(XUiM_PlayerInventory __instance, ref ItemStack _removedStack, out int __state)
    {
        __state = KCFC_Scope.Suspend();
        if (__state == 0 || _removedStack == null) return;
        int own = __instance.GetItemCount(_removedStack.itemValue);
        if (own < _removedStack.count) _removedStack = new ItemStack(_removedStack.itemValue, own);
    }

    public static void Finalizer(int __state) => KCFC_Scope.Resume(__state);
}

// ---- The toolbelt, extended in tool scope ----

// The toolbelt's count includes the containers, and the bag too where vanilla
// checks the toolbelt and bag separately (so a repair can use a few from each).
[HarmonyPatch(typeof(Inventory))]
[HarmonyPatch("GetItemCount", new[] { typeof(ItemValue), typeof(bool), typeof(int), typeof(int), typeof(bool) })]
public class KCFC_ToolbeltCountPatch
{
    public static void Postfix(Inventory __instance, ItemValue _itemValue, bool _ignoreModdedItems, ref int __result)
    {
        EntityPlayerLocal player = KCFC_ToolScope.Owner(__instance);
        if (player == null || _itemValue == null) return;
        if (KCFC_ToolScope.WithBag) __result += player.bag.GetItemCount(_itemValue, -1, -1, _ignoreModdedItems);
        if (!_itemValue.HasQuality) __result += KCFC_Containers.Count(_itemValue.type);
    }
}

// A toolbelt removal that comes up short takes the rest from the bag (where
// the scope counts it), then the containers, and reports the whole amount,
// so vanilla's "toolbelt removed it all" branch is the one that runs.
[HarmonyPatch(typeof(Inventory))]
[HarmonyPatch("DecItem")]
public class KCFC_ToolbeltDecItemPatch
{
    public static void Postfix(Inventory __instance, ItemValue _itemValue, int _count, bool _ignoreModdedItems,
        IList<ItemStack> _removedItems, ref int __result)
    {
        EntityPlayerLocal player = KCFC_ToolScope.Owner(__instance);
        int rest = _count - __result;
        if (player == null || _itemValue == null || rest <= 0) return;

        int inBag = KCFC_ToolScope.WithBag ? player.bag.GetItemCount(_itemValue, -1, -1, _ignoreModdedItems) : 0;
        int nearby = _itemValue.HasQuality ? 0 : KCFC_Containers.Count(_itemValue.type);
        // Not enough anywhere (the containers changed since the check): take
        // nothing more and let vanilla fall back to the bag on its own.
        if (KCFC_ToolScope.AllOrNothing && inBag + nearby < rest) return;

        if (inBag > 0) rest -= player.bag.DecItem(_itemValue, rest, _ignoreModdedItems, _removedItems);
        if (rest > 0 && nearby > 0)
        {
            int got = KCFC_Containers.Take(_itemValue, rest);
            if (got > 0) _removedItems?.Add(new ItemStack(_itemValue.Clone(), got));
            rest -= got;
        }
        __result = _count - rest;
    }
}
