# ORBIT Fika integration

**Built into ORBIT.dll**, loaded automatically when Fika is present. Install the same updated
ORBIT on the host, every playing client and the headless client if used. Remove the legacy
`Orbit.Fika.dll`; a separate addon is no longer needed. Solo does not load this component.

The embedded bridge 1.3.0 adds car extraction state and final bot pose synchronization:

- Cars used by ORBIT share starts, cancellations and departures, including after reconnection.
  Player payment and eligibility stay native. Ordinary exits keep their existing behavior.
- A bot's final position is sent before Fika's native death message and applied just before
  the client creates its corpse. This includes Ghost deaths that occur before a movement update.
  Fika still owns health, inventory, kill credit and corpse creation.

These messages are negotiated through the existing handshake. Older integrations receive no
unknown packets, but every participant needs the update for these fixes. `WORLD SYNC:` logs
distinguish sending, receiving and applying state; warnings are throttled. A send log alone
does not prove that a corpse appeared. Native reconnect countdown timestamps are also repaired.

Protocol and adapter tests cover Fika 2.4.2 and 2.4.6 APIs. Real multiplayer validation is still required.

## Retained features

PiP Disabler scoped wake support requires the updated ORBIT client and addon on the host or
headless and playing clients. PiP itself is only needed on players using it. While PiP handles
the player's active optic, the addon reports its zoom and field of view to the host at most four
times per second. Reports expire after 1.5 seconds and are cleared when PiP stops handling the
optic or the player disconnects. Normal scope behavior remains unchanged for other players.
This capability is negotiated through the existing handshake; older addons receive no new packets.

The integration synchronizes the host's Ghost firefight shot timing and stops queued audio when a shooter dies, wakes or leaves the fight.

It retains PiP Disabler scoped wake synchronization and synchronization of switches activated by ORBIT's multi-step objectives,
alongside door synchronization and Ghost fight audio. It requires ORBIT 3.0.0 or newer. Use the
same ORBIT client build on the host or headless and every playing client.
Older addons do not synchronize these switch interactions. Door compatibility is requested once
per network session, with a warning if the host does not respond; use matching addon versions.

ORBIT performs its usual local door operations. The addon watches only doors involved in those
operations, briefly checks that their state and angle have settled, and sends a terminal snapshot
to compatible clients. Awake bots retain Fika's normal interaction animations. Ghost door changes
are reconciled without a body interaction or an additional door sound on clients.

The old synthetic Breach interaction is removed. A normal unlock never breaks the door.
Snapshots include the real broken state when a door has actually been breached. Local-only doors
are excluded. Solo does not load this addon, and its normal ORBIT door operations are preserved.

Native interactions invalidate older pending corrections. Client actions use acknowledgements
on the same reliable ordered channel as Fika interactions, so an in-flight old snapshot cannot
undo a new local action. The core also discards delayed door finalizers superseded by an external
interaction. Reconnection samples the current host door objects instead of replaying cached states.

Diagnostics start with `DOOR SYNC:`. Debug logs show sent/applied state and angle. Warnings name
doors that failed to settle or could not be found on a client. A send log is not proof of receipt.
No map-wide polling is performed; an unresolved operation expires after 16 seconds.

Build both projects in Release. Isolated protocol, ordering, engine-adapter and native Ghost
regression tests accompany this change. A real host/headless multiplayer raid remains necessary
to validate Unity animations, geometry and interaction timing before release.
