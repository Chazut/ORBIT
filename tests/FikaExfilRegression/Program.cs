using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx.Logging;
using HarmonyLib;
using HarmonyLib.Public.Patching;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.Utils;

// Run in a fresh process for each version so the two Fika assemblies cannot be unified.
internal static class Program
{
    private const BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Main(string[] args)
    {
        if (args.Length != 4)
            throw new ArgumentException("Pass <SPT root> <ORBIT.dll> <Fika.Core.dll> <2.4.2|2.4.6>.");
        var folders = new[] {
            Path.Combine(args[0], "EscapeFromTarkov_Data", "Managed"),
            Path.Combine(args[0], "BepInEx", "core"),
            Path.Combine(args[0], "BepInEx", "plugins", "spt")
        };
        AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
        {
            var name = new AssemblyName(e.Name).Name + ".dll";
            if (name == "Fika.Core.dll") return Assembly.LoadFrom(args[2]);
            if (name == "ORBIT.dll") return Assembly.LoadFrom(args[1]);
            var file = folders.Select(folder => Path.Combine(folder, name)).FirstOrDefault(File.Exists);
            return file == null ? null : Assembly.LoadFrom(file);
        };
        var fika = Assembly.LoadFrom(args[2]);
        var version = fika.GetName().Version.ToString(3);
        Check(version == args[3], "Unexpected Fika DLL: " + version);
        var main = Assembly.LoadFrom(args[1]);
        Assembly integration;
        using (var stream = main.GetManifestResourceStream("ORBIT.Fika.Integration.dll"))
        using (var bytes = new MemoryStream())
        {
            stream.CopyTo(bytes);
            integration = Assembly.Load(bytes.ToArray());
        }

        var bridge = integration.GetType("Orbit.Fika.WorldSyncBridge", true);
        var modern = version == "2.4.6";
        Check(modern || version == "2.4.2", "Unsupported test fixture version");
        var countdown = fika.GetType("Fika.Core.Networking.Packets.Generic.SubPackets." +
            (modern ? "ExfilCountdownPacket" : "ExfilCountdown"), true);
        Check(countdown.IsValueType == modern, "Unexpected countdown packet layout");
        CheckFieldInjection(countdown.GetMethod("Execute"), bridge.GetMethod("BeforeNativeCountdown", PrivateStatic), "ExfilName", true);
        var snapshot = fika.GetType("Fika.Core.Networking.Packets.World.RequestSubPackets+ExfiltrationRequest", true);
        CheckFieldInjection(snapshot.GetMethod("HandleResponse"), bridge.GetMethod("AfterExitSnapshot", PrivateStatic), "Data", false);
        CheckConstructorRollback(integration);
        CheckFailureDuringCleanup(main);
        CheckPatchLifecycle(integration, countdown.GetMethod("Execute"), bridge.GetMethod("BeforeNativeCountdown", PrivateStatic));
        CheckNetworkUnsubscribe(integration, fika);
        Console.WriteLine($"PASS: Fika {version}, HarmonyX {typeof(Harmony).Assembly.GetName().Version}, countdown, snapshot and startup rollback.");
    }

    private static void CheckFieldInjection(MethodInfo original, MethodInfo patch, string field, bool prefix)
    {
        // Exercise the shipped HarmonyX emitter with both real methods from the packaged DLLs.
        var info = new PatchInfo();
        // These are the public registration methods in SPT's HarmonyX 2.9.
#pragma warning disable CS0618
        if (prefix) info.AddPrefix(patch, "orbit.test", Priority.First, Array.Empty<string>(), Array.Empty<string>(), false);
        else info.AddPostfix(patch, "orbit.test", Priority.Normal, Array.Empty<string>(), Array.Empty<string>(), false);
#pragma warning restore CS0618
        using (var copy = new DynamicMethodDefinition(original))
        using (var context = new ILContext(copy.Definition))
        {
            context.Invoke(il => HarmonyManipulator.Manipulate(original, info, il));
            var call = copy.Definition.Body.Instructions.Single(i => i.OpCode == OpCodes.Call &&
                i.Operand is MethodReference method && method.Name == patch.Name && method.DeclaringType.FullName == patch.DeclaringType.FullName);
            var load = call.Previous;
            Check(load.OpCode == OpCodes.Ldfld && load.Operand is FieldReference member &&
                member.Name == field && member.DeclaringType.FullName == original.DeclaringType.FullName.Replace('+', '/'),
                $"Expected {original.DeclaringType.Name}.{field} before {patch.Name}, got {load}");
            Check(original.DeclaringType.GetField(field).FieldType == patch.GetParameters().Single().ParameterType,
                "Patch field type mismatch");
            // JIT the complete wrapper without running Fika's game-world code.
            var wrapper = copy.Generate();
            var signature = wrapper.GetParameters().Select(p => p.ParameterType).Concat(new[] { wrapper.ReturnType }).ToArray();
            RuntimeHelpers.PrepareDelegate(wrapper.CreateDelegate(Expression.GetDelegateType(signature)));
        }
    }

    public static void OtherPostfix() {}

    private static void CheckPatchLifecycle(Assembly integration, MethodInfo execute, MethodInfo prefix)
    {
        var orbit = new Harmony("orbit.test");
        var other = new Harmony("other.test");
        using (var log = new ManualLogSource("countdown patch test"))
        {
            other.Patch(execute, postfix: new HarmonyMethod(typeof(Program).GetMethod(nameof(OtherPostfix))));
            try
            {
                orbit.Patch(execute, prefix: new HarmonyMethod(prefix) { priority = Priority.First });
                Check(Harmony.GetPatchInfo(execute).Owners.Contains(orbit.Id), "Countdown hook missing");
                var cleanup = integration.GetType("Orbit.Fika.FikaPatchCleanup", true).GetMethod("Unpatch", PrivateStatic);
                cleanup.Invoke(null, new object[] { orbit, log });
                var owners = Harmony.GetPatchInfo(execute).Owners;
                Check(!owners.Contains(orbit.Id) && owners.Contains(other.Id), "Cleanup must only remove the owning bridge's patches");
            }
            finally { other.UnpatchSelf(); }
        }
    }

    public sealed class BrokenBridge : IDisposable
    {
        public void Initialize(object logger) => throw new InvalidOperationException("original initialization failure");
        public void Update() => throw new InvalidOperationException("disposed bridge still active");
        public void Dispose() => throw new InvalidOperationException("secondary cleanup failure");
    }

    private static void CheckNetworkUnsubscribe(Assembly integration, Assembly fika)
    {
        // Exercise a partially initialized plugin without starting an EFT scene.
        var dispatcher = fika.GetType("Fika.Core.Modding.FikaEventDispatcher", true);
        var callbacks = dispatcher.GetField("OnFikaEvent", PrivateStatic);
        var before = (Delegate)callbacks.GetValue(null);
        var subscription = dispatcher.GetEvent("OnFikaEvent");
        var type = integration.GetType("Orbit.Fika.OrbitFikaPlugin", true);
        var plugin = (IDisposable)Activator.CreateInstance(type);
        type.GetField("_initialized", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(plugin, true);
        subscription.AddEventHandler(null, Delegate.CreateDelegate(subscription.EventHandlerType, type.GetMethod("OnFikaEvent", PrivateStatic)));
        plugin.Dispose();
        Check(Equals(before, callbacks.GetValue(null)), "Partial shutdown leaked the network handler");
    }

    private static void CheckConstructorRollback(Assembly integration)
    {
        using (var log = new ManualLogSource("rollback test"))
        {
            var warnings = new List<string>();
            log.LogEvent += (_, e) => warnings.Add(e.Data.ToString());
            var cleanup = integration.GetType("Orbit.Fika.FikaPatchCleanup", true).GetMethod("Rollback", PrivateStatic);
            var original = new InvalidOperationException("patch registration failed");
            try
            {
                try { throw original; }
                catch
                {
                    cleanup.Invoke(null, new object[] { new BrokenBridge(), log });
                    throw;
                }
            }
            catch (Exception error)
            {
                Check(ReferenceEquals(error, original), "Rollback replaced the patch registration error");
            }
            Check(warnings.Count == 1 && warnings[0].Contains("secondary cleanup failure"), "Missing secondary cleanup diagnostic");
        }
    }

    private static void CheckFailureDuringCleanup(Assembly main)
    {
        var type = main.GetType("Orbit.EmbeddedFika", true);
        var loader = Activator.CreateInstance(type, true);
        var errors = new List<string>();
        type.GetMethod("Start", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(loader, new object[] {
            true, false, (Func<object>)(() => new BrokenBridge()), null, (Action<string>)(_ => {}), (Action<string>)errors.Add
        });
        type.GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(loader, null);
        ((IDisposable)loader).Dispose();
        Check(errors.Count == 2 && errors[0].Contains("original initialization failure") &&
            errors[0].Contains("BrokenBridge.Initialize") && errors[1].Contains("secondary cleanup failure"),
            "Partial initialization must report the original stack, then the cleanup failure, once each");
    }
}
