using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using Platform;
using UnityEngine;

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
