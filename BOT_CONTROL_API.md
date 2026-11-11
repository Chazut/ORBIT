# Per-bot control and sleep API

## Drive without Ghost sleep

For mods that want ORBIT to keep driving a bot, but keep its body awake:

```csharp
Orbit.Api.OrbitBotControl.SetBotSleepAllowed(botOwner, false, "YourMod");

// Later, restore the normal Ghost policy:
Orbit.Api.OrbitBotControl.SetBotSleepAllowed(botOwner, true, "YourMod");
```

The sleep-only API keeps the existing ORBIT agent, squad, objectives, loot tasks and normal combat
handoffs. It does not force ORBIT onto bots excluded by role or take control away from combat AI.
If already in Ghost, the bot wakes safely on the next update (landing or initialization can delay this).
Managed squads sleep together, so keeping one member awake also prevents its ORBIT squad from sleeping;
sleeping peers wake through the normal squad transition. Other squads are unaffected.

`true` as the `allowed` argument removes this sleep restriction; it does not put the bot to sleep
immediately. `EnableBot` and `SetBotSleepAllowed` change independent policies: neither undoes the
other. Full `ExcludeBot` always blocks sleep, even if `SetBotSleepAllowed(bot, true)` was called.
To return from full exclusion to drive-without-sleep, set sleep allowed to `false`, then call `EnableBot`.

```csharp
bool sleepBlocked = Orbit.Api.OrbitBotControl.IsBotSleepBlocked(botOwner);
```

This query includes both sleep-only restrictions and full exclusions, including pending wakes.
It describes API policy, not physical readiness. Repeat calls are safe; the last sleep-policy call wins.
Like the control API below, use it on the Unity thread on the AI authority, per spawned BotOwner.
Calls return false outside the runtime, on another thread or for invalid/dead/human bots. Removal and
raid end clear the policy. The `source` is a log label, not a per-mod lock. Other mods' limiters are unaffected.
Logs use `BOT SLEEP: blocked`, `awake`, `allowed`, `wake pending` and `wake retry`.

Optional integration can cache the public method without a hard ORBIT dependency:

```csharp
var setSleepAllowed = api?.GetMethod("SetBotSleepAllowed",
    new[] { typeof(EFT.BotOwner), typeof(bool), typeof(string) });
bool accepted = setSleepAllowed?.Invoke(null, new object[] { botOwner, false, "YourMod" }) is true;
```

Here `api` is the type resolved in the optional-reference example below. An older build may not have this method.

## Full control exclusion

For mods that need to keep specific AI awake and handle their behavior themselves.
ORBIT does not automatically identify or exclude PBT cheaters. The owning mod decides which bots to exclude.

```csharp
// On the Unity thread, once your mod has identified this spawned bot.
bool accepted = Orbit.Api.OrbitBotControl.ExcludeBot(botOwner, "PBT");

// Later, after your mod releases control of this bot:
bool enableAccepted = Orbit.Api.OrbitBotControl.EnableBot(botOwner, "PBT");
```

`true` means the request was accepted, including repeat calls. ORBIT stops controlling the bot,
cancels its loot work, removes it from its ORBIT squad, and wakes its body if it was in Ghost Mode.
Other squad members keep their normal behavior. The game/SAIN/PBT can then control the bot.

Cleanup and wake run on the next ORBIT update. Physical activation can take longer if a safe landing
has not been found. The API does not revive a dead bot, force an initializing bot active or teleport
an airborne bot to an arbitrary floor. ORBIT's normal distance and population limits do not block this wake.

```csharp
bool excluded = Orbit.Api.OrbitBotControl.IsBotExcluded(botOwner);
bool stillGhost = Orbit.Api.OrbitTelemetry.IsBotDormant(botOwner.ProfileId);
```

`excluded` includes accepted requests still waiting for cleanup/wake. `stillGhost == false` only describes
ORBIT dormancy; it is not a general bot-readiness check. For readiness, also check the bot's native
activation state. Logs distinguish `requested`, `wake pending`, `release retry` and `released`.

The exclusion stays in effect even while the calling mod's layer is idle. `EnableBot` removes it on
the next ORBIT update, after any pending safe wake/cleanup. `IsBotExcluded` stays true until that is
complete. Repeating either request is safe; if they alternate before the update, the last request wins.

Enabling restores the bot's normal eligibility for ORBIT and Ghost Mode (unless a separate sleep-only
restriction remains). It does not force ORBIT to
take over during combat or bypass role exclusions or your Ghost settings. An eligible brain layer
gets a fresh ORBIT agent and rejoins its native squad's ORBIT group (or a new group if none remains).
Old detached objectives/loot tasks are not resumed. Bots with no eligible ORBIT layer retain native behavior.
`EnableBot` also returns true when no API exclusion exists; it does not change anything in that case.
The `source` string is for logging, not separate locks: these calls change one per-bot exclusion.

Both calls are scoped to this `BotOwner` instance. Removal/raid end clears it; call again after respawning.
Matching profile IDs never transfer an exclusion. Logs include `enable requested`, `enable retry` and `enabled`.

Call on the machine running the bots: solo, Fika host or headless. Playing clients do not send
exclusion requests through this API. It does not require PBT or Fika and does not change other mods'
independent limiters. Outside an active ORBIT runtime, on a background thread, or for a dead/invalid/human
bot, both calls return `false`.

## Optional reference

If ORBIT is an optional integration, look up the public type only when its plugin is loaded and
cache the method once:

```csharp
var api = AppDomain.CurrentDomain.GetAssemblies()
    .Select(a => a.GetType("Orbit.Api.OrbitBotControl", false))
    .FirstOrDefault(t => t != null);
var exclude = api?.GetMethod("ExcludeBot", new[] { typeof(EFT.BotOwner), typeof(string) });
var enable = api?.GetMethod("EnableBot", new[] { typeof(EFT.BotOwner), typeof(string) });

// Later, on the Unity thread for each chosen spawn:
bool accepted = exclude?.Invoke(null, new object[] { botOwner, "PBT" }) is true;

// Later, after releasing your own controller, also on the Unity thread:
bool enableAccepted = enable?.Invoke(null, new object[] { botOwner, "PBT" }) is true;
```

An older ORBIT version simply has no method. Retry a rejected call after ORBIT/AI initialization,
not every frame. The two examples are alternatives; only one call is needed.
