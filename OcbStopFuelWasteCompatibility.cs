using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;

// Optional adapters for OcbStopFuelWaste 1.1.0. No reference to its DLL is required.
internal static class OcbStopFuelWasteCompatibility
{
    internal static MethodInfo Find(string nestedType, string method) =>
        AccessTools.Method(AccessTools.TypeByName("StopFuelWaste+" + nestedType), method);

    [HarmonyPatch]
    internal static class ServerWork
    {
        static bool Prepare() => Find("TileEntityWorkstation_UpdateTick", "HasWork") != null;
        static MethodBase TargetMethod() => Find("TileEntityWorkstation_UpdateTick", "HasWork");

        static bool Prefix(TileEntityWorkstation __0, ref bool __result)
        {
            if (!DrokkStorage.config.multiViewersEnabled) return true;
            // IsCrafting is a UI/serialized flag and may be stale on server-owned queues.
            __result = HasWork(__0);
            return false;
        }
    }

    internal static bool HasWork(TileEntityWorkstation station)
    {
        if (station.Queue != null && station.Queue.Any(item =>
            item?.Recipe != null && item.Multiplier > 0)) return true;

        // Campfire ingredients are not independent work. Only forge melting should keep
        // burning after the crafting queue finishes. Retain OCB's input-slot limit.
        if (station.isModuleUsed == null || station.isModuleUsed.Length <= 4
            || !station.isModuleUsed[4] || station.Input == null) return false;
        return station.Input.Take(Math.Max(0, station.InputSlotCount))
            .Any(item => item != null && !item.IsEmpty());
    }

    [HarmonyPatch]
    internal static class UiShutdown
    {
        static bool Prepare() => Find("XUiC_WorkstationFuelGrid_Update", "Postfix") != null;
        static MethodBase TargetMethod() => Find("XUiC_WorkstationFuelGrid_Update", "Postfix");

        // Shared UI rows are refreshed outside Update, so their edges cannot reliably
        // signal completion. OCB's server UpdateTick remains responsible for shutting down
        // and IsBurning's setter broadcasts the authoritative state to all viewers.
        static bool Prefix() => !DrokkStorage.config.multiViewersEnabled;
    }
}
