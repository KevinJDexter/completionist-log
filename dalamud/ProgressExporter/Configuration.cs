using System;
using System.Collections.Generic;
using Dalamud.Configuration;

namespace ProgressExporter;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 2;
    /// <summary>Code the phone app must send to read your progress over Wi-Fi.</summary>
    public string PairCode { get; set; } = "";
    public bool WifiSync { get; set; } = true;
    public int Port { get; set; } = 47823;
    public bool IncludeName { get; set; } = false;
    /// <summary>Categories switched off; everything else is shared.</summary>
    public HashSet<string> NotShared { get; set; } = new();
    public DateTime? LastSync { get; set; }
    public string LastSyncSummary { get; set; } = "";

    public bool Shares(string key) => !NotShared.Contains(key);
    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
