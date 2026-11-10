# Per-bot control exclusion

For mods that need to keep specific AI awake and handle their behavior themselves.
ORBIT does not automatically identify or exclude PBT cheaters. The owning mod decides which bots to exclude.

```csharp
// On the Unity thread, once your mod has identified this spawned bot.
bool accepted = Orbit.Api.OrbitBotControl.ExcludeBot(botOwner, "PBT");
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

The exclusion stays in effect even while the calling mod's layer is idle. It is scoped to this
`BotOwner` instance and ends on removal/raid end. Call again after respawning; there is deliberately
no re-enable operation in this initial API. Matching profile IDs never transfer an exclusion.

Call on the machine running the bots: solo, Fika host or headless. Playing clients do not send
exclusion requests through this API. It does not require PBT or Fika and does not change other mods'
independent limiters. Outside an active ORBIT runtime, on a background thread, or for a dead/invalid/human
bot, `ExcludeBot` returns `false`.

## Optional reference

If ORBIT is an optional integration, look up the public type only when its plugin is loaded and
cache the method once:

```csharp
var api = AppDomain.CurrentDomain.GetAssemblies()
    .Select(a => a.GetType("Orbit.Api.OrbitBotControl", false))
    .FirstOrDefault(t => t != null);
var exclude = api?.GetMethod("ExcludeBot", new[] { typeof(EFT.BotOwner), typeof(string) });

// Later, on the Unity thread for each chosen spawn:
bool accepted = exclude?.Invoke(null, new object[] { botOwner, "PBT" }) is true;
```

An older ORBIT version simply has no method. Retry a rejected call after ORBIT/AI initialization,
not every frame. The two examples are alternatives; only one call is needed.
