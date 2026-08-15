using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
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

    // null when the file went missing, which is the same as the alert being switched off
    private string alertPath;

    public override bool Initialise()
    {
        alertPath = Asset("alert.wav");

        // decoding on the first alert would hitch it, but a bad wav must not take the whole plugin down
        try
        {
            if (alertPath != null) GameController.SoundController.PreloadSound(alertPath);
        }
        catch
        {
            alertPath = null;
        }

        return true;
    }

    // a compiled plugin sits beside its copied content, a source build reports the source folder, and
    // which one you get depends on how HUD loaded you, so try both
    private string Asset(params string[] parts)
    {
        foreach (var dir in new[] { DirectoryFullName, Path.GetDirectoryName(GetType().Assembly.Location) })
        {
            if (string.IsNullOrEmpty(dir)) continue;

            var path = Path.Combine(parts.Prepend(dir).ToArray());
            if (File.Exists(path)) return path;
        }

        return null;
    }

    // resolved once per zone and kept, the class can't change under you
    private MercClass areaMercenary;
    private int areaAttempts;

    public override void AreaChange(AreaInstance area)
    {
        areaMercenary = null;
        areaAttempts = AreaResolveAttempts;
    }

    // the area plugin vector isn't populated the instant AreaChange fires, so keep asking for a
    // couple of seconds before giving up on the zone
    private const int AreaResolveAttempts = 120;

    public override Job Tick()
    {
        if (areaMercenary != null || areaAttempts <= 0) return null;
        if (!GameController.InGame || GameController.IsLoading) return null;

        areaAttempts--;
        areaMercenary = MercData.ClassAt(MercenaryMemory.AreaClass(GameController));

        // AreaChange is what clears areaMercenary, so this can only land once a zone
        if (areaMercenary != null) Alert(areaMercenary);
        return null;
    }

    private void Alert(MercClass merc)
    {
        if (alertPath == null || Settings.AlertVolume.Value <= 0f) return;
        if (!MercData.ClassBuilds(merc.Id).Any(x => Settings.AlertBuilds.Contains(x.Id))) return;

        GameController.SoundController.PlaySound(alertPath, Settings.AlertVolume.Value);
    }

    public override void Render()
    {
        try
        {
            // one entity walk feeds both overlays, and neither being on means it isn't worth doing
            var sightings = Settings.WorldOverlay || Settings.AreaMercenary
                ? WorldOverlay.Sightings(GameController)
                : new List<MercSighting>();

            if (areaMercenary != null && Settings.AreaMercenary)
            {
                // only the real offer upgrades the line. wild mercenaries aren't what the zone rolled,
                // so with none of them active it stays on the class it preloaded with
                AreaMercenaryOverlay.Draw(Graphics, Settings, areaMercenary,
                    sightings.FirstOrDefault(x => x.Active),
                    GameController.Window.GetWindowRectangleTimeCache);
            }

            var ui = GameController.IngameState.IngameUi;
            Element window = ui.MercenaryEncounterWindow;
            if (window == null || !window.IsValid || !window.IsVisible) window = ui.MirageWishesPanel;

            // MirageWishesPanel shares its address with PopUpWindow and DestroyConfirmationWindow,
            // so the only reliable test is whether the thing actually holds a merc offer
            var open = window != null && window.IsValid && window.IsVisible;
            var snapshot = open ? Snapshot(window) : null;

            if (Settings.WorldOverlay)
            {
                WorldOverlay.Draw(GameController, Graphics, Settings, sightings,
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
                if (Settings.WarrantSearchKey.PressedOnce()) Open(FromMemory(warrant.Merc, Settings));
                if (Settings.WarrantPriceCheckKey.PressedOnce()) PriceCheck(warrant.Merc);
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
                        ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoFocusOnAppearing))
                {
                    if (ImGui.Button("Trade Search", new Vector2(160f, buttonHeight))) Search(snapshot);

                    ImGui.SameLine();
                    if (ImGui.Button("Price Check", new Vector2(160f, buttonHeight)))
                    {
                        PriceCheck(MercenaryMemory.Encounter(GameController));
                    }
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
        var json = Settings.PreferMemory
            ? FromMemory(MercenaryMemory.Encounter(GameController), Settings)
            : null;
        json ??= BuildQueryJson(snapshot, Settings);

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

    private static string BuildQueryJson(MercSnapshot snapshot, ExileTraffickingSettings settings)
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

        return QueryJson(typeOption, skills, settings, MercData.BuildForArchetype(snapshot.Archetype)?.Id);
    }

    private const string PriceCheckUrl = "https://xddbsns.com/mercenary-price-check.html";

    // the site takes no url parameters, so the merc rides over on the clipboard in the same tooltip
    // shape the game's own Ctrl+C gives, and you paste it there. sections split on the dashes, the
    // Build line is what the parser anchors on, and everything after it reads as a skill
    public static string WarrantText(MemMerc merc)
    {
        var build = merc?.Build;
        if (build == null || merc.Skills.Count == 0) return null;

        var infamous = MercData.TypeOptionForHash(merc.BuildHash)?.EndsWith("Noble", StringComparison.Ordinal) == true;

        var text = new StringBuilder();
        text.AppendLine($"Build: {MercData.DisplayName(build, infamous)}");
        text.AppendLine($"Mercenary Level: {merc.Level}");

        var wrote = false;
        foreach (var skill in merc.Skills)
        {
            var name = MercData.SkillName(skill.TradeId);
            if (name == null) continue;

            text.AppendLine("--------");
            text.AppendLine(name);
            wrote = true;

            // a support line the parser can't read takes its whole skill down with it, so an unknown
            // name or tier gets dropped rather than written out broken
            foreach (var hash in skill.Supports)
            {
                var support = MercData.SupportName($"mercenary.support_{hash}");
                var tier = MercData.SupportTier(hash);
                if (support != null && tier > 0) text.AppendLine($"{support} (Tier: {tier})");
            }
        }

        return wrote ? text.ToString() : null;
    }

    private void PriceCheck(MemMerc merc)
    {
        var text = WarrantText(merc);
        if (text == null) return;

        ImGui.SetClipboardText(text);
        Process.Start(new ProcessStartInfo(PriceCheckUrl) { UseShellExecute = true });
    }

    // the handler descriptor already carries trade ids, so this path never touches panel text
    private static string FromMemory(MemMerc merc, ExileTraffickingSettings settings)
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

        return QueryJson(typeOption, skills, settings, build.Id);
    }

    public static string QueryJson(string typeOption,
        IReadOnlyList<(string Skill, IReadOnlyList<string> Supports)> skills,
        ExileTraffickingSettings settings, string buildId = null)
    {
        // with neither toggle on, ratings don't drive the query at all
        var ratings = settings.IncludeGood || settings.IncludeBricked ? settings.Ratings : null;

        bool Included(Rating rating) =>
            rating == Rating.Good ? settings.IncludeGood : rating == Rating.Bricked && settings.IncludeBricked;

        // a support you switched on pulls its skill along, on its own it would match nothing
        bool Wanted(string skillId, IReadOnlyList<string> supportIds)
        {
            var skillName = MercData.SkillName(skillId);
            return Included(Ratings.Skill(ratings, buildId, skillName)) ||
                   supportIds.Any(x =>
                       Included(Ratings.Support(ratings, buildId, skillName, MercData.SupportName(x))));
        }

        // nothing switched on would leave every filter off, so fall back to the positional count
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
                    ? Included(Ratings.Support(ratings, buildId, skillName, MercData.SupportName(supportIds[i])))
                    : i < settings.EnabledSupports.Value;
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
