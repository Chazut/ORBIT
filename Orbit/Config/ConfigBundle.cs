using System.IO;
using System.Reflection;
using Newtonsoft.Json;

namespace Orbit.Config;

/// <summary>
/// JSON-backed configuration bundle. Reads-or-creates a file under the
/// plugin's Config/ directory on first access; subsequent calls to
/// <see cref="Reload"/> re-read it from disk so hot-edits take effect
/// without a game restart.
/// </summary>
public class ConfigBundle<T> where T : class
{
    private readonly string _filePath;

    public ConfigBundle(string path, T defaultValue)
    {
        _filePath = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) + "/Config", path);

        if (!File.Exists(_filePath))
        {
            var parent = Directory.GetParent(_filePath);
            if (parent != null)
            {
                Directory.CreateDirectory(parent.FullName);
            }
            File.WriteAllText(_filePath, JsonConvert.SerializeObject(defaultValue, Formatting.Indented));
        }

        Value = JsonConvert.DeserializeObject<T>(File.ReadAllText(_filePath));
    }

    public T Value { get; private set; }

    public void Reload()
    {
        Value = JsonConvert.DeserializeObject<T>(File.ReadAllText(_filePath));
    }
}
