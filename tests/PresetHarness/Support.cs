namespace SPTarkov.DI.Annotations
{
    public enum InjectionType { Singleton }
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class InjectableAttribute(InjectionType type) : Attribute
    {
        public InjectionType Type { get; } = type;
    }
}

namespace SPTarkov.Common.Models.Logging
{
    public interface ISptLogger<T>
    {
        void Info(string message);
        void Error(string message);
    }
    public sealed class TestLogger<T> : ISptLogger<T>
    {
        public List<string> Lines { get; } = new();
        public void Info(string message) => Lines.Add(message);
        public void Error(string message) => Lines.Add(message);
    }
}
