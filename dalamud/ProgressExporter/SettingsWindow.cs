using System;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace ProgressExporter;

/// <summary>The /progex window: Wi-Fi pairing, what's shared, and export to a file.</summary>
public sealed class SettingsWindow : Window
{
    private readonly Plugin plugin;
    private static readonly Vector4 Gold = new(0.88f, 0.70f, 0.35f, 1f);
    private static readonly Vector4 Muted = new(0.62f, 0.62f, 0.62f, 1f);
    private static readonly Vector4 Green = new(0.42f, 0.75f, 0.44f, 1f);
    private static readonly Vector4 Red = new(0.90f, 0.45f, 0.40f, 1f);

    public SettingsWindow(Plugin plugin) : base("Progress Exporter###ProgressExporterSettings")
    {
        this.plugin = plugin;
        Size = new Vector2(460, 560);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw()
    {
        var c = plugin.Config;

        // ---- Wi-Fi sync ----
        ImGui.TextColored(Gold, "Wi-Fi sync");
        var wifi = c.WifiSync;
        if (ImGui.Checkbox("Share my progress with the Completionist Log app on my home network", ref wifi)) plugin.SetWifi(wifi);
        if (c.WifiSync)
        {
            ImGui.Text("Pair code:");
            ImGui.SameLine();
            ImGui.SetWindowFontScale(1.4f);
            ImGui.TextUnformatted(c.PairCode);
            ImGui.SetWindowFontScale(1f);
            ImGui.SameLine();
            if (ImGui.SmallButton("Copy")) ImGui.SetClipboardText(c.PairCode);
            ImGui.SameLine();
            if (ImGui.SmallButton("New code")) plugin.NewCode();
            var ips = WifiServer.LocalAddresses();
            ImGui.TextColored(Muted, $"This PC: {(ips.Count > 0 ? string.Join(", ", ips) : "no home network address found")} · port {c.Port}");
            if (plugin.Server.Error is { } err) ImGui.TextColored(Red, $"Not listening: {err}");
            else ImGui.TextColored(Green, "Listening for the app");
            ImGui.TextColored(Muted, c.LastSync is { } t ? $"Last sync: {t.ToLocalTime():MMM d, h:mm tt}" : "Not synced yet");
            if (c.LastSyncSummary.Length > 0) ImGui.TextWrapped(c.LastSyncSummary);
            ImGui.PushStyleColor(ImGuiCol.Text, Muted);
            ImGui.TextWrapped("In the app: Settings > From the game > Pair with your PC, then enter the code. Only devices on your home network that know the code can read your progress.");
            ImGui.PopStyleColor();
        }

        ImGui.Separator();

        // ---- What's shared ----
        ImGui.TextColored(Gold, "What's shared");
        ImGui.PushStyleColor(ImGuiCol.Text, Muted);
        ImGui.TextWrapped("Used for both Wi-Fi sync and export files. Everything is read-only; the app only ever adds ticks.");
        ImGui.PopStyleColor();
        if (ImGui.BeginTable("shared", 2))
        {
            foreach (var (key, label) in Plugin.Categories)
            {
                ImGui.TableNextColumn();
                var on = c.Shares(key);
                if (ImGui.Checkbox(label, ref on))
                {
                    if (on) c.NotShared.Remove(key); else c.NotShared.Add(key);
                    c.Save();
                }
            }
            ImGui.EndTable();
        }
        var name = c.IncludeName;
        if (ImGui.Checkbox("Include my character's name", ref name)) { c.IncludeName = name; c.Save(); }

        ImGui.Separator();

        // ---- Export to a file ----
        ImGui.TextColored(Gold, "Export to a file");
        if (ImGui.Button("Export now")) plugin.ExportFile();
        ImGui.SameLine();
        if (ImGui.Button("Open folder"))
        {
            try { System.IO.Directory.CreateDirectory(Plugin.ExportDir); Process.Start(new ProcessStartInfo { FileName = Plugin.ExportDir, UseShellExecute = true }); } catch { }
        }
        ImGui.TextColored(Muted, "Saves to Documents\\Completionist Log");
    }
}
