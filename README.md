<p align="center">
  <img src="branding/orbit-logo-1024.png" width="130" alt="ORBIT" />
</p>

<h1 align="center">ORBIT</h1>

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

**2.0's headliner is 👻 Ghost Mode**: far-away bots go dormant to free
your CPU while ORBIT keeps playing them. The fps gains are massive
(+45% average in my heavy-population benchmark, testers report +50 to
+100%) and the world stays alive instead of freezing. 2.0 also adds
a server **web UI** for every setting and a visual **zone editor** to
draw your own hotspots on the map.

[📷 Screenshot](https://i.imgur.com/WSWqb8d.png) · Pair with [Raid Review]([https://forge.sp-tarkov.com/mod/1479/raid-review](https://sp-mod.com/mod/1479/raid-review)) to replay it all · Questions & feedback: [ORBIT Discord thread](https://discord.com/channels/875684761291599922/1509314495019745451)

## What a raid looks like

- A 4-man squad rolls "clean out Resort", splits the rooms, opens a
  locked door or two, and extracts once the bags are worth it.
- The squad's Rat grabs the 5k mag his Chad walked past: personalities
  decide everything.
- Scavs hold their turf; a lucky few roam the whole map.
- Gunfire behind a hill: two ghost squads settling it off-screen. Push
  the sound and the fight turns real.

## The pillars

### 👻 Ghost Mode (2.0) - the fps game changer

The biggest fps upgrade a bot-heavy raid can get. My heavy-population
Shoreline benchmark measured **+45% average fps** with far fewer
stutters; RC testers reported anywhere from **+50 to +100%** depending
on map, modlist and hardware, and "buttery smooth" frametimes across
the board. Ships **ON by default** - one switch in the web UI turns it
off.

Classic limiters freeze distant bots and kill the living world with
them. Ghost Mode puts only the **body** to sleep - the expensive
EFT machinery - while ORBIT keeps thinking. Ghost squads keep
walking their routes and keep looting in real time. When two hostile
ghosts spot each other (optic-scaled range, terrain and forests block
line of sight), the fight plays out over a real window with audible
distant gunfire matching each side's real weapons (suppressors
included), real casualties and wounded survivors. Get close while
it's still going and it escalates into an actual firefight.

Waking is seamless: proximity, damage, or aiming through a scope (the
wake range stretches with your magnification). Everything is tunable,
down to which bot types sleep by default and how bloody ghost fights
get.

**2.1: configurable hearing.** The Ghost Mode page lets you set a base investigation
chance from 0 to 100% for each PMC personality archetype and for PlayerScavs.
Defaults preserve the existing behaviour. Distance still reduces the final chance,
down to half at the hearing limit; 0% disables that archetype's investigations.
Settings belong to the selected preset and apply from the next raid. Native and
faction bots keep their original hearing behaviour.
Investigations of simulated fights target the nearest audible shooter rather than
the centre between both sides. Each shooter's suppressor sets its own hearing range;
RaidReview records the same source selected for the investigation. One curiosity
roll per squad and fight is retained, with positions cached for the existing hearing poll.

**2.1: Ghost meets awake bot.** By default, eligible awake groups go into Ghost
before proximity wakes are evaluated, including ORBIT, vanilla and faction groups.
Combat, player distance, scoped view, cooldowns and the population floor still apply.
If a nearby group must stay awake, the existing proximity wake rules apply.
Select "Wake the Ghost group" for the previous behaviour. Simulated combat is unchanged.

ORBIT loot sessions can continue in Ghost after their current body-dependent step
finishes, preserving the target and inventory transactions. Door finalisation also
continues outside the body. Nearby doors use a spatial index and a two-second cache,
refreshed early when the bot leaves its covered area. Door state changes remain visible
without rescanning. These transitions still need raid validation.

**2.1: movement without takeover.** The Ghost Mode page's "Vanilla / faction
movement" toggle preserves supported bots' original decisions while their
bodies sleep: vanilla patrol and follower actions, MoreBotsAPI hunts, regrouping
and searching, UNTAR/RUAF checkpoints, and RoguesVRaiders travel, patrol, lockdown
and hunt objectives. Their original destinations determine where they move.
Checkpoint waiting periods, cover changes and RoguesVRaiders squad objectives
remain controlled by their original mods. Optional integrations require their
native manager or squad state to be available. Supported patrol layers can also
walk or run to native covers, including Goons, Shturman, cultists and several
boss guards. Cover destinations and arrival handling stay with the native brain.
Sniper scavs follow the Scavs sleep toggle too. Their peaceful cover travel and
native standby can hand over to Ghost without waiting for vanilla patrol to resume.
The standby transition keeps their position, prevents its physical cover/teleport
fallback while asleep, and restores the native standby preference on wake. Their
settled prone overwatch can continue in Ghost, keeping their native positions and
decisions. Combat, player proximity and scoped-wake protections still apply.
In Ghost versus Ghost encounters, sniper scavs detect targets from at least
200 m horizontally, extended by optics up to 400 m before night penalties.
Height does not reduce their detection radius. Terrain and buildings still block
sight, and combat resolution uses the real distance between the bots.
ISB checkpoints (including Black Division event checkpoints), tactical movement
and Hunt/Camera Hunt preserve their original manager and squad membership during
sleep. The optional lifecycle bridge checks the installed mod's method contracts;
an incompatible tactical component keeps its bot awake.
Truncated paths are retried while the bots remain asleep. A stranded bot can
receive a hidden relocation of up to 8 m to a point connected to its destination,
subject to visibility, collision, floor and proximity checks. If no suitable
point exists, it keeps retrying in Ghost. Repeated rescues stay within the
original blocked area, avoid previously tried landings and check the start of
the new route. Small relocation loops do not count as progress.
At a repeatedly blocked navmesh edge, recovery also checks small landings along
the failed segment, with the same collision and visibility protections. Identical
segment failures progressively space path retries from 2 to 30 seconds.
Already overlapping native Ghost allies can separate with a short correction of
up to 1.5 m that increases horizontal clearance. Group identity, combat, player
visibility, walls, doors and navigation checks still apply, and their native orders remain intact.
After 45 seconds without leaving a
small area on a checkpoint travel order, ORBIT asks the faction's own selector
for another reachable cover at the same checkpoint. A failed selection keeps
the current goal. Stalled MoreBotsAPI followers can similarly request another
regroup point from their original hunt manager. The leader continues waiting
until the original action acknowledges the follower's arrival.
Rejected alternatives report whether the point was unchanged, the path was
incomplete or its first steps were blocked.
Native stops, checkpoint waits and simulated fights suspend
this recovery.
Doors, special interactions and unsupported behaviours keep the group awake.
Other faction behaviours need their own compatibility support; this is not
universal support for every custom brain. Requires "Ghost movement"; disabling
either toggle restores stationary sleep for bots without takeover.
Partizan can follow his native tactical mine approaches while asleep. His
tracking timer remains active; placing traps, prewarming mines and weapon
interactions require his body to wake. A distant remembered quarry can remain
in memory during mine preparation, with visible, nearby or recently seen
enemies still preventing sleep. Other specialized native actions can still
require waking. Refusal snapshots identify the blocking state and brain layer.
Reloads, weapon switches, grenade throws, medical use and door operations requested from native
decision selection are deferred until the body wakes. ISB mission movement can
retain a similarly distant, old enemy memory only while ISB's own mission guard
also approves it. Combat layers and unknown specialized actions still wake.

Simulated fights include ORBIT, vanilla and faction groups alike. Both sides
hold position for the fight; an actual wake returns the encounter to real combat.
Native movement still needs in-raid validation.

UNTAR's legacy raider hunts follow the UNTAR takeover toggle even though their
spawn role is `pmcBot`. Hunts whose owner cannot be identified retain their native
behaviour. The startup configuration and `FACTION HUNT` logs show the applied policy.

*Fika: designed for co-op (an optional `Orbit.Fika` addon syncs the
fight sounds to every client) but untested so far.*

### 🎯 Objectives and extraction

Squads roll 1-5 goals at spawn: high-value loot zones, kill hunts
anchored on PvP hotspots, real EFT quest triggers. The leader takes the
anchor, the others splinter to nearby loot and cover. They extract for
real reasons - enough roubles, goals completed, or the raid clock - and
they coordinate on shared exfils like the car.

At a car, squads wait up to 90 seconds for their teammates before starting
the 60-second countdown. A bot leaving solo skips the squad wait. After a
combat interruption, a bot must return to the extraction area to leave;
if the car has departed or its trigger cannot be reached, it seeks another exit.

### 🎒 Looting that feels human

A custom loot engine built on BSG's own pickup APIs. Per-personality
value thresholds (a GigaChad ignores what a Rat treasures), realistic
search timings and animations, inside-out container draining, and
mid-raid **gear upgrades**: a bot that finds a better rifle swaps to it,
strips the good mods off his old one, and leaves it on the corpse for
you to find.

<details>
<summary>More loot details</summary>

- Value is judged per inventory slot (price ÷ size): a 50k key beats a
  bulky 60k backpack.
- Scavs pick things up opportunistically (dice per item) instead of
  using thresholds. PlayerScavs loot like PMCs.
- Money, grenades and dogtags are always taken.
- Squad memory: a Chad's "nothing good here" verdict doesn't stop the
  squad's Rat from cleaning the same container later.
- Weapon swaps are scored on ergo/recoil/range/ammo (with per-map
  weights), never on price alone, and only count ammo the bot can
  actually use.
- Rig/backpack swaps transfer the whole carry first; if one item won't
  fit, the swap is cancelled. Scavs never swap, they only fill empty
  slots.
- PMC corpses keep their melee; secured containers are never touched.
- Coverage rolls make squads miss a few items per room, like real
  players do.
</details>

### 🧠 Personalities run the show

Everything above bends to each bot's SAIN personality: what's worth
picking up, how much of a room gets covered, who hunts and who rats, how
early they extract, whether they force locked doors, even how strong a
ghost squad fights off-screen. Two squads never play the same raid.

### 🗺️ Your raid, your rules (2.0)

Every behaviour setting lives in a **web UI** on your SPT server
(`/orbit`, one button away from the F12 menu). It applies at the next
raid and works headless. Full-config export/import included, to back
up or share your tuning.

**2.1: configuration presets.** The preset selector is available on every page.
Each personal preset keeps global settings and all zone maps together. Switches save
the edits you are leaving; **Save** applies edits to the current preset. The built-in
**Default** is protected: changing a setting or zone automatically creates **Custom**.
Use **Presets** to duplicate, rename, delete inactive presets or export a complete setup.
Undo history starts fresh when switching presets. Most settings apply next raid;
faction takeover and personality brain lists still require a game restart.

Upgrading from 2.0 automatically keeps existing tuning as **Custom** if it differs
from defaults, including changes made only in the zone editor. Unchanged installations
start on **Default**. Original config and zone files are backed up under
`user/mods/ORBIT/presets/legacy-2.0/`. Saved presets and the active selection live in
`presets/library.json`; `config.json` and `zones/*.json` remain compatibility copies.

Drop JSON files into **`user/mods/ORBIT/addon/`** to add presets automatically.
Each direct subfolder becomes one preset named after the folder, combining its JSON
files, including nested folders. For example, `addon/Live-like/` can hold a global
config and a zone pack: selecting **Live-like** applies both. Files directly in
`addon/` remain individual presets. Existing global config exports, `orbit-zones/1` packs,
single-map files named after their map ID, and complete `orbit-preset/1` exports are
recognized. Scans run at startup, every five seconds in the UI, and before client
config fetches (at most once per five seconds). Partial addons replace
only the supplied settings and maps, keeping the rest of your current setup.
Addon sources are protected too: edits create a personal copy. A valid update to the
selected addon is applied automatically, including at server startup. Personal copies
keep their own tuning. Missing or incomplete files keep the last valid active version.
Keep the folder name when updating a folder addon; filenames inside may change.
For individual files directly in `addon/`, keep the same filename. Files within a
folder are applied in alphabetical path order; later files override the supplied
settings and whole maps if they overlap. One invalid file rejects the entire folder
update and keeps its last valid selection. Malformed addons are listed with an error
and leave other presets available.

The **zone editor** renders each map and lets you draw the hotspots
that steer squad routing: drag, resize, attract or repel, tune BSG's
own zones, mark which ones can host kill hunts. Export your setup as a
**zone pack** and publish it on the Forge as an ORBIT addon, or import
someone else's.

**2.1: bot types and floors.** Each zone can target several bot types or factions
and a named floor. The editor shows the corresponding floor plan from tarkov.dev,
using the same SPT map catalogue as RaidReview. Choose a floor and a bot preview
before drawing, or edit a zone's **Bot types** and **Zone floor** afterwards.
Copies, undo/redo and zone packs retain these choices. Existing custom zones still
apply to all types and floors unless configured otherwise.

Built-in zones use automatic native floors, shown in read-only form. The initial
editor data comes from SPT spawn positions; loading a map refreshes it from the
scene's spawn and patrol points, including zones that span several floors.
Existing zone files migrate automatically when the server starts. Their original
contents are kept in a `.pre-native-floors.bak` file beside each changed JSON.
Migration preserves radii, forces, bot filters and custom zones. Older imported
packs also adopt the built-ins' native floors.

Zones influence bots controlled by ORBIT, awake or Ghost. Shared squad destinations
use the leader's type. Floor hotspots select destinations on that floor and check
the arrival height; bots use the existing navigation paths and stairs to reach them.
Repellers remain preferences, not walls. Bots retaining their native behaviour
without takeover keep their original routing.

Map reworks that keep the vanilla location id (Interchange Rework, Manimal's
Interchange and Lighthouse 1.0 backports) are detected per raid and get their own zone
set and render, listed as "Interchange (1.0 rework)" in the editor.

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

**Required**: [SAIN](https://forge.sp-tarkov.com/mod/791/sain-solarints-ai-modifications-full-ai-combat-system-replacement) - ORBIT plugs into its personality system.

**Recommended**: spawn and loadout mods shape *who* spawns with *what
gear*; ORBIT decides *where they go and what they do*. The layers don't
fight. [APBS](https://forge.sp-tarkov.com/mod/963/algorithmic-progression-bot-system), [ABPS](https://forge.sp-tarkov.com/mod/2103/another-better-progression-system), [Raid Review](https://forge.sp-tarkov.com/mod/1479/raid-review).

**Do not combine with**: any other mod that moves, quests, loots or
culls bots. That includes [QuestingBots](https://forge.sp-tarkov.com/mod/1109/questing-bots), [LootingBots](https://forge.sp-tarkov.com/mod/812/looting-bots), Phobos itself, and AI limiter / culling
mods (AILimit, Adaptive Bot Culling...) - ORBIT ships its own limiter.
If a mod replaces bot brains or dispatches bots somewhere, assume it
conflicts.

## Troubleshooting

Bots frozen or acting weird? In order: check for the unsupported mods
above, test with vanilla raid times (extended raids are a known trouble
source), and if you run ABPS try regenerating its config. Still broken:
use the [50/50 method](https://wiki.sp-tarkov.com/en/5050-method), and
before reporting, reproduce with just
**ORBIT + SAIN + BigBrain + Waypoints + ABPS** on default configs (per
Shynd's classic advice: everyone's modpack is unique, shrink yours
first). Then come say hi on the [ORBIT thread](https://discord.com/channels/875684761291599922/1509314495019745451).

## Roadmap highlights

**Next (2.1)**: validate native Ghost movement and zones by bot type and floor
in raids, and extend compatibility beyond vanilla patrols and MoreBotsAPI hunts.
Per-map Ghost Mode settings are on the maybe list.

No ETA, no promises: camp & ambush decisions, post-combat self-heal,
squad splitting with radio comms, boss hunting and faction rivalries
(Firefly's idea), airdrop ambushes, a "rally flare" item that pulls the
whole map onto a point, multi-step objectives (Kiba alarm, ULTRA power,
Reserve D-2...), switch-gated and Red-Rebel-style exfils, cross-raid
player heatmaps feeding bot routing (Fiodor's idea), per-map ORBIT
toggle. Suggestions land on the Discord thread.

## Known issues

- Ghost Mode and the zone editor are brand new in 2.0: expect tuning
  passes. Fika support for the limiter is designed in but untested.
- Native Ghost movement supports selected patrol and hunt behaviours.
  Unsupported actions keep the group awake. With "Vanilla / faction movement"
  disabled, bots without takeover still sleep in place. Native movement and
  its interaction with modded brains need in-raid validation.
- Most Reserve exfils need switches ORBIT can't operate yet.
- Floor zones need a reachable navigation point on the selected floor. They do not
  create paths through blocked stairs, operate switches or create spawn areas.
  Floor routing and map height metadata still need in-raid validation.
- Faction-mod takeover (RUAF / UNTAR / Black Division) can misbehave;
  leave those toggles OFF if it does. ISB takeover works.
- Labs security gates can trap bots (BSG pathing quirk, checkpoint
  tuning planned).
- Possible clash with CactusPie's auto-transfer-loot mod (bot pickups
  may land in YOUR tagged containers). Investigating.
- Rare stuck bots; they usually free themselves within a minute.

## About AI

Full transparency: I build ORBIT with **Claude**.

I'll be honest, I barely open an IDE these days, at work or at home:
Claude writes most of this code. I still read it, question it, and own
every design decision in it.

The ideas don't come from the AI. Neither do the evenings spent in test
raids watching bots live, digging through logs and replays, and
finetuning values until a raid finally feels right. This project eats a
LOT of personal time, and that's the part no assistant can do for me.

Without the AI, ORBIT simply wouldn't exist: a project this size doesn't
fit in one person's free time otherwise.

If that's a dealbreaker for you, no hard feelings. If you judge a mod on
what it does, give it a try.

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
