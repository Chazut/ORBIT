# Per-bot control exclusion

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

Enabling restores the bot's normal eligibility for ORBIT and Ghost Mode. It does not force ORBIT to
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
