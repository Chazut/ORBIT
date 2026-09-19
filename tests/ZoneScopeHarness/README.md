# Zone scope checks

Run `dotnet run --project tests/ZoneScopeHarness -c Release`.

Compiles the production floor catalogue, scope contract, waypoint scope partial, client zone JSON
models and server zone store. Checks stacked floors, local building bounds, variant renders,
population filters, attraction from another floor, repulsion, reachable kill anchors, floor arrival,
old JSON, cross-serializer round trips, pack import/export, deep copies and history snapshots.
Also checks native spawn/patrol collection, multi-floor arrivals, startup file migration,
backup preservation, old packs, live metadata updates and pending editor changes.

Grid, bot, vector and path APIs are doubles. This does not validate Unity navigation, movement,
door interactions, live Blazor hosting in SPT, or raid performance.
