using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using Platform;
using UnityEngine;

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

        scratch.Clear();
        BlockSourcesIn(world, at.x - range, at.z - range, at.x + range, at.z + range, scratch);
        foreach (KCFC_Source source in scratch)
        {
            // The container open in front of you is yours to manage by hand.
            if (!source.IsUserAccessing && source.Usable(user, from, range, rules)) nearby.Add(source);
        }
        if (rules.Vehicles) AddEntities(world, from, range, user, rules);
        return nearby;
    }

    private static readonly List<KCFC_Source> scratch = new List<KCFC_Source>();
    private static readonly List<Entity> areaEntities = new List<Entity>();

    // Every block container with its block inside the rectangle (inclusive),
    // unfiltered, as this machine holds it. Unloaded chunks are skipped.
    public static void BlockSourcesIn(World world, int minX, int minZ, int maxX, int maxZ, List<KCFC_Source> into)
    {
        for (int cx = World.toChunkXZ(minX); cx <= World.toChunkXZ(maxX); cx++)
        {
            for (int cz = World.toChunkXZ(minZ); cz <= World.toChunkXZ(maxZ); cz++)
            {
                if (!(world.GetChunkSync(cx, cz) is Chunk chunk)) continue;
                List<TileEntity> entities = chunk.GetTileEntities()?.list;
                if (entities == null) continue;
                for (int i = 0; i < entities.Count; i++)
                {
                    if (!(KCFC_Source.For(entities[i]) is KCFC_BlockSource source)) continue;
                    Vector3i pos = source.Pos;
                    if (pos.x >= minX && pos.x <= maxX && pos.z >= minZ && pos.z <= maxZ) into.Add(source);
                }
            }
        }
    }

    // Server: the containers in a square of blocks around `center` (any
    // height) that `user` may use, live, nearest the centre first. Nobody's
    // open window is skipped: the server's copy is the real one, and takes
    // from it are forwarded to whoever has it open.
    public static List<KCFC_Source> InArea(World world, Vector3i center, int halfSize, PlatformUserIdentifierAbs user, KCFC_Config rules)
    {
        List<KCFC_Source> sources = new List<KCFC_Source>();
        int minX = center.x - halfSize, maxX = center.x + halfSize, minZ = center.z - halfSize, maxZ = center.z + halfSize;
        BlockSourcesIn(world, minX, minZ, maxX, maxZ, sources);
        sources.RemoveAll(source => !source.Permits(user, rules));
        if (rules.Vehicles)
        {
            areaEntities.Clear();
            Bounds bounds = new Bounds(center.ToVector3(), new Vector3(halfSize * 2 + 1, 512f, halfSize * 2 + 1));
            world.GetEntitiesInBounds(typeof(EntityVehicle), bounds, areaEntities);
            world.GetEntitiesInBounds(typeof(EntityDrone), bounds, areaEntities);
            foreach (Entity entity in areaEntities)
            {
                KCFC_EntitySource live = KCFC_EntitySource.Live(entity);
                if (live != null && live.Permits(user, rules)) sources.Add(live);
            }
            areaEntities.Clear();
        }
        Vector3 from = center.ToVector3();
        sources.Sort((a, b) => (a.Center - from).sqrMagnitude.CompareTo((b.Center - from).sqrMagnitude));
        return sources;
    }

    // Totals per item type over `sources`.
    public static int CountIn(List<KCFC_Source> sources, int itemType)
    {
        int total = 0;
        foreach (KCFC_Source source in sources)
        {
            for (int i = 0; i < source.Length; i++)
            {
                ItemStack stack = source.Slot(i);
                if (Eligible(stack) && stack.itemValue.type == itemType) total += stack.count;
            }
        }
        return total;
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
