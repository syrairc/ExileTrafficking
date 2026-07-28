using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using ExileCore;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.MemoryObjects;
using ImGuiNET;
using Newtonsoft.Json;
using Vector2 = System.Numerics.Vector2;

namespace ExileTrafficking;

record MercSkill(string Name, IReadOnlyList<string> Supports);

record MercSnapshot(string Archetype, IReadOnlyList<MercSkill> Skills);

public class ExileTrafficking : BaseSettingsPlugin<ExileTraffickingSettings>
{
    private static readonly Dictionary<string, string> Skills;
    private static readonly Dictionary<string, string> Supports;
    private static readonly Dictionary<string, string> Archetypes;

    static ExileTrafficking()
    {
        var tables = LoadTables() ?? new Dictionary<string, Dictionary<string, string>>();
        Skills = tables.GetValueOrDefault("skills") ?? new();
        Supports = tables.GetValueOrDefault("supports") ?? new();
        Archetypes = tables.GetValueOrDefault("archetypes") ?? new();
    }

    public override void Render()
    {
        try
        {
            var ui = GameController.IngameState.IngameUi;
            Element window = ui.MercenaryEncounterWindow;
            if (window == null || !window.IsValid || !window.IsVisible) window = ui.MirageWishesPanel;
            if (window == null || !window.IsValid || !window.IsVisible) return;

            var rect = window.GetClientRect();
            var (anchorY, buttonHeight) = Anchor(window, rect.Bottom);

            ImGui.SetNextWindowPos(new Vector2(
                rect.X + Settings.ButtonNudgeX.Value,
                anchorY + Settings.ButtonNudgeY.Value));

            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
            try
            {
                if (ImGui.Begin("##mercsearch",
                        ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove |
                        ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground |
                        ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoFocusOnAppearing) &&
                    ImGui.Button("Trade Search", new Vector2(160f, buttonHeight)))
                {
                    Search(window);
                }
            }
            finally
            {
                ImGui.End();
                ImGui.PopStyleVar();
            }
        }
        catch
        {
        }
    }

    private static (float Y, float Height) Anchor(Element window, float bottom)
    {
        if (window is not MercenaryEncounterWindow encounter) return (bottom + 4f, 45f);

        var takeItem = encounter.TakeItemButton;
        if (takeItem != null && takeItem.IsValid && takeItem.IsVisible)
        {
            var r = takeItem.GetClientRect();
            return (r.Y, r.Height);
        }

        return (bottom - 45f - 27f, 45f);
    }

    private void Search(Element window)
    {
        var snapshot = ReadPanel(window);
        if (snapshot == null) return;

        var league = Settings.LeagueOverride.Value;
        if (string.IsNullOrWhiteSpace(league))
        {
            league = GameController.IngameState.ServerData.League;
        }

        if (string.IsNullOrWhiteSpace(league)) return;

        var json = BuildQueryJson(snapshot, Settings.EnabledSupports.Value);
        if (json == null) return;

        var url = $"https://www.pathofexile.com/trade/search/{Uri.EscapeDataString(league.Trim())}" +
                  $"?q={Uri.EscapeDataString(json)}";
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private static MercSnapshot ReadPanel(Element window)
    {
        try
        {
            var archetype = FindText(window, 12, Archetypes);
            if (archetype == null) return null;

            var container = Descendants(window, 12).FirstOrDefault(x =>
            {
                var children = x.Children;
                if (children == null || children.Count < 2) return false;

                return children.All(child => FindText(child, 3, Skills) != null);
            });
            if (container == null) return null;

            var skills = new List<MercSkill>();
            foreach (var row in container.Children)
            {
                var name = FindText(row, 3, Skills);
                if (name == null) return null;

                skills.Add(new MercSkill(name, ReadSupports(row)));
            }

            if (skills.Count == 0) return null;

            return new MercSnapshot(archetype, skills);
        }
        catch
        {
            return null;
        }
    }

    private static List<string> ReadSupports(Element row) =>
        Descendants(row, 4)
            .Select(x => new { Element = x, Name = FindText(x.Tooltip, 3, Supports) })
            .Where(x => x.Name != null)
            .OrderBy(x => x.Element.GetClientRect().X)
            .Select(x => x.Name)
            .ToList();

    private static string FindText(Element root, int depth, Dictionary<string, string> table) =>
        Descendants(root, depth)
            .Select(e =>
            {
                var text = e?.TextNoTags;
                if (string.IsNullOrWhiteSpace(text)) text = e?.Text;
                return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
            })
            .FirstOrDefault(t => Lookup(table, t) != null);

    private static IEnumerable<Element> Descendants(Element root, int depth)
    {
        if (root == null) yield break;

        yield return root;
        if (depth <= 0) yield break;

        foreach (var child in root.Children ?? Enumerable.Empty<Element>())
        {
            foreach (var descendant in Descendants(child, depth - 1))
            {
                yield return descendant;
            }
        }
    }

    private static string BuildQueryJson(MercSnapshot snapshot, int enabledSupports)
    {
        var typeOption = Lookup(Archetypes, snapshot.Archetype);
        if (typeOption == null) return null;

        var skillIds = new List<string>();
        var linkedGroups = new List<object>();

        foreach (var skill in snapshot.Skills)
        {
            var skillId = Lookup(Skills, skill.Name);
            if (skillId == null) return null;

            skillIds.Add(skillId);

            var filters = new List<object>();
            foreach (var support in skill.Supports)
            {
                var supportId = Lookup(Supports, support);
                if (supportId == null) return null;

                filters.Add(new { id = supportId, disabled = filters.Count >= enabledSupports });
            }

            if (filters.Count == 0) continue;

            filters.Insert(0, new { id = skillId, disabled = false });
            linkedGroups.Add(new { type = "mercenary", filters });
        }

        linkedGroups.Insert(0, new { type = "and", filters = skillIds.Select(x => new { id = x }).ToList() });

        return JsonConvert.SerializeObject(new
        {
            query = new
            {
                type = new { option = typeOption, discriminator = "mercenary_warrant" },
                stats = linkedGroups,
                status = new { option = "available" },
            },
            sort = new { price = "asc" },
        });
    }

    private static string Lookup(Dictionary<string, string> table, string key) =>
        !string.IsNullOrWhiteSpace(key) && table.TryGetValue(key.Trim(), out var value) ? value : null;

    private static Dictionary<string, Dictionary<string, string>> LoadTables()
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ExileTrafficking.mercdata.json");
            using var reader = new StreamReader(stream);
            return JsonConvert.DeserializeObject<Dictionary<string, Dictionary<string, string>>>(reader.ReadToEnd());
        }
        catch
        {
            return null;
        }
    }
}
