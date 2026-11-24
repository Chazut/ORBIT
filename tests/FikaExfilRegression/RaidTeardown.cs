#if NET8_0_OR_GREATER
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx.Logging;
using Mono.Cecil;
using Mono.Cecil.Cil;

internal static partial class Program
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    // Run Fika's actual subscription table without starting a Unity scene or a transport.
    private sealed class PacketManager
    {
        internal readonly object Manager;
        internal readonly IDictionary Callbacks;
        private readonly FieldInfo processorField;
        private readonly object processor;
        internal PacketManager(Assembly fika, string role, ManualLogSource log)
        {
            var type = fika.GetType("Fika.Core.Networking.Fika" + role, true);
            Manager = RuntimeHelpers.GetUninitializedObject(type);
            type.GetField("_logger", PrivateInstance).SetValue(Manager, log);
            processorField = type.GetField("_packetProcessor", PrivateInstance);
            processor = Activator.CreateInstance(processorField.FieldType);
            SetProcessorAvailable(true);
            Callbacks = (IDictionary)processorField.FieldType.GetField("_callbacks", PrivateInstance).GetValue(processor);
        }

        internal void SetProcessorAvailable(bool available) => processorField.SetValue(Manager, available ? processor : null);
    }

    private static void CheckPacketLifecycle(Assembly integration, Assembly fika)
    {
        using (var log = new ManualLogSource("packet lifecycle"))
        {
            var errors = new List<string>();
            log.LogEvent += (_, e) => {
                if ((e.Level & (LogLevel.Error | LogLevel.Warning | LogLevel.Fatal)) != 0)
                    errors.Add(e.Data.ToString());
            };
            var host = new PacketManager(fika, "Server", log);
            var client = new PacketManager(fika, "Client", log);
            var type = integration.GetType("Orbit.Fika.OrbitFikaPlugin", true);
            type.GetField("_log", PrivateStatic).SetValue(null, log);
            var ghostEvent = type.GetMethod("OnFikaEvent", PrivateStatic);
            var scopeType = integration.GetType("Orbit.Fika.ScopeSyncBridge", true);
            using var scopes = (IDisposable)Activator.CreateInstance(scopeType, PrivateInstance, null, new object[] { log }, null);
            var scopeEvent = scopeType.GetMethod("OnEvent", PrivateInstance);
            void Send(string name, PacketManager target)
            {
                var evt = fika.GetType("Fika.Core.Modding.Events.FikaNetworkManager" + name + "Event", true);
                var args = new[] { Activator.CreateInstance(evt, target.Manager) };
                ghostEvent.Invoke(null, args);
                scopeEvent.Invoke(scopes, args);
            }

            Send("Created", host);
            Check(host.Callbacks.Count == 1, "Host must receive scopes");
            Send("Destroyed", host);
            Send("Destroyed", host);
            Check(host.Callbacks.Count == 0, "Host retained a packet subscription");
            Send("Created", client);
            Check(client.Callbacks.Count == 1, "Client must receive ghost fights");
            Send("Created", host);
            Send("Destroyed", client);
            Check(client.Callbacks.Count == 0 && host.Callbacks.Count == 1, "Late destroy affected the replacement manager");
            Send("Created", client);
            Check(client.Callbacks.Count == 1 && host.Callbacks.Count == 0, "Host-to-client change leaked subscriptions");
            Send("Destroyed", client);
            Check(client.Callbacks.Count == 0, "Client retained a packet subscription");
            foreach (var manager in new[] { host, client })
            {
                manager.SetProcessorAvailable(false);
                try
                {
                    Send("Created", manager);
                    throw new InvalidOperationException("Expected Fika registration to fail without a packet processor");
                }
                catch (TargetInvocationException e) when (e.InnerException is NullReferenceException) { }
                finally { manager.SetProcessorAvailable(true); }
                Send("Destroyed", manager);
                Send("Created", manager);
                Check(manager.Callbacks.Count == 1, "Registration must recover on the next network creation");
                Send("Destroyed", manager);
            }
            scopes.Dispose();
            Check(errors.Count == 0, string.Join(Environment.NewLine, errors));
        }
    }

    private static void CheckSceneTeardown(Assembly main)
    {
        var diag = main.GetType("Orbit.Helpers.DoorRoutingDiag", true);
        var pending = (IList)diag.GetField("_pending", PrivateStatic).GetValue(null);
        var path = diag.GetField("_path", PrivateStatic);
        var clear = diag.GetMethod("Clear", PrivateStatic);
        var entryType = pending.GetType().GetGenericArguments().Single();
        // Extraction can preempt the delayed sample. Use managed stand-ins for the native objects.
        for (var raid = 0; raid < 2; raid++)
        {
            var entry = Activator.CreateInstance(entryType);
            foreach (var fieldName in new[] { "Door", "Link" })
            {
                var field = entryType.GetField(fieldName);
                field.SetValue(entry, RuntimeHelpers.GetUninitializedObject(field.FieldType));
            }
            pending.Add(entry);
            var dummyPath = RuntimeHelpers.GetUninitializedObject(path.FieldType);
            GC.SuppressFinalize(dummyPath);
            path.SetValue(null, dummyPath);
            clear.Invoke(null, null);
            clear.Invoke(null, null);
            Check(pending.Count == 0 && path.GetValue(null) == null, "Door diagnostic retained the previous raid");
        }

        var sain = main.GetType("Orbit.Sain.SainPersonality", true);
        var world = sain.GetField("_cachedBotManagerWorld", PrivateStatic);
        var manager = sain.GetField("_cachedBotManager", PrivateStatic);
        var localWorld = world.FieldType.Assembly.GetType("ClientLocalGameWorld", true);
        world.SetValue(null, RuntimeHelpers.GetUninitializedObject(localWorld));
        manager.SetValue(null, new object());
        sain.GetMethod("ClearRaid", PrivateStatic).Invoke(null, null);
        Check(world.GetValue(null) == null && manager.GetValue(null) == null, "SAIN held the finished raid");

        var corpse = main.GetType("Orbit.Navigation.CorpseApproach", true);
        var landing = main.GetType("Orbit.Systems.BotLandingGuard", true);
        foreach (var sample in new[] { (corpse, "reachPath", "ClearRaid"), (landing, "_localPath", "Clear") })
        {
            var field = sample.Item1.GetField(sample.Item2, PrivateStatic);
            var nativePath = RuntimeHelpers.GetUninitializedObject(field.FieldType);
            GC.SuppressFinalize(nativePath);
            field.SetValue(null, nativePath);
            sample.Item1.GetMethod(sample.Item3, PrivateStatic).Invoke(null, null);
            Check(field.GetValue(null) == null, sample.Item1.Name + " retained its navigation path");
        }

        using (var assembly = AssemblyDefinition.ReadAssembly(main.Location))
        {
            var dispose = assembly.MainModule.GetType("Orbit.Patches.OrbitDisposePatch").Methods.Single(m => m.Name == "Postfix");
            foreach (var cleanupMethod in new[] { diag.FullName + ".Clear", sain.FullName + ".ClearRaid",
                corpse.FullName + ".ClearRaid", "Orbit.Systems.MultiStepAccess.Reset", "Orbit.Navigation.OperationSwitchReach.Reset" })
            {
                var cleanup = dispose.Body.Instructions.Single(i => i.Operand is MethodReference m &&
                    m.DeclaringType.FullName + "." + m.Name == cleanupMethod);
                Check(dispose.Body.ExceptionHandlers.Any(h => h.HandlerType == ExceptionHandlerType.Finally &&
                    cleanup.Offset >= h.HandlerStart.Offset && (h.HandlerEnd == null || cleanup.Offset < h.HandlerEnd.Offset)),
                    cleanupMethod + " must run even if a subsystem fails to dispose");
            }
        }
    }
}
#endif
