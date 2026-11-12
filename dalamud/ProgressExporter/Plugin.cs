using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;

namespace ProgressExporter;

/// <summary>
/// Read-only exporter for the Completionist Log app. /progex writes one JSON file
/// with what the game client knows you've completed or unlocked. Nothing is uploaded.
/// </summary>
public sealed unsafe class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IChatGui Chat { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;

    internal Configuration Config { get; }
    private WifiServer server;
    internal WifiServer Server => server;
    private readonly WindowSystem windows = new("ProgressExporter");
    private readonly SettingsWindow settings;

    internal static readonly (string Key, string Label)[] Categories =
    {
        ("quests", "Completed quests"), ("duties", "Cleared duties"), ("recipes", "Crafting log"), ("jobs", "Job levels"),
        ("blu", "Blue Magic spells"), ("orchestrion", "Orchestrion rolls"), ("cards", "Triple Triad cards"), ("mounts", "Mounts"),
        ("minions", "Minions"), ("emotes", "Emotes"), ("facewear", "Facewear"), ("accessories", "Fashion accessories"),
        ("timers", "Retainers and leve allowances"), ("gathering", "Gathering log"), ("exploration", "Aetherytes, vistas, currents"),
        ("ranks", "Ranks and reputation"),
    };
    internal static string ExportDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Completionist Log");

    private const string Command = "/progex";

    // The same level brackets as the app's crafting grid
    private static readonly Dictionary<string, int[]> Brackets = new()
    {
        ["arr"] = new[] { 1, 6, 11, 16, 21, 26, 31, 36, 41, 46 },
        ["hw"] = new[] { 51, 56 }, ["sb"] = new[] { 61, 66 }, ["shb"] = new[] { 71, 76 },
        ["ew"] = new[] { 81, 86 }, ["dt"] = new[] { 91, 96 },
    };
    private static readonly Dictionary<string, int> Cap = new() { ["arr"] = 50, ["hw"] = 60, ["sb"] = 70, ["shb"] = 80, ["ew"] = 90, ["dt"] = 100 };
    private static readonly string[] CraftAbbr = { "CRP", "BSM", "ARM", "GSM", "LTW", "WVR", "ALC", "CUL" };

    public Plugin()
    {
        Config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        if (Config.PairCode.Length == 0) { Config.PairCode = WifiServer.NewCode(); Config.Save(); }
        server = new WifiServer(this);
        if (Config.WifiSync) server.Start();
        settings = new SettingsWindow(this);
        windows.AddWindow(settings);
        PluginInterface.UiBuilder.Draw += windows.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += settings.Toggle;
        PluginInterface.UiBuilder.OpenMainUi += settings.Toggle;
        CommandManager.AddHandler(Command, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the Progress Exporter window. /progex export saves a file; /progex pair shows the Wi-Fi pair code; /progex newcode makes a new one; /progex wifi on|off.",
        });
    }

    public void Dispose()
    {
        CommandManager.RemoveHandler(Command);
        PluginInterface.UiBuilder.Draw -= windows.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= settings.Toggle;
        PluginInterface.UiBuilder.OpenMainUi -= settings.Toggle;
        windows.RemoveAllWindows();
        server.Dispose();
    }

    internal void SetWifi(bool on)
    {
        Config.WifiSync = on; Config.Save();
        server.Dispose(); server = new WifiServer(this);
        if (on) server.Start();
    }

    internal void NewCode() { Config.PairCode = WifiServer.NewCode(); Config.Save(); Chat.Print($"[Progress Exporter] New pair code: {Config.PairCode}. Pair the app again."); }

    /// <summary>A short line naming what an export holds, e.g. "quests (2,341), duties (412), crafting log".</summary>
    internal static string Summary(Dictionary<string, object> d)
    {
        var parts = new List<string>();
        foreach (var (key, label) in Categories)
        {
            if (!d.TryGetValue(key, out var v)) continue;
            var name = label.ToLowerInvariant().Replace("completed ", "").Replace("cleared ", "");
            if (v is System.Collections.ICollection col && key != "recipes") parts.Add($"{name} ({col.Count:N0})");
            else parts.Add(name);
        }
        return parts.Count > 0 ? string.Join(", ", parts) : "nothing (every category is switched off)";
    }

    internal void ExportFile()
    {
        if (!CanExport()) { Chat.PrintError("[Progress Exporter] Log in to a character first."); return; }
        try
        {
            var data = Collect(Config.IncludeName);
            Directory.CreateDirectory(ExportDir);
            var path = Path.Combine(ExportDir, $"completionist-log-game-{DateTime.Now:yyyy-MM-dd-HHmm}.json");
            File.WriteAllText(path, JsonSerializer.Serialize(data));
            Chat.Print($"[Progress Exporter] Saved {Path.GetFileName(path)} to Documents\\Completionist Log with {Summary(data)}.");
        }
        catch (Exception e)
        {
            Log.Error(e, "Export failed");
            Chat.PrintError($"[Progress Exporter] Export failed: {e.Message}");
        }
    }

    internal bool CanExport() => ClientState.IsLoggedIn && PlayerState.IsLoaded;

    private void OnCommand(string command, string args)
    {
        var a = args.Trim().ToLowerInvariant();
        if (a == "") { settings.Toggle(); return; }
        if (a == "export" || a == "export name") { ExportFile(); return; }
        if (a == "pair")
        {
            var ips = WifiServer.LocalAddresses();
            Chat.Print($"[Progress Exporter] Pair code: {Config.PairCode}. In the app: Settings > From the game > Pair with your PC, and enter this code.");
            Chat.Print($"[Progress Exporter] Wi-Fi sync is {(Config.WifiSync ? "on" : "off")}. This PC: {(ips.Count > 0 ? string.Join(", ", ips) : "no home network address found")}, port {Config.Port}.");
            return;
        }
        if (a == "newcode") { NewCode(); return; }
        if (a == "wifi off") { SetWifi(false); Chat.Print("[Progress Exporter] Wi-Fi sync is off."); return; }
        if (a == "wifi on") { SetWifi(true); Chat.Print("[Progress Exporter] Wi-Fi sync is on."); return; }
        Chat.Print("[Progress Exporter] /progex opens the window. Also: /progex export, /progex pair, /progex newcode, /progex wifi on|off.");
    }

    // ---- second batch: timers, gathering log, exploration, ranks (each part fails on its own) ----
    private void CollectMore(Dictionary<string, object> outp)
    {
        var ui = UIState.Instance();
        var ps = FFXIVClientStructs.FFXIV.Client.Game.UI.PlayerState.Instance();

        // retainers (known once you've used a summoning bell this session) and leve allowances
        try
        {
            var timers = new Dictionary<string, object> { ["leves"] = (int)QuestManager.Instance()->NumLeveAllowances };
            var rm = RetainerManager.Instance();
            if (rm != null && rm->IsReady)
            {
                var list = new List<object>();
                int n = rm->GetRetainerCount();
                for (uint i = 0; i < n; i++)
                {
                    var r = rm->GetRetainerBySortedIndex(i);
                    if (r == null || r->RetainerId == 0) continue;
                    list.Add(new Dictionary<string, object> { ["name"] = r->NameString, ["venture"] = (int)r->VentureId, ["complete"] = (long)r->VentureComplete });
                }
                timers["retainers"] = list;
            }
            outp["timers"] = timers;
        }
        catch (Exception e) { Log.Warning(e, "timers"); }

        // gathering log: gathered item ids
        try
        {
            var gathered = new List<uint>();
            foreach (var g in DataManager.GetExcelSheet<GatheringItem>())
                if (g.RowId > 0 && g.RowId < ushort.MaxValue && QuestManager.IsGatheringItemGathered((ushort)g.RowId)) gathered.Add(g.RowId);
            outp["gathering"] = gathered;
        }
        catch (Exception e) { Log.Warning(e, "gathering"); }

        // exploration: attuned aetherytes, sightseeing log, aether currents per zone set
        try
        {
            var ae = new List<uint>();
            foreach (var a in DataManager.GetExcelSheet<Aetheryte>()) if (a.RowId > 0 && ui->IsAetheryteUnlocked(a.RowId)) ae.Add(a.RowId);
            var vistas = new List<uint>();
            uint first = 0; bool firstSet = false;
            foreach (var v in DataManager.GetExcelSheet<Adventure>())
            {
                if (!firstSet) { first = v.RowId; firstSet = true; }
                if (ps->IsAdventureComplete(v.RowId - first)) vistas.Add(v.RowId);
            }
            var sets = new Dictionary<string, object>();
            foreach (var set in DataManager.GetExcelSheet<AetherCurrentCompFlgSet>())
            {
                int field = 0, fieldTotal = 0; var quests = new List<uint>();
                foreach (var c in set.AetherCurrents)
                {
                    if (c.RowId == 0) continue;
                    bool got = ps->IsAetherCurrentUnlocked(c.RowId);
                    var qid = c.IsValid ? c.Value.Quest.RowId : 0;
                    if (qid != 0) { if (got) quests.Add(qid); }
                    else { fieldTotal++; if (got) field++; }
                }
                if (fieldTotal > 0 || quests.Count > 0) sets[set.RowId.ToString()] = new object[] { field, fieldTotal, quests };
            }
            outp["exploration"] = new Dictionary<string, object> { ["aetherytes"] = ae, ["vistas"] = vistas, ["currents"] = sets };
        }
        catch (Exception e) { Log.Warning(e, "exploration"); }

        // ranks: Grand Company, allied societies, Custom Deliveries
        try
        {
            var ranks = new Dictionary<string, object>();
            var gc = PlayerState.GrandCompany;
            if (gc.RowId != 0 && gc.IsValid) ranks["gc"] = (int)PlayerState.GetGrandCompanyRank(gc.Value);
            var soc = new Dictionary<string, int>();
            foreach (var t in DataManager.GetExcelSheet<BeastTribe>())
                if (t.RowId > 0 && t.RowId < 255) { var rk = ps->GetBeastTribeRank((byte)t.RowId); if (rk > 0) soc[t.RowId.ToString()] = rk; }
            ranks["societies"] = soc;
            var sat = SatisfactionSupplyManager.Instance();
            if (sat != null) { var hearts = new List<int>(); foreach (var h in sat->SatisfactionRanks) hearts.Add(h); ranks["deliveries"] = hearts; }
            outp["ranks"] = ranks;
        }
        catch (Exception e) { Log.Warning(e, "ranks"); }
    }

    private static string Txt(Lumina.Text.ReadOnly.ReadOnlySeString s) => s.ExtractText().Trim();
    private static void Add(List<string> list, Lumina.Text.ReadOnly.ReadOnlySeString s) { var t = Txt(s); if (t.Length > 0) list.Add(t); }

    internal Dictionary<string, object> Collect(bool includeName)
    {
        var ui = UIState.Instance();
        var ps = FFXIVClientStructs.FFXIV.Client.Game.UI.PlayerState.Instance();
        var outp = new Dictionary<string, object>
        {
            ["app"] = "completionist-log-game",
            ["v"] = 1,
            ["at"] = DateTime.UtcNow.ToString("o"),
        };
        if (includeName) outp["character"] = PlayerState.CharacterName;
        outp["shared"] = Categories.Select(c => c.Key).Where(Config.Shares).ToList();

        // job levels
        var jobs = new Dictionary<string, int>();
        foreach (var cj in DataManager.GetExcelSheet<ClassJob>())
        {
            var ab = Txt(cj.Abbreviation);
            if (ab.Length == 0) continue;
            int lv = PlayerState.GetClassJobLevel(cj);
            if (lv > 0) jobs[ab] = lv;
        }
        outp["jobs"] = jobs;

        // completed quests: id and name
        var quests = new List<object[]>();
        foreach (var q in DataManager.GetExcelSheet<Quest>())
        {
            var n = Txt(q.Name);
            if (n.Length == 0) continue;
            if (QuestManager.IsQuestComplete(q.RowId)) quests.Add(new object[] { q.RowId, n });
        }
        outp["quests"] = quests;

        // cleared duties (duty finder entries backed by instanced content)
        var duties = new List<uint>();
        foreach (var c in DataManager.GetExcelSheet<ContentFinderCondition>())
        {
            if (c.ContentLinkType != 1 || c.Content.RowId == 0) continue;
            if (UIState.IsInstanceContentCompleted(c.Content.RowId)) duties.Add(c.RowId);
        }
        outp["duties"] = duties;

        // crafting log: done/total for each 5-level bracket, plus star and master recipes at the cap
        var recipes = new Dictionary<string, Dictionary<string, int[][]>>();
        var all = new List<(int job, int lv, bool star, bool done, bool furn)>();
        foreach (var r in DataManager.GetExcelSheet<Recipe>())
        {
            if (r.ItemResult.RowId == 0 || !r.RecipeLevelTable.IsValid) continue;
            int job = (int)r.CraftType.RowId;
            if (job < 0 || job >= CraftAbbr.Length) continue;
            var lt = r.RecipeLevelTable.Value;
            bool star = lt.Stars > 0 || r.SecretRecipeBook.RowId != 0;
            var cat = r.ItemResult.IsValid ? r.ItemResult.Value.ItemUICategory.RowId : 0;
            bool furn = cat == 57 || cat == 95 || (cat >= 65 && cat <= 80);
            all.Add((job, lt.ClassJobLevel, star, QuestManager.IsRecipeComplete(r.RowId), furn));
        }
        var furnOut = new Dictionary<string, Dictionary<string, int[][]>>();
        foreach (var furnPass in new[] { false, true })
        {
            var target = furnPass ? furnOut : recipes;
            for (int j = 0; j < CraftAbbr.Length; j++)
            {
                var mine = all.Where(a => a.job == j && a.furn == furnPass).ToList();
                var perX = new Dictionary<string, int[][]>();
                foreach (var (x, starts) in Brackets)
                {
                    var row = new List<int[]>();
                    foreach (var st0 in starts)
                    {
                        var b = mine.Where(a => !a.star && a.lv >= st0 && a.lv <= st0 + 4).ToList();
                        row.Add(new[] { b.Count(a => a.done), b.Count });
                    }
                    var st = mine.Where(a => a.star && a.lv == Cap[x]).ToList();
                    row.Add(new[] { st.Count(a => a.done), st.Count });
                    perX[x] = row.ToArray();
                }
                target[CraftAbbr[j]] = perX;
            }
        }
        outp["furn"] = furnOut;
        outp["recipes"] = recipes;

        // collections, by name (plain loops: unlock checks go through game pointers)
        var orch = new List<string>();
        foreach (var o in DataManager.GetExcelSheet<Orchestrion>()) if (o.RowId > 0 && ps->IsOrchestrionRollUnlocked(o.RowId)) Add(orch, o.Name);
        outp["orchestrion"] = orch;
        var cards = new List<string>();
        foreach (var c in DataManager.GetExcelSheet<TripleTriadCard>()) if (c.RowId > 0 && c.RowId < ushort.MaxValue && ui->IsTripleTriadCardUnlocked((ushort)c.RowId)) Add(cards, c.Name);
        outp["cards"] = cards;
        var mounts = new List<string>();
        foreach (var m in DataManager.GetExcelSheet<Mount>()) if (m.RowId > 0 && ps->IsMountUnlocked(m.RowId)) Add(mounts, m.Singular);
        outp["mounts"] = mounts;
        var minions = new List<string>();
        foreach (var m in DataManager.GetExcelSheet<Companion>()) if (m.RowId > 0 && ui->IsCompanionUnlocked(m.RowId)) Add(minions, m.Singular);
        outp["minions"] = minions;
        var emotes = new List<string>();
        foreach (var e in DataManager.GetExcelSheet<Emote>()) if (e.RowId > 0 && e.RowId < ushort.MaxValue && ui->IsEmoteUnlocked((ushort)e.RowId)) Add(emotes, e.Name);
        outp["emotes"] = emotes;
        var face = new List<string>();
        foreach (var g in DataManager.GetExcelSheet<Glasses>())
        {
            if (g.RowId == 0 || g.RowId >= ushort.MaxValue || !ps->IsGlassesUnlocked((ushort)g.RowId)) continue;
            Add(face, g.Name);
            if (g.Style.IsValid) Add(face, g.Style.Value.Name);
        }
        outp["facewear"] = face.Distinct().ToList();
        var acc = new List<string>();
        foreach (var o in DataManager.GetExcelSheet<Ornament>()) if (o.RowId > 0 && ps->IsOrnamentUnlocked(o.RowId)) Add(acc, o.Singular);
        outp["accessories"] = acc;

        // Blue Magic spells learned
        var blu = new List<string>();
        foreach (var a in DataManager.GetExcelSheet<AozAction>())
        {
            if (a.RowId == 0 || !a.Action.IsValid) continue;
            var act = a.Action.Value;
            if (act.UnlockLink.RowId != 0 && ui->IsUnlockLinkUnlockedOrQuestCompleted(act.UnlockLink.RowId)) Add(blu, act.Name);
        }
        outp["blu"] = blu;
        CollectMore(outp);
        foreach (var (key, _) in Categories) if (!Config.Shares(key)) outp.Remove(key);
        if (!Config.Shares("recipes")) outp.Remove("furn");
        return outp;
    }
}
