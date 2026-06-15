> ⚠️ **Heads up:** ORBIT is built on **Phobos**'s foundation (MIT-licensed).
> Full credits at the bottom. ORBIT wouldn't exist without [Janky's](https://forge.sp-tarkov.com/user/72916/jankytheclown) work!

> **ORBIT** - Objective-driven Raid Bot Intelligence Tactics
> 
> Smarter bots. Real objectives. Raids that feel alive.

Bots in your raids no longer just patrol and shoot. With ORBIT, they have
goals: rich loot spots to clear, PvP hotspots to hunt, quest triggers to
visit, and a real reason to head for extract. They coordinate, loot
together, and leave when they're done - just like players.

Built on [Phobos](https://discord.com/channels/875684761291599922/1337131427803955200)'s foundations (advection field, cell dispatch, squad movement), with a custom looting layer on top of BSG vanilla APIs (originally inspired by [LootingBots](https://forge.sp-tarkov.com/mod/812/looting-bots), now fully rewritten) and quest routing inspired by [QuestingBot](https://forge.sp-tarkov.com/mod/1109/questing-bots) (but A LOT simpler, no code reuse) - all integrated into a single coherent system (with extra features) instead of three layers fighting for control.

It started as my own personal "best of the three" - picking the parts I
liked from each and gluing them together. Along the way it grew well
beyond that, into something I'm proud enough of to share.

Pair it with the latest [Raid Review](https://forge.sp-tarkov.com/mod/1479/raid-review) to see what every bot was doing on the post-raid map replay.

[📷 Screenshot](https://i.imgur.com/WSWqb8d.png)

Questions, bug reports, feedback: **[ORBIT Discord thread](https://discord.com/channels/875684761291599922/1509314495019745451)**.

## ORBIT

### What It Does

Every bot squad in your raid rolls a small list of goals at spawn:

- **Loot a rich zone** - clean out a high-value area, room by room
- **Hunt for fights** - anchor a known PvP hotspot, prowl for kills
- **Run a quest** - visit a real EFT quest trigger like a player would

How they pursue those goals depends on their SAIN personality:

- **Rats / Cowards** - careful, low-risk, loot a lot, extract early
- **Average** - balanced, will do a bit of everything
- **Chads / GigaChads** - aggressive, hunt PvP, skip cheap loot, push extracts
- **Timmys** - wander a bit, make weird picks, get to the wrong room sometimes

Squads coordinate: the leader picks the target, the rest spread to nearby
loot or cover. They open locked doors (sometimes), chain-loot adjacent
containers, and credit the right teammate when the corpse needs looting.

They extract when one of three things happens: they've looted enough money,
they've finished all their goals, or the raid is getting late.

### How Squads Pick Targets

Each squad's main objectives live in **cells** on the map (a coarse grid).
Once the squad's leader picks an anchor, the rest of the system works in
two layers:

**Main anchor** - the squad's current focus. One member (the leader by
default) walks straight to it. For a Kills main this is a PvP hotspot;
for a LootValue main it's a specific high-value POI in the target cell;
for a Quest main it's the trigger point of a real EFT quest.

**Splinter targets** - while the leader handles the anchor, the other
members fan out to nearby POIs inside the same cell (loose loot, corpses,
containers, synthetic patrol points). Each splinter is picked around the
member's own position with a random reservoir sample, so a 4-PMC squad
naturally ends up working a small area without all stacking on one spot.
A splinter is kept across anchor flips if it's still in range of the new
anchor - bots don't yo-yo between random POIs when the leader chain-loots
the next container two metres away.

**Own-kill credit** - when a squad scores a kill, the specific member
that landed it is the one routed straight to the corpse on the next
dispatch, not a random teammate. The killer loots the body they dropped.

**Coverage roll** - on entering a high-value loot cell, each POI inside
the cell rolls against the squad's coverage value (per-personality:
Cautious 85-95%, Average 65-75%, Aggressive 50-60%, GigaChad 30-45%). POIs
that lose the roll are quietly skipped so the squad never vacuums the
room 100%, like a real player who missed a few items.

### Looting In Detail

The looting layer is custom, built straight on top of BSG's vanilla bot
pickup APIs. It handles containers, corpses and loose world items, with a
focus on making the bots feel like real players rather than vacuum
cleaners - and bots don't just hoard, they **upgrade their own gear**
with what they find (see the swap layer below).

**Per-bot value gate (PMCs and PlayerScavs)**
- Each PMC has its own loot threshold rolled from its SAIN personality:
  Chad ~15k/slot, Average ~10k/slot, Cautious (Rat/Coward) ~5k/slot,
  GigaChad ~20k/slot, Timmy 0 (everything goes). PlayerScavs fall back to
  a 5k default.
- Value is judged **per inventory slot** (handbook price ÷ item size), so
  a tiny key worth 50k beats a 60k backpack that takes 15 slots.
- A Chad walking past a 5k mag won't bother; a Rat in the same squad will
  happily grab it.

**Bot scavs: opportunistic random pickups**
- AI scavs don't use a value threshold. They roll a per-item dice (default
  30% chance to grab) - matches the vanilla feel where scavs pick up the
  odd item but don't deliberately empty a corpse.
- PlayerScavs are excluded from this and use the PMC-style threshold path.

**Smart squad memory**
- When a Chad opens a container and rejects everything, the same POI is
  added to his personal skip list - he won't be sent back. His Chad
  teammates also skip it (same threshold). But the squad's Rat can still
  be dispatched there and clean up what the Chad refused.
- The squad's own blacklist (a hard "we're done here") only triggers when
  items were actually taken, when the POI was empty, or on transaction
  failures - never on a pure value rejection.

**Always-pick items**
- Currency stacks, frag grenades, and dogtags bypass both the value gate
  and the scav random roll. A real player never walks past a dogtag.

**Realistic search timing**
- Containers play an open/close animation (~2.5s) with the bot kneeling
  in front of the lid.
- Corpses are drained on two interleaved tracks: a **visible track**
  (helmet, weapons, scabbard, etc., grabbed sequentially with ~0.8s
  between each) and a **search track** (vest, armour, backpack, pockets,
  one slot at a time with progressive per-item reveal: 1.5s initial + 0.4s
  per extra item, capped at 8s).
- Slot order is randomised so the bot doesn't always go backpack-first,
  vest-second, pockets-third.
- Loose items trigger the kneel-and-grab animation per pickup.

**Drain order**
- Items inside grid containers (wallets, money cases, rigs, backpacks,
  pockets) are emptied **inside-out**: cash and contents first, then the
  empty wrapper. Avoids the bug where picking the wrapper consumes the
  contents and the bot then fumbles around trying to grab items that have
  already moved.
- Weapon + mods chains drain root-first (the weapon itself, then any
  detachable mods), same for armour + plates.

**Mod filtering on weapons**
- Only attachments flagged as "removable in raid" (scopes, mags, grips,
  silencers, foregrips, mounts, charging handles, dust covers, sights,
  lasers, lamps) are considered. Barrels, buttstocks, handguards and
  receivers are dropped from the queue - nobody disassembles a rifle
  mid-firefight.

**Corpse exclusions**
- PMC corpses keep their melee weapon (Scabbard slot) on the body, same
  as live EFT. Scav corpses are fully lootable.
- Secured containers are never touched, on any corpse.

**In-raid gear upgrades (the swap layer)**
- Bots upgrade what they wear and carry mid-raid: weapons, body armour,
  helmets, rigs, backpacks and headsets all compete against what the bot
  already has. Corpse gear can **displace** the bot's current piece when
  it's clearly better (a real upgrade, not a sidegrade); containers and
  ground loot only ever fill empty slots.
- Weapons aren't judged on price. The scorer weighs ergonomics, recoil,
  effective range and ammo quality, with per-map weights (a CQB build
  rates differently on Factory than on Woods) - and it only counts mags
  and ammo the bot can actually use. A sniper rifle with no compatible
  ammo in reach scores like the paperweight it is.
- A no-downgrade guard stops bots from trading a good armour-piercing
  rifle for an expensive shotgun just because the price tag says so.
- The displaced weapon goes to the corpse - but not before the bot strips
  the expensive mods (scope, suppressor, grip...) into its bag and keeps
  the mags that fit its new gun. Kill the bot later and you'll find the
  old weapon on the body, picked clean.
- Rig and backpack swaps move the bot's **entire carry into the new
  piece first** - if a single item wouldn't fit, the swap is cancelled
  rather than dropping anything. Armoured rigs only compete with
  armoured rigs.
- Scavs never swap - they only fill empty slots, vanilla style.

**Chain-loot sweep**
- After successfully looting a POI, the bot looks for nearby loose items
  or corpses within a short radius and chains to them directly - mirrors
  the way a player picks up adjacent items before walking away.
- Same-floor preference: a candidate two metres away on the floor above
  loses to one ten metres away on the same floor, so the bot doesn't
  yo-yo between basement and lobby on Resort.
- Each sweep candidate gets its own coverage roll.

### The Little Details

The stuff that makes bots feel deliberate instead of scripted:

**Movement & squads**
- Bots roam **freely** between their objectives (Phobos advection field),
  but a pull draws them toward their main goals - so they wander like
  players, not on rails, while still trending somewhere meaningful.
- Squads spread out: the leader takes the main target, the others fan
  out to nearby loot or cover instead of all stacking on one spot.
- A drifting bot won't drag its whole squad off-mission - there's a
  leash that keeps the group loosely together.
- No teleport rescues. If a bot can't reach something, it gives up and
  picks a new target like a real player would, instead of magically
  warping around.
- Scavs stay around their spawn area by default; PMCs roam the whole
  map. Both tunable.

**Doors**
- Bots only open the doors they actually need to pass through - they
  don't fiddle with every door they walk past.
- Want loot behind a locked door? They can roll to force it open, with
  a **configurable success rate** (aggressive personalities roll higher than cautious ones).

**Loot awareness**
- Bots only know about corpses they actually saw drop or that their
  squad killed - no magically pathing across the map to a body they
  couldn't possibly know about.
- See the dedicated **Looting In Detail** section above for the full
  picture (per-personality thresholds, scav random roll, smart squad
  memory, search timing, drain order, etc.).

**Objectives & extract**
- Three objective types: roam a PvP hotspot for kills, clean out a
  high-value loot zone, or visit a real EFT quest trigger.
- Squads extract for real reasons: they've looted enough, finished
  their goals, or the raid's running late - and they'll coordinate on
  shared-timer exfils (like the car) instead of leaving each other
  behind.

Almost everything above is tunable in the F12 menu, and most of it
shifts automatically based on each bot's SAIN personality.

### Installation

1. Install dependencies first:
   - [BigBrain](https://forge.sp-tarkov.com/mod/902/bigbrain) by [DrakiaXYZ](https://forge.sp-tarkov.com/user/27605/drakiaxyz)
   - [Waypoints - Expanded Navmesh](https://forge.sp-tarkov.com/mod/827/waypoints-expanded-navmesh)
   - [SAIN](https://forge.sp-tarkov.com/mod/791/sain-solarints-ai-modifications-full-ai-combat-system-replacement)
2. Extract the zip in your SPT root folder.
3. Launch the game. You'll see `ORBIT 1.0.0` in the bottom-left version
   label when it's loaded.

All tuning lives in the F12 menu - open it in-game and tweak live.

**Two recommended SAIN tweaks**:
- Tweak SAIN personality chances (see the next section).
- Disable SAIN's extract layer so it doesn't fight ORBIT's extract logic. Open `BepInEx/plugins/SAIN/Presets/<your_preset>/GlobalSettings.json` and set:
```json
"Extract": {
  "SAIN_EXTRACT_TOGGLE": false
}
```

### Personalities (Recommended SAIN Config)

ORBIT was tuned around a specific personality distribution. SAIN's own
defaults work fine, but if you want raids that match what I tested against,
go into SAIN's F12 config under **Personality → Assignment** and set:

| Personality   | Chance |
|---------------|--------|
| Rat           | 10     |
| Wreckless     | 5      |
| SnappingTurtle| 5      |
| Coward        | 5      |
| Chad          | 5      |
| Timmy         | 3      |
| GigaChad      | 3      |

Set `Can be randomly assigned` to **True** for each one.

This gives roughly a third of your PMCs interesting personalities - the
distribution ORBIT was built around.

**Note for [Twitch Player](https://forge.sp-tarkov.com/mod/1895/sain-twitch-players) users**: **Twitch Player** sets several personalities chance to **0** by default, so it's important to apply the SAIN settings as above.

### Compatible & Recommended

Spawn / loadout mods are fine and actually recommended — they shape *who* spawns and *what gear* they bring, while ORBIT decides *where they go and what they do*. The two layers don't fight each other.

- **[SAIN](https://forge.sp-tarkov.com/mod/791/sain-solarints-ai-modifications-full-ai-combat-system-replacement)** — **REQUIRED**. ORBIT plugs into SAIN's personality system; without it the mod won't load.
- **[APBS](https://forge.sp-tarkov.com/mod/963/algorithmic-progression-bot-system)** — progression-based bot loadouts. Recommended.
- **[ABPS](https://forge.sp-tarkov.com/mod/2103/another-better-progression-system)** — alternative loadout progression. Recommended.
- Other pure spawn / loadout / loot-table mods should be safe too.

### Unsupported Mods

**ORBIT supports only one other AI mod: [SAIN](https://forge.sp-tarkov.com/mod/791/sain-solarints-ai-modifications-full-ai-combat-system-replacement)**

Any other AI / bot-behaviour mod will either fight ORBIT for control or
duplicate work it already does. Don't install them alongside ORBIT.

**[QuestingBot](https://forge.sp-tarkov.com/mod/1109/questing-bots)**
- QuestingBots actually *simulates* quests - bots plant items, hold zones
  for the required time, etc.
- ORBIT is simpler: bots just route to the quest trigger location, no
  real quest mechanics.
- Both want to assign the same bot a quest at the same time → conflict.
  Pick one.

**[Phobos](https://discord.com/channels/875684761291599922/1337131427803955200)**
- ORBIT is built on Phobos's foundations, same advection field, same
  cell dispatch logic, same squad movement model. Running both means
  two systems trying to move the same bots.

**[LootingBots](https://forge.sp-tarkov.com/mod/812/looting-bots)**
- ORBIT has its own loot pipeline driving BSG vanilla pickup APIs,
  with per-personality value thresholds. Running both means two
  systems racing to loot the same containers.

**Any other "AI overhaul" mod**
- If a mod replaces bot brain logic, dispatches bots somewhere, or
  controls looting / extracting / questing, assume it conflicts unless
  proven otherwise.

**AI Limiter / Bot Culling mods**
- Anything that deactivates, pauses, freezes, or culls bots while
  they're still alive (typically to save CPU when bots are off-screen)
  breaks ORBIT's core loop. ORBIT relies on every squad running their
  full lifecycle in the background — patrolling, looting, fighting,
  extracting — even when you're nowhere near them. The moment a bot
  gets deactivated mid-objective, it never reaches the next waypoint,
  never engages, never extracts. No support planned on the ORBIT side
  for now — a built-in degraded-tickrate option for off-screen squads
  is on the roadmap as a potential future workaround.

### Troubleshooting

**Bots freeze / stand still / never extract**
- Check for AI-limiter / bot-culling mods (see Unsupported Mods).
- If you've extended raid duration via SVM, RaidOverhaul, Custom Raid
  Times etc., test with vanilla raid times — long raids have been
  reported to cause odd bot behaviour over time.
- If you have ABPS installed and recently upgraded or reinstalled it,
  delete its config file and let it regenerate from defaults — a
  stale ABPS config has been reported to clash.

**If the above doesn't help → 50/50 method**

[The 50/50 method](https://wiki.sp-tarkov.com/en/5050-method) is the
canonical SPT way to pin down a mod conflict: disable half your mods,
test the raid, see if the issue persists, split again, repeat until the
culprit is isolated. Tedious but reliable.

**If you're going to report the issue**

Quoting Shynd (FIKA dev):

> When reporting aberrant behavior to a mod dev it is best to do so
> with a much smaller subset of your normal mods — for ORBIT I would
> have just ABPS / SAIN / ORBIT — and with nothing changed in SVM or
> anything else. Each individual person here has a functionally unique
> mod pack. Let's try to make it as similar as possible before trying
> to help fix issues that may only occur for you specifically.

So before pinging me: try to reproduce the issue with just
**ORBIT + SAIN + BigBrain + Waypoints + ABPS** and default configs
across the board. If the issue still reproduces there, then I have
something I can actually act on.

**Still stuck?**

Drop by the ORBIT thread in the SPT 4.0 Discord or the SPT 4.0 support
channel — link in Support.

### Roadmap

No ETA, no promises, but on the list:

**Behaviour**
- Members can extract alone if they personally hit their loot threshold
- Squads can decide to camp + ambush instead of always roaming
- Squad rally — when one member starts taking fire, the rest break from their current objective and bee-line to support (currently each bot reacts to combat individually via SAIN, so an isolated teammate can get gunned down while squadmates have already exited combat on their end)
- Smarter movement - checking corners, scanning the rear, less straight-line dashing
- Less static regrouping (bots are easy 1-taps while waiting for squadmates)
- Post-combat self-heal if meds are in inventory
- Squad splitting with radio comms
- New personalities
- Detect bots spawned on isolated navmesh islands (e.g. near Streets transits, Factory silo) and teleport them once to a valid spot nearby so they stay in the raid instead of standing still until raid end. TP destination must respect a safety radius from the player (and other bots) so the rescue can't drop a bot right in front of someone
- Prone or crouch when looting a body in the open to minimise silhouette (a bot lying flat on a corpse in a field is way harder to spot than one standing over it)
- Weapon-type → behaviour archetype hint (CQB-leaning loadout pushes harder, sniper loadout stays back) driven by MOA + RPM + scope presence, biasing POI selection so a Mosin squad doesn't get routed into Resort interiors
- Cross-raid player-movement heatmap — aggregate the player positions raid-review already logs into a per-map occurrence map, then weight squad dispatch toward those hotspots so the side routes a player habitually rats through stop being safe over time (suggested by Fiodor on Discord)
- Built-in degraded-tickrate mode for off-screen / far-from-player squads — runs the dispatch / loot / extract loops at a lower frequency (e.g. once every 5-10s instead of every tick) to reclaim CPU on lower-end machines without flat-out deactivating bots the way external AI-limiter mods do. Goal: keep the "full lifecycle" guarantee while trying to help out low-end PCs
- Investigate why extended raid duration breaks bot behaviour — users running SVM / RaidOverhaul / Custom Raid Times with longer-than-vanilla raid lengths consistently report bots going inert, freezing, or behaving erratically (multiple Discord reports — Chern's terrain clipping, others). Currently in Troubleshooting as a "test with vanilla raid times first" recommendation, but the underlying cause isn't understood. Worth tracing what state ORBIT (or a dependency) accumulates over time that fails past the vanilla window

**Objectives**
- "Marked-key loot rush" for high-tier squads
- "Spawn rush" for the most aggressive personalities
- "Boss hunting"
- Faction-vs-faction "hunt" objective: a squad actively seeks out another faction's bots instead of just roaming. Enables rivalries like cultists hunting PMCs, UNTAR hunting scavs, or ISB ↔ Black Division hunting each other. Doubles as a robustness win for custom/vanilla bots whose stock SAIN/BSG nodes sometimes freeze and stop moving (suggested by Firefly on Discord - ISB author)
- Airdrop / helicopter crash / BTR objectives — squads slow-approach the drop zone, hold position at a nearby vantage for a few minutes (ambush window), then close in and loot. Mimics how players treat airdrops in live — nobody walks straight to the smoke.
- Multi-step objectives (activate → loot/extract):
  - Interchange Kiba (disable alarm → loot)
  - Interchange ULTRA (power on → loot)
  - Interchange Object #21WS keycard container (power on → loot)
  - Interchange Object #11SR room (power on → toilet switch → loot → extract inside)
  - Customs scav-base exfil (power on → extract)
  - Reserve bunker exfil (switch → extract)
  - Reserve D-2 (switch 1 → door switch → extract)

**Extracts**
- WorldEvent exfils (Reserve / Customs switch-gated)
- Train exfil (Armored Train availability window)
- "Drop backpack" exfils (Empty / EmptyOrSize) - usable when bot has no backpack, OR wounded bots drop the bag and use them anyway
- HasItem (RedRebel-style - bot must own a Red Rebel in inventory, but don't consume it; ignore the paracord and WearsItem gear constraints entirely)
- Chance roll on whether a squad will use the car / V-Ex (SharedTimer) exfil
- Emergency extract: a member who is bleeding or low on HP with no heals left heads for the nearest exfil alone - the rest of the squad keeps playing its objectives

**Looting**
- Post-loot inventory sort so the grid stays usable as the bot fills up
- Stack-aware pricing (currency / ammo stacks evaluated as bulk value,
  not single-unit)

**Tuning**
- Faction takeover split: patrols → ORBIT, checkpoints → vanilla (RUAF / UNTAR / BlackDivision)
- Flip the faction-control model to opt-IN instead of opt-OUT — ORBIT only controls explicitly enabled bot types, safer for future custom-bot mods

**Animations / polish**
- Keycard / key swipe animation for PMC bots opening locked rooms (currently they call the Unlock function instantly with no anim — should walk to the reader and play the swipe like a player)

### Known Issues

- **1.0.0 is the first public release** - a few rough edges are expected. Bug reports and feedback on the [Discord thread](https://discord.com/channels/875684761291599922/1509314495019745451) are very welcome.
- **Most Reserve exfils require switches ORBIT doesn't operate yet** - bots there mostly stay until killed or raid end.
- **Bots walk into the Lighthouse minefield** - the rebuilt POI generation doesn't exclude minefield zones yet, so bots can be routed straight into them. Fix planned for 1.0.1.
- **Possible interaction with CactusPie's "Transfer Loot Into Container Automatically" mod** - reported symptom: items a bot loots end up in YOUR tagged containers (SICC case, etc.). Best theory at the moment is that ORBIT routes pickups through BSG vanilla APIs (same path as the player), so a mod hooking those APIs may end up applying its logic to bot pickups too. Investigating.
- **Bots stuck at spawn on isolated navmesh** - vanilla SPT quirk where a spawn point lands a bot on a tiny chunk of navmesh disconnected from the rest of the map (Streets near transits, Factory inside the silo, etc.). Shows up more often with ORBIT than pure vanilla because vanilla's built-in TP rescue is disabled (it was teleporting bots constantly on every unreachable pick). Fix planned: targeted TP rescue that fires only when a bot is genuinely stuck for X seconds.
- **Rare stuck bots** - usually unstick themselves within a minute. Still iterating.
- **Mod conflicts** - tested with my own config. Yours may differ. Report anything obviously broken on [GitHub](https://github.com/Chazut/ORBIT/issues).

### About AI

I want to be upfront: I used **Claude** as a coding assistant on this mod.

That doesn't mean it's vibe-coded slop. I spent days reading the source
of Phobos, and built custom debug overlays in Raid Review
so I could *see* what every mod was doing per-frame before writing a
single line of ORBIT. I'm the architect; the LLM is a productivity tool -
same as a senior dev using Stack Overflow doesn't make them a fraud.

I have 10+ years of professional dev experience. I know what I'm shipping.

If that's a dealbreaker for you, I understand - uninstall and move on, no
hard feelings. If you can judge a mod on what it does rather than how it
was written, give it a try.

### Credits

A huge thank you to the authors listed below.

- [Phobos](https://discord.com/channels/875684761291599922/1337131427803955200) by [janky](https://forge.sp-tarkov.com/user/72916/jankytheclown) - the original advection-field cell dispatch that ORBIT is built around (MIT, used with explicit permission - see screenshot below).
- [QuestingBot](https://forge.sp-tarkov.com/mod/1109/questing-bots) by [danW](https://forge.sp-tarkov.com/user/27632/danw) - inspired the quest-routing concept, no code reused.
- [LootingBots](https://forge.sp-tarkov.com/mod/812/looting-bots) by [Skwizzy](https://forge.sp-tarkov.com/user/28069/skwizzy) and [ArchangelWTF](https://forge.sp-tarkov.com/user/52282/archangelwtf) - ORBIT started out as a Phobos + LB merge; over time many features were added and the looting layer was rewritten from scratch on top of BSG vanilla APIs to fit ORBIT's design better. No LB code left in the current release.
- [SAIN](https://forge.sp-tarkov.com/mod/791/sain-solarints-ai-modifications-full-ai-combat-system-replacement) by [Solarint](https://forge.sp-tarkov.com/user/27463/solarint), [ArchangelWTF](https://forge.sp-tarkov.com/user/52282/archangelwtf) and [DrakiaXYZ](https://forge.sp-tarkov.com/user/27605/drakiaxyz) - without it, no personality system to plug into
- [BigBrain](https://forge.sp-tarkov.com/mod/902/bigbrain) by [DrakiaXYZ](https://forge.sp-tarkov.com/user/27605/drakiaxyz)
- The **SPT team** for an amazing modding framework
- The **SPT Discord** 
- **You**, for trying the mod

**Phobos authorization from Janky:**

![Phobos authorization from Janky](https://i.imgur.com/ifGx54S.png)

### Support

If ORBIT made your raids more interesting and want to support my work, feel free to buy me a coffee!

[![ko-fi](https://ko-fi.com/img/githubbutton_sm.svg)](https://ko-fi.com/chazut)

All my mods are free and open source. Your support keeps me motivated to create more!
