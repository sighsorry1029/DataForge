# DataForge checks

Build and deploy the final merged Debug DLL:

```powershell
dotnet build DataForge.sln -c Debug -p:DeployToGame=true
dotnet run --project tests/DataForge.LogicChecks -c Debug --no-build
dotnet run --project tests/DataForge.ApiChecks -c Debug --no-build -- bin/Debug/DataForge.dll
```

The two compatibility tools are standalone projects so ordinary pure-logic tests do not acquire game/Harmony dependencies. Use original game Managed directories, including preserved client and dedicated-server snapshots; never point these checks at compile-only/publicized references.

```powershell
$managed = 'C:\Program Files (x86)\Steam\steamapps\common\Valheim\valheim_Data\Managed'
$core = 'C:\Program Files (x86)\Steam\steamapps\common\Valheim\BepInEx\core'
dotnet run --project tests/DataForge.GameCompatibilityChecks -- check bin/Debug/DataForge.dll $managed $core
dotnet build tests/DataForge.TranspilerChecks -c Debug
& tests/DataForge.TranspilerChecks/bin/Debug/net48/DataForge.TranspilerChecks.exe bin/Debug/DataForge.dll $managed $core
dotnet run --project tests/DataForge.DomainChecks -c Debug -- bin/Debug/DataForge.dll $managed $core
```

- `GameCompatibilityChecks` uses Mono.Cecil 0.11.6 under .NET 8. It resolves direct game method/field instructions, checks literal/static mismatches, and checks explicit class/method Harmony target signatures and argument/field/result injection. It reports dynamic optional-mod targets separately. It also contains the reproducible, source-hash-pinned ServerSync adaptation described in `Libs/ServerSync.README.md`.
- `TranspilerChecks` uses .NET Framework 4.8 and the installed Harmony/Cecil pair. It reads original game IL symbolically and executes DataForge's two transpilers, checks the four craft calls, unchanged upgrader/refund calls, one capacity replacement, label preservation and replacement stack signatures including `cheated`/`pickedUp`/`dropIfFullInv`. It initializes the new cached reflection accessors against original game declarations. It does not execute emitted game code or fake Unity instances. The installed Harmony/MonoMod does not support this test under .NET 8; keep the separate net48 runner.

`DomainChecks` runs the merged YAML converter and plain managed game data on .NET 8.
It checks legacy resource tuples, metadata omission versus explicit false/zero,
rebuild and reference-copy round trips, and invalid input rejection. It constructs
the codec without manager initialization: the installed Harmony needs the net48
runner while the merged YAML library needs default-interface-method support.
Prefab lookup, baseline capture from Unity objects, live reload and gameplay are
not exercised by this process.

The pattern-rejection logging path requires the game's ThreadingHelper singleton. Exercise altered patterns/other transpilers in-game; the standalone runner deliberately does not initialize a fake BepInEx/Unity process. Static checks do not prove Harmony patch installation, per-frame performance, native Unity calls, UI appearance or network behavior.

## Valheim 1.0.7 runtime checklist

1. Restart the game after Debug deployment. Confirm DataForge initializes without `ZRoutedRpc.Everybody`, ambiguous Harmony target or missing-signature errors. Existing processes retain the previously loaded DLL.
2. In a disposable world, open the new build menu with mouse and controller: confirm DataForge does not add a top-level piece list, and check configured entries through the existing Categories list. Move a same-name mod category such as `Furniture: GB_Parchment_Tool`; its pieces must remain in the one existing Furniture category after repeated PieceManager refreshes, and `pieceCategory.reference.yml` must still describe the pre-DataForge source table and category. Verify category order/custom labels, search, favorites, simple tools, comfort badges and both hover highlights. Change language and configuration while open; close/reopen, switch tools and reconnect. Pooled buttons must not retain another piece's badge/highlight.
3. Craft one item and batches with integer/fractional acquisition multipliers, full inventories, partial stacks and quality/world-level differences. Check capacity and output use one reservation, ingredients are consumed once, upgrades and all three upgrader outcomes remain vanilla, and cheated state is carried through. Take several cooked items and verify slot removal, output count and cheated state.
4. Test client/host and dedicated server, Steam and crossplay: initial/reconnect sync, non-admin changes to locked settings, icon source authority, large/fragmented payloads, duplicate/late messages and disconnect cleanup. Check item/piece/status/recipe clone restoration and API notifications through a second world.
5. Repeat with the optional mods actually in use (MagicPlugin, VNEI, VeiledRecipes, Jotunn/PieceManager). Other mods' old-game binaries are not fixed by DataForge's bundled ServerSync patch. Check Harmony ordering and no duplication after repeated menu/world lifecycles.

The packaging manifest requires BepInExPack Valheim 5.4.2350 as specified by the project workflow; changing that manifest does not replace an installed BepInEx runtime.

## Source ownership policy checks (2026-09-10)

Reference-section ownership now retains plugin GUIDs internally, rejects ambiguous embedded-resource owners and partial-token guesses, and detects same-name bundle reloads using bundle instance identity plus resource readiness. Different GUIDs with the same display name no longer suppress an asset-name collision. The existing reference YAML section naming and public clone API format are unchanged; more uncertain entries can appear under Unknown / Untracked.

`AssetOwnerMatching.cs` is identically vendored in ServeYouRight and DataForge so the independently installed mods need no shared runtime DLL. Keep both copies and the collision tests synchronized when changing this policy. LogicChecks includes eight ownership assertions in one additional test case. DataForge's reference-generation catalog retains its own lifecycle; ServeYouRight's gameplay filter and clone bridge are separate consumers with different policies.
