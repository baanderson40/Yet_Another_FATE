# Empty-zone rotation

Enable **Rotate empty zones** in settings, then add zones and use **Up/Down** to order them. Rotation is disabled by default. Unlock indicators are refreshed from the player's aetheryte list; locked zones remain selectable for future use.

- The plugin can see FATEs only in the current territory. Rotation visits destinations; it does not scan remote zones.
- By default, any visible preparing/running local FATE keeps the player in that zone. **Also rotate when no FATE meets selection filters** is a separate, disabled-by-default option. Temporary movement-failure cooldowns do not make a FATE ineligible for this option.
- Defaults: 30 seconds continuously empty, at least 5 seconds of ready-world scan settling, and 60 seconds of backoff after exhausting a circuit. All three are configurable. Initial enable/re-enable also requires the scan grace, even with a zero empty delay; readiness loss restarts it.
- The successor to the current zone is checked first, wrapping through the selected order. Current, already-checked, locked, unavailable, and over-budget destinations are skipped. If the current zone is not selected, traversal starts at the top.
- Rotation honors **Enable Teleport Advisor** and **Max teleport cost** (zero means unlimited, matching the advisor). It chooses the cheapest unlocked aetheryte within the budget using the existing zone and teleport services.
- Each destination gets at most one issued or genuinely failed teleport request per circuit. Failed/canceled requests have a 60-second destination cooldown. Busy/Blocked contention retries the same destination with a capped 2–8 second backoff, without recording a destination attempt or failure. A request has a 60-second arrival timeout. A destination that never becomes ready suspends rotation after 90 seconds from issue.
- Expected territory arrival preserves the circuit, including territory zero while an owned teleport is loading. Unrelated nonzero territories suspend rotation. Editing the selected list restarts circuit accounting and the empty timer, retains cooldowns for unchanged IDs, and preserves an already-issued destination until settling finishes.
- Combat, post-FATE collection/reward grace, follow-up waiting, and Twist of Fate preservation take precedence over leaving. Death enters the existing recovery flow; pending run stops prevent new rotation requests.
- Disable/logout resets rotation. Manual zone travel, manual Teleport/Return casts observed before rotation issues, and debug travel suspend rotation. Toggle automation or rotation off/on to resume. An already-issued game teleport is not recalled; no replacement request is issued after cancellation of rotation ownership.
- Manual Teleport/Return during arrival settling is also unowned travel. Disabling rotation or teleports while a rotation cast is outstanding suspends further hops, even if re-enabled before that cast completes. After travel settles, toggle rotation off/on to resume.

Main and debug windows show phase, destination, countdown, circuit progress, and last failure. Debug reports include the same status.

## Verification

Release build and temporary `/tmp/opencode/integration-tests.csproj` and `parity-tests.csproj` harnesses are used. `RotationTests.cs` exercises the compiled production controller, adapter, and automator chain with simulated time and service inputs, including arrival continuity, failure cooldowns, configuration edits, safety gates, and competing travel.

Live game teleport/cast timing, loading screens, buff expiry, reward UI timing, and unlocked-zone UI still require an in-game smoke test.
