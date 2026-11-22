namespace SPTarkov.DI.Annotations
{
    public enum InjectionType { Singleton }
    public class InjectableAttribute(InjectionType type) : Attribute { public InjectionType Type => type; }
}

namespace SPTarkov.Common.Models.Logging
{
    public interface ISptLogger<T> { void Info(string message); void Error(string message); }
    internal sealed class Logger<T> : ISptLogger<T>
    {
        public void Info(string message) { }
        public void Error(string message) => Console.Error.WriteLine(message);
    }
}

namespace UnityEngine { public class Object { } }
namespace UnityEngine.SceneManagement
{
    internal static class SceneManager
    {
        internal static int sceneCount => 0;
        internal static Scene GetSceneAt(int index) => throw new ArgumentOutOfRangeException(nameof(index));
    }
    internal record struct Scene(string name);
}

namespace Fums.Customs.Client
{
    internal static class BundleSceneLoader { internal static bool Active { get; set; } }
}
