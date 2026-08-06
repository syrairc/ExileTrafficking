using System.Collections.Generic;
using System.Linq;
using ExileImGui;
using ImGuiNET;

namespace ExileTrafficking;

public static class RatingsUi
{
    private static readonly Rating[] Options = { Rating.Good, Rating.Neutral, Rating.Bricked };

    // rebuilt each frame from the colour settings, so the buttons match the panel boxes and the overlay
    private static readonly Controls.GroupButton[] Buttons = new Controls.GroupButton[3];

    private static void SyncButtons(ExileTraffickingSettings settings)
    {
        Buttons[0] = new Controls.GroupButton("G", settings.GoodColor.Value);
        Buttons[1] = new Controls.GroupButton("-", settings.NeutralColor.Value);
        Buttons[2] = new Controls.GroupButton("X", settings.BrickedColor.Value);
    }

    private static bool Rate(string id, ref Rating rating)
    {
        // fade rather than desaturate the losers, so all three keep their hue and only the pick is full strength
        var clicked = Controls.ButtonGroup(id, Buttons, System.Array.IndexOf(Options, rating));
        if (clicked < 0) return false;

        rating = Options[clicked];
        return true;
    }

    private static string search = "";
    private static bool onlyRated;
    private static string importText = "";
    private static string importStatus = "";
    private static Dictionary<string, BuildRating> pendingImport;

    public static void Draw(ExileTraffickingSettings settings)
    {
        if (!ImGui.BeginTabBar("##et_tabs")) return;

        try
        {
            if (ImGui.BeginTabItem("Ratings"))
            {
                try { DrawRatings(settings); } finally { ImGui.EndTabItem(); }
            }

            if (ImGui.BeginTabItem("Import / Export"))
            {
                try { DrawShare(settings); } finally { ImGui.EndTabItem(); }
            }
        }
        finally
        {
            ImGui.EndTabBar();
        }
    }

    // name stretches, the rating buttons sit in a pinned column so long names cannot run under them
    private static readonly (string Name, float Width)[] Columns = { ("skill", 0f), ("rating", 78f) };

    private const ImGuiTableFlags TableFlags = ImGuiTableFlags.RowBg | ImGuiTableFlags.NoBordersInBody;

    private static void DrawRatings(ExileTraffickingSettings settings)
    {
        SyncButtons(settings);

        ImGui.SetNextItemWidth(-1f);
        ImGui.InputTextWithHint("##et_search", "Search archetypes / skills / supports...", ref search, 64);
        ImGui.Checkbox("Only show rated", ref onlyRated);
        ImGui.SameLine();
        ImGui.TextDisabled($"{MercData.BuildsByName.Count(b => b.Skills.Count > 0)} archetypes");

        foreach (var build in MercData.BuildsByName)
        {
            var rated = Ratings.Count(settings.Ratings, build.Id);
            if (onlyRated && rated == 0) continue;

            var skills = Visible(build, settings).ToList();
            if (skills.Count == 0) continue;

            var label = rated > 0
                ? $"{build.Name}   {build.Skills.Count} skills - {rated} rated###{build.Id}"
                : $"{build.Name}   {build.Skills.Count} skills###{build.Id}";

            if (!ImGui.CollapsingHeader(label)) continue;

            ImGui.PushID(build.Id);
            if (rated > 0 && ImGui.SmallButton("Export this archetype"))
            {
                ImGui.SetClipboardText(ShareCode.Encode(settings.Ratings, build.Id));
            }

            if (Tables.Begin("##skills", Columns, showHeader: false, flags: TableFlags))
            {
                var row = 0;
                foreach (var skill in skills) DrawSkill(settings, build, skill, ref row);
                Tables.End();
            }

            ImGui.PopID();
        }
    }

    private static IEnumerable<string> Visible(MercBuild build, ExileTraffickingSettings settings)
    {
        var buildMatches = BuildMatches(build);

        foreach (var (skill, supports) in build.Skills)
        {
            if (onlyRated &&
                Ratings.Skill(settings.Ratings, build.Id, skill) == Rating.Neutral &&
                !supports.Any(s => Ratings.Support(settings.Ratings, build.Id, skill, s) != Rating.Neutral))
            {
                continue;
            }

            if (buildMatches || Matches(skill) || supports.Any(Matches)) yield return skill;
        }
    }

    private static bool Matches(string text) =>
        string.IsNullOrWhiteSpace(search) || Text.Matches(search, text ?? "");

    private static bool BuildMatches(MercBuild build) =>
        Matches(build.Name) || build.Infamous.Any(Matches);

    // one table row per skill, its supports as further rows while the node is open. the tree node's
    // own indent is what nests them, so supports must be drawn before TreePop
    private static void DrawSkill(ExileTraffickingSettings settings, MercBuild build, string skill, ref int row)
    {
        var supports = build.Skills[skill];
        var rating = Ratings.Skill(settings.Ratings, build.Id, skill);
        bool open;

        using (Tables.Row(row++))
        {
            ImGui.TableNextColumn();
            open = ImGui.TreeNodeEx($"{skill}##node", supports.Count == 0
                ? ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.Bullet | ImGuiTreeNodeFlags.SpanFullWidth
                : ImGuiTreeNodeFlags.SpanFullWidth);

            ImGui.TableNextColumn();
            if (Rate("##skill", ref rating))
            {
                Ratings.SetSkill(settings.Ratings, build.Id, skill, rating);
            }
        }

        if (!open) return;

        foreach (var support in supports)
        {
            if (!Matches(support) && !Matches(skill) && !BuildMatches(build)) continue;

            var value = Ratings.Support(settings.Ratings, build.Id, skill, support);
            if (onlyRated && value == Rating.Neutral) continue;

            using (Tables.Row(row++))
            {
                ImGui.TableNextColumn();
                ImGui.TreeNodeEx(support, ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.NoTreePushOnOpen |
                                          ImGuiTreeNodeFlags.SpanFullWidth);

                ImGui.TableNextColumn();
                if (Rate("##support", ref value))
                {
                    Ratings.SetSupport(settings.Ratings, build.Id, skill, support, value);
                }
            }
        }

        ImGui.TreePop();
    }

    private static void DrawShare(ExileTraffickingSettings settings)
    {
        ImGui.TextDisabled($"{settings.Ratings?.Count ?? 0} archetypes, {ShareCode.RatingCount(settings.Ratings)} ratings");

        if (ImGui.Button("Copy everything to clipboard"))
        {
            ImGui.SetClipboardText(ShareCode.Encode(settings.Ratings));
            importStatus = "copied";
        }

        ImGui.Separator();
        ImGui.SetNextItemWidth(420f);
        ImGui.InputTextWithHint("##et_import", "ET1:...", ref importText, 8192);
        ImGui.SameLine();
        if (ImGui.Button("Paste"))
        {
            importText = ImGui.GetClipboardText() ?? "";
        }

        if (ImGui.Button("Read string"))
        {
            pendingImport = ShareCode.Decode(importText);
            importStatus = pendingImport == null
                ? "not a valid ET1 string"
                : $"{pendingImport.Count} archetypes, {ShareCode.RatingCount(pendingImport)} ratings";
        }

        if (!string.IsNullOrEmpty(importStatus)) ImGui.TextUnformatted(importStatus);

        if (pendingImport == null) return;

        if (ImGui.Button("Replace"))
        {
            ShareCode.Apply(settings.Ratings, pendingImport, replace: true);
            Done("replaced");
        }

        ImGui.SameLine();
        if (ImGui.Button("Merge"))
        {
            ShareCode.Apply(settings.Ratings, pendingImport, replace: false);
            Done("merged");
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel")) Done("cancelled");
    }

    private static void Done(string status)
    {
        pendingImport = null;
        importText = "";
        importStatus = status;
    }
}
