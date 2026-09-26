# Death-return routing audit

## Implemented scope

Recovery snapshots the death territory, position, and selected FATE before release. It keeps that snapshot across homepoint and intermediate-city loading transitions.

1. An unlocked direct aetheryte in the death territory takes precedence. Select the closest known X/Z position, retain its teleport subindex, and bypass optional same-zone teleport profitability/cost settings.
2. When no direct destination is unlocked, optionally use Lifestream for a sheet-backed city/aethernet route:
   - Find destination-territory aethernet nodes in `Aetheryte`, ordered by distance to the saved position.
   - Resolve an `IsAetheryte` primary with the same nonzero `AethernetGroup`, and require that exact primary in the live unlocked teleport list.
   - Teleport to that primary, including when already in its city but away from its network. Revalidate the unlock at dispatch.
   - Wait for the actual gateway territory, loading readiness, and `Lifestream.GetActiveAetheryte()` matching the primary ID.
   - Invoke `Lifestream.AethernetTeleportById(uint) -> bool` for the destination node.
   - Firmament uses the verified Foundation gateway (aetheryte 70) and `Lifestream.AethernetTeleportToFirmament() -> bool`, targeting territory 886.
3. Verify actual death territory, settled loading state, and an idle provider before handing movement back to the automator. Re-query the destination FATE table; resume the same live FATE occurrence at its refreshed center, or resume selection if it ended/disappeared.

Lifestream remains optional. No `/li` string commands, custom-alias payloads, reflected provider instances, or invented territory/position IPC are used.

## Completion and cancellation

- Require `IsBusy`, `GetActiveAetheryte`, `Abort`, and the chosen destination endpoint before city dispatch.
- Do not start either leg while Lifestream reports someone else's work. Claim ownership only around our request; a rejected request relinquishes it.
- Provider idleness alone is not arrival. Allow a 30-second transition window after the aethernet request; bound the complete return to two minutes. Gateway teleports retain the three-attempt cap.
- Cancel owned aethernet work on timeout, request exception, manual-travel takeover, another death, disable, logout, or disposal. Retry cleanup after temporary provider/abort failure. Pending cleanup blocks replacement direct returns.
- Never abort a queue that was already busy before our request. Drop ownership when our issued queue is observed idle.
- `Abort` is a global provider operation, not a request-token API. Concurrent external replacement of an owned queue cannot be identified reliably through this IPC contract. An already-issued native teleport cast can finish after cancellation; cancellation prevents subsequent routing steps.

## Verified source contracts

Reference checkout: `../ffxiv-knowledge/reference-plugins/`.

- **Lifestream**, commit `62a6f68fc0966530f6baa380c1082e41f6cd6d7c`:
  - `Lifestream/IPC/IPCProvider.cs`: `IsBusy`, `Abort`, `GetActiveAetheryte`, `AethernetTeleportById`, and the dedicated Firmament endpoint.
  - `Lifestream/Systems/Legacy/DataStore.cs`: constructs primary/shard networks from nonzero `AethernetGroup` and `IsAetheryte`.
  - `Lifestream/Tasks/SameWorld/TaskTryTpToAethernetDestination.cs`: the IPC's local-network request does not enable global partial-name or teleport fallback. Acceptance means queued, not successful arrival.
  - `Lifestream/Data/CustomAliasCommand.cs`: a movement command's `Territory` field waits for that territory; it does not calculate a route to it. Custom aliases require caller-supplied steps.
- **FATE Toolkit / bundleoftweaks**, commit `4e216987ad1f817a5ec43a2961147580bc71fd63`:
  - `ffxiv_bundledtweaks/Tweaks/FateToolKit/FateToolKitTask.cs`, `Revive`: calls inherited `TeleportTo(lastZone, lastPos)` and `UseAethernet(lastZone, lastPos)`.
  - `.gitmodules` and the `clib` gitlink pin dependency to `d294d6aac55414f095e5c2cd54f1875e82cf4e9f`. The local nested submodule was empty; the pinned sources were inspected directly:
    - [TaskSystem/TaskBase.cs](https://github.com/Jaksuhn/clib/blob/d294d6aac55414f095e5c2cd54f1875e82cf4e9f/TaskSystem/TaskBase.cs): native teleport/aethernet orchestration, including UI interaction and packet dispatch. These are library methods, not exported routing IPC.
    - [Utils/Coords.cs](https://github.com/Jaksuhn/clib/blob/d294d6aac55414f095e5c2cd54f1875e82cf4e9f/Utils/Coords.cs): closest node and same-group primary resolution.

## Genuine remaining limits

- No verified general `(territory, position)` route planner is exported by these providers. A territory without a direct unlock, a sheet-backed aethernet node connected to an unlocked primary, or the supported Firmament route still requires manual return. Arbitrary overland borders, ferries, quest-gated transitions, housing/custom networks, and multi-network journeys need their own route graph and execution logic.
- Lifestream's numeric node endpoint internally resolves a name and uses its network/menu implementation. Destination territory is independently verified; node selection can still fail because of locks, menus, provider configuration, or runtime changes. Such failure is bounded and reported rather than assumed successful.
- Saved position chooses a return entry point; this is not an arbitrary exact-coordinate routing API. Subsequent travel targets the refreshed selected FATE, not necessarily the corpse coordinates. Direct-return paths do not add optional within-territory aethernet optimization.
- Original public-instance identity is not restored. The reference's own `Revive` also marks this as unresolved.
- Automated checks use fake game/provider boundaries. Live client teleport, menu, loading, and aethernet behavior still require in-game validation.

## Verification

- `/tmp/opencode/integration-tests.csproj`: original 261-assertion baseline passed; expanded suite passes **323 assertions**, including 56 DI registrations / 38 constructors and actual chain/handler optional-route completion/cancellation paths.
- `/tmp/opencode/parity-tests.csproj`: **43 assertions passed** against linked production sources.
- Release solution build: **0 warnings, 0 errors**. `git diff --check` passed.
- Integration harness now uses its own intermediate directory, avoiding an assets-file collision with the parity project. Test artifacts remain outside the repository.
