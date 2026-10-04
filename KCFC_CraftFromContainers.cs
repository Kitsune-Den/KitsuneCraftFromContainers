using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using Platform;
using UnityEngine;

// KitsuneCraftFromContainers — crafting counts and uses ingredients from your
// own storage boxes near you, not just your backpack and toolbelt. Built for
// 7 Days to Die 3.3, where ITileEntityLootable is gone and a box's contents
// live in TEFeatureStorage.ItemGrid.
//
// Scope. Only the crafting UI sees the boxes. XUiM_PlayerInventory's count /
// has / remove methods are extended ONLY while one of the crafting entry
// points below is on the stack (KCFC_Scope), so ammo counts, quests, traders,
// repairs and lockpicks keep reading the real inventory. Crafting windows
// with their own input grid (the forge) are left alone: vanilla removes from
// that grid, never the backpack.
//
// Which boxes. Player-placed storage within `range` blocks (settings.txt).
// Skipped: boxes locked against you, slots you locked inside a box, and boxes
// whose label contains "nocraft". Only plain stackable items are taken, never
// anything with quality or attached mods.
//
// Multiplayer. In 3.3 a client edits a box by sending its WHOLE contents to
// the server (NetPackageTileEntity, last writer wins), and a player with the
// box open ignores server updates to it until they close it, at which point
// their copy is written back. Writing our own copy would therefore duplicate
// items whenever someone else had the box open. Instead:
//
//   1. The crafter's client takes the items from its local copy WITHOUT
//      syncing it (ItemStackGrid.suppressChanged), and sends
//      "kcfc_take <x> <y> <z> <item type> <count>" to the server inside a
//      NetPackageConsoleCmdServer.
//   2. The server checks the request, takes the items from the real box, and
//      forwards the same line to every player who has that box open, inside
//      a NetPackageConsoleCmdClient.
//   3. Those players' clients take the items from the copy they are looking
//      at, so what they write back on close already reflects the craft.
//
// Like KitsuneSharedMagazines, all of this rides vanilla packages (no custom
// NetPackage IDs), and the kcfc_ lines are swallowed before the console sees
// them. The client only uses boxes after the server answers "kcfc_hello" with
// "kcfc_ok", so a client with the mod on a server without it just crafts from
// the backpack as normal.

public class KitsuneCraftFromContainersInit : IModApi
{
    private static bool inited;

    public void InitMod(Mod _modInstance)
    {
        if (inited) return;
        inited = true;
        KCFC_Settings.Load(_modInstance.Path);
        Log.Out("[KitsuneCraftFromContainers] Applying patches (range " + KCFC_Settings.Range + " blocks)");
        new Harmony(GetType().ToString()).PatchAll(Assembly.GetExecutingAssembly());
    }
}

public static class KCFC_Settings
{
    public static int Range = 15;

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
                string value = line.Substring(eq + 1).Trim();
                if (key == "range" && int.TryParse(value, out int range))
                {
                    Range = Mathf.Clamp(range, 1, 64);
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
}

public static class KCFC_Containers
{
    private const string OptOutLabel = "nocraft";
    private const float CacheSeconds = 1f;

    private static readonly List<TEFeatureStorage> nearby = new List<TEFeatureStorage>();
    private static readonly List<TEFeatureStorage> none = new List<TEFeatureStorage>();
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

    // The checks both sides agree on. The server re-runs them against its own
    // copy before it takes anything.
    public static bool Usable(TEFeatureStorage storage, PlatformUserIdentifierAbs user, Vector3 from, float range)
    {
        TileEntityComposite parent = storage?.Parent;
        if (parent == null || !parent.PlayerPlaced) return false;

        TEFeatureLockable lockable = storage.lockFeature;
        if (lockable != null && lockable.IsLocked() && (user == null || !lockable.IsUserAllowed(user))) return false;

        string label = parent.GetFeature<TEFeatureSignable>()?.displayText;
        if (!string.IsNullOrEmpty(label) && label.IndexOf(OptOutLabel, StringComparison.OrdinalIgnoreCase) >= 0) return false;

        Vector3 center = storage.ToWorldPos().ToVector3() + new Vector3(0.5f, 0.5f, 0.5f);
        return (center - from).sqrMagnitude <= range * range;
    }

    // Boxes in range of the local player. The list is cached for a second;
    // the contents are read live from each box every time.
    public static List<TEFeatureStorage> Nearby()
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

        int range = KCFC_Settings.Range;
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
                    TileEntity te = entities[i];
                    if (te == null || !te.TryGetSelfOrFeature<TEFeatureStorage>(out TEFeatureStorage storage)) continue;
                    // The box open in front of you is yours to manage by hand.
                    if (storage.IsUserAccessing()) continue;
                    if (Usable(storage, user, from, range)) nearby.Add(storage);
                }
            }
        }
        return nearby;
    }

    public static int Count(int itemType)
    {
        int total = 0;
        List<TEFeatureStorage> boxes = Nearby();
        for (int b = 0; b < boxes.Count; b++)
        {
            ItemStackGrid grid = boxes[b].ItemGrid;
            for (int i = 0; i < grid.Length; i++)
            {
                if (grid.IsLocked(i)) continue;
                ItemStack stack = grid.GetItem(i);
                if (Eligible(stack) && stack.itemValue.type == itemType) total += stack.count;
            }
        }
        return total;
    }

    public static void AppendStacks(List<ItemStack> into)
    {
        if (into == null) return;
        List<TEFeatureStorage> boxes = Nearby();
        for (int b = 0; b < boxes.Count; b++)
        {
            ItemStackGrid grid = boxes[b].ItemGrid;
            for (int i = 0; i < grid.Length; i++)
            {
                if (grid.IsLocked(i)) continue;
                ItemStack stack = grid.GetItem(i);
                if (Eligible(stack)) into.Add(stack.Clone());
            }
        }
    }

    // The crafter's side of a removal. Returns how many were taken.
    public static int Take(ItemValue itemValue, int count)
    {
        bool isServer = SingletonMonoBehaviour<ConnectionManager>.Instance.IsServer;
        int taken = 0;
        List<TEFeatureStorage> boxes = Nearby();
        for (int b = 0; b < boxes.Count && taken < count; b++)
        {
            TEFeatureStorage storage = boxes[b];
            // A remote client must not sync its copy (see the header); the
            // server's own copy IS the real box, so the host syncs normally.
            int got = TakeFrom(storage, itemValue.type, count - taken, sync: isServer);
            if (got <= 0) continue;
            taken += got;
            if (isServer)
            {
                KCFC_Net.ForwardToViewers(storage, itemValue.type, got, GameManager.Instance.World.GetPrimaryPlayerId());
            }
            else
            {
                KCFC_Net.SendTake(storage, itemValue.type, got);
            }
        }
        return taken;
    }

    // Takes up to `count` of a plain item from one box, skipping locked slots.
    // ChangeCount empties a slot through ItemStack.Clear(), which raises no
    // change event, so when syncing we raise one per touched slot ourselves:
    // that refreshes an open loot window and sends the box out (SetModified).
    public static int TakeFrom(TEFeatureStorage storage, int itemType, int count, bool sync)
    {
        ItemStackGrid grid = storage.ItemGrid;
        int removed = 0;
        List<int> touched = sync ? new List<int>() : null;
        bool wasSuppressed = grid.suppressChanged;
        if (!sync) grid.suppressChanged = true;
        try
        {
            for (int i = 0; i < grid.Length && removed < count; i++)
            {
                if (grid.IsLocked(i)) continue;
                ItemStack stack = grid.GetItem(i);
                if (!Eligible(stack) || stack.itemValue.type != itemType) continue;
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
}

public static class KCFC_Net
{
    public const string HelloVerb = "kcfc_hello";
    public const string OkVerb = "kcfc_ok";
    public const string TakeVerb = "kcfc_take";

    private const int MaxHellos = 3;
    private const float HelloRetrySeconds = 10f;
    // Slack over the server's own range for player movement between the
    // client's check and the request arriving.
    private const float ServerRangeSlack = 4f;
    private const int MaxTakePerRequest = 100000;

    private static World helloWorld;
    private static bool serverHasMod;
    private static int hellosSent;
    private static float lastHelloAt = -100f;

    // True once boxes may be used: always on the server / single player, and
    // on a client once the server has said it runs the mod.
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

    public static string TakeLine(TEFeatureStorage storage, int itemType, int count)
    {
        Vector3i pos = storage.ToWorldPos();
        return TakeVerb + " " + pos.x + " " + pos.y + " " + pos.z + " " + itemType + " " + count;
    }

    public static void SendTake(TEFeatureStorage storage, int itemType, int count)
    {
        SingletonMonoBehaviour<ConnectionManager>.Instance.SendToServer(
            NetPackageManager.GetPackage<NetPackageConsoleCmdServer>().Setup(TakeLine(storage, itemType, count)));
    }

    // Server: a client line. Returns false if it is not ours.
    public static bool OnServerLine(World world, ClientInfo sender, string cmd)
    {
        if (cmd == HelloVerb)
        {
            sender?.SendPackage(NetPackageManager.GetPackage<NetPackageConsoleCmdClient>().Setup(OkVerb, true));
            return true;
        }
        if (!cmd.StartsWith(TakeVerb + " ", StringComparison.Ordinal)) return false;
        if (sender == null) return true;

        if (!ParseTake(cmd, out Vector3i pos, out int itemType, out int count) || count > MaxTakePerRequest)
        {
            Log.Warning("[KitsuneCraftFromContainers] Ignored a malformed take from " + sender.playerName);
            return true;
        }

        EntityPlayer player = world.GetEntity(sender.entityId) as EntityPlayer;
        TEFeatureStorage storage = StorageAt(world, pos);
        if (player == null || storage == null
            || !KCFC_Containers.Usable(storage, sender.InternalId, player.position, KCFC_Settings.Range + ServerRangeSlack))
        {
            Log.Warning("[KitsuneCraftFromContainers] Refused a take by " + sender.playerName + " from " + pos
                + " (no usable box there for them)");
            return true;
        }

        int got = KCFC_Containers.TakeFrom(storage, itemType, count, sync: true);
        if (got < count)
        {
            // The client's copy was ahead of the box. Rare (two people pulling
            // the same items at once); the crafter keeps the difference.
            Log.Warning("[KitsuneCraftFromContainers] " + sender.playerName + " crafted with " + count + " of item "
                + itemType + " from " + pos + " but only " + got + " were there");
        }
        if (got > 0) ForwardToViewers(storage, itemType, got, sender.entityId);
        return true;
    }

    // Server: tell everyone looking into this box (except the crafter, and
    // except the host, whose window shows the real box) to drop the items
    // from the copy they will write back.
    public static void ForwardToViewers(TEFeatureStorage storage, int itemType, int count, int crafterEntityId)
    {
        ConnectionManager connections = SingletonMonoBehaviour<ConnectionManager>.Instance;
        int hostId = GameManager.Instance.World.GetPrimaryPlayerId();
        string line = null;
        foreach (int viewer in Viewers(storage))
        {
            if (viewer == crafterEntityId || viewer == hostId) continue;
            ClientInfo client = connections?.Clients?.ForEntityId(viewer);
            if (client == null) continue;
            line = line ?? TakeLine(storage, itemType, count);
            client.SendPackage(NetPackageManager.GetPackage<NetPackageConsoleCmdClient>().Setup(line, true));
        }
    }

    private static List<int> Viewers(TEFeatureStorage storage)
    {
        List<int> ids = new List<int>();
        LockManager locks = LockManager.Instance;
        foreach (LockManager.LockEntry entry in locks.singleLocks.Values)
        {
            if (ReferenceEquals(entry.Target, storage) && locks.singleLocks.TryGetByValue(entry, out int id) && !ids.Contains(id))
            {
                ids.Add(id);
            }
        }
        foreach (LockManager.LockEntry entry in locks.sharedLocks.Values)
        {
            if (!ReferenceEquals(entry.Target, storage) || !locks.sharedLocks.TryGetByValue(entry, out IReadOnlyCollection<int> holders)) continue;
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
        if (line == OkVerb)
        {
            serverHasMod = true;
            helloWorld = world;
            Log.Out("[KitsuneCraftFromContainers] Server runs the mod; crafting can use nearby boxes");
            // Wake the crafting UI so an already-open recipe list recounts.
            XUiM_PlayerInventory inventory = world.GetPrimaryPlayer()?.PlayerUI?.xui?.PlayerInventory;
            inventory?.dispatchBackpackItemsChanged();
            return true;
        }
        if (!line.StartsWith(TakeVerb + " ", StringComparison.Ordinal)) return false;
        if (!ParseTake(line, out Vector3i pos, out int itemType, out int count)) return true;

        // Someone else crafted from the box we have open. Our window ignores
        // the server's copy until we close it, so apply the take here too.
        TEFeatureStorage storage = StorageAt(world, pos);
        if (storage != null && storage.IsUserAccessing())
        {
            KCFC_Containers.TakeFrom(storage, itemType, count, sync: true);
        }
        return true;
    }

    private static TEFeatureStorage StorageAt(World world, Vector3i pos)
    {
        TileEntity te = world.GetTileEntity(pos);
        return te != null && te.TryGetSelfOrFeature<TEFeatureStorage>(out TEFeatureStorage storage) ? storage : null;
    }

    private static bool ParseTake(string line, out Vector3i pos, out int itemType, out int count)
    {
        pos = Vector3i.zero;
        itemType = 0;
        count = 0;
        string[] parts = line.Split(' ');
        if (parts.Length != 6
            || !int.TryParse(parts[1], out int x) || !int.TryParse(parts[2], out int y) || !int.TryParse(parts[3], out int z)
            || !int.TryParse(parts[4], out itemType) || !int.TryParse(parts[5], out count))
        {
            return false;
        }
        pos = new Vector3i(x, y, z);
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
// the recipe list already includes the boxes the first time it opens.
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
// it in, so it gets the box stacks added directly.
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
            Log.Error("[KitsuneCraftFromContainers] adding boxes to the recipe list failed: " + ex);
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
// through GetItemCount, which then includes the boxes.
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

// Vanilla RemoveItems, with the boxes as a third source after backpack and
// toolbelt. Kept faithful to vanilla, including checking HasItems at a
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
