# Yet Another FATE

## Build and tests

- Requires .NET 10 SDK and Dalamud API 15 development files. Production targets Windows/x64.
- Plugin build: `dotnet build YetAnotherFATE/YetAnotherFATE.csproj --configuration Release -p:Platform=x64`.
- Release DLL: `D:\Coding\C#\Yet_Another_FATE\YetAnotherFATE\bin\x64\Release\YetAnotherFATE.dll`.
- Full solution build: `dotnet build YetAnotherFATE.sln --configuration Release -p:Platform=x64`.
- Parity harness: `dotnet run --project tests/parity-tests.csproj --configuration Release`.
- Integration harness on PowerShell:
  `$env:DALAMUD_HOME = "$env:APPDATA\XIVLauncher\addon\Hooks\dev"`
  `dotnet run --project tests/integration-tests.csproj --configuration Release -p:Platform=x64 -p:DalamudHome="$env:DALAMUD_HOME"`
- Harnesses are console executables, not `dotnet test` projects; no filter exists. Integration tests need `Dalamud.dll`, `FFXIVClientStructs.dll`, `Lumina.dll`, and `Lumina.Excel.dll` under `DALAMUD_HOME`.
- No repository lint, format, code-generation command, or CI workflow exists. Preserve `YetAnotherFATE/packages.lock.json` and ignore `reference/`, `bin/`, `obj/`, `.vs/`, and test output.

## Architecture and safety

- `YetAnotherFATE/Plugin.cs` is the Microsoft DI composition root.
- `Modules/Automator/AutomatorModule.cs` drives framework updates; `Chains/AutomatorChain.cs` coordinates lifecycle; `Modules/Automator/Handlers/` owns state-specific behavior.
- `Services/` contains travel, movement, combat, zone, retainer, and companion boundaries. `Commands/`, `Windows/`, and `Renderers/` are the user-facing boundary.
- `Data/Combat/**/*.json` are source assets copied to output; BossMod preset names and paths are runtime contracts. Build injects `YetAnotherFATE.DevBuild` timestamp metadata.

Preserve IPC capability checks, cancellation, backoff, ownership, and restoration. Never overwrite user-owned BossMod presets or cancel provider work not owned by YAF. vnavmesh is effectively required for travel; other providers are optional. Live teleport, loading, reward UI, buff timing, and provider behavior require in-game smoke testing.
