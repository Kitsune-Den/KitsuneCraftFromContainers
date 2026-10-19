using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using Platform;
using UnityEngine;

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
