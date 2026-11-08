using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;

namespace CompletionistExporter;

/// <summary>
/// Read-only exporter for the Completionist Log app. /clexport writes one JSON file
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

    private const string Command = "/clexport";

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
        CommandManager.AddHandler(Command, new CommandInfo(OnCommand)
        {
            HelpMessage = "Write your progress to a file for the Completionist Log app. Add \"name\" to include your character's name.",
        });
    }

    public void Dispose() => CommandManager.RemoveHandler(Command);

    private void OnCommand(string command, string args)
    {
        if (!ClientState.IsLoggedIn || !PlayerState.IsLoaded)
        {
            Chat.PrintError("[Completionist Log] Log in to a character first.");
            return;
        }
        try
        {
            var data = Collect(args.Trim().Equals("name", StringComparison.OrdinalIgnoreCase));
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Completionist Log");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"completionist-log-game-{DateTime.Now:yyyy-MM-dd-HHmm}.json");
            File.WriteAllText(path, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = false }));
            Chat.Print($"[Completionist Log] Saved {Path.GetFileName(path)} to Documents\\Completionist Log. Put it on your phone (Google Drive works) and choose Import game export in the app.");
        }
        catch (Exception e)
        {
            Log.Error(e, "Export failed");
            Chat.PrintError($"[Completionist Log] Export failed: {e.Message}");
        }
    }

    private static string Txt(Lumina.Text.ReadOnly.ReadOnlySeString s) => s.ExtractText().Trim();
    private static void Add(List<string> list, Lumina.Text.ReadOnly.ReadOnlySeString s) { var t = Txt(s); if (t.Length > 0) list.Add(t); }

    private Dictionary<string, object> Collect(bool includeName)
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
        var all = new List<(int job, int lv, bool star, bool done)>();
        foreach (var r in DataManager.GetExcelSheet<Recipe>())
        {
            if (r.ItemResult.RowId == 0 || !r.RecipeLevelTable.IsValid) continue;
            int job = (int)r.CraftType.RowId;
            if (job < 0 || job >= CraftAbbr.Length) continue;
            var lt = r.RecipeLevelTable.Value;
            bool star = lt.Stars > 0 || r.SecretRecipeBook.RowId != 0;
            all.Add((job, lt.ClassJobLevel, star, QuestManager.IsRecipeComplete(r.RowId)));
        }
        for (int j = 0; j < CraftAbbr.Length; j++)
        {
            var mine = all.Where(a => a.job == j).ToList();
            var perX = new Dictionary<string, int[][]>();
            foreach (var (x, starts) in Brackets)
            {
                var row = new List<int[]>();
                foreach (var s in starts)
                {
                    var b = mine.Where(a => !a.star && a.lv >= s && a.lv <= s + 4).ToList();
                    row.Add(new[] { b.Count(a => a.done), b.Count });
                }
                var st = mine.Where(a => a.star && a.lv == Cap[x]).ToList();
                row.Add(new[] { st.Count(a => a.done), st.Count });
                perX[x] = row.ToArray();
            }
            recipes[CraftAbbr[j]] = perX;
        }
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
        var bardings = new List<string>();
        foreach (var b in DataManager.GetExcelSheet<BuddyEquip>()) if (b.RowId > 0 && ui->Buddy.IsBuddyEquipUnlocked(b.RowId)) Add(bardings, b.Name);
        outp["bardings"] = bardings;
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
        return outp;
    }
}
