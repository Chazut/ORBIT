# ORBIT Fika addon

Version 1.2 adds door synchronization alongside Ghost fight audio. Use the matching new ORBIT
client and addon on the host or headless and every playing client. The server mod is unchanged.
An older addon cannot receive the new door protocol. A new client requests compatibility once
per network session and logs a warning if the host does not respond; use matching versions.

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
