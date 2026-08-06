using System.Collections.Generic;
using System.Linq;
using ExileImGui;
using ImGuiNET;

namespace ExileTrafficking;

public static class RatingsUi
{
    private static readonly (string Label, Rating Value)[] Options =
    {
        ("G", Rating.Good),
        ("-", Rating.Neutral),
        ("X", Rating.Bricked),
    };

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

    private static void DrawRatings(ExileTraffickingSettings settings)
    {
        ImGui.SetNextItemWidth(260f);
        ImGui.InputTextWithHint("##et_search", "Search archetypes / skills / supports...", ref search, 64);
        ImGui.SameLine();
        ImGui.Checkbox("Only show rated", ref onlyRated);
        ImGui.SameLine();
        ImGui.TextDisabled($"{MercData.BuildsByName.Count} archetypes");

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
            ImGui.Indent();
            if (rated > 0 && ImGui.SmallButton("Export this archetype"))
            {
                ImGui.SetClipboardText(ShareCode.Encode(settings.Ratings, build.Id));
            }

            foreach (var skill in skills) DrawSkill(settings, build, skill);
            ImGui.Unindent();
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

    private static void DrawSkill(ExileTraffickingSettings settings, MercBuild build, string skill)
    {
        var supports = build.Skills[skill];
        var rating = Ratings.Skill(settings.Ratings, build.Id, skill);

        ImGui.PushID(skill);
        var open = ImGui.TreeNodeEx($"{skill}##node",
            supports.Count == 0 ? ImGuiTreeNodeFlags.Leaf : ImGuiTreeNodeFlags.None);
        ImGui.SameLine();
        if (Controls.Segmented($"##skill_{skill}", ref rating, Options))
        {
            Ratings.SetSkill(settings.Ratings, build.Id, skill, rating);
        }

        if (open)
        {
            foreach (var support in supports)
            {
                if (!Matches(support) && !Matches(skill) && !BuildMatches(build)) continue;

                var value = Ratings.Support(settings.Ratings, build.Id, skill, support);
                if (onlyRated && value == Rating.Neutral) continue;

                ImGui.PushID(support);
                ImGui.TextUnformatted(support);
                ImGui.SameLine(240f);
                if (Controls.Segmented("##support", ref value, Options))
                {
                    Ratings.SetSupport(settings.Ratings, build.Id, skill, support, value);
                }

                ImGui.PopID();
            }

            ImGui.TreePop();
        }

        ImGui.PopID();
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
