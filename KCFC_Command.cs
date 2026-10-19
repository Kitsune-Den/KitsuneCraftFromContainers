using System;
using System.Collections.Generic;
using Platform;
using UnityEngine;

// `kcfc` in the F1 console. Every player may run it (DefaultPermissionLevel
// 1000), and it runs on their own machine, since what it reports is what
// their crafting sees.
//   kcfc / kcfc status   the rules in effect, and every container in range
//                        with whether it counts and, if not, why
//   kcfc repair          repair the land claim you're standing in
public class KCFC_ConsoleCmd : ConsoleCmdAbstract
{
    private const int MaxListed = 40;

    public override bool IsExecuteOnClient => true;
    public override int DefaultPermissionLevel => 1000;

    public override string[] getCommands() => new[] { "kcfc" };

    public override string getDescription() => "Kitsune Craft From Containers: what crafting can use, and claim repair";

    public override string getHelp()
    {
        return "Usage:\n"
            + "  kcfc            the rules in effect and the containers in range, with why any are skipped\n"
            + "  kcfc status     the same\n"
            + "  kcfc repair     repair the land claim you're standing in from its containers\n";
    }

    public override void Execute(List<string> _params, CommandSenderInfo _senderInfo)
    {
        string verb = _params.Count > 0 ? _params[0].ToLowerInvariant() : "status";
        switch (verb)
        {
            case "status":
                Status();
                break;
            case "repair":
                Repair();
                break;
            default:
                Output(getHelp());
                break;
        }
    }

    private static void Output(string line) => SingletonMonoBehaviour<SdtdConsole>.Instance.Output(line);

    private static void Status()
    {
        World world = GameManager.Instance?.World;
        ConnectionManager connections = SingletonMonoBehaviour<ConnectionManager>.Instance;
        EntityPlayerLocal player = world?.GetPrimaryPlayer();
        if (world == null || player == null)
        {
            // A dedicated server's console: there is no "near me".
            Output("Server settings: " + KCFC_Settings.Local.ToLine());
            Output("Run kcfc in game to see which containers a player's crafting uses.");
            return;
        }

        bool ready = KCFC_Net.Ready;
        if (!connections.IsServer)
        {
            Output(ready
                ? "The server runs the mod. Its settings: " + (KCFC_Net.ServerConfig?.ToLine() ?? "none sent (0.1.0)")
                : "The server hasn't said it runs the mod (yet). Until it does, crafting uses only your inventory.");
        }
        KCFC_Config rules = KCFC_Settings.Effective;
        Output("In effect: " + rules.ToLine());
        if (!ready) return;

        int range = rules.Range;
        Vector3 from = player.position;
        Vector3i at = World.worldToBlockPos(from);
        PlatformUserIdentifierAbs user = PlatformManager.InternalLocalUserIdentifier;

        List<KCFC_Source> found = new List<KCFC_Source>();
        KCFC_Containers.BlockSourcesIn(world, at.x - range, at.z - range, at.x + range, at.z + range, found);
        foreach (Entity entity in KCFC_Bags.Around(world, from, range))
        {
            KCFC_EntitySource live = KCFC_EntitySource.Live(entity);
            if (live != null) found.Add(live);
        }
        found.Sort((a, b) => (a.Center - from).sqrMagnitude.CompareTo((b.Center - from).sqrMagnitude));

        int counted = 0;
        int listed = 0;
        foreach (KCFC_Source source in found)
        {
            float distance = (source.Center - from).magnitude;
            if (distance > range) continue;
            string why = source.IsUserAccessing ? "open in front of you" : source.Refusal(user, from, range, rules);
            if (why == null && source is KCFC_EntitySource vehicle && !connections.IsServer
                && KCFC_Bags.Get(world, vehicle.EntityId) == null)
            {
                why = "waiting for the server to report its contents";
            }
            if (why == null) counted++;
            if (listed++ >= MaxListed) continue;
            Output(string.Format("  {0} {1} at {2}, {3:0.0}m{4}", why == null ? "+" : "-", source.Name, Where(source),
                distance, why == null ? "" : ": " + why));
        }
        if (listed > MaxListed) Output("  ... and " + (listed - MaxListed) + " more");
        Output(counted + " of " + listed + " containers within " + range + " blocks count.");

        PersistentPlayerData me = GameManager.Instance.persistentPlayers?.GetPlayerData(user);
        if (KCFC_ClaimRepair.TryFindClaim(at, me, out Vector3i claimBlock))
        {
            Output("You're in a claim you can repair (block at " + claimBlock + ")"
                + (rules.ClaimRepair ? ": kcfc repair, or Repair on the claim block." : ", but claim repair is off."));
        }
    }

    private static string Where(KCFC_Source source)
    {
        if (source is KCFC_BlockSource block) return block.Pos.ToString();
        Vector3i pos = World.worldToBlockPos(source.Center);
        return pos.ToString();
    }

    private static void Repair()
    {
        World world = GameManager.Instance?.World;
        EntityPlayerLocal player = world?.GetPrimaryPlayer();
        if (player == null)
        {
            Output("Run kcfc repair in game, standing in the claim.");
            return;
        }
        if (!KCFC_Net.Ready)
        {
            Output("The server hasn't said it runs the mod, so it can't repair the claim.");
            return;
        }
        PersistentPlayerData me = GameManager.Instance.persistentPlayers?.GetPlayerData(PlatformManager.InternalLocalUserIdentifier);
        if (!KCFC_ClaimRepair.TryFindClaim(World.worldToBlockPos(player.position), me, out Vector3i claimBlock))
        {
            Output("You're not standing in a land claim you own or are allied to.");
            return;
        }
        KCFC_ClaimRepair.Request(world, claimBlock, player);
    }
}
