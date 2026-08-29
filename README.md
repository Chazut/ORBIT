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

Every bot squad in your raid spawns with a plan: a rich area to strip, a
PvP hotspot to hunt, a quest spot to visit. They work it as a team, loot
like players, upgrade their gear along the way, and head for extract when
they're done. Kill one late in the raid and his backpack tells the story
of where he's been.

Built on [Phobos](https://discord.com/channels/875684761291599922/1337131427803955200)'s foundations (MIT, with [Janky's](https://forge.sp-tarkov.com/user/72916/jankytheclown) blessing - full credits below).

**2.0** adds a server **web UI** for every setting, a built-in **AI
limiter** that turns far-away squads into ghosts instead of freezing the
world, and a visual **zone editor** to draw your own hotspots on the map.

[📷 Screenshot](https://i.imgur.com/WSWqb8d.png) · Pair with [Raid Review](https://forge.sp-tarkov.com/mod/1479/raid-review) to replay it all · Questions & feedback: [ORBIT Discord thread](https://discord.com/channels/875684761291599922/1509314495019745451)

## What a raid looks like

- A 4-man PMC squad rolls "clean out Resort": the leader picks the rooms,
  the wingmen fan out around him, they skip loot their personality finds
  cheap, force a locked door or two, and push for extract once the bags
  are worth it.
- The squad's Rat happily grabs the 5k mag his Chad teammate walked past.
- Scavs hold their home quartier; roughly one squad in five rolls the
  right to wander across the map instead.
- Distant gunfire crackles behind a hill: two ghost squads are settling
  it off-screen. Rotate on the sound and the fight turns real before you
  arrive.

## The pillars

### 🎯 Objectives and extraction

Squads roll 1-5 goals at spawn: high-value loot zones, kill hunts
anchored on PvP hotspots, real EFT quest triggers. The leader takes the
anchor, the others splinter to nearby loot and cover. They extract for
real reasons - enough roubles, goals completed, or the raid clock - and
they coordinate on shared exfils like the car.

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

### 👻 The ghost world (AI limiter, 2.0)

Classic limiters freeze distant bots and kill the living world with
them. ORBIT's limiter puts only the **body** to sleep - the expensive
EFT machinery - while ORBIT keeps thinking. Ghost squads keep
walking their routes and keep looting in real time. When two hostile
ghosts spot each other (optic-scaled range, terrain and forests block
line of sight), the fight plays out over a real window with audible
distant gunfire, real casualties and wounded survivors. Get close while
it's still going and it escalates into an actual firefight.

Waking is seamless: proximity, damage, or aiming through a scope (the
wake range stretches with your magnification). Everything is tunable,
down to which bot types sleep by default and how bloody ghost fights
get.

*Fika: designed for co-op (an optional `Orbit.Fika` addon syncs the
fight sounds to every client) but untested so far.*

### 🗺️ Your raid, your rules (2.0)

Every behaviour setting lives in a **web UI** on your SPT server
(`/orbit`, one button away from the F12 menu). It applies at the next
raid and works headless.

The **zone editor** renders each map and lets you draw the hotspots
that steer squad routing: drag, resize, attract or repel, tune BSG's
own zones, mark which ones can host kill hunts. Export your setup as a
**zone pack** and publish it on the Forge as an ORBIT addon, or import
someone else's.

## Install

1. Install the dependencies: [BigBrain](https://forge.sp-tarkov.com/mod/902/bigbrain) · [Waypoints](https://forge.sp-tarkov.com/mod/827/waypoints-expanded-navmesh) · [SAIN](https://forge.sp-tarkov.com/mod/791/sain-solarints-ai-modifications-full-ai-combat-system-replacement)
2. Extract the zip into your SPT root folder (client plugin + server mod).
3. Launch. `ORBIT 2.0.0` shows in the bottom-left version label.
4. Configure from the web UI: F12 → **Open web config UI** (or browse to
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
**Global Settings → Extract**: turn **SAIN Extract Behavior** Off, then
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

No ETA, no promises: camp & ambush decisions, post-combat self-heal,
squad splitting with radio comms, boss hunting and faction rivalries
(Firefly's idea), airdrop ambushes, a "rally flare" item that pulls the
whole map onto a point, multi-step objectives (Kiba alarm, ULTRA power,
Reserve D-2...), switch-gated and Red-Rebel-style exfils, cross-raid
player heatmaps feeding bot routing (Fiodor's idea), per-map ORBIT
toggle. Suggestions land on the Discord thread.

## Known issues

- The AI limiter and zone editor are brand new in 2.0: expect tuning
  passes. Fika support for the limiter is designed in but untested.
- Most Reserve exfils need switches ORBIT can't operate yet.
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
