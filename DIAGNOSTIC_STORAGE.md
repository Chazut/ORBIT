# Diagnostic storage

ORBIT and COMET each manage their own folder under `BepInEx`. Configure them in
**F12 > the mod > Diagnostics storage**:

- Automatic cleanup: enabled.
- Recent raids to keep: **20**, including every split log and performance file.
- Storage limit: **5 GB per mod** (5,000,000,000 bytes).
- **No age limit.** Old archives remain until the storage limit requires pruning.

Older diagnostic raids are compressed into `archives/raid-*.zip`. Every file's
SHA-256 is verified from the ZIP before its original is removed. If space is
still over the limit, the oldest verified archives are removed first. The twenty
most recent raids always take priority, even if they alone exceed the limit.

Move comparisons you want to preserve indefinitely into a **protected** subfolder.
That folder is excluded from cleanup and the quota. Alternatively, create an empty
file named `the-log.jsonl.keep` or `the-archive.zip.keep`; one marked file protects
its entire raid. Unrecognized files, linked paths and manual debug ZIPs are not
deleted. They can prevent reaching the storage limit.

ORBIT now records exact raid membership for its diagnostic captures. Older captures
are grouped only when their journal time window is unambiguous; otherwise they
stay untouched. The last-raid debug export and Raid Review data remain available.

Cleanup runs in a background worker outside raids, after startup or a return to
the menu. A new raid interrupts compression. Active writers are skipped and the
next pass retries. Quiet logging, performance capture and the RR stream are unchanged.
To inspect an archived raid, extract its ZIP to a separate working folder.

`Tests/StorageHarness` exercises the production file operations and BepInEx settings.
Its `--plan <mod-folder> COMET|ORBIT [report.json]` mode is read-only;
`--clean` uses the same engine and refuses manual cleanup while the game runs.
