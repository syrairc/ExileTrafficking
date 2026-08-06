using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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
    // a throw in here blanks the whole settings page, so never let one escape
    public override void DrawSettings()
    {
        base.DrawSettings();

        try
        {
            RatingsUi.Draw(Settings);
        }
        catch (Exception e)
        {
            ImGui.TextUnformatted($"settings error: {e.Message}");
        }
    }

    public override void Render()
    {
        try
        {
            var ui = GameController.IngameState.IngameUi;
            Element window = ui.MercenaryEncounterWindow;
            if (window == null || !window.IsValid || !window.IsVisible) window = ui.MirageWishesPanel;
            if (window == null || !window.IsValid || !window.IsVisible) return;

            // MirageWishesPanel shares its address with PopUpWindow and DestroyConfirmationWindow,
            // so the only reliable test is whether the thing actually holds a merc offer
            var snapshot = Snapshot(window);
            if (snapshot == null) return;

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
                    Search(snapshot);
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

    // ReadPanel walks the subtree, too expensive to redo every frame just to keep the button honest
    private Element cachedWindow;
    private MercSnapshot cachedSnapshot;
    private readonly Stopwatch cacheAge = Stopwatch.StartNew();

    private MercSnapshot Snapshot(Element window)
    {
        if (!ReferenceEquals(window, cachedWindow) || cacheAge.ElapsedMilliseconds > 250)
        {
            cachedWindow = window;
            cachedSnapshot = ReadPanel(window);
            cacheAge.Restart();
        }

        return cachedSnapshot;
    }

    private void Search(MercSnapshot snapshot)
    {
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
            var archetype = FindText(window, 12, MercData.ArchetypeId, true);
            if (archetype == null) return null;

            var container = Descendants(window, 12, true).FirstOrDefault(x =>
            {
                var children = x.Children;
                if (children == null || children.Count < 2) return false;

                return children.All(child => FindText(child, 3, MercData.SkillId, true) != null);
            });
            if (container == null) return null;

            var skills = new List<MercSkill>();
            foreach (var row in container.Children)
            {
                var name = FindText(row, 3, MercData.SkillId, true);
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
            .Select(x => new { Element = x, Name = FindText(x.Tooltip, 3, MercData.SupportId) })
            .Where(x => x.Name != null)
            .OrderBy(x => x.Element.GetClientRect().X)
            .Select(x => x.Name)
            .ToList();

    private static string FindText(Element root, int depth, Func<string, string> lookup, bool visibleOnly = false) =>
        Descendants(root, depth, visibleOnly)
            .Select(e =>
            {
                var text = e?.TextNoTags;
                if (string.IsNullOrWhiteSpace(text)) text = e?.Text;
                return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
            })
            .FirstOrDefault(t => lookup(t) != null);

    // visibleOnly matters on the panel itself: the popup container keeps every popup as a child and
    // only flips the local flag, so the merc offer is still sitting there while a confirm dialog is up.
    // must stay off for tooltip walks, those are hidden until hovered.
    private static IEnumerable<Element> Descendants(Element root, int depth, bool visibleOnly = false)
    {
        if (root == null) yield break;
        if (visibleOnly && !root.IsVisibleLocal) yield break;

        yield return root;
        if (depth <= 0) yield break;

        foreach (var child in root.Children ?? Enumerable.Empty<Element>())
        {
            foreach (var descendant in Descendants(child, depth - 1, visibleOnly))
            {
                yield return descendant;
            }
        }
    }

    private static string BuildQueryJson(MercSnapshot snapshot, int enabledSupports)
    {
        var typeOption = MercData.ArchetypeId(snapshot.Archetype);
        if (typeOption == null) return null;

        var skillIds = new List<string>();
        var linkedGroups = new List<object>();

        foreach (var skill in snapshot.Skills)
        {
            var skillId = MercData.SkillId(skill.Name);
            if (skillId == null) return null;

            skillIds.Add(skillId);

            var filters = new List<object>();
            foreach (var support in skill.Supports)
            {
                var supportId = MercData.SupportId(support);
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
}
