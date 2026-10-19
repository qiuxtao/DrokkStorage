using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using HarmonyLib;

internal static class Program
{
    private static int assertions;
    private static bool? refreshAccessing;

    private static void Main()
    {
        // Register the resolver before the JIT needs any game types.
        string gamePath = Path.GetFullPath(Metadata("GamePath"));
        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            foreach (string directory in new[]
            {
                Path.Combine(gamePath, "7DaysToDie_Data", "Managed"),
                Path.Combine(gamePath, "Mods", "0_TFP_Harmony")
            })
            {
                string path = Path.Combine(directory, name.Name + ".dll");
                if (File.Exists(path)) return context.LoadFromAssemblyPath(path);
            }
            return null;
        };
        Run();
    }

    private static string Metadata(string key) => Assembly.GetExecutingAssembly()
        .GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == key).Value;

    private static void Assert(bool result, string message)
    {
        assertions++;
        if (!result) throw new Exception("FAIL: " + message);
    }

    private static bool SkipRender() => false;

    private static bool SuppressPatchLog(string __0, int __1)
    {
        Assert(__1 > 0, "transpiler injection installed: " + __0);
        return false;
    }

    private static void ObserveRefresh(TileEntityWorkstation te) =>
        refreshAccessing = te.IsUserAccessing();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Run()
    {
        string modPath = Path.GetFullPath(Metadata("ModPath"));
        byte[] expectedHash = SHA256.HashData(File.ReadAllBytes(modPath));
        byte[] actualHash = SHA256.HashData(File.ReadAllBytes(typeof(DrokkStorage).Assembly.Location));
        Assert(expectedHash.SequenceEqual(actualHash), "loaded mod matches ModPath");

        var harmony = new Harmony("drokkstorage.headless.regression");
        harmony.Patch(AccessTools.Method(typeof(TileEntityWorkstation), "UpdateVisible"),
            prefix: new HarmonyMethod(typeof(Program), nameof(SkipRender)));
        harmony.Patch(AccessTools.Method(typeof(DrokkStoragePatches.WorkstationLiveSync), "RefreshFromTileEntity"),
            prefix: new HarmonyMethod(typeof(Program), nameof(ObserveRefresh)));
        harmony.Patch(AccessTools.Method(typeof(DrokkStoragePatches), "LogPatchResult"),
            prefix: new HarmonyMethod(typeof(Program), nameof(SuppressPatchLog)));
        foreach (Type type in new[]
        {
            typeof(DrokkStoragePatches.TileEntityWorkstation_read_Patch),
            typeof(DrokkStoragePatches.TileEntityWorkstation_UpdateTick_Patch),
            typeof(DrokkStoragePatches.WorkstationWindowGroup_OnClose_Patch),
            typeof(DrokkStoragePatches.WorkstationWindowGroup_syncTEfromUI_Patch),
            typeof(DrokkStoragePatches.XUiC_WorkstationFuelGrid_Update_Patch),
            typeof(DrokkStoragePatches.XUiC_RecipeStack_Update_Patch),
            typeof(DrokkStoragePatches.XUiM_Workstation_SetRecipeQueueItems_Patch),
            typeof(DrokkStoragePatches.XUiC_CraftingQueue_AddRecipeToCraft_Patch),
            typeof(DrokkStoragePatches.XUiC_CraftingQueue_AddItemToRepair_Patch),
            typeof(DrokkStoragePatches.XUiC_RecipeStack_HandleOnPress_Patch),
            typeof(DrokkStoragePatches.ItemActionEntryCraft_OnActivated_QueueReservation_Patch),
            typeof(DrokkStoragePatches.XUiM_PlayerInventory_RemoveItems_Patch),
            typeof(DrokkStoragePatches.XUiC_WorkstationInputGrid_RemoveItems_Patch)
        })
        {
            harmony.CreateClassProcessor(type).Patch();
            Console.WriteLine("PATCH OK: " + type.Name);
        }

        GameTimer.m_Instance = (GameTimer)RuntimeHelpers.GetUninitializedObject(typeof(GameTimer));
        GameTimer.Instance.ticks = 1000;
        foreach (var mode in new[] { TileEntity.StreamModeRead.FromServer, TileEntity.StreamModeRead.FromClient })
        foreach (bool enabled in new[] { false, true })
        foreach (bool accessing in new[] { false, true })
        foreach (bool waiting in new[] { false, true })
            ReadCase(mode, enabled, accessing, waiting);
        Console.WriteLine("PASS: 16 actual binary network reads; clocks/tools, pending writes and feature toggle");

        RoundTripCases();
        ExceptionalReadCases();
        TickCases();
        CloseCases();
        QueueOperationCases();
        QueueUiCases(harmony);
        FuelButtonCases(harmony);
        SmoothCountdownCases();
        RemovalLedgerCases();
        AutoIgnitionCases(harmony);
        Console.WriteLine($"ALL PASS: {assertions} assertions; mod={modPath}; SHA256={Convert.ToHexString(actualHash)}; Harmony={typeof(Harmony).Assembly.GetName().Version}");
    }

    private static int queuedCount, ignitionCalls;
    private static bool CaptureQueueCount(int __1, ref bool __result)
    {
        queuedCount = __1;
        __result = true;
        return false;
    }
    private static bool CaptureIgnition() { ignitionCalls++; return false; }

    private static void AutoIgnitionCases(Harmony harmony)
    {
        var add = AccessTools.Method(typeof(XUiC_CraftingWindowGroup), "AddItemToQueue", new[] { typeof(Recipe), typeof(int) });
        var turnOn = AccessTools.Method(typeof(XUiC_WorkstationFuelGrid), "TurnOn");
        harmony.Patch(add, prefix: new HarmonyMethod(typeof(Program), nameof(CaptureQueueCount)));
        harmony.Patch(turnOn, prefix: new HarmonyMethod(typeof(Program), nameof(CaptureIgnition)));
        try
        {
            var reservationType = typeof(DrokkStoragePatches).GetNestedType("CraftQueueReservation", BindingFlags.NonPublic);
            foreach (bool workstation in new[] { true, false })
            foreach (int count in new[] { 2, 100 })
            {
                var group = (XUiC_CraftingWindowGroup)RuntimeHelpers.GetUninitializedObject(
                    workstation ? typeof(XUiC_WorkstationWindowGroup) : typeof(XUiC_CraftingWindowGroup));
                if (group is XUiC_WorkstationWindowGroup window)
                    window.fuelWindow = (XUiC_WorkstationFuelGrid)RuntimeHelpers.GetUninitializedObject(typeof(XUiC_WorkstationFuelGrid));
                var recipe = (Recipe)RuntimeHelpers.GetUninitializedObject(typeof(Recipe));
                var reservation = Activator.CreateInstance(reservationType, nonPublic: true);
                reservationType.GetField("Group").SetValue(reservation, group);
                reservationType.GetField("Recipe").SetValue(reservation, recipe);
                reservationType.GetField("CraftCount").SetValue(reservation, count);
                AccessTools.Field(typeof(DrokkStoragePatches), "pendingCraftQueue").SetValue(null, reservation);
                queuedCount = ignitionCalls = 0;
                Assert((bool)AccessTools.Method(typeof(DrokkStoragePatches), "CommitCraftQueue").Invoke(null, new object[] { group, recipe }), "commit succeeds");
                Assert(queuedCount == count, "ignition preserves explicit batch quantity");
                Assert(ignitionCalls == (workstation ? 1 : 0), "only workstation craft ignites fuel once");
            }
        }
        finally
        {
            harmony.Unpatch(add, HarmonyPatchType.All, harmony.Id);
            harmony.Unpatch(turnOn, HarmonyPatchType.All, harmony.Id);
        }
    }

    private static void RemovalLedgerCases()
    {
        var type = typeof(DrokkStoragePatches).GetNestedType("CraftRemovalTransaction", BindingFlags.NonPublic);
        var ledger = Activator.CreateInstance(type, nonPublic: true);
        var record = type.GetMethod("RecordNewItems");
        var value = new ItemValue(0, false);
        value.type = 1;
        var first = new ItemStack(value, 1);
        var second = new ItemStack(value.Clone(), 1);
        var list = new List<ItemStack> { first, second };
        record.Invoke(ledger, new object[] { list, false });
        var removed = (List<ItemStack>)type.GetField("Removed").GetValue(ledger);
        Assert(removed.Sum(stack => stack.count) == 2, "equal independent pulls both count toward payment/refund");
        record.Invoke(ledger, new object[] { list, false });
        Assert(removed.Sum(stack => stack.count) == 2, "re-reading the same removal records cannot double-refund");
    }

    private static TileEntityWorkstation NewWorkstation(bool accessing, bool waiting)
    {
        var ws = (TileEntityWorkstation)RuntimeHelpers.GetUninitializedObject(typeof(TileEntityWorkstation));
        ws.fuel = Array.Empty<ItemStack>();
        ws.input = Array.Empty<ItemStack>();
        ws.output = Array.Empty<ItemStack>();
        ws.tools = new ItemStack[1];
        ws.toolsNet = Array.Empty<ItemStack>();
        ws.lastInput = Array.Empty<ItemStack>();
        ws.queue = Array.Empty<RecipeQueueItem>();
        ws.currentMeltTimesLeft = new[] { 10f, 10f };
        ws.isModuleUsed = new bool[5];
        ws.currentBurnTimeLeft = 10;
        ws.lastTickTime = 7;
        ws.isBurning = false;
        ws.lockHandleWaitingFor = waiting ? (byte)1 : byte.MaxValue;
        ws.SetUserAccessing(accessing);
        return ws;
    }

    private static byte[] Payload()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(0); writer.Write(0); writer.Write(0); // Network base TileEntity position.
        writer.Write((byte)50); // Workstation serialization version.
        for (int i = 0; i < 5; i++) writer.Write((byte)0); // Fuel/input/tools/output/queue.
        writer.Write((short)0); // Completed recipes.
        writer.Write(true); writer.Write(75f);
        writer.Write((byte)2); writer.Write(9f); writer.Write(8f);
        writer.Write(true); writer.Write((ulong)40); writer.Write((byte)0); // Placed/elapsed/lastInput.
        return stream.ToArray();
    }

    private static void ReadCase(TileEntity.StreamModeRead mode, bool enabled, bool accessing, bool waiting)
    {
        DrokkStorage.config.multiViewersEnabled = enabled;
        DrokkStorage.config.isDebug = false;
        var ws = NewWorkstation(accessing, waiting);
        refreshAccessing = null;
        using var reader = new PooledBinaryReader();
        reader.SetBaseStream(new MemoryStream(Payload()));
        ws.read(reader, mode);

        bool bypass = enabled && accessing && !waiting;
        bool stateAccepted = mode == TileEntity.StreamModeRead.FromClient || !accessing || bypass;
        string context = $"{mode},enabled={enabled},open={accessing},waiting={waiting}";
        Assert(ws.IsUserAccessing() == accessing, "restore access: " + context);
        Assert(ws.isBurning == stateAccepted, "burn flag: " + context);
        Assert(ws.currentBurnTimeLeft == (stateAccepted ? 75 : 10), "burn clock: " + context);
        Assert(ws.currentMeltTimesLeft[0] == (stateAccepted ? 9 : 10), "melt clock: " + context);
        Assert(ws.lastTickTime == (stateAccepted ? 960UL : 7UL), "tick epoch: " + context);
        Assert(ws.tools.Length == (!accessing || bypass ? 0 : 1), "apply tools: " + context);
        Assert(reader.BaseStream.Position == reader.BaseStream.Length, "consume exact packet: " + context);
        if (enabled) Assert(refreshAccessing == accessing, "restore access BEFORE UI refresh: " + context);
    }

    private static void RoundTripCases()
    {
        // One minimal item entry lets ItemValue.Write serialize a nonempty stack without
        // loading the game's item database. RecipeQueueItem uses its real wire format,
        // but the recipe itself is absent because no crafting database is loaded.
        ItemClass.list = new ItemClass[2];
        ItemClass.list[1] = (ItemClass)RuntimeHelpers.GetUninitializedObject(typeof(ItemClass));
        var source = NewWorkstation(false, false);
        source.tools = Array.Empty<ItemStack>();
        source.output = new[] { new ItemStack(new ItemValue { type = 1 }, 7) };
        source.queue = new[] { new RecipeQueueItem
        {
            Multiplier = 3, IsCrafting = true, CraftingTimeLeft = 12, OneItemCraftTime = 20,
            StartingEntityId = 123, Quality = 4
        } };

        foreach (var mode in new[] { TileEntity.StreamModeRead.FromServer, TileEntity.StreamModeRead.FromClient })
        foreach (bool enabled in new[] { false, true })
        foreach (bool accessing in new[] { false, true })
        foreach (bool waiting in new[] { false, true })
        {
            DrokkStorage.config.multiViewersEnabled = enabled;
            var target = NewWorkstation(accessing, waiting);
            target.output = new[] { new ItemStack(new ItemValue { type = 1 }, 2) };
            target.queue = new[] { new RecipeQueueItem { Multiplier = 1, CraftingTimeLeft = 5 } };
            using var stream = new MemoryStream();
            var writer = new PooledBinaryWriter();
            writer.SetBaseStream(stream);
            source.write(writer, mode == TileEntity.StreamModeRead.FromServer
                ? TileEntity.StreamModeWrite.ToClient : TileEntity.StreamModeWrite.ToServer);
            writer.Flush();
            stream.Position = 0;
            using var reader = new PooledBinaryReader();
            reader.SetBaseStream(stream);
            target.read(reader, mode);

            string context = $"{mode},enabled={enabled},open={accessing},waiting={waiting}";
            bool acceptItems = !accessing || (enabled && !waiting);
            Assert(target.output[0].itemValue.type == 1, "output item identity: " + context);
            Assert(target.output[0].count == (acceptItems ? 7 : 2), "output quantity: " + context);
            Assert(target.queue[0].Multiplier == (waiting ? 1 : 3), "queue multiplier: " + context);
            Assert(target.queue[0].CraftingTimeLeft == (waiting ? 5 : 12), "queue time: " + context);
            Assert(target.queue[0].IsCrafting == !waiting, "queue crafting state: " + context);
            Assert(target.IsUserAccessing() == accessing, "roundtrip access restored: " + context);
            Assert(stream.Position == stream.Length, "roundtrip packet completely consumed: " + context);
        }
        Console.WriteLine("PASS: 16 actual game write/read roundtrips; output identity/quantity and queue echo guard");
    }

    private static void ExceptionalReadCases()
    {
        foreach (var mode in new[] { TileEntity.StreamModeRead.FromServer, TileEntity.StreamModeRead.FromClient })
        {
            DrokkStorage.config.multiViewersEnabled = true;
            var ws = NewWorkstation(true, false);
            using var reader = new PooledBinaryReader();
            reader.SetBaseStream(new MemoryStream(new byte[12]));
            bool threw = false;
            try { ws.read(reader, mode); }
            catch (EndOfStreamException) { threw = true; }
            Assert(threw, "original malformed packet exception preserved");
            Assert(ws.IsUserAccessing(), "access flag restored on exceptional " + mode);
        }

        var persisted = NewWorkstation(true, false);
        DrokkStoragePatches.TileEntityWorkstation_read_Patch.Prefix(
            persisted, TileEntity.StreamModeRead.Persistency, out bool changed);
        Assert(!changed && persisted.IsUserAccessing(), "persistency prefix leaves access unchanged");
        Console.WriteLine("PASS: real Harmony finalizers restore access on malformed packets and preserve errors");
    }

    private static void TickCases()
    {
        foreach (bool enabled in new[] { false, true })
        foreach (bool remote in new[] { false, true })
        {
            DrokkStorage.config.multiViewersEnabled = enabled;
            var world = (FakeWorld)RuntimeHelpers.GetUninitializedObject(typeof(FakeWorld));
            world.Remote = remote;
            Assert(DrokkStoragePatches.TileEntityWorkstation_UpdateTick_Patch.Prefix(world)
                == (!enabled || !remote), "tick ownership matrix");
            if (enabled && remote)
            {
                var ws = NewWorkstation(true, false);
                ws.UpdateTick(world);
                Assert(ws.currentBurnTimeLeft == 10 && ws.lastTickTime == 7, "real remote UpdateTick suppressed");
            }
        }
        Console.WriteLine("PASS: actual remote tick suppressed; local/disabled prefixes preserve vanilla path");
    }

    private static void CloseCases()
    {
        var a = (XUiC_WorkstationWindowGroup)RuntimeHelpers.GetUninitializedObject(typeof(XUiC_WorkstationWindowGroup));
        var b = (XUiC_WorkstationWindowGroup)RuntimeHelpers.GetUninitializedObject(typeof(XUiC_WorkstationWindowGroup));
        foreach (bool enabled in new[] { false, true })
        {
            DrokkStorage.config.multiViewersEnabled = enabled;
            DrokkStoragePatches.WorkstationLiveSync.ClosingWindow = null;
            Assert(DrokkStoragePatches.WorkstationWindowGroup_syncTEfromUI_Patch.Prefix(a) == !enabled, "automatic UI snapshot writes only in vanilla mode");
            DrokkStoragePatches.WorkstationWindowGroup_OnClose_Patch.Prefix(a, out var previous);
            Assert(DrokkStoragePatches.WorkstationWindowGroup_syncTEfromUI_Patch.Prefix(a)
                == !enabled, "close blocks only shared snapshot");
            Assert(DrokkStoragePatches.WorkstationWindowGroup_syncTEfromUI_Patch.Prefix(b) == !enabled, "shared snapshots blocked outside close too");
            DrokkStoragePatches.WorkstationWindowGroup_OnClose_Patch.Prefix(b, out var nestedPrevious);
            DrokkStoragePatches.WorkstationWindowGroup_OnClose_Patch.Finalizer(nestedPrevious);
            Assert(ReferenceEquals(DrokkStoragePatches.WorkstationLiveSync.ClosingWindow, a), "nested close restores previous window");
            DrokkStoragePatches.WorkstationWindowGroup_OnClose_Patch.Finalizer(previous);
            Assert(DrokkStoragePatches.WorkstationLiveSync.ClosingWindow == null, "closing state resets");
        }
        Console.WriteLine("PASS: automatic snapshots blocked in shared mode; vanilla path and nested cleanup intact");
    }

    private static void QueueOperationCases()
    {
        DrokkStorage.config.multiViewersEnabled = true;
        var recipe = (Recipe)RuntimeHelpers.GetUninitializedObject(typeof(Recipe));
        recipe.itemValueType = 1;
        RecipeQueueItem Row(int count, float seconds, int owner = 123) => new RecipeQueueItem
        {
            Recipe = recipe, Multiplier = (short)count, StartingEntityId = owner,
            CraftingTimeLeft = seconds, OneItemCraftTime = 10, IsCrafting = true
        };
        var te = NewWorkstation(false, false);
        te.queue = new[] { new RecipeQueueItem(), new RecipeQueueItem(), new RecipeQueueItem(), Row(100, 8) };
        foreach (int quantity in new[] { 1, 2, 3 })
        {
            int slot = Array.FindLastIndex(te.queue, it => it.Recipe == null);
            var ui = te.queue.ToArray();
            ui[slot] = Row(quantity, 10);
            DrokkStoragePatches.WorkstationLiveSync.BeginAdd(slot);
            RecipeQueueItem[] result;
            try { result = DrokkStoragePatches.WorkstationLiveSync.MergeWithTileEntity(te, ui); }
            finally { DrokkStoragePatches.WorkstationLiveSync.EndAdd(); }
            Assert(result[slot].Multiplier == quantity, "same-recipe new count remains " + quantity);
            Assert(result[slot].CraftingTimeLeft == 10 && !result[slot].IsCrafting, "waiting task keeps full time and never crafts");
            Assert(result[3].Multiplier == 100 && result[3].CraftingTimeLeft == 8, "active task count/progress preserved");
            Assert(!ReferenceEquals(result[3], te.queue[3]), "merge clones existing tasks");
            te.queue = result;
        }
        Assert(te.queue.Sum(it => it.Multiplier) == 106, "100+1+2+3 retains exact total quantity");
        DrokkStoragePatches.WorkstationLiveSync.BeginCancel(2, recipe, 123, 1);
        try { te.queue = DrokkStoragePatches.WorkstationLiveSync.MergeWithTileEntity(te, te.queue); }
        finally { DrokkStoragePatches.WorkstationLiveSync.EndCancel(); }
        Assert(te.queue[0].Multiplier == 0 && te.queue[3].Multiplier == 100,
            "cancel duplicate recipe removes selected 1-item task, not active 100-item task");
        Assert(te.queue[2].Multiplier == 2 && te.queue[1].Multiplier == 3, "cancel preserves FIFO order of other waiting duplicates");
        var stale = te.queue.Select(it => Row(100, 1)).ToArray();
        var untouched = DrokkStoragePatches.WorkstationLiveSync.MergeWithTileEntity(te, stale);
        Assert(untouched.Sum(it => it.Multiplier) == 105, "stale UI cannot add, resurrect or resize a task without an explicit operation");
        DrokkStoragePatches.WorkstationLiveSync.BeginCancel(3, recipe, 123, 100);
        DrokkStoragePatches.XUiC_RecipeStack_HandleOnPress_Patch.Finalizer(true);
        Assert(DrokkStoragePatches.WorkstationLiveSync.MergeWithTileEntity(te, stale)[3].Multiplier == 100,
            "exceptional cancellation cleanup cannot leak into the next queue operation");
        te.queue[1].StartingEntityId = 456;
        DrokkStoragePatches.WorkstationLiveSync.BeginAdd(0);
        try
        {
            var ui = te.queue.ToArray(); ui[0] = Row(7, 10);
            var result = DrokkStoragePatches.WorkstationLiveSync.MergeWithTileEntity(te, ui);
            Assert(result[1].StartingEntityId == 456 && result[1].Multiplier == 3,
                "other player's task preserved while adding identical recipe");
            Assert(result[0].Multiplier == 7 && result[2].Multiplier == 2,
                "new add remains behind all older waiting tasks after cancellation");
        }
        finally { DrokkStoragePatches.WorkstationLiveSync.EndAdd(); }
        var rows = te.queue.Select(it => (XUiC_RecipeStack)RuntimeHelpers.GetUninitializedObject(typeof(XUiC_RecipeStack))).ToArray();
        foreach (var row in rows) { row.craftingTimeLeft = 1; row.IsCrafting = true; }
        te.lastTickTime = GameTimer.Instance.ticks;
        DrokkStoragePatches.WorkstationLiveSync.CorrectQueueDisplay(te, rows, resetProgress: true);
        Assert(rows[3].IsCrafting && rows[3].craftingTimeLeft == 8, "server corrects active display drift");
        Assert(!rows[1].IsCrafting && rows[1].craftingTimeLeft == 10, "server corrects waiting display drift");
        te.isModuleUsed[3] = true; te.isBurning = false;
        DrokkStoragePatches.WorkstationLiveSync.CorrectQueueDisplay(te, rows);
        Assert(!rows[3].IsCrafting, "fuel pause stops display countdown");
        te.isBurning = true;
        DrokkStoragePatches.WorkstationLiveSync.CorrectQueueDisplay(te, rows);
        Assert(rows[3].IsCrafting && !rows[1].IsCrafting, "resume activates only the head task");
        var fuel = (XUiC_WorkstationFuelGrid)RuntimeHelpers.GetUninitializedObject(typeof(XUiC_WorkstationFuelGrid));
        DrokkStoragePatches.XUiC_WorkstationFuelGrid_Update_Patch.AutoCycle(fuel);
        DrokkStoragePatches.XUiC_WorkstationFuelGrid_Update_Patch.AutoTurnOff(fuel);
        Assert(!fuel.isOn, "shared fuel automatic actions do not invoke vanilla mutating paths");
        Console.WriteLine("PASS: duplicate task counts/times, exact cancellation, foreign tasks, stale UI, fuel pause/resume and automatic-write guards");
    }

    private static void QueueUiCases(Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(TileEntityWorkstation), "setModified"),
            prefix: new HarmonyMethod(typeof(Program), nameof(SkipRender)));
        DrokkStorage.config.multiViewersEnabled = true;
        var te = NewWorkstation(false, false);
        var recipe = (Recipe)RuntimeHelpers.GetUninitializedObject(typeof(Recipe));
        recipe.itemValueType = 1;
        te.queue = new[] { new RecipeQueueItem(), new RecipeQueueItem(), new RecipeQueueItem(),
            new RecipeQueueItem { Recipe = recipe, Multiplier = 100, CraftingTimeLeft = 8,
                OneItemCraftTime = 10, StartingEntityId = 123 } };
        var window = (XUiC_WorkstationWindowGroup)RuntimeHelpers.GetUninitializedObject(typeof(XUiC_WorkstationWindowGroup));
        window.WorkstationData = new XUiM_Workstation(te);
        var group = (XUiWindowGroup)RuntimeHelpers.GetUninitializedObject(typeof(XUiWindowGroup));
        group.Controller = window;
        var queue = (XUiC_CraftingQueue)RuntimeHelpers.GetUninitializedObject(typeof(XUiC_CraftingQueue));
        queue.windowGroup = group;
        window.craftingQueue = queue;
        var rows = Enumerable.Range(0, 4).Select(_ => (XUiC_RecipeStack)RuntimeHelpers.GetUninitializedObject(typeof(XUiC_RecipeStack))).ToArray();
        queue.queueItems = rows.Cast<XUiController>().ToArray();
        foreach (var row in rows) { row.windowGroup = group; row.oneItemCraftTime = 10; row.StartingEntityId = 123; }
        rows[3].recipe = recipe; rows[3].recipeCount = 100; rows[3].craftingTimeLeft = 8;
        var output = te.output;
        bool success = false;
        Assert(DrokkStoragePatches.XUiC_CraftingQueue_AddRecipeToCraft_Patch.Prefix(queue, ref success, out var before), "capture genuine add intent");
        rows[2].recipe = recipe; rows[2].recipeCount = 1; rows[2].craftingTimeLeft = 10;
        DrokkStoragePatches.XUiC_CraftingQueue_AddRecipeToCraft_Patch.Postfix(queue, recipe, true, before);
        Assert(te.queue[3].Multiplier == 100 && te.queue[2].Multiplier == 1, "actual add postfix and queue setter retain 100+1");
        Assert(te.queue[2].CraftingTimeLeft == 10, "actual queue setter retains new full clock");
        Assert(ReferenceEquals(output, te.output), "queue edit never overwrites output grid");
        rows[2].IsCrafting = true;
        DrokkStoragePatches.XUiC_RecipeStack_Update_Patch.Prefix(rows[2]);
        Assert(!rows[2].IsCrafting, "waiting row cannot subtract time even if vanilla activated it");
        te.isModuleUsed[3] = true; te.isBurning = false;
        rows[3].IsCrafting = true;
        DrokkStoragePatches.XUiC_RecipeStack_Update_Patch.Prefix(rows[3]);
        Assert(!rows[3].IsCrafting, "head row cannot subtract time while server is paused");
        te.isBurning = true;
        DrokkStoragePatches.XUiC_RecipeStack_Update_Patch.Prefix(rows[3]);
        Assert(rows[3].IsCrafting, "head resumes when server is burning");
        Assert(DrokkStoragePatches.WorkstationLiveSync.CanCancel(te.queue, 2, rows[2]), "selected duplicate can cancel");
        rows[2].recipeCount = 100;
        Assert(!DrokkStoragePatches.WorkstationLiveSync.CanCancel(te.queue, 2, rows[2]), "stale duplicate count cannot refund");
        rows[2].recipeCount = 1;
        rows[3].recipe = null; rows[3].recipeCount = 0;
        DrokkStoragePatches.WorkstationLiveSync.BeginCancel(3, recipe, 123, 100);
        try { DrokkStoragePatches.WorkstationLiveSync.SyncQueueToTileEntity(window); }
        finally { DrokkStoragePatches.WorkstationLiveSync.EndCancel(); }
        Assert(te.queue[3].Multiplier == 1 && te.queue[3].CraftingTimeLeft == 10,
            "cancel active duplicate promotes next task with its own complete clock");
        Assert(te.lastTickTime == GameTimer.Instance.ticks, "new head does not inherit elapsed time from cancelled head");
        te.queue[0] = new RecipeQueueItem { Recipe = recipe, Multiplier = 1 };
        te.queue[1] = new RecipeQueueItem { Recipe = recipe, Multiplier = 1 };
        te.queue[2] = new RecipeQueueItem { Recipe = recipe, Multiplier = 1 };
        success = true;
        Assert(!DrokkStoragePatches.XUiC_CraftingQueue_AddRecipeToCraft_Patch.Prefix(queue, ref success, out before) && !success,
            "full authoritative queue rejects stale UI add");
        DrokkStorage.config.multiViewersEnabled = false;
        success = true;
        Assert(DrokkStoragePatches.XUiC_CraftingQueue_AddRecipeToCraft_Patch.Prefix(queue, ref success, out before), "disabled feature preserves vanilla add");
        rows[2].IsCrafting = true;
        DrokkStoragePatches.XUiC_RecipeStack_Update_Patch.Prefix(rows[2]);
        Assert(rows[2].IsCrafting, "disabled feature preserves vanilla display");
        Console.WriteLine("PASS: actual add hook/setter, no output overwrite, waiting/head display gates, stale cancellation and queue-full checks");
    }

    private static bool availableFuel;
    private static bool ReadFuelAvailability(ref bool __result) { __result = availableFuel; return false; }
    private static bool SetEnabledForHeadlessView(XUiView __instance, bool value) { __instance.enabled = value; return false; }
    private static void FuelButtonCases(Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(XUiC_WorkstationFuelGrid), "hasAnyFuel"),
            prefix: new HarmonyMethod(typeof(Program), nameof(ReadFuelAvailability)));
        harmony.Patch(AccessTools.PropertySetter(typeof(XUiView), "Enabled"),
            prefix: new HarmonyMethod(typeof(Program), nameof(SetEnabledForHeadlessView)));
        DrokkStorage.config.multiViewersEnabled = true;
        var window = (XUiC_WorkstationWindowGroup)RuntimeHelpers.GetUninitializedObject(typeof(XUiC_WorkstationWindowGroup));
        var fuel = (XUiC_WorkstationFuelGrid)RuntimeHelpers.GetUninitializedObject(typeof(XUiC_WorkstationFuelGrid));
        var button = (XUiV_Button)RuntimeHelpers.GetUninitializedObject(typeof(XUiV_Button));
        var controller = (XUiController)RuntimeHelpers.GetUninitializedObject(typeof(XUiController));
        controller.ViewComponent = button; fuel.button = controller; window.fuelWindow = fuel;
        var te = NewWorkstation(false, false);
        DrokkStoragePatches.WorkstationLiveSync.WindowOpened(window);
        try
        {
            var refresh = AccessTools.Method(typeof(DrokkStoragePatches.WorkstationLiveSync), "RefreshFuelControls");
            foreach (bool burning in new[] { false, true })
            foreach (bool fuelPresent in new[] { false, true })
            foreach (float remaining in new[] { 0f, 5f })
            {
                te.isBurning = burning; te.currentBurnTimeLeft = remaining; availableFuel = fuelPresent;
                button.enabled = !(fuelPresent || remaining > 0);
                refresh.Invoke(null, new object[] { te });
                Assert(button.Enabled == (fuelPresent || remaining > 0), "button availability follows received fuel state");
                Assert(fuel.isOn == burning, "received burn flag refreshes controls without a toggle write");
            }
        }
        finally { DrokkStoragePatches.WorkstationLiveSync.WindowClosed(window); }
        Console.WriteLine("PASS: received fuel enables/disables button, remaining burn time and remote on/off control state");
    }
    private static void SmoothCountdownCases()
    {
        DrokkStorage.config.multiViewersEnabled = true;
        var recipe = (Recipe)RuntimeHelpers.GetUninitializedObject(typeof(Recipe));
        recipe.itemValueType = 1;
        var te = NewWorkstation(false, false);
        te.queue = new[] {
            new RecipeQueueItem { Recipe = recipe, Multiplier = 1, CraftingTimeLeft = 60, OneItemCraftTime = 60 },
            new RecipeQueueItem { Recipe = recipe, Multiplier = 1, CraftingTimeLeft = 43, OneItemCraftTime = 60 }
        };
        var rows = Enumerable.Range(0, 2).Select(_ => (XUiC_RecipeStack)RuntimeHelpers.GetUninitializedObject(typeof(XUiC_RecipeStack))).ToArray();
        rows[1].IsCrafting = true; rows[1].craftingTimeLeft = 42;
        te.lastTickTime = GameTimer.Instance.ticks - 20;
        var authoritative = te.queue[1];
        DrokkStoragePatches.WorkstationLiveSync.CorrectQueueDisplay(te, rows);
        Assert(rows[1].craftingTimeLeft == 42, "43-second snapshot aged one second displays 42 without a backward flash");
        Assert(rows[0].craftingTimeLeft == 60 && !rows[0].IsCrafting, "waiting task never subtracts snapshot age");
        Assert(authoritative.CraftingTimeLeft == 43 && ReferenceEquals(authoritative, te.queue[1]), "display estimate never mutates authoritative queue");
        te.lastTickTime = GameTimer.Instance.ticks;
        for (int i = 0; i < 8; i++)
        {
            rows[1].craftingTimeLeft -= 0.1f;
            float previous = rows[1].craftingTimeLeft;
            DrokkStoragePatches.WorkstationLiveSync.CorrectQueueDisplay(te, rows);
            Assert(rows[1].craftingTimeLeft <= previous, "repeated older snapshots never increase a running display");
        }
        te.queue[1].CraftingTimeLeft = 40;
        DrokkStoragePatches.WorkstationLiveSync.CorrectQueueDisplay(te, rows);
        Assert(rows[1].craftingTimeLeft == 40, "newer server progress advances display");
        te.isModuleUsed[3] = true; te.isBurning = false;
        te.queue[1].CraftingTimeLeft = 41;
        DrokkStoragePatches.WorkstationLiveSync.CorrectQueueDisplay(te, rows);
        Assert(!rows[1].IsCrafting && rows[1].craftingTimeLeft == 41, "pause restores exact server remaining time");
        te.isBurning = true;
        DrokkStoragePatches.WorkstationLiveSync.CorrectQueueDisplay(te, rows);
        Assert(rows[1].IsCrafting && rows[1].craftingTimeLeft == 41, "resume starts from server pause checkpoint");
        rows[1].craftingTimeLeft = 0;
        te.queue[1].CraftingTimeLeft = 60;
        DrokkStoragePatches.WorkstationLiveSync.CorrectQueueDisplay(te, rows, resetProgress: true);
        Assert(rows[1].craftingTimeLeft == 60, "next item may reset countdown to its full duration");
        te.lastTickTime = GameTimer.Instance.ticks + 20;
        te.queue[1].CraftingTimeLeft = 50;
        DrokkStoragePatches.WorkstationLiveSync.CorrectQueueDisplay(te, rows, resetProgress: true);
        Assert(rows[1].craftingTimeLeft == 50, "clock reset cannot underflow snapshot age");
        te.lastTickTime = 0;
        te.queue[1].CraftingTimeLeft = 0.5f;
        DrokkStoragePatches.WorkstationLiveSync.CorrectQueueDisplay(te, rows, resetProgress: true);
        Assert(rows[1].craftingTimeLeft == 0, "display never becomes negative while waiting for server completion");
        Console.WriteLine("PASS: aged snapshots, monotonic running countdown, waiting tasks, pause/resume and next-item reset");
    }
    private sealed class FakeWorld : World
    {
        public bool Remote;
        public override bool IsRemote() => Remote;
    }
}
