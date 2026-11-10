# ORBIT roadmap

## Planned for 3.1.1

Target release: early in the week of November 9, 2026 (tentative).

- Implemented: [per-bot control API](BOT_CONTROL_API.md) for Harmony/PBT and other integrations. The calling mod explicitly excludes a bot with `ExcludeBot` (wake and release control), then can restore normal ORBIT/Ghost eligibility with `EnableBot`. Reapply exclusions on each spawn. Automated checks pass; in-raid PBT validation is still pending.

## Other ideas

No ETA, no promises:

- A "rally flare" item that pulls the whole map onto a point.
- Additional special extracts.
- Cross-raid player heatmaps feeding bot routing (Fiodor's idea).
- A configurable, weighted chance to send bots toward zones tied to the player's active kill quests on the relevant map (Shynd's idea).
- An optional distance-weighting toggle for Rush objective selection, favouring nearby targets when enabled and preserving the current variety when disabled (Klide's idea).
- Squad members watching the sides and rear while travelling, with coordinated look directions (baratrent's idea).

Suggestions land on the Discord thread.

[Back to the README](README.md)
