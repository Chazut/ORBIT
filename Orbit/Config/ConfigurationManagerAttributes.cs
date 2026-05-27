namespace Orbit.Config;

/// <summary>
/// Standard BepInEx ConfigurationManager attribute helper — copied verbatim
/// per the documented convention (see
/// https://github.com/BepInEx/BepInEx.ConfigurationManager). Each ORBIT
/// Config.Bind call attaches one of these via the ConfigDescription tag
/// argument to control Order, IsAdvanced, etc. in the F12 manager UI.
/// </summary>
#pragma warning disable 0169, 0414, 0649
internal sealed class ConfigurationManagerAttributes
{
    public bool? ShowRangeAsPercent;
    public System.Action<BepInEx.Configuration.ConfigEntryBase> CustomDrawer;
    public CustomHotkeyDrawerFunc CustomHotkeyDrawer;
    public delegate void CustomHotkeyDrawerFunc(BepInEx.Configuration.ConfigEntryBase setting, ref bool isCurrentlyAcceptingInput);
    public bool? Browsable;
    public string Category;
    public object DefaultValue;
    public bool? HideDefaultButton;
    public bool? HideSettingName;
    public string Description;
    public string DispName;
    public int? Order;
    public bool? ReadOnly;
    public bool? IsAdvanced;
    public System.Func<object, string> ObjToStr;
    public System.Func<string, object> StrToObj;
}
