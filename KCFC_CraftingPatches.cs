using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using Platform;
using UnityEngine;

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
