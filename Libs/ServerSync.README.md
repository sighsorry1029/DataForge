# Bundled ServerSync: Valheim 1.0.7 adaptation

This is the existing DataForge bundled fork, not an upstream ServerSync upgrade.
The source binary is preserved in commit `933dc9f030915be4ddb749861c6ec7b81fa4628b`, path `Libs/ServerSync.dll`.

| Binary | SHA-256 |
| --- | --- |
| Before | `166956302a294e224474b26f4c7d58409084ad3f48bd0af1feb7551f229c8f60` |
| After | `bd7bbcca2a9e72fe7d5315865f76dec6bb0eb902e93c55e06afab60848d04237` |

Valheim changed `ZRoutedRpc.Everybody` from a static `long` field to `const long = 0`.
Three `ldsfld` instructions were changed to `ldc.i8 0` in:

- `ConfigSync.sendZPackage`
- `ConfigSync.AddConfigEntry`'s settings-change closure
- `ConfigSync.AddCustomValue`'s value-change closure

RPC names, payloads, version negotiation, permission checks, fragmentation, buffered login and assembly identity are preserved. The original and adapted metadata have identical type/member sets and attributes; only these three method body hashes differ (ignoring metadata tokens). Game DLLs are not patched.

To reproduce, extract that exact original binary from Git to a temporary file and run:

```powershell
dotnet run --project tests/DataForge.GameCompatibilityChecks -- patch-serversync <original-ServerSync.dll> <adapted-output.dll> <original-1.0.7-assembly_valheim.dll>
```

The tool pins Mono.Cecil 0.11.6, verifies the source SHA-256, the target constant's original metadata and exactly three field loads before writing. It rejects a different library or game contract instead of silently applying an unreviewed patch.

Both client 1.0.7 (Steam 25185596) and dedicated server 1.0.7 (25185644) pass the final merged DLL's static member/patch checks. Multiplayer, crossplay and Unity execution remain separate runtime checks. Do not apply this binary transformation to other mods' ServerSync copies without reviewing their own versions and policies.
