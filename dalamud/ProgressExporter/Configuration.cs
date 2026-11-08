using System;
using Dalamud.Configuration;

namespace ProgressExporter;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;
    /// <summary>Code the phone app must send to read your progress over Wi-Fi.</summary>
    public string PairCode { get; set; } = "";
    public bool WifiSync { get; set; } = true;
    public int Port { get; set; } = 47823;

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
