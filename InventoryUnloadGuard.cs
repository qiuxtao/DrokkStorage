using System;
using System.Reflection;
using HarmonyLib;

// OnEntityUnload cleans the toolbelt before Bag.Clear raises its last UI refresh event.
// UI readers must tolerate that interval without interrupting world cleanup.
[HarmonyPatch(typeof(XUiM_PlayerInventory), nameof(XUiM_PlayerInventory.GetToolbeltItemStacks))]
public static class InventoryUnloadGuard
{
    private static readonly FieldInfo Slots = AccessTools.Field(typeof(Inventory), "slots");

    public static bool Prefix(Inventory ___toolbelt, ref ItemStack[] __result)
    {
        if (___toolbelt != null && Slots.GetValue(___toolbelt) != null) return true;
        __result = Array.Empty<ItemStack>();
        return false;
    }
}
