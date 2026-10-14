# Workstation synchronization regression

Requires .NET 8 SDK and local game assemblies. Run from the repository root:

```powershell
dotnet build DrokkStorage.csproj -c Release -p:GamePath="D:\SteamLibrary\steamapps\common\7 Days To Die"
dotnet run --project tests/WorkstationSync/WorkstationSync.csproj -p:GamePath="D:\SteamLibrary\steamapps\common\7 Days To Die"
```

Use `-p:ModPath="/absolute/path/to/qiuxtaostorage.dll"` to test another compiled DLL. The harness checks the loaded DLL against that file using SHA-256. Game binaries are not distributed with the tests.

The harness applies the actual mod Harmony patches to the actual game assembly. It checks open/closed windows, pending acknowledgements, both network directions, the feature toggle, packet clocks and tools, output/queue serialization, remote tick suppression, exceptional reads, and scoped/nested close guards.

Additional regressions cover `100 + 1 + 2 + 3` duplicate recipe tasks, selected-task cancellation, FIFO order after cancellation, untouched foreign tasks, stale UI snapshots, actual add hooks and queue setters, paused/waiting display gates, and received fuel enabling/disabling the button. Isolated controller fixtures exercise UI hooks; the fuel query, visual Enabled setter and network send are intercepted because Unity and a network connection are absent.

Countdown cases also cover snapshot age, repeated old snapshots without backward display jumps, unchanged authoritative queue data, waiting rows, pause/resume, next-item resets and clock resets.

This is a headless protocol and patch regression, not an end-to-end multiplayer run. Rendering and UI refresh are intercepted; no world, player, loaded recipe database, or network transport is started. The test process uses HarmonyX 2.13 on .NET 8 because the game's bundled Mono Harmony runtime cannot patch under .NET 8. Production still builds against the game's bundled Harmony.
