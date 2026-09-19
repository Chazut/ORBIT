# Preset harness

Run `dotnet run --project tests/PresetHarness -c Release`.

Compiles the production preset, config, history and zone services with small logging/DI doubles.
Uses temporary directories only. Covers first install, 2.0 migration and exact backups, automatic
Custom copies, saved game payloads, independent editing and history, partial/combined addons,
automatic source updates (editor scans, restart and client fetch), concurrent personal edits,
folder bundles and migration from file selections, versioned filenames, deterministic overrides,
discovery errors, restart persistence, native metadata, malformed files and failed-save recovery.
Does not exercise SPT DI, Blazor circuits or an actual raid.
