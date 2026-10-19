using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

// Presentation only: never writes recipe quantities, ingredient data or workstation state.
public static class SharedCountdownDisplay
{
    private sealed class Clock
    {
        public Recipe Recipe;
        public int Owner, Count;
        public float Duration, Remaining, LastTime;
        public bool Running;
    }

    private static readonly ConditionalWeakTable<XUiC_RecipeStack, Clock> clocks =
        new ConditionalWeakTable<XUiC_RecipeStack, Clock>();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static float UnityTime() => Time.time;
    private static Func<float> timeSource = UnityTime;
    public static float Read(float raw, XUiC_RecipeStack row) => Sample(raw, row, timeSource());

    public static float Sample(float raw, XUiC_RecipeStack row, float now)
    {
        if (!DrokkStorage.config.multiViewersEnabled
            || !(row.windowGroup?.Controller is XUiC_WorkstationWindowGroup)) return raw;
        raw = Math.Max(0f, raw);
        var recipe = row.GetRecipe();
        if (recipe == null || row.GetRecipeCount() <= 0)
        {
            clocks.Remove(row);
            return raw;
        }
        float duration = row.GetOneItemCraftTime();
        bool running = row.IsCrafting;
        bool exists = clocks.TryGetValue(row, out var clock);
        if (!exists) clock = clocks.GetOrCreateValue(row);
        bool sameRecipe = exists && (ReferenceEquals(clock.Recipe, recipe)
            || (clock.Recipe != null && clock.Recipe.itemValueType == recipe.itemValueType
                && clock.Recipe.craftingArea == recipe.craftingArea
                && clock.Recipe.craftingTier == recipe.craftingTier));
        bool reset = !sameRecipe || clock.Owner != row.StartingEntityId
            || row.GetRecipeCount() > clock.Count || clock.Duration != duration
            || !running || !clock.Running || now < clock.LastTime;
        if (reset) clock.Remaining = raw;
        else
        {
            // Run the whole-batch label at one second per second even while the current
            // item is at zero awaiting its server tick. A multiplier decrement must not
            // reset this clock. Ignore normal tick-sized discrepancies in either direction.
            clock.Remaining = Math.Max(0f, clock.Remaining - (now - clock.LastTime));
            if (clock.Remaining - raw > 3f) clock.Remaining = raw;
        }
        clock.Recipe = recipe;
        clock.Owner = row.StartingEntityId;
        clock.Count = row.GetRecipeCount();
        clock.Duration = duration;
        clock.Running = running;
        clock.LastTime = now;
        return clock.Remaining;
    }

    [HarmonyPatch]
    public static class TimerTextPatch
    {
        static MethodBase TargetMethod() => AccessTools.Method(typeof(XUiC_RecipeStack), "updateRecipeData");

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var codes = instructions.ToList();
            var field = AccessTools.Field(typeof(XUiC_RecipeStack), "totalCraftTimeLeft");
            int patched = 0;
            for (int i = 0; i < codes.Count; i++)
            {
                if (!codes[i].LoadsField(field)) continue;
                codes.InsertRange(i + 1, new[] {
                    new CodeInstruction(OpCodes.Ldarg_0),
                    new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(SharedCountdownDisplay), nameof(Read)))
                });
                patched++;
                i += 2;
            }
            if (patched != 1) throw new InvalidOperationException("RecipeStack timer label field was not found exactly once.");
            return codes;
        }
    }
}
