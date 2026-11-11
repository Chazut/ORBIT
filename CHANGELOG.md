# ORBIT 3.1.1

For SPT 4.1.x. Changes since ORBIT 3.1.0. This patch focuses on looting, movement, Ghost transitions and objective recovery, and adds a public API for other mods.

## Airdrops and looting

- Fix bots reaching an airdrop but refusing to loot because the crate's own collider blocked the arrival check. The check now targets the crate and accepts its own colliders while still rejecting unrelated obstacles. Thanks to **Scootis_McPootis** for the report and debug ZIP.
- Preserve a selected airdrop through combat, looting a kill and temporary assignment changes. Bots can resume the same drop without another chance roll, retaining their camp progress. Extraction, completion and an unavailable crate still end the detour.
- Prevent Ghost sleep during unsettled weapon or inventory operations. Wake a sleeping bot when a loot transfer needs its active inventory, and recover stale weapon-selection flags only after verifying that the weapon and hands are settled.
- Retry temporary inventory failures on the same loot target, preserve partial loot, and stop reporting failed inspections as successfully completed.
- Apply temporary access exclusions consistently to objective selection, fallback targets and own-kill loot. Keep inaccessible items available to other eligible squad members and allow later retries when access changes.
- Reduce rapid retries and repeated logs for rejected loot targets. Distinguish temporary bot unavailability from a genuinely unreachable route.
- Avoid redundant weapon redraws and wait for conflicting inventory operations to finish before starting another transfer.
- Use the corpse or airdrop's own floor for arrival checks instead of an unrelated overlapping zone.

## Camps, ambushes and sniper objectives

- Fix **Camp** and **Camp then loot** approaches choosing the wrong floor. Search for a reachable approach within the configured area, including when its center is not walkable, and check the floor again before counting camp time.
- Improve cover selection and fallback positions throughout the camp area. Preserve squad spacing, combat interruptions and the camp-before-loot order, with bounded searches and stair detours.
- Reject custom rush destinations with no suitable nearby navigable ground before spending a long time searching for a route. Add clearer placement and path rejection diagnostics.
- Add a physical-cover fallback for Kill ambushes when normal cover hints are missing or unusable. Keep the configured area, floor, spacing and visibility checks.
- Report the actual ambush search radius to RaidReview instead of an incorrect `0 m`.
- Correct sniper sightline checks and add detailed reasons for rejected posts, including floor, mesh and obstruction failures.
- Keep sniper squad members at separate approach and holding positions instead of stacking everyone on the sniper's point.

## Doors, navigation and control

- Preserve ORBIT's registered bot/door collision rules after the game's interaction cleanup, including delayed cleanup. Thanks to **Kaanori (Kaan)** for the investigation and proposed fixes in [issue #22](https://github.com/Chazut/ORBIT/issues/22).
- Pause stuck recovery during door animations within a bounded recovery window. Prevent recovery from closing an occupied doorway or reopening a closed or locked door from stale state.
- Release completed owned door interactions and verify the door's physical open position before completing an operation.
- Keep a bot waiting for a queued replacement route in the moving state instead of prematurely failing its objective.
- Track actual travel progress across jumps, short airborne pauses and route changes. Replan or abandon persistent stalls instead of repeating physical assists indefinitely, while preserving deliberate waits and combat interruptions.
- Avoid active directional mine trigger areas during navigation, accounting for their orientation and floor.
- Distinguish a medical need from active treatment so bots can return to ORBIT after combat when they are not actually using medicine. Preserve active treatment and danger avoidance, including a short quiet period after danger clears.
- Require a real extraction request and the selected exit before accepting an extraction waypoint as an objective or fallback.
- Prevent repeated visits to exhausted synthetic patrol points. Suspend shared-objective recovery while every member is extracting solo.

## Ghost Mode and raid cleanup

- Retain pending wake requests when safe placement is temporarily unavailable, including the full real-fight awake protection period.
- Improve ground checks for moving Ghosts and overlapping squads. Recheck beneath the bot and distribute placement attempts while preserving floor, clearance and query-budget limits.
- Clear door-navigation references at raid end and before registering the next raid's doors, including when the manager is absent or cleanup fails. Thanks to **R_F** for identifying references retained from previous raids.
- Add bounded diagnostics for wake placement, loot outcomes, door recovery, brain attachment and rotation problems. Rotation diagnostics observe behavior without changing steering.

## Public bot-control API

- Add `Orbit.Api.OrbitBotControl.ExcludeBot` and `EnableBot` so another mod can release a bot from ORBIT and Ghost Mode, then restore its normal eligibility. Thanks to **Harmony** for the request and integration testing.
- Add `SetBotSleepAllowed` and `IsBotSleepBlocked` for mods that want ORBIT to keep driving a bot while keeping it awake. Thanks to **DeepMitze** for requesting this mode. Managed squads share sleep state, so keeping one member awake also keeps its ORBIT squad awake.
- Keep full exclusion and sleep-only policies independent. Requests are scoped to the spawned bot, use safe wake handling, and are cleared when the bot is removed or the raid ends. Calling mods choose the bots; ORBIT does not automatically identify PBT cheaters.
- Fix a brain-initialization exception found during Harmony's API test, when custom layers were created before the native brain agent was ready.
- See the [API documentation](https://github.com/Chazut/ORBIT/blob/main/BOT_CONTROL_API.md) for calls, readiness checks and optional integration. Calls belong on the Unity thread of the machine running the bots: solo, Fika host or headless.

## Updating and Fika

- Replace both the ORBIT client and server files. Existing settings and presets are preserved.
- The **Fika addon remains at 1.2.0**, with no addon changes in this release. Use ORBIT **3.1.1** on the host/headless and every playing client, and keep the Fika addon installed when using Fika.
- This patch does not replace addon-authored zone coordinates. Check for updates from your preset's author if a custom point itself is misplaced.

Thanks again to R_F, Kaanori, Scootis_McPootis, Harmony, DeepMitze and everyone who supplied reports and raid logs.

# ORBIT 3.1.0

For SPT 4.1.x. Changes since ORBIT 3.0.0. Fika addon 1.2.0 remains compatible and unchanged.

## Preset review and web UI

- Review a preset before applying it. Compare its settings, zones and rush points with ORBIT Default, and adjust values in a temporary draft.
- A summary groups behavior changes by settings section and map changes by location. Each zone or rush point is counted once, with Added, Modified or Removed labels.
- Search for a setting, map or point, and switch between differences only and the complete setup.
- Click a zone or rush point to preview that entry on its map. Compare its default and preset positions, switch floors and zoom in without displaying every other change. Room-rush previews show both the door and loot area.
- Edit values, restore defaults or undo draft tweaks before applying. Cancel leaves the active setup unchanged. Modified presets become custom copies; an unchanged addon selection keeps receiving addon updates.
- Applying config only or zones only preserves the other part of the current setup. Missing addon sections also keep the current setup.
- Refresh the home page with feature shortcuts, preset guidance and links to the wiki. Recommend the optional SAIN Personality Mixer for more varied PMC personalities.

## Custom rush camps

- Camp and CampThenLoot points look for reachable cover inside their configured area and on the correct floor, instead of making bots wait directly on the point.
- When native cover is unavailable, try sheltered positions near walls or doors, then fall back to a reachable random position within the area.
- Squad members use separate holding positions. The camp timer starts after arrival, and combat and urgent extraction retain priority.
- Cover searches remain bounded and spread across frames.

## Extraction

- Exclude flare-required exits, including Klimov Street, from bot extraction choices. Recheck the selected exit before extraction and abandon a flare exit if it was already assigned.
- Filter extract-camp targets using supported exit requirements and current eligibility. Unsupported co-op exits are excluded, while supported conversions to paid exits remain usable. Multi-step exits require an active supported operation.
- Raise default loot extraction thresholds: Timmy **250,000-500,000 RUB**, Cautious **400,000-800,000 RUB**. Existing custom values are preserved.

## Debug exports

- Request remote host/headless debug exports on demand through server notifications, removing the repeated idle HTTP relay checks.
- Acknowledge a request before collecting the archive, with bounded waits and reconnection handling.
- Log only routine `/orbit/debug/relay` request messages at Debug instead of Info. Other Info messages, warnings and errors are unaffected; no SPT files need editing.

## Fika addon and updating

- **Fika addon 1.2.0 is unchanged and works with ORBIT 3.1.0.** Keep it installed when using Fika.
- Update both ORBIT client and server files. For Fika, use ORBIT 3.1.0 and addon 1.2.0 on the host/headless and every playing client.
- Existing presets and settings are preserved. Refresh the web UI after updating, using Ctrl+Shift+R if it is cached.

# ORBIT 3.0.0 + Fika Addon 1.2.0

For SPT 4.1.x. Cumulative changes since ORBIT 2.1.1 and Fika addon 1.1.0, including the 3.0 betas and addon release candidates.

## New objectives and squad behavior

- **Rush objectives:** PMCs can hunt nearby spawns, search boss locations, loot marked rooms or visit custom targets. Boss Rush does not require the boss to have spawned. Boss kills by squadmates count toward the objective, and surviving squad members can collect the remaining loot.
- **Custom Rush activities:** Other points can search, camp, loot, or camp then loot, with configurable durations and loot radii. Approaches account for destinations on different floors. Default Techlight loot rush points are included.
- **Sniper positions:** a scoped PMC takes a firing position while squadmates cover nearby. Configure the locations, viewing directions and how long they stay. Default hold durations are longer, and snipers occasionally move to nearby cover or another viewing angle on the same roof. They favour holding their position after a noise, let SAIN react to danger, and can return to their post after the alert. Bots abandon blocked approach or return routes after a bounded search.
- **Ambushes and extract camping:** squads can hold cover in their hunting area or camp an extract after finishing their other objectives.
- **Multi-step objectives:** bots can turn on power, use switches, unlock doors and use keycard readers before looting or extracting. Destinations include KIBA, ULTRA Medical and Saferoom on Interchange, D-2 and Hermetic on Reserve, and ZB-013 on Customs. Door approaches account for the correct floor, while opening and unlocking still require reaching the door.
- **Airdrop detours:** squads can head toward a drop, cover the area, loot it and then resume their plans. Several squads can decide to go for the same crate.
- **Personality-based travel:** route choices, preference for cover, pauses and sprinting vary with personality. These settings are configurable.
- **Squad looting and regrouping:** squadmates wait for looters according to their personality and can regroup in nearby cover.
- **No-backpack extracts:** optional support for bots to drop their bag and use these exits when the bag is empty or they need an emergency extraction. The dropped bag remains lootable.
- **Recorded rescue routes:** supported objectives can use recorded routes when normal pathfinding struggles with complex approaches.

## Ghost Mode

- Staggered non-urgent wakes and reconciled late-arriving squad members during sleep transitions.

- Ghost detection accounts for weather, visibility and each bot's vision equipment.
- Ghost snipers at their posts can spot awake enemies within scoped range and wake up with the spotted enemy as their target. Scoped range is preserved for that contact while they hold their post, with normal visibility and shooting checks still applying.
- Native bots can approach and inspect corpses, and transfer stored items, while remaining in Ghost Mode. They still wake for actions that need an active body.
- Ghost firefights have more varied shot sequences, observation pauses and reload breaks, with longer pauses at distance.
- Pending gunfire stops when a shooter dies, wakes or leaves the fight.
- Improved movement around small path corners and recovery from stalled native hunts.
- Added PiP Disabler compatibility for scoped wake, including remote players with the updated Fika addon.
- Added Bot Healing Fixes compatibility while native bots are in Ghost Mode.

## Performance

- **Work spread across frames:** more objective planning, quest reachability checks, pathfinding and loot processing run in small batches across multiple frames to reduce CPU spikes.
- Shared work queues, bounded search budgets and staggered non-urgent wakes reduce the amount of planning and activation work performed in a single frame. Performance still depends on the map, active bots, other mods and hardware.
- **Lighter full logging:** verbose logs are written in the background to reduce their impact during testing.
- Reduced repeated reflection lookups when reading bot personalities.

## Settings, addon tools and reporting

- Updated default zones and objective locations.
- Tagilla's Boss Rush targets on Interchange Rework are now disabled by default. For existing presets, in the Rush settings tab, select **Interchange Rework → Boss** and click **Restore native suggestions for this tab**, then **Save**.
- Configure objectives, movement styles and squad waiting behavior by personality in the web UI.
- Override selected behavior settings per map, including Ghost distances, guard times and objective preferences. Non-PMC sprint chances can also be tuned by bot type.
- Place Rush and Sniper points directly on the map.
- **Capture Rush door:** an optional in-game tool to create custom room-rush objectives for supported locked and unlocked doors. Enable it in **F12 → ORBIT → Authoring tools**, face a door until its interaction prompt appears, then press **Left Ctrl + F8** or your configured shortcut. The door ID is copied to the clipboard and a Marked Rush point is saved in the active preset, with approach/room positions and loot radius prefilled. The tool is **off by default** and only scans when you press the shortcut.
- Review captured points in **Rush objectives → Marked**. Uncertain room placement stays disabled until reviewed; existing points keep their settings. Protected presets become a personal copy. Save or discard pending web editor changes before capturing. Captures apply **next raid** and are included in zone exports. Use matching ORBIT 3.0 client and server versions; powered access sequences and excluded rooms remain protected.
- Zone Editor exports include Rush and Sniper points and their individual settings for the selected maps.
- Rename personal presets directly in the web UI. Editing an addon preset creates a custom copy and preserves the original.
- Disabling Ghost Mode greys out its page options without changing their saved values.
- **Create debug ZIP** in F12 collects the last raid log, available ORBIT performance diagnostics and matching Raid Review data when RR is installed. Position exports are more reliable on large raids, with clearer messages when a transfer fails.
- With **Raid Review 1.9.0 or another build supporting raid sharing**, debug ZIPs can also include an importable archive of a completed raid. Extract **RaidReview/raid-export.zip** and use RR's **Import raid** button. Older RR versions keep the existing JSON diagnostics; unavailable archives are reported without blocking those diagnostics.
- In Fika, creating a debug ZIP can also request the matching host/headless archive through the shared server, with transfer progress and caching. Uploads happen when requested. If no cached archive exists, the host must still be online with the raid available.
- Added diagnostics for sprint interruptions, jumps, rescue progress and sniper contacts to help investigate reports. Logs also record whether Quiet Mode is enabled and which log levels are active.

## Fixes

- Corrected rejection of valid prone capsule geometry during Ghost wake placement, including Cultists. Capsules shorter than their diameter use a spherical end-cap shape instead of repeatedly failing as `controller-unavailable`. Ground, floor, clearance and occupancy checks remain active.
- Added specific placement failure reasons and throttled pose/collider diagnostics to distinguish unsafe ground from invalid body data.
- Added brain names, brain types and ORBIT registration details to native-bot diagnostics when investigating missing behaviour ownership.

- Fixed squads getting stuck when approaching airdrops.
- Fixed bots getting stuck at doors during multi-step objectives, including ULTRA Medical. Failed interactions now retry within a limit or release the objective.
- Fixed Kill Ambush squads searching indefinitely for cover. They try up to four positions in the same area, then abandon the objective if no suitable position is found.
- Improved sprint continuity around turns, brief losses of ground contact and repeated short interruptions. Normal personality-based pauses, jumps and vaulting remain supported.
- Fixed floating weapons remaining visible in Ghost Mode and weapon visibility issues after waking.
- Fixed cases where waking a Ghost moved it away from its current position. Ground and floor checks help prevent placement on an unrelated roof or floor, and squad members retry individually when a safe position is unavailable.
- Improved transitions around looting and equipment changes, including recovery of the hands controller after waking.
- Preserved eligible close-range hostile contacts while expiring stale noise investigations.
- Fixed unnecessary Ghost wakes caused by an enemy merely remembering a bot without a current threat.
- Fixed stale reload state that could keep native bots awake after reloading had ended.
- Fixed stale Ghost noise investigations.
- Improved locked-room loot checks for individual squad members. An unrelated locked door on another floor no longer makes an unreachable item appear accessible.
- Fixed cases where a loot objective ended while nearby loot remained, or a bot repeatedly returned to an exhausted patrol point.
- Stalled objectives are released before reassignment so bots can choose another target.
- Fixed bots continuing loot operations after extraction, including related Fika/headless errors.
- Fixed Kill objectives set to all floors inheriting an unrelated overlapping loot zone's floor restriction.
- Fixed ordinary extracts being closed by bot departures. Car extracts still allow only one departure per raid.
- Bots can now use **Scav Lands on Reserve when SVM converts it to a paid extraction**. The normal cooperative exit remains subject to its cooperation requirement.
- Extraction navigation keeps the exits authorized for each bot's spawn and advances through connected intermediate steps when a full route is unavailable.
- Improved recovery from failed routes, blocked spawns and approaches on uneven terrain.
- Recovery teleports now require real movement toward a reachable objective before counting as successful. Attempts without progress are spaced out, while visibility and floor protections remain in place.

## ORBIT Fika Addon 1.2.0

For ORBIT 3.0. Cumulative changes since addon 1.1.0, including the 1.2 release candidates.

### Multi-step interactions

- Synchronize switches used by ORBIT multi-step objectives from the bot owner to playing clients, including power and extraction switches.
- Retain supported ORBIT door synchronization alongside the new switch events.
- Door compatibility is negotiated once per network session; a missing host response produces a warning. Use matching addon versions throughout the session.

### Scoped waking

- Synchronize remote optic zoom and field of view when PiP Disabler controls the player's active optic, so the bot owner can apply ORBIT scoped waking.
- Reports are limited to four per second, expire after 1.5 seconds and are cleared when optic handling stops or the player disconnects.
- PiP Disabler remains optional. Normal scope handling is retained for other players; unsupported peers do not receive the new capability's packets.

### Ghost firefight audio

- Remote listeners follow the bot owner's shot schedule, including bursts, observation pauses and reload breaks.
- Synchronize fight termination and shooter removal, death or waking so obsolete queued shots stop.
- Preserve positional sound playback for each listener.

### Fika setup and compatibility

Install **ORBIT 3.0.0** and **ORBIT Fika Addon 1.2.0** on the host/headless and every playing client. The addon is only needed for Fika. Update the entire group together; older addon versions cannot reproduce the current shot schedule.

The stable build retains the RC3 wire protocol and behaviour; the version suffix is removed. This addon supplements Fika synchronization for ORBIT events, rather than replacing Fika's general bot, inventory and corpse replication.

## Updating

- Install the complete ZIP with matching client and server components. Requires compatible SAIN, BigBrain and Waypoints builds for SPT 4.1.x.
- Existing 2.1 settings and preset/zone addons remain compatible. Missing settings receive defaults; existing saved values are retained.
- Disable SAIN extraction when using ORBIT extraction.
- Use **Raid Review 1.9.0** for the current replay integration and portable raid sharing.
- Fika users need **ORBIT Fika Addon 1.2.0** with matching versions on the host/headless and all playing clients. The addon is optional for solo play.
- Capture Rush door remains OFF by default. Save or discard pending web edits before capturing; changes apply next raid.

## Known limitations

- Sniper and multi-step routes can still fail on difficult or disconnected navigation, especially custom map layouts.
- Mysterious marked room on Streets remains excluded from Marked Rush while its special access and exit are unsupported.
- The Sniper objective is disabled by default on original Interchange; the recorded rooftop rescue route targets Interchange Rework.
- Native Ghost support covers specific activities. Actions that need an active body still wake the bot.
- Captured room loot areas may need manual correction before enabling the point.

[Installation, settings and technical documentation](https://github.com/Chazut/ORBIT/wiki)
