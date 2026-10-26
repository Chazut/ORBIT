<p align="center">
  <img src="branding/orbit-logo-1024.png" width="130" alt="ORBIT" />
</p>

<h1 align="center">ORBIT</h1>

**ORBIT 3.0.0**: [Full changelog](CHANGELOG.md) · [Fika addon 1.2.0](Orbit.Fika/CHANGELOG.md) · [Wiki](https://github.com/Chazut/ORBIT/wiki)

<p align="center">
  <b>Objective-driven Raid Bot Intelligence Tactics</b><br/>
  Smarter bots. Real objectives. Raids that feel alive.
</p>

<p align="center">
  <img src="https://img.shields.io/github/stars/Chazut/ORBIT?style=flat-square&label=STARS&color=007ec6" />
  <img src="https://img.shields.io/github/issues/Chazut/ORBIT?style=flat-square&label=ISSUES&color=44cc11" />
  <img src="https://img.shields.io/github/downloads/Chazut/ORBIT/total?style=flat-square&label=DOWNLOADS&color=44cc11" />
</p>

---

Every bot squad in your raid follows a plan: a rich area to strip, a
PvP hotspot to hunt, a quest spot to visit. They work it as a team, loot
like players, upgrade their gear along the way, and head for extract when
they're done. Kill one late in the raid and his backpack tells the story
of where he's been.

Built on [Phobos](https://discord.com/channels/875684761291599922/1337131427803955200)'s foundations (MIT, with Janky's blessing - full credits below).

**👻 Ghost Mode**: far-away bots go dormant to free
your CPU while ORBIT keeps playing them. The fps gains are massive
(+45% average in my heavy-population benchmark, testers report +50 to
+100%) and the world stays alive instead of freezing. **Ghost Mode supports
ORBIT-controlled bots as well as vanilla and faction bots.** A server **web UI**
and visual **zone editor** let you target zones by floor and bot type,
or share your setup through presets.

[📷 Screenshot](https://i.imgur.com/WSWqb8d.png) · Pair with [Raid Review](https://forge.sp-tarkov.com/mod/1479/raid-review) to replay it all · Questions & feedback: [ORBIT Discord thread](https://discord.com/channels/875684761291599922/1509314495019745451)

## What a raid looks like

- A 4-man squad rolls "clean out Resort", splits the rooms, opens a
  locked door or two, and extracts once the bags are worth it.
- The squad's Rat grabs the 5k mag his Chad walked past: personalities
  decide everything.
- Scavs hold their turf; a lucky few roam the whole map.
- Gunfire behind a hill: two ghost squads settling it off-screen. Push
  the sound and the fight turns real.

## The pillars

### 👻 Ghost Mode - the fps game changer

My heavy-population Shoreline benchmark measured **+45% average fps** with
far fewer stutters; RC testers reported anywhere from **+50 to +100%** depending
on map, modlist and hardware. Ships **ON by default** - one switch in
the web UI turns it off.

Classic limiters freeze distant bots and kill the living world with
them. Ghost Mode puts only the **body** to sleep - the expensive
EFT machinery - while their behaviour continues. ORBIT-controlled ghosts
keep walking their routes and looting in real time; native bots follow
their original behaviour. When two hostile ghosts spot each other
(optic-scaled range, terrain and forests block
line of sight), the fight plays out over a real window with audible
distant gunfire matching each side's real weapons (suppressors
included), real casualties and wounded survivors. Get close while
it's still going and it escalates into an actual firefight.

Bots wake on proximity, damage, or aiming through a scope (the wake
range stretches with your magnification). Tune which bot types sleep,
whether they move and loot, and how bloody ghost fights get.

**Vanilla and faction bots can use Ghost Mode without ORBIT takeover.**
Enable their **Dormant** toggle, **Ghost movement** and **Vanilla / faction movement**.
Leave faction **Take over** toggles OFF, or keep **Vanilla xxx** checked,
to preserve their original behaviour. These bots keep their own routing
and are not influenced by ORBIT zones. ISB White Tusks always keep their
native behaviour, even with takeover enabled.

Set gunfire investigation chances by personality for ORBIT-controlled PMC
and PlayerScav squads on the Ghost Mode page. For encounters with awake bots, choose
whether to put eligible awake bots to sleep or wake the ghosts; combat and
player proximity protections still apply.

*Fika: the optional `Orbit.Fika` addon syncs ghost fight sounds and ORBIT door states. Install the same version of it
on the host (or headless) and all playing clients.*

### 🎯 Objectives and extraction

ORBIT-controlled PMC and PlayerScav squads receive goals: high-value loot
zones, kill hunts anchored on PvP hotspots, visits to EFT quest locations.
The leader takes the anchor, the others splinter to nearby loot and cover. They extract for
real reasons - enough roubles, goals completed, or the raid clock - and
they coordinate on shared exfils like the car. Spawn-side exit restrictions remain mandatory when
a long path fails: bots advance along connected approach legs toward an authorized
exit, then use its normal arrival and extraction checks.

PMC and PlayerScav squads can hold cover within a Kill main or receive an
extract-camping main. A squad can roll one extract camp, pursued after all its other
mains. It then holds until its time-based extraction threshold, with emergency
departures still available. Airdrops create temporary detours: eligible nearby squads
roll as soon as the plane releases the crate, approach its estimated landing area,
then hold cover. Once the hold is over and the crate has landed, one member loots
while the others cover, before the squad resumes its objectives. Several squads
can choose the same target. If no cover formation is possible, the squad approaches
the drop and loots as soon as it lands, without an ambush hold; other members guard nearby.
**Ambush & camping** controls personality chances,
hold times, distances and cover requirements. Later airdrop checks let squads
reconsider a drop after entering range or leaving combat.

PMCs can also roll multi-step objectives that activate power and interact with
switches, doors or card readers before looting or extracting. Long or partially
reachable approaches are retried through connected intermediate points, with a
bounded step timeout. PlayerScavs never select chains requiring keys or keycards.

The **Extraction** page can allow no-backpack exits when the bag is empty or the
bot is making a wounded emergency extraction. The bot drops the real bag at the
entrance, leaving its contents lootable, and must enter the exit without it.
A squad only chooses such an exit when each member staying with the squad qualifies.

### 🎒 Looting that feels human

ORBIT-controlled bots use a custom loot engine built on BSG's pickup APIs.
Per-personality value thresholds (a GigaChad ignores what a Rat treasures), realistic
search timings and animations, inside-out container draining, and
mid-raid **gear upgrades**: a bot that finds a better rifle swaps to it,
strips the good mods off his old one, and leaves it on the corpse for
you to find.

<details>
<summary>More loot details</summary>

- Value is judged per inventory slot (price ÷ size): a 50k key beats a
  bulky 60k backpack.
- Regular scavs pick things up opportunistically (dice per item) instead of
  using thresholds. PlayerScavs loot like PMCs.
- Money, fragmentation grenades and dogtags bypass value and scav chance checks,
  provided the bot has room to carry them.
- Squad memory: a Chad's "nothing good here" verdict doesn't stop the
  squad's Rat from cleaning the same container later.
- Weapon swaps are scored on ergo/recoil/range/ammo (with per-map
  weights), never on price alone, and only count ammo the bot can
  actually use.
- Rig/backpack swaps transfer the whole carry first; if one item won't
  fit, the swap is cancelled. Regular scavs never swap, they only fill empty
  slots.
- PMC corpses keep their melee; secured containers are never touched.
- Coverage rolls make squads miss a few items per room, like real
  players do.
</details>

### 🧠 Personalities run the show

ORBIT uses SAIN personalities to shape PMC behaviour: what's worth
picking up, how much of a room gets covered, who hunts and who rats, how
early they extract, whether they force locked doors, even how strong a
ghost squad fights off-screen. Two squads never play the same raid.

### 🗺️ Your raid, your rules

ORBIT's behaviour settings live in a **web UI** on your SPT server
(`/orbit`, one button away from the F12 menu), including headless setups.
Most settings apply next raid; faction takeover changes require a game
restart. Full-config export/import lets you back up or share your tuning.

**Presets:** save, switch and share settings and zones together.
Custom tuning from 2.0 is preserved as **Custom**. Editing **Default** or an
addon creates a personal copy.

<details>
<summary>Sharing addon presets</summary>

Put your exported JSON files in one folder under the server's
`user/mods/ORBIT/addon/`, for example `addon/MyPreset/config.json` and
`addon/MyPreset/zones.orbitzones.json`. Each addon folder becomes one
selectable preset; a partial preset keeps settings and maps it does not supply.

Keep that folder structure in your ZIP, and keep the **same folder and
filenames** for updates so they overwrite the previous files. Put version
numbers in the ZIP name. Updates refresh the selected addon automatically;
personal copies keep their own edits.

</details>

The **zone editor** renders each map and lets you draw the hotspots
that steer squad routing: drag, resize, attract or repel, tune BSG's
own zones, mark which ones can host kill hunts. Export your setup as a
**zone pack** and publish it on the Forge as an ORBIT addon, or import
someone else's.

Name your custom zones and target specific bot types, factions
and floors. Reserve includes floors 2 through 5; Customs includes its fourth
floor. Zones influence ORBIT-controlled bots, awake or Ghost, and still need
a reachable route to the selected area.

## Install

1. Install the dependencies: [BigBrain](https://forge.sp-tarkov.com/mod/902/bigbrain) · [Waypoints](https://forge.sp-tarkov.com/mod/827/waypoints-expanded-navmesh) · [SAIN](https://forge.sp-tarkov.com/mod/791/sain-solarints-ai-modifications-full-ai-combat-system-replacement)
2. Extract the zip into your SPT root folder (client plugin + server mod).
3. Configure from the web UI: F12 → **Open web config UI** (or browse to
   `https://127.0.0.1:6969/orbit`).

<details>
<summary>Recommended SAIN setup (2 tweaks)</summary>

**1. Personality spread** - ORBIT was tuned around this distribution.
SAIN's config is a web UI too since 4.1: browse to
`https://127.0.0.1:6969/sain/presets` (your server address), open the
**Personalities** tab, and for each personality below set
**Can Be Randomly Assigned = On** with its **Randomly Assigned Chance**:

| Personality | Rat | Wreckless | SnappingTurtle | Coward | Chad | Timmy | GigaChad |
|---|---|---|---|---|---|---|---|
| Chance | 10 | 5 | 5 | 5 | 5 | 3 | 3 |

(Heads up for [Twitch Player](https://forge.sp-tarkov.com/mod/1895/sain-twitch-players) users: it zeroes several of these by default.)

**2. Extract layer** - let ORBIT own extraction. Same UI,
**Global Settings → General → Extract**: turn **SAIN Extract Behavior** Off, then
Save (editing a built-in preset creates an editable copy).
</details>

## Compatibility

**Required**: BigBrain, Waypoints and SAIN (linked under Install above).
ORBIT uses SAIN's personality system.

**Recommended**: spawn and loadout mods shape *who* spawns with *what
gear*; ORBIT decides *where its squads go and what they do*.
[APBS](https://sp-mod.com/mod/1594/apbs-acids-progressive-bot-system), [ABPS](https://sp-mod.com/mod/2097/abps-acids-bot-placement-system), [Raid Review](https://forge.sp-tarkov.com/mod/1479/raid-review).

**Do not combine with**: [QuestingBots](https://forge.sp-tarkov.com/mod/1109/questing-bots), [LootingBots](https://forge.sp-tarkov.com/mod/812/looting-bots), Phobos itself, or other AI limiter / culling
mods (AILimit, Adaptive Bot Culling...). Their systems overlap with ORBIT's
objectives, looting or Ghost Mode. Supported faction mods have their own
**Take over** toggles; leave them OFF to retain the author's behaviour.

### Bot Healing Fixes (3.0)

Bot Healing Fixes 1.0.0 is detected automatically when installed. Its first-aid
and surgery replacement keeps control of awake bots. For native bots in Ghost,
ORBIT guards the asynchronous medicine entry before it touches the body or
starts an item operation: simulated wound care stays simulated, while physical
care requests a wake before normal medical decisions retry. No setting is needed.
If the optional bridge cannot bind to a changed API, native bots stay awake and
the log explains the fallback. Other Bot Healing Fixes patches remain unchanged.

## Troubleshooting

### Sprint stability (3.0)

Awake bots controlled by ORBIT walk through short cover adjustments and sharp
turns. Sprint starts require an aligned, stable direction for 0.35 seconds and
at least 8 m to the destination; bots already running slow down within 5 m.
Rejected native sprint requests wait for another stable window before retrying.
Path corners use the player's actual sprint state. Existing guard and travel
observation pauses are retained, as are faction and personality sprint settings.
Repeated transitions or rejected starts produce a bounded `SPRINT LOOP`
diagnostic, even with Quiet logging enabled, for the next debug ZIP.

Brief ground-contact loss retains the last grounded travel input for at most
0.15 seconds, on the same route and within 1 m. It never starts a sprint.
Native jumps, falls, vaults and their landing states keep control of the body;
ORBIT suspends pose changes and new unstuck actions until ordinary grounded movement
resumes. Existing recovery from persistent off-mesh falls remains available.
`SPRINT GROUND` records contact/traversal episodes and native sprint drops;
`SPRINT STOP` identifies explicit ORBIT stops. These Quiet-independent logs are
capped per bot (four episode pairs and six stops). Correlate them with the visible
stutter and RaidReview; a contact loss alone does not establish its cause.

Beta 7 also detects three confirmed sprints lasting at most one second that end
within six seconds while ordinary travel still permits sprinting. The bot walks
for two seconds, then retries through the normal stable-start check. Walking input
and the route continue. Voluntary stops, turns, doors, insufficient stamina, combat,
route changes and native jumps/vaults clear the detector and any remaining recovery.
Each activation writes one `SPRINT RECOVERY` line, including Quiet mode, for the debug
ZIP. Rejected starts without an observed sprint do not trigger this protection.

Beta 7 also releases a destination after the 30-second no-progress watchdog
blacklists it. Both the bot's retained target and a matching squad target are
cleared, so the next dispatch can select another point. Unrelated squad targets,
combat pauses and the separate extraction retry rules are preserved.

Multi-step door navigation also keeps the real door handle or selected card
reader elevation when planning a route. A bot below ULTRA Medical can continue
searching for stairs instead of treating the space underneath the door as its
destination. Opening still requires the native standing position, physical
reach and an unobstructed interaction on arrival.

### Boss rush completion and loot (3.0)

A squad's own boss kill completes its Boss Rush search, even if the killer dies.
Finding the target boss's corpse within 30 m also completes the search, with a
2.5 m height tolerance. This proximity rule does not change Ghost vision.
An eligible survivor then attempts to loot the body before the automatic
departure for completed objectives. Another survivor can take over if the looter
dies or cannot collect it. Combat pauses collection, native transfers finish,
and unavailable bodies or a bounded collection timeout let the squad move on.
Urgent extraction and disabled looting retain their normal priority.

### Sniper alert retention (3.0)

While holding a sniper post, the scoped member occasionally moves to another nearby cover or sightline, roughly every 45 to 75 seconds when enough camp time remains. Candidate searches use the shared planning budget and prefer native cover with a clear view. Every path corner must remain within 12 metres of the first occupied post and within 0.75 metres of its height, including the path returned by the movement queue. Blocked moves are abandoned without a teleport, jump recovery or resetting the camp timer. Followers keep their own guard positions. Combat interrupts the move and SAIN keeps priority; the existing return-to-post behaviour resumes after the alert.

Default time at the post is 2-4 minutes for Timmy, 4-8 for Cautious, 3-6 for
Average, 1.5-3 for Aggressive and 1-2 for Very Aggressive. A duration is sampled
within that personality's range; reaching the post starts the hold timer.
Existing presets retain their saved durations unless edited.

Snipers already holding a post keep SAIN's combat decisions after an unseen
contact, while ordinary movement stays within 12 m of the post on the same
floor. SAIN can choose a local cover or angle; routes that leave the roof are
rejected. Incoming fire, recent wounds, suppression, grenades, close known
threats, visible enemies, squad orders and self-care release this noise guard.
After the alert, an active sniper objective returns to its post with the
original remaining hold time. `SNIPER ALERT` logs identify holding, rejected
routes, local moves, release reasons and return/resume events.

### Other rush behaviors (3.0)

In **Rush objectives > Other**, each point can search/patrol, camp, loot, or camp
then loot. Camp duration starts on arrival and pauses during combat or own-kill
detours. Loot has its own radius and time limit; an inventory transfer already
in progress can finish before the objective ends. Existing points keep Search.
These settings are included in saved presets and exported zone packs.
Other points on another floor use the expanded search for stair access, including
detours away from the target. An unreachable point is still skipped after the
existing retry or travel limit.

### Ghost wake placement (3.0)

Ghost wake-ups validate physical ground and body clearance on the current floor.
If the current spot is blocked, ORBIT tries nearby lateral positions with a short,
clear route and a limited height difference. It keeps retrying when none is safe.
A blocked member no longer delays the other members of its group.

Early bot deaths now record damage type, spawn timing, position and the death
call chain in diagnostic logs. Updated Raid Review records deaths without a
killer as KIA and reserves Extracted for confirmed extractions. Older ambiguous
removals are shown as Removed.

### Capture custom room rush points (3.0)

Enable **F12 > ORBIT > Authoring tools > Capture Rush door** (off by default).
Face a door until its interaction prompt appears and press **Left Ctrl + F8**,
or set your own **Capture door shortcut** in F12. The scene door ID is copied to
the clipboard and a Marked Rush point is saved in the active preset, with the
approach position, room loot position and radius prefilled. The tool only scans
the scene when you press the shortcut.

Review or rename the point in **Rush objectives > Marked**. Uncertain room
placement is saved disabled so you can check the coordinates before enabling it.
Existing points for that door keep their settings. Save or discard pending web
editor changes before capturing; Default and addon presets become a personal
copy. Changes apply next raid and are included when exporting zones for an addon.
Both the client and server need this version of ORBIT. Powered access sequences
and rooms already excluded from Marked Rush cannot be bypassed by this tool.

### Debug ZIP (3.0)

Use **F12 > ORBIT > Create debug ZIP** on the player client. Solo and Fika hosts
export their local report. Fika clients automatically request the matching
host/headless report through the shared SPT server, including Raid Review data
when RR is installed on that host. Update ORBIT on the player, host/headless and
SPT server. The Fika addon is not required for this transfer.

Keep the host/headless and SPT server running for the first collection. File
collection and transfer start only when the button is clicked; background
heartbeats carry session metadata only. The button shows preparation, upload
and download stages, with byte progress and cancellation. Transfers use 128 KiB
blocks at up to 1 MiB/s per direction, with a 256 MiB compressed archive limit.

The server temporarily caches completed host snapshots for up to 24 hours
(32 raids, 1 GiB total). Re-exporting that snapshot needs only the SPT server
and does not upload the host files again. Exports made during a raid are
provisional and refreshed on the next request. A missing/offline host, failed
transfer or missing RR is reported explicitly; the local ZIP remains usable.
Only the host's retained raid can be collected if it is not already cached.
Automatic matching applies to raids recorded with this update; older debug
records without the shared Fika identity remain local exports.

The ZIP keeps player files at the root and host files under **Host/**.
`report.json` records the shared raid identity, cache use and missing data.
Logging settings still apply on the machine running the bots: enable
Performance logging and, when requested, disable Quiet logging and select the
needed log levels on the host/headless before reproducing an issue. Exporting
cannot recover diagnostics that were never recorded.

Bots frozen or acting weird? In order: check for the unsupported mods
above, test with vanilla raid times (extended raids are a known trouble
source), and if you run ABPS try regenerating its config. Still broken:
use the [50/50 method](https://wiki.sp-tarkov.com/en/5050-method), and
before reporting, reproduce with just
**ORBIT + SAIN + BigBrain + Waypoints + ABPS** on default configs (per
Shynd's classic advice: everyone's modpack is unique, shrink yours
first). Then come say hi on the [ORBIT thread](https://discord.com/channels/875684761291599922/1509314495019745451).

## Roadmap highlights

No ETA, no promises: post-combat self-heal, squad splitting with radio
comms, faction rivalries (Firefly's idea), a "rally flare" item that
pulls the whole map onto a point, additional special extracts and
cross-raid player heatmaps feeding bot routing (Fiodor's idea).
Suggestions land on the Discord thread.

**Planned for 3.1 (Shynd's idea):** a configurable, weighted chance to
send bots toward zones tied to the player's active kill quests, when
the raid takes place on the relevant map.

## Known issues

- Some native or modded actions require bots to stay awake. Bosses that
  normally hold position may also remain still in Ghost.
- Complex or disconnected routes can still block special objectives and
  extracts. Mysterious marked room remains excluded from Marked Rush.
- Faction-mod takeover (RUAF / UNTAR / Black Division) can misbehave;
  leave those toggles OFF if it does. Ghost Mode can still be used.
- Labs security gates can trap bots (BSG pathing quirk, checkpoint
  tuning planned).
- Possible clash with CactusPie's auto-transfer-loot mod (bot pickups
  may land in YOUR tagged containers). Investigating.
- Bots can still occasionally get stuck on map geometry.

## About ORBIT

### AI usage

**I'm a C# developer with over 10 years of experience, and I use AI to generate ORBIT's code.**

**In my view, writing code is only a small part of creating a mod on this scale.** Much of the work is deciding how everything should behave and making it work together: extensive test raids, reproducing bugs, analysing logs and Raid Review recordings, building diagnostic dashboards, tuning performance and checking for regressions.

AI lets me spend less time writing the implementation and more time on that work. I still have to question its output, test it against what actually happens in-game, and keep iterating until the result feels right. The design choices and responsibility for what I release remain mine.

**I'm proud of ORBIT's stability, reliability and quality today.** They reflect that ongoing work and the feedback of people testing it. I hope you'll judge the mod by how it plays and how it is maintained.

### Origins & lessons learned

ORBIT started as a personal project: a fork of **Phobos**, with looting code from **Looting Bots**. It grew from there into the mod you see today.

**I made a mistake when I first shared it:** I published on the Forge without asking either author for permission first. I wrongly assumed the MIT licenses were all I needed, without checking the Forge's additional permission requirements. Looking back, I feel bad about how I handled it, and I understand why the authors were upset.

I took the mod down and contacted them. **Janky gave permission to continue using Phobos. Arch did not give permission to reuse Looting Bots' code**, so I removed that implementation and rebuilt the looting system from scratch before republishing.

Phobos remains part of ORBIT's foundation. Looting Bots was part of its beginnings, and Questing Bots inspired the quest-routing concept without any code being reused. Those contributions deserve clear credit, however much ORBIT has changed since.

**ORBIT wouldn't exist without AI, or without the work modders did long before these tools existed.** AI learns from human-created material, including publicly available code. I don't know which specific mod repositories were in its training data, but I want to acknowledge the people who built and shared the knowledge these tools draw on.

For ORBIT's new systems, I've taken care not to copy code from other mod repositories. The reused Phobos foundation and credited contributions remain openly acknowledged.

That experience changed how I approach sharing my work: check permissions first, be clear about what comes from other projects, and own my mistakes. The credits are part of that history.

**ORBIT 3.0 is a very different mod from its first release.** Everything has been rebuilt except Phobos's advection system, and many custom features have been added along the way. Please judge ORBIT for what it is today, rather than for its first version or how it started.

## Credits

- [Phobos](https://discord.com/channels/875684761291599922/1337131427803955200) by [janky](https://forge.sp-tarkov.com/user/72916/jankytheclown) - the advection-field cell dispatch ORBIT is built around (MIT, used with explicit permission, see below)
- [QuestingBot](https://forge.sp-tarkov.com/mod/1109/questing-bots) by [danW](https://forge.sp-tarkov.com/user/27632/danw) - inspired the quest routing concept, no code reused
- [LootingBots](https://forge.sp-tarkov.com/mod/812/looting-bots) by [Skwizzy](https://forge.sp-tarkov.com/user/28069/skwizzy) & [ArchangelWTF](https://forge.sp-tarkov.com/user/52282/archangelwtf) - ORBIT began as a Phobos + LB merge; the loot layer has since been rewritten from scratch
- [SAIN](https://forge.sp-tarkov.com/mod/791/sain-solarints-ai-modifications-full-ai-combat-system-replacement) by [Solarint](https://forge.sp-tarkov.com/user/27463/solarint), [ArchangelWTF](https://forge.sp-tarkov.com/user/52282/archangelwtf) & [DrakiaXYZ](https://forge.sp-tarkov.com/user/27605/drakiaxyz) - the personality system everything plugs into
- [BigBrain](https://forge.sp-tarkov.com/mod/902/bigbrain) by [DrakiaXYZ](https://forge.sp-tarkov.com/user/27605/drakiaxyz)
- [Andrewgdewar](https://github.com/Andrewgdewar) - per-faction area-roaming percentages (community PR)
- [tarkov.dev](https://tarkov.dev) - the zone editor's map renders
- The **SPT team**, the **SPT Discord**, and **you** for trying the mod

**Phobos authorization from Janky:**

![Phobos authorization from Janky](https://i.imgur.com/ifGx54S.png)

## Support

If ORBIT made your raids more interesting, feel free to buy me a coffee!

[![ko-fi](https://ko-fi.com/img/githubbutton_sm.svg)](https://ko-fi.com/chazut)

All my mods are free and open source. Your support keeps me motivated to create more!
