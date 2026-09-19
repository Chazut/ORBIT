Run `dotnet run --project tests/NativeGhostHarness -c Release`.

This harness compiles the production native ghost bridge against small engine API doubles.
It checks native route completion and successive orders, pause preservation, fight pinning,
optional hunt adapter ticks, rejection of unsupported actions, fallback before blocked movement,
and cleanup. It requires no game installation or test framework packages.

Unity physics, NavMesh geometry, Harmony patch composition and the actual native brains still
require validation in SPT. A passing harness is not evidence of correct behaviour in a raid.
