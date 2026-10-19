using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using Platform;
using UnityEngine;

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
