using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using Platform;
using UnityEngine;

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
