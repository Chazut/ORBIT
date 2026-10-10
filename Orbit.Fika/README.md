# ORBIT Fika addon

PiP Disabler scoped wake support requires the updated ORBIT client and addon on the host or
headless and playing clients. PiP itself is only needed on players using it. While PiP handles
the player's active optic, the addon reports its zoom and field of view to the host at most four
times per second. Reports expire after 1.5 seconds and are cleared when PiP stops handling the
optic or the player disconnects. Normal scope behavior remains unchanged for other players.
This capability is negotiated through the existing handshake; older addons receive no new packets.

Version 1.2.0-rc.2 adds PiP Disabler scoped wake synchronization. It also includes synchronization of switches activated by ORBIT's multi-step objectives,
alongside door synchronization and Ghost fight audio. It requires ORBIT 3.0.0 or newer. Use the
matching ORBIT 3.0 beta client and addon on the host or headless and every playing client.
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
