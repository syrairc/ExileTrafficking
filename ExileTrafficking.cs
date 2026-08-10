using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using ExileCore;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.MemoryObjects;
using ImGuiNET;
using Newtonsoft.Json;
using RectangleF = SharpDX.RectangleF;
using Vector2 = System.Numerics.Vector2;

namespace ExileTrafficking;

public record MercSupport(string Name, Element Icon);

public record MercSkill(string Name, IReadOnlyList<MercSupport> Supports, Element Row);

public record MercSnapshot(string Archetype, IReadOnlyList<MercSkill> Skills);

public class ExileTrafficking : BaseSettingsPlugin<ExileTraffickingSettings>
{
    // a throw in here blanks the whole settings page, so never let one escape. base.DrawSettings is
    // handed over as the General tab's body rather than drawn above the bar
    public override void DrawSettings()
    {
        try
        {
            SettingsUi.Draw(Settings, base.DrawSettings);
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

            // MirageWishesPanel shares its address with PopUpWindow and DestroyConfirmationWindow,
            // so the only reliable test is whether the thing actually holds a merc offer
            var open = window != null && window.IsValid && window.IsVisible;
            var snapshot = open ? Snapshot(window) : null;

            if (Settings.WorldOverlay)
            {
                WorldOverlay.Draw(GameController, Graphics, Settings,
                    snapshot != null ? window.GetClientRect() : (RectangleF?)null);
            }

            var warrant = WarrantTooltip.Hovered(GameController);
            if (warrant != null)
            {
                if (Settings.WarrantTooltip)
                {
                    WarrantTooltip.Draw(Graphics, Settings, warrant,
                        GameController.Window.GetWindowRectangleTimeCache);
                }

                // only listens while you're actually on a warrant, so a plain letter key is fine
                if (Settings.WarrantSearchKey.PressedOnce()) Open(FromMemory(warrant.Merc,
                    Settings.EnabledSupports.Value, Settings.SearchRated ? Settings.Ratings : null));
            }

            if (snapshot == null) return;

            if (Settings.PanelHighlight) PanelHighlight.Draw(Graphics, snapshot, Settings);

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
        var ratings = Settings.SearchRated ? Settings.Ratings : null;

        var json = Settings.PreferMemory
            ? FromMemory(MercenaryMemory.Encounter(GameController), Settings.EnabledSupports.Value, ratings)
            : null;
        json ??= BuildQueryJson(snapshot, Settings.EnabledSupports.Value, ratings);

        Open(json);
    }

    private void Open(string json)
    {
        if (json == null) return;

        var league = Settings.LeagueOverride.Value;
        if (string.IsNullOrWhiteSpace(league))
        {
            league = GameController.IngameState.ServerData.League;
        }

        if (string.IsNullOrWhiteSpace(league)) return;

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

                skills.Add(new MercSkill(name, ReadSupports(row), row));
            }

            if (skills.Count == 0) return null;

            return new MercSnapshot(archetype, skills);
        }
        catch
        {
            return null;
        }
    }

    private static List<MercSupport> ReadSupports(Element row) =>
        Descendants(row, 4)
            .Select(x => new MercSupport(FindText(x.Tooltip, 3, MercData.SupportId), x))
            .Where(x => x.Name != null)
            .OrderBy(x => x.Icon.GetClientRect().X)
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

    private static string BuildQueryJson(MercSnapshot snapshot, int enabledSupports,
        Dictionary<string, BuildRating> ratings)
    {
        var typeOption = MercData.ArchetypeId(snapshot.Archetype);
        if (typeOption == null) return null;

        var skills = new List<(string, IReadOnlyList<string>)>();
        foreach (var skill in snapshot.Skills)
        {
            var skillId = MercData.SkillId(skill.Name);
            if (skillId == null) return null;

            var supports = new List<string>();
            foreach (var support in skill.Supports)
            {
                var supportId = MercData.SupportId(support.Name);
                if (supportId == null) return null;

                supports.Add(supportId);
            }

            skills.Add((skillId, supports));
        }

        return QueryJson(typeOption, skills, enabledSupports, ratings,
            MercData.BuildForArchetype(snapshot.Archetype)?.Id);
    }

    // the handler descriptor already carries trade ids, so this path never touches panel text
    private static string FromMemory(MemMerc merc, int enabledSupports, Dictionary<string, BuildRating> ratings)
    {
        var build = merc?.Build;
        if (build == null || merc.Skills.Count == 0) return null;

        // must come off the hash, not the build: an infamous mercenary folds to the same build but is
        // a separate warrant on trade
        var typeOption = MercData.TypeOptionForHash(merc.BuildHash);
        if (typeOption == null) return null;

        var skills = merc.Skills
            .Select(x => (x.TradeId, (IReadOnlyList<string>)x.SupportTradeIds.ToList()))
            .ToList();

        return QueryJson(typeOption, skills, enabledSupports, ratings, build.Id);
    }

    public static string QueryJson(string typeOption,
        IReadOnlyList<(string Skill, IReadOnlyList<string> Supports)> skills, int enabledSupports,
        Dictionary<string, BuildRating> ratings = null, string buildId = null)
    {
        // a support rated good pulls its skill along, on its own it would match nothing
        bool Wanted(string skillId, IReadOnlyList<string> supportIds)
        {
            var skillName = MercData.SkillName(skillId);
            return Ratings.Skill(ratings, buildId, skillName) == Rating.Good ||
                   supportIds.Any(x =>
                       Ratings.Support(ratings, buildId, skillName, MercData.SupportName(x)) == Rating.Good);
        }

        // nothing rated good would leave every filter off, so fall back to the positional count
        var rated = ratings != null && buildId != null && skills.Any(x => Wanted(x.Skill, x.Supports));

        var linkedGroups = new List<object>();
        var skillFilters = new List<object>();

        foreach (var (skillId, supportIds) in skills)
        {
            var skillOn = !rated || Wanted(skillId, supportIds);
            skillFilters.Add(new { id = skillId, disabled = !skillOn });

            if (supportIds.Count == 0) continue;

            var skillName = rated ? MercData.SkillName(skillId) : null;
            var filters = new List<object> { new { id = skillId, disabled = !skillOn } };
            for (var i = 0; i < supportIds.Count; i++)
            {
                var on = rated
                    ? Ratings.Support(ratings, buildId, skillName, MercData.SupportName(supportIds[i])) == Rating.Good
                    : i < enabledSupports;
                filters.Add(new { id = supportIds[i], disabled = !on });
            }

            linkedGroups.Add(new { type = "mercenary", filters });
        }

        linkedGroups.Insert(0, new { type = "and", filters = skillFilters });

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
