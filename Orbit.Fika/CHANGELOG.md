# ORBIT Fika Addon 1.2.0

For ORBIT 3.0. Cumulative changes since addon 1.1.0, including the 1.2 release candidates.

## Multi-step interactions

- Synchronize switches used by ORBIT multi-step objectives from the bot owner to playing clients, including power and extraction switches.
- Retain supported ORBIT door synchronization alongside the new switch events.
- Door compatibility is negotiated once per network session; a missing host response produces a warning. Use matching addon versions throughout the session.

## Scoped waking

- Synchronize remote optic zoom and field of view when PiP Disabler controls the player's active optic, so the bot owner can apply ORBIT scoped waking.
- Reports are limited to four per second, expire after 1.5 seconds and are cleared when optic handling stops or the player disconnects.
- PiP Disabler remains optional. Normal scope handling is retained for other players; unsupported peers do not receive the new capability's packets.

## Ghost firefight audio

- Remote listeners follow the bot owner's shot schedule, including bursts, observation pauses and reload breaks.
- Synchronize fight termination and shooter removal, death or waking so obsolete queued shots stop.
- Preserve positional sound playback for each listener.

## Updating

Install **ORBIT 3.0.0** and **ORBIT Fika Addon 1.2.0** on the host/headless and every playing client. The addon is only needed for Fika. Update the entire group together; older addon versions cannot reproduce the current shot schedule.

The stable build retains the RC3 wire protocol and behaviour; the version suffix is removed. This addon supplements Fika synchronization for ORBIT events, rather than replacing Fika's general bot, inventory and corpse replication.
