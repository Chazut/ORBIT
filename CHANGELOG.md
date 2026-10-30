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
