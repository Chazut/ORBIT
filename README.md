<p align="center">
  <img src="branding/orbit-logo-1024.png" width="130" alt="ORBIT" />
</p>

<h1 align="center">ORBIT</h1>

<p align="center">
  <b>Objective-driven Raid Bot Intelligence Tactics</b><br/>
  Raids that feel alive, and a "Ghost Mode" to run it all at high fps.
</p>

<p align="center">
  <img src="https://img.shields.io/github/stars/Chazut/ORBIT?style=flat-square&label=STARS&color=007ec6" />
  <img src="https://img.shields.io/github/issues/Chazut/ORBIT?style=flat-square&label=ISSUES&color=44cc11" />
  <img src="https://img.shields.io/github/downloads/Chazut/ORBIT/total?style=flat-square&label=DOWNLOADS&color=44cc11" />
</p>

> ⚠️ Built on Phobos's MIT-licensed foundation, with [Janky's](https://sp-mod.com/user/72916/jankytheclown) permission.

Bots pursue objectives, loot, upgrade their gear and extract. Ghost Mode keeps distant activity going while reducing CPU load.

**ORBIT 3.0 · SPT 4.1.x · Solo & Fika**

[Wiki](https://github.com/Chazut/ORBIT/wiki) · [Discord](https://discord.com/channels/875684761291599922/1509314495019745451) · [GitHub](https://github.com/Chazut/ORBIT)

[Features](#features) · [Install & start](#install--start) · [Customize](#customize) · [Compatibility & Fika](#compatibility--fika) · [Help & FAQ](#help--faq) · [About ORBIT](#about-orbit) · [Credits & support](#credits--support)

---

**ORBIT 3.0.0**: [Downloads](https://github.com/Chazut/ORBIT/releases) · [Full changelog](CHANGELOG.md) · [Fika addon 1.2.0](CHANGELOG.md#orbit-fika-addon-120)

## Features

### More to do in every raid

| Feature | What you can encounter |
|---|---|
| **Raid objectives** | Loot runs, hunting and visits to quest locations for PMCs and AI PlayerScavs. |
| **Rush objectives** | PMC squads rush spawn sectors, boss areas, marked rooms or custom destinations. |
| **Sniper positions** | A scoped PMC takes a firing position while squadmates guard nearby. |
| **Ambushes & extract camps** | Squads hold hotspots or wait near extracts, using cover and sightlines. |
| **Airdrops** | Squads approach falling crates and cover each other while looting. |
| **Multi-step operations** | Power, switches and doors: KIBA, ULTRA Medical, Saferoom, D-2 and more. |
| **Looting** | Bots search loose loot, containers and bodies, based on value, space and personality. |
| **Equipment upgrades** | Better weapons, armour, rigs, bags and headsets; useful attachments stripped from weapons. |
| **Squads & personalities** | Shared goals, looting cover and regrouping. SAIN personalities shape travel, risk and priorities. |
| **Extraction** | PMCs and AI PlayerScavs leave based on objectives, loot, time or injuries. Includes supported conditional exits and backpack drops. |

*Regular Scavs use separate roaming and looting behaviour. Special objectives vary by bot type, personality and settings.*

### Ghost Mode

Distant bots use a lighter simulation to reduce CPU load while continuing supported activities.

- **Continued activity:** movement, looting and gunfire investigation.
- **Off-screen combat:** simulated battles with gunfire, wounds and lootable casualties, or real fights that wake the bots. Ghost fights can also be disabled.
- **Vision:** terrain, weather and visibility affect distant encounters.
- **Automatic waking:** proximity, combat and scoped views; optional waking for spectating.
- **Native support:** supported bosses and factions keep their own routines while in Ghost.

### Make it your raid

- **Web UI & presets:** tune behaviour, Ghost settings and faction control; save and share setups, with supported per-map overrides.
- **Zone editor:** attraction/avoidance zones, floors, bot filters and optional player convergence.
- **Door capture:** create custom room-rush points directly from an in-game door.
- **Fika & diagnostics:** co-op addon, optional Raid Review integration and built-in debug ZIP export.

> **ORBIT handles activity between fights and Ghost simulation.** Awake combat stays with SAIN/native AI. Spawns and starting gear stay with your spawn system.

## Install & start

### Requirements

Use builds compatible with your **SPT 4.1.x** version:

- [SAIN](https://sp-mod.com/mod/791/sain-solarints-ai-modifications-full-ai-combat-system-replacement)
- [BigBrain](https://sp-mod.com/mod/902/bigbrain)
- [Waypoints](https://sp-mod.com/mod/827/waypoints-expanded-navmesh)

> **Highly recommended: [SAIN Personality Mixer](https://github.com/Chazut/SAIN-Personality-Mixer).** This optional SAIN addon lets you set PMC personality chances in F12, regardless of level or gear, for more varied ORBIT behavior.

### Installation

1. Close the game and SPT server.
2. Install the dependencies and extract the **complete ORBIT ZIP** into your SPT install folder. Merge the folders.
3. Start the server, launcher and game.
4. **Turn SAIN extraction OFF** and save your SAIN preset.
5. Open **F12 > ORBIT > Open web config UI**. Start with the **Default** preset, click **Save**, then play a raid.

Local UI: `https://127.0.0.1:6969/orbit`, with the server running. Use your server's address if different.

Keep **Ghost Mode**, **Ghost movement** and **Vanilla / faction movement** ON for your first test.

### Updating

Close the game/server, install the complete update and check its release notes.

Most behaviour changes apply to the next raid. **Restart the game after changing faction takeover or Vanilla switches.**

[Detailed installation guide](https://github.com/Chazut/ORBIT/wiki/Installation-and-updating) · [Quick start](https://github.com/Chazut/ORBIT/wiki/Quick-start)

## Customize

### Presets

Use **Presets > Save as new preset**. Apply or export behaviour settings, zones, or both. Editing Default or an addon creates a personal copy.

### Find the setting you need

| I want to... | Look here |
|---|---|
| Change objectives | Main objectives, Rush objectives, Ambush & camping, Multi-step operations |
| Change how PMCs travel or loot | SAIN personalities and Looting |
| Keep squads in the raid longer | Extraction: loot, objective-completion and time triggers |
| Reduce simulated Ghost deaths | Ghost Mode: Rare fight frequency and lower lethality |
| Keep a faction's original routines | Factions: leave takeover OFF or enable its Vanilla option |
| Change where bots travel | Zone editor, Zones & convergence and Map settings |
| Share my setup | Presets > Export preset ZIP |

### Shape the map

Draw attraction/avoidance zones, set their radius, filter by floor or bot type, and edit rush destinations. Zones affect ORBIT-controlled bots.

[See the visual zone editor](https://imgur.com/8zGYnfK.png)

### How do I attract bots to my quest area?

1. Open **Zone editor** and select your map/layout.
2. Double-click the quest location to create a zone, or select an existing one.
3. Choose the targets you need under **Bot types** and set **Zone floor** if relevant.
4. Keep **Force min/max** positive for attraction. Set **Radius min/max** to reach nearby routes: this is the attraction range, not the quest boundary.
5. Enable **Kill-main anchor** if you also want PMCs and AI PlayerScavs to choose this area as a hunting objective.
6. Click **Save** and test in a new raid. Increase force for a stronger pull, or radius to reach farther.

Only **ORBIT-controlled bots** follow zones. For regular Scavs, increase **Scavs area roaming** if you want them to leave their usual areas. Results depend on their routes and other objectives.

### Community setups

[Baseline Zones by FoxHead](https://sp-mod.com/addon/124/baseline-zones) · [Live Like by TomiNyxer](https://sp-mod.com/addon/135/live-like-orbit-full-config-routes). Check version compatibility before importing.

[Configuration guide](https://github.com/Chazut/ORBIT/wiki/Configuration-guide) · [Presets & addons](https://github.com/Chazut/ORBIT/wiki/Presets-and-addons) · [Zone editor guide](https://github.com/Chazut/ORBIT/wiki/Zones-and-map-settings)

## Compatibility & Fika

### Other mods

| Mod or system | What to know |
|---|---|
| **SAIN** | (Required) Turn its extraction OFF when using ORBIT extraction. |
| **[SAIN Personality Mixer](https://github.com/Chazut/SAIN-Personality-Mixer)** | (Highly recommended) Custom PMC personality chances for more varied ORBIT behavior. Optional. |
| **Spawn & loadout mods** | (Recommended) [ABPS](https://sp-mod.com/mod/2097/abps-acids-bot-placement-system) and [APBS](https://sp-mod.com/mod/1594/apbs-acids-progressive-bot-system) handle spawns/gear independently of ORBIT. |
| **[Raid Review](https://sp-mod.com/mod/1479/raid-review)** | (Recommended) Replay of routes, objectives, looting and Ghost activity. |
| **Faction mods** | (Compatible) Per-faction takeover and supported native Ghost behaviour. Some actions require waking. |
| **Bot Healing Fixes** | (Compatible) Supported medicine routine paused in native Ghost; unchanged while awake. |
| **PiP Disabler** | (Compatible) Scoped-wake integration, including remote views with the matching Fika addon. |
| **Questing Bots, Looting Bots & Phobos** | Incompatible |
| **Other AI limiters / culling mods** | Incompatible |
| **Smoothie** | (Not recommended) Reported weird movements and animations. |

### Playing with Fika

Install the **complete ORBIT ZIP**, dependencies and matching **[Fika addon](https://sp-mod.com/addon/123/orbit-fika-addon)** on the host, joining players and headless client if used. Keep versions identical.

The addon synchronizes **Ghost gunfire, supported doors/switches and remote scope views**. Configuration is shared through the SPT server.

[Full compatibility notes](https://github.com/Chazut/ORBIT/wiki/Compatibility) · [Fika setup](https://github.com/Chazut/ORBIT/wiki/Fika) · [Raid Review](https://sp-mod.com/mod/1479/raid-review)

## Help & FAQ

### ORBIT or Questing Bots + Looting Bots?

Please stop asking 😅 Give each setup several raids and see which feels better to **YOU**. Both handle bot movement outside combat, but each has its own approach and feel. Most people answering haven't tried both. **Form your own opinion!**

### Why isn't a bot using an ORBIT objective?

Main objectives are for PMCs and AI PlayerScavs. Scavs have separate roaming/looting behaviour; some bosses and factions stay native. All can use supported Ghost behaviour.

### Will Ghost Mode improve my FPS?

YES. Gains depend on your CPU, map, bot population and modpack. Simulated Ghost fights cost less than real fights.

### Something looks wrong

Check versions and overlapping mods, then try the Default preset. For a report:

1. Set **Quiet logging OFF** and enable **all Log levels** in F12 before reproducing.
2. For performance issues, also enable **Performance logging** before the raid.
3. Note the map, bot name and raid time; click **Create debug ZIP**, then **Open debug folder**.
4. Share the ZIP and a short description on [Discord](https://discord.com/channels/875684761291599922/1509314495019745451) or [GitHub](https://github.com/Chazut/ORBIT/issues).

Reports include available diagnostics and supported Raid Review data. Difficult navigation and unsupported native actions remain limitations; see the wiki below.

[Troubleshooting](https://github.com/Chazut/ORBIT/wiki/Troubleshooting) · [Performance guide](https://github.com/Chazut/ORBIT/wiki/Performance) · [Debug reports](https://github.com/Chazut/ORBIT/wiki/Debug-reports-and-Raid-Review)

Future plans: [Roadmap](ROADMAP.md).

## About ORBIT

### AI usage

**I'm a C# developer with over 10 years of experience, and I use AI to generate ORBIT's code.**

**In my opinion, writing code is only a small part of creating a mod on this scale.** Much of the work is deciding how everything should behave and making it work together: extensive test raids, reproducing bugs, analysing logs and Raid Review recordings, building diagnostic dashboards, tuning performance and checking for regressions.

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

## Credits & support

- **[Phobos](https://discord.com/channels/875684761291599922/1337131427803955200) by [Janky](https://sp-mod.com/user/72916/jankytheclown):** ORBIT's advection-field cell-routing foundation, MIT-licensed and used with explicit permission.
- **[Questing Bots](https://sp-mod.com/mod/1109/questing-bots) by [danW](https://sp-mod.com/user/27632/danw):** inspiration for quest routing; no code reused.
- **[Looting Bots](https://sp-mod.com/mod/812/looting-bots) by [Skwizzy](https://sp-mod.com/user/28069/skwizzy) and [ArchangelWTF](https://sp-mod.com/user/52282/archangelwtf):** ORBIT began as a Phobos + Looting Bots merge; its loot layer has since been rewritten from scratch.
- **[SAIN](https://sp-mod.com/mod/791/sain-solarints-ai-modifications-full-ai-combat-system-replacement) by [Solarint](https://sp-mod.com/user/27463/solarint), [ArchangelWTF](https://sp-mod.com/user/52282/archangelwtf) and [DrakiaXYZ](https://sp-mod.com/user/27605/drakiaxyz):** combat AI and the personality system ORBIT uses.
- **[BigBrain](https://sp-mod.com/mod/902/bigbrain) by [DrakiaXYZ](https://sp-mod.com/user/27605/drakiaxyz).**
- **[Andrewgdewar](https://github.com/Andrewgdewar):** per-faction area-roaming percentages (community PR).
- **[tarkov.dev](https://tarkov.dev):** map renders used by the zone editor.
- **The SPT team, testers, addon authors and community:** feedback, reports, ideas and patience through all those betas.

[View Janky's Phobos authorization](https://i.imgur.com/ifGx54S.png)

Enjoying ORBIT? [Buy me a coffee](https://ko-fi.com/chazut). Thank you!

[Source code](https://github.com/Chazut/ORBIT) · [Complete wiki](https://github.com/Chazut/ORBIT/wiki) · [Join the discussion](https://discord.com/channels/875684761291599922/1509314495019745451)
