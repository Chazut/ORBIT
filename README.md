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
cleaners.

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

### Roadmap

No ETA, no promises, but on the list:

**Behaviour**
- Members can extract alone if they personally hit their loot threshold
- Squads can decide to camp + ambush instead of always roaming
- Smarter movement - checking corners, scanning the rear, less straight-line dashing
- Less static regrouping (bots are easy 1-taps while waiting for squadmates)
- Post-combat self-heal if meds are in inventory
- Squad splitting with radio comms
- New personalities
- Detect bots spawned on isolated navmesh islands (e.g. near Streets transits, Factory silo) and teleport them once to a valid spot nearby so they stay in the raid instead of standing still until raid end. TP destination must respect a safety radius from the player (and other bots) so the rescue can't drop a bot right in front of someone

**Objectives**
- "Marked-key loot rush" for high-tier squads
- "Spawn rush" for the most aggressive personalities
- "Boss hunting"
- Airdrop / helicopter crash / BTR objectives
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
- Fallback to next-closest exfil if the chosen one is unreachable

**Looting (post-MVP)**
- In-raid weapon swap when bots find something strictly better (gun +
  mods + matching mags + ammo carried together)
- In-raid armour / helmet / rig / headwear swap, with item transfer from
  the old rig into the new one
- Magazine compatibility check (caliber vs the bot's current weapon)
  before considering a mag worth taking
- Spare ammo preload to the secure container instead of crowding the
  main inventory
- Strip-then-throw on a weapon the bot is about to discard - keep the
  scope / silencer / grip / laser, drop the rest
- Post-loot inventory sort so the grid stays usable as the bot fills up
- Teammates can grab a dead squadmate's spawn gear and stash it
  somewhere quiet - simulates a real squad taking care of their
  fallen friends' stuff
- Stack-aware pricing (currency / ammo stacks evaluated as bulk value,
  not single-unit)

**Tuning**
- Faction takeover split: patrols → ORBIT, checkpoints → vanilla (RUAF / UNTAR / BlackDivision)

### Known Issues

- **1.0.0 is the first public release** - a few rough edges are expected. Bug reports and feedback on the [Discord thread](https://discord.com/channels/875684761291599922/1509314495019745451) are very welcome.
- **Most Reserve exfils require switches ORBIT doesn't operate yet** - bots there mostly stay until killed or raid end.
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
