using System;
using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

// Repairing a whole land claim from its containers.
//
// 3.3's land claim block has an area repair (TEFeatureAreaRepair): the server
// walks every damaged block within 22 blocks of the claim block and repairs
// the ones it can pay for, at a hammer's cost, from the claim block's own
// storage. Nothing in the game triggers it. This adds a "Repair" command to
// the claim block's radial (and `kcfc repair`), both of which send vanilla's
// NetPackageLandClaimRepair. When the server's `claimrepair` is on, the
// repair that package starts is replaced with one that:
//   - pays from every container in the claim the requester may use, by the
//     same rules as crafting (the claim block's storage is one of them);
//   - covers the server's real claim size instead of a fixed 22;
//   - is refused during a blood moon unless `claimrepairbloodmoon` is on,
//     and stops if one starts;
//   - is refused to anyone but the claim's owner and their allies (vanilla
//     accepts the package from anyone);
//   - tells the requester what it repaired and what it was short of.
// Blocks that broke down to a weaker block stay downgraded, as with a hammer.
public static class KCFC_ClaimRepair
{
    public const string ReplyVerb = "kcfc_repair";

    // Blocks repaired between frames.
    private const int BlocksPerFrame = 64;
    // How many missing materials the reply lists.
    private const int MissingListed = 5;

    // The radial command's full name, "<feature name>:repair", once the claim
    // block's commands are built.
    public static string CommandName;

    // Server: claim blocks being repaired right now.
    private static readonly HashSet<Vector3i> running = new HashSet<Vector3i>();

    public static int HalfSize => Math.Max(0, (GameStats.GetInt(EnumGameStats.LandClaimSize) - 1) / 2);

    // Client: the claim block whose claim covers `pos` and belongs to `me` or
    // an ally, if any.
    public static bool TryFindClaim(Vector3i pos, PersistentPlayerData me, out Vector3i claimBlock)
    {
        claimBlock = Vector3i.zero;
        Dictionary<Vector3i, PersistentPlayerData> claims = GameManager.Instance?.persistentPlayers?.m_lpBlockMap;
        if (me == null || claims == null) return false;
        int half = HalfSize;
        foreach (KeyValuePair<Vector3i, PersistentPlayerData> claim in claims)
        {
            if (Math.Abs(pos.x - claim.Key.x) > half || Math.Abs(pos.z - claim.Key.z) > half) continue;
            if (!IsOwnerOrAlly(claim.Value, me)) continue;
            claimBlock = claim.Key;
            return true;
        }
        return false;
    }

    private static bool IsOwnerOrAlly(PersistentPlayerData owner, PersistentPlayerData player)
    {
        return owner != null && player != null && (owner.PrimaryId.Equals(player.PrimaryId) || owner.IsAlly(player));
    }

    // Client: ask the server to repair the claim whose block is at `claimBlock`.
    public static void Request(World world, Vector3i claimBlock, EntityPlayerLocal player)
    {
        TEFeatureAreaRepair feature = world.GetTileEntity(claimBlock)?.GetSelfOrFeature<TEFeatureAreaRepair>();
        if (feature == null)
        {
            Say(player, "There's no land claim block there.");
            return;
        }
        if (!KCFC_Settings.Effective.ClaimRepair)
        {
            Say(player, "Claim repair is off on this server.");
            return;
        }
        feature.IsRepairing = true;
        Say(player, "Repairing the claim...");
        if (SingletonMonoBehaviour<ConnectionManager>.Instance.IsServer)
        {
            feature.RepairAll(world, claimBlock, player.entityId);
        }
        else
        {
            SingletonMonoBehaviour<ConnectionManager>.Instance.SendToServer(
                NetPackageManager.GetPackage<NetPackageLandClaimRepair>().Setup(claimBlock, _beginRepair: true));
        }
    }

    // Server: the repair NetPackageLandClaimRepair asked for.
    public static IEnumerator Run(TEFeatureAreaRepair feature, World world, Vector3i claimBlock, int requesterId)
    {
        KCFC_Config rules = KCFC_Settings.Local;
        PersistentPlayerData requester = GameManager.Instance.persistentPlayers?.GetPlayerDataFromEntityID(requesterId);
        PersistentPlayerData owner = GameManager.Instance.persistentPlayers?.GetLandProtectionBlockOwner(claimBlock);

        string refusal = null;
        if (!IsOwnerOrAlly(owner, requester)) refusal = "denied";
        else if (running.Contains(claimBlock)) refusal = "busy";
        else if (BloodMoon(world) && !rules.ClaimRepairBloodMoon) refusal = "bloodmoon";
        if (refusal != null)
        {
            Finish(feature, claimBlock, requesterId, ReplyVerb + " " + refusal);
            yield break;
        }

        running.Add(claimBlock);
        int repaired = 0;
        int skipped = 0;
        bool stopped = false;
        Dictionary<int, int> missing = new Dictionary<int, int>();
        try
        {
            int half = HalfSize;
            int minX = claimBlock.x - half, maxX = claimBlock.x + half, minZ = claimBlock.z - half, maxZ = claimBlock.z + half;
            List<KCFC_Source> sources = KCFC_Containers.InArea(world, claimBlock, half, requester.PrimaryId, rules);
            Dictionary<int, int> totals = new Dictionary<int, int>();
            List<Vector3i> damaged = new List<Vector3i>();
            int sinceYield = 0;

            for (int cx = World.toChunkXZ(minX); cx <= World.toChunkXZ(maxX) && !stopped; cx++)
            {
                for (int cz = World.toChunkXZ(minZ); cz <= World.toChunkXZ(maxZ) && !stopped; cz++)
                {
                    if (BloodMoon(world) && !rules.ClaimRepairBloodMoon)
                    {
                        stopped = true;
                        break;
                    }
                    if (!(world.GetChunkSync(cx, cz) is Chunk chunk)) continue;

                    damaged.Clear();
                    Vector3i origin = chunk.worldPosIMin;
                    yield return chunk.LoopOverAllBlocksCoroutine((x, y, z, bv) =>
                    {
                        if (bv.isair || bv.damage <= 0) return;
                        Vector3i pos = origin + new Vector3i(x, y, z);
                        if (pos.x >= minX && pos.x <= maxX && pos.z >= minZ && pos.z <= maxZ) damaged.Add(pos);
                    });

                    // Containers can change between chunks (players, other
                    // repairs), so count again for each.
                    totals.Clear();
                    foreach (Vector3i pos in damaged)
                    {
                        switch (RepairBlock(world, pos, sources, totals, missing))
                        {
                            case Outcome.Repaired:
                                repaired++;
                                break;
                            case Outcome.Short:
                                skipped++;
                                break;
                        }
                        if (++sinceYield >= BlocksPerFrame)
                        {
                            sinceYield = 0;
                            yield return null;
                        }
                    }
                }
            }
        }
        finally
        {
            running.Remove(claimBlock);
        }

        string line = ReplyVerb + " " + (stopped ? "stopped" : "done") + " " + repaired + " " + skipped + " " + MissingList(missing);
        Log.Out("[KitsuneCraftFromContainers] Claim repair at " + claimBlock + " for entity " + requesterId + ": "
            + line.Substring(ReplyVerb.Length + 1));
        Finish(feature, claimBlock, requesterId, line.TrimEnd());
    }

    private enum Outcome
    {
        NotRepairable,
        Repaired,
        Short,
    }

    // Vanilla's area repair for one block, paying from `sources`.
    private static Outcome RepairBlock(World world, Vector3i pos, List<KCFC_Source> sources, Dictionary<int, int> totals, Dictionary<int, int> missing)
    {
        BlockValue bv = world.GetBlock(pos);
        if (bv.isair || bv.ischild || bv.damage <= 0) return Outcome.NotRepairable;
        Block block = bv.Block;
        int damage = bv.damage;
        float fraction = (float)damage / block.MaxDamage;
        List<Block.SItemNameCount> items = block.RepairItems;
        if (block.RepairItemsMeshDamage != null && block.shape.UseRepairDamageState(bv))
        {
            damage = 1;
            fraction = 1f;
            items = block.RepairItemsMeshDamage;
        }
        if (items == null) return Outcome.NotRepairable;

        int[] types = new int[items.Count];
        int[] counts = new int[items.Count];
        bool enough = true;
        for (int i = 0; i < items.Count; i++)
        {
            ItemValue item = ItemClass.GetItem(items[i].ItemName);
            if (item == null || item.type <= 0) return Outcome.NotRepairable;
            types[i] = item.type;
            counts[i] = Utils.FastMax((int)(items[i].Count * fraction * block.ResourceScale), 1);
            if (!totals.TryGetValue(item.type, out int total))
            {
                total = KCFC_Containers.CountIn(sources, item.type);
                totals[item.type] = total;
            }
            if (total < counts[i]) enough = false;
        }
        if (!enough)
        {
            for (int i = 0; i < types.Length; i++)
            {
                missing.TryGetValue(types[i], out int sum);
                missing[types[i]] = sum + counts[i];
            }
            return Outcome.Short;
        }

        for (int i = 0; i < types.Length; i++)
        {
            int rest = counts[i];
            foreach (KCFC_Source source in sources)
            {
                if (rest <= 0) break;
                int got = source.TakeFrom(types[i], rest, sync: true);
                if (got <= 0) continue;
                rest -= got;
                // Nobody crafted this, so everyone with it open hears.
                KCFC_Net.ForwardToViewers(source, types[i], got, -1);
            }
            totals[types[i]] -= counts[i] - rest;
        }
        block.DamageBlock(world, pos, bv, -damage, -1);
        return Outcome.Repaired;
    }

    private static bool BloodMoon(World world)
    {
        return world.aiDirector?.BloodMoonComponent?.BloodMoonActive == true;
    }

    // "<type>:<count>,..." for the biggest shortfalls.
    private static string MissingList(Dictionary<int, int> missing)
    {
        List<KeyValuePair<int, int>> sorted = new List<KeyValuePair<int, int>>(missing);
        sorted.Sort((a, b) => b.Value.CompareTo(a.Value));
        List<string> parts = new List<string>();
        for (int i = 0; i < sorted.Count && i < MissingListed; i++) parts.Add(sorted[i].Key + ":" + sorted[i].Value);
        return string.Join(",", parts);
    }

    // Server: tell the requester, and clear the claim block's "repairing"
    // flag the way vanilla's own repair does.
    private static void Finish(TEFeatureAreaRepair feature, Vector3i claimBlock, int requesterId, string line)
    {
        World world = GameManager.Instance.World;
        EntityPlayer primary = world?.GetPrimaryPlayer();
        if (primary != null && primary.entityId == requesterId)
        {
            feature.IsRepairing = false;
            OnReply(world, line);
            return;
        }
        ConnectionManager connections = SingletonMonoBehaviour<ConnectionManager>.Instance;
        connections.SendPackage(NetPackageManager.GetPackage<NetPackageLandClaimRepair>().Setup(claimBlock, _beginRepair: false),
            _onlyClientsAttachedToAnEntity: false, requesterId);
        ClientInfo client = connections.Clients?.ForEntityId(requesterId);
        if (client != null) KCFC_Net.SendLine(client, line);
    }

    // Requester: "kcfc_repair <done|stopped> <repaired> <skipped> [<type>:<count>,...]"
    // or "kcfc_repair <denied|busy|bloodmoon>".
    public static void OnReply(World world, string line)
    {
        EntityPlayerLocal player = world?.GetPrimaryPlayer();
        string[] parts = line.Split(' ');
        if (parts.Length < 2) return;
        switch (parts[1])
        {
            case "denied":
                Say(player, "You can only repair a claim you own or are allied to.");
                return;
            case "busy":
                Say(player, "That claim is already being repaired.");
                return;
            case "bloodmoon":
                Say(player, "You can't repair the claim during a blood moon.");
                return;
        }
        if (parts.Length < 4 || !int.TryParse(parts[2], out int repaired) || !int.TryParse(parts[3], out int skipped)) return;

        string text;
        if (parts[1] == "stopped") text = "A blood moon started: stopped after repairing " + Blocks(repaired) + ".";
        else if (repaired == 0 && skipped == 0) text = "Nothing on the claim needs repairing.";
        else text = "Repaired " + Blocks(repaired) + " on the claim.";
        if (skipped > 0)
        {
            text += " " + Blocks(skipped) + " still need";
            List<string> items = new List<string>();
            if (parts.Length > 4)
            {
                foreach (string entry in parts[4].Split(','))
                {
                    int colon = entry.IndexOf(':');
                    if (colon <= 0 || !int.TryParse(entry.Substring(0, colon), out int type)
                        || !int.TryParse(entry.Substring(colon + 1), out int count))
                    {
                        continue;
                    }
                    ItemClass item = ItemClass.GetForId(type);
                    items.Add(count + " " + (item?.GetLocalizedItemName() ?? "item " + type));
                }
            }
            text += items.Count > 0 ? ": " + string.Join(", ", items) + "." : " more materials.";
        }
        Say(player, text);
    }

    private static string Blocks(int n) => n == 1 ? "1 block" : n + " blocks";

    // On screen if there's a player here, and in the console either way.
    public static void Say(EntityPlayerLocal player, string text)
    {
        Log.Out("[KitsuneCraftFromContainers] " + text);
        if (player != null) GameManager.ShowTooltip(player, text);
    }
}

// The server side: vanilla's repair, replaced while `claimrepair` is on.
[HarmonyPatch(typeof(TEFeatureAreaRepair))]
[HarmonyPatch("RepairAll")]
public class KCFC_ClaimRepairPatch
{
    public static bool Prefix(TEFeatureAreaRepair __instance, World _world, Vector3i _blockPos, int _requesterId)
    {
        if (!KCFC_Settings.Local.ClaimRepair) return true;
        GameManager.Instance.StartCoroutine(KCFC_ClaimRepair.Run(__instance, _world, _blockPos, _requesterId));
        return false;
    }
}

// The "Repair" command on the claim block's radial. Its label is already in
// the game's localization ("blockcommand_TEFeatureAreaRepair:repair"). The
// command lists are built once per block type, so it is always added and
// enabled per use below.
[HarmonyPatch(typeof(TEFeatureAreaRepair))]
[HarmonyPatch("InitBlockActivationCommands")]
public class KCFC_ClaimRepairCommandPatch
{
    public static void Postfix(TEFeatureAreaRepair __instance,
        Action<BlockActivationCommand, TileEntityComposite.EBlockCommandOrder, TileEntityFeatureData> _addCallback)
    {
        if (__instance.FeatureData == null) return;
        _addCallback(new BlockActivationCommand("repair", "wrench", _enabled: false),
            TileEntityComposite.EBlockCommandOrder.Normal, __instance.FeatureData);
        KCFC_ClaimRepair.CommandName = __instance.FeatureData.Name + ":repair";
    }
}

// Vanilla enables it for the claim's owner (TEFeatureAreaRepair's own check);
// this also needs the server to run the repair.
[HarmonyPatch(typeof(TileEntityComposite))]
[HarmonyPatch("UpdateBlockActivationCommands")]
public class KCFC_ClaimRepairEnablePatch
{
    public static void Postfix(BlockActivationCommand[] _commands, ref bool __result)
    {
        string name = KCFC_ClaimRepair.CommandName;
        if (name == null || _commands == null) return;
        bool any = false;
        for (int i = 0; i < _commands.Length; i++)
        {
            if (_commands[i].text == name && _commands[i].enabled)
            {
                _commands[i].enabled = KCFC_Net.Ready && KCFC_Settings.Effective.ClaimRepair;
            }
            any |= _commands[i].enabled;
        }
        __result = any;
    }
}

[HarmonyPatch(typeof(BlockCompositeTileEntity))]
[HarmonyPatch("OnBlockActivated", new[] { typeof(string), typeof(WorldBase), typeof(Vector3i), typeof(BlockValue), typeof(EntityPlayerLocal) })]
public class KCFC_ClaimRepairActivatePatch
{
    public static bool Prefix(string _commandName, WorldBase _world, Vector3i _blockPos, EntityPlayerLocal _player, ref bool __result)
    {
        if (_commandName == null || _commandName != KCFC_ClaimRepair.CommandName || !(_world is World world)) return true;
        try
        {
            KCFC_ClaimRepair.Request(world, _blockPos, _player);
        }
        catch (Exception ex)
        {
            Log.Error("[KitsuneCraftFromContainers] starting a claim repair failed: " + ex);
        }
        __result = true;
        return false;
    }
}
