> **ORBIT** - Objective-driven Raid Bot Intelligence Tactics
> 
> Smarter bots. Real objectives. Raids that feel alive.

Bots in your raids no longer just patrol and shoot. With ORBIT, they have
goals: rich loot spots to clear, PvP hotspots to hunt, quest triggers to
visit, and a real reason to head for extract. They coordinate, loot
together, and leave when they're done - just like players.

Built on [Phobos](https://discord.com/channels/875684761291599922/1337131427803955200)'s foundations  (advection field, cell dispatch, squad movement), with looting ported from [LootingBots](https://forge.sp-tarkov.com/mod/812/looting-bots) and quest routing inspired by [QuestingBot](https://forge.sp-tarkov.com/mod/1109/questing-bots), integrated into a single coherent system (with extra features) instead of three layers fighting for control.

It started as my own personal "best of the three" - picking the parts I
liked from each and gluing them together. Along the way it grew well
beyond that, into something I'm proud enough of to share.

Pair it with the latest [Raid Review](https://forge.sp-tarkov.com/mod/1479/raid-review) to see what every bot was doing on the post-raid map replay.

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

### Installation

1. Install dependencies first:
   - [BigBrain](https://forge.sp-tarkov.com/mod/902/bigbrain) by [DrakiaXYZ](https://forge.sp-tarkov.com/user/27605/drakiaxyz)
   - [SAIN](https://forge.sp-tarkov.com/mod/791/sain-solarints-ai-modifications-full-ai-combat-system-replacement)
2. Extract the zip in your SPT root folder.
3. Launch the game. You'll see `ORBIT 1.0.0` in the bottom-left version
   label when it's loaded.

All tuning lives in the F12 menu - open it in-game and tweak live.

**Two recommended SAIN tweak**: 
- Tweak SAIN Personalities chance (see the next tab).
- disable SAIN's extract layer so it doesn't fight ORBIT's extract logic. Open `BepInEx/plugins/SAIN/Presets/<your_preset>/GlobalSettings.json` and set:
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

Any other AI / bot-behavior mod will either fight ORBIT for control or
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
- ORBIT has its own loot pipeline with per-personality thresholds.
- Running both means bots loot inconsistently and ORBIT's extract
  triggers can't track the loot correctly.

**Any other "AI overhaul" mod**
- If a mod replaces bot brain logic, dispatches bots somewhere, or
  controls looting / extracting / questing, assume it conflicts unless
  proven otherwise.

### Roadmap

No ETA, no promises, but on the list:

- Bots prefer loot on the same floor (no more elevator yo-yo on Resort)
- Members can extract alone if they personally hit their loot threshold
- Squads can decide to camp + ambush instead of always roaming
- "Marked-key loot rush" objective for high-tier squads
- "Spawn rush" objective for the most aggressive personalities
- "Boss hunting" objectives
- Smarter movement - checking corners, scanning the rear, less straight-line
  dashing
- Reserve exfils (currently disabled - they all need switches/levers)
- Squad splitting with radio comms
- New personalities
- Airdrop / helicopter crash / BTR objectives

### Known Issues

- **Reserve exfils disabled for bots** - every Reserve exfil needs
  switches/levers ORBIT can't handle yet. Bots on Reserve stay until killed
  or the raid ends.
- **Rare stuck bots** - usually unstick themselves within a minute. Still
  iterating.
- **Mod conflicts** - tested with my own config. Yours may differ. Report
  anything obviously broken on [GitHub](https://github.com/Chazut/ORBIT/issues).
- **Initial release** - expect a few rough edges. Please report what you
  find.

### About AI

I want to be upfront: I used **Claude** as a coding assistant on this mod.

That doesn't mean it's vibe-coded slop. I spent days reading the source
of Phobos, LootingBots, and QuestingBot, and built custom debug overlays
in Raid Review so I could *see* what every mod was doing per-frame before
writing a single line of ORBIT. I'm the architect; the LLM is a productivity
tool - same as a senior dev using Stack Overflow doesn't make them a fraud.

I have 10+ years of professional dev experience. I know what I'm shipping.

If that's a dealbreaker for you, I understand - uninstall and move on, no
hard feelings. If you can judge a mod on what it does rather than how it
was written, give it a try.

### Credits

A huge thank you to the authors listed below - their MIT-licensed code formed the foundation I built ORBIT on top of.
> ORBIT reuses code from **Phobos** and **LootingBots**, under the MIT license. Permission requests to both authors are in progress.
> ORBIT will return to the Hub once cleared.

-  [Phobos](https://discord.com/channels/875684761291599922/1337131427803955200) by [janky](https://forge.sp-tarkov.com/user/72916/jankytheclown) - the original advection-field
  cell dispatch that ORBIT is build around.
- [LootingBots](https://forge.sp-tarkov.com/mod/812/looting-bots) by [Skwizzy](https://forge.sp-tarkov.com/user/28069/skwizzy) and [ArchangelWTF](https://forge.sp-tarkov.com/user/52282/archangelwtf) - what lets ORBIT's bots open containers and grab gear.
- [QuestingBot](https://forge.sp-tarkov.com/mod/1109/questing-bots) by [danW](https://forge.sp-tarkov.com/user/27632/danw) - inspired the quest-routing concept, no code reused.
- [SAIN](https://forge.sp-tarkov.com/mod/791/sain-solarints-ai-modifications-full-ai-combat-system-replacement) by [Solarint](https://forge.sp-tarkov.com/user/27463/solarint), [ArchangelWTF](https://forge.sp-tarkov.com/user/52282/archangelwtf) and [DrakiaXYZ](https://forge.sp-tarkov.com/user/27605/drakiaxyz) - without it, no personality system to plug into
- [BigBrain](https://forge.sp-tarkov.com/mod/902/bigbrain) by [DrakiaXYZ](https://forge.sp-tarkov.com/user/27605/drakiaxyz)
- The **SPT team** for an amazing modding framework
- The **SPT Discord** 
- **You**, for trying the mod

### Support

If ORBIT made your raids more interesting and want to support my work, feel free to buy me a coffee!

[![ko-fi](https://ko-fi.com/img/githubbutton_sm.svg)](https://ko-fi.com/chazut)

All my mods are free and open source. Your support keeps me motivated to create more!
