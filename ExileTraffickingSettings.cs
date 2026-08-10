using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using ExileCore.Shared.Attributes;
using ExileCore.Shared.Interfaces;
using ExileCore.Shared.Nodes;
using ExileImGui;
using ImGuiNET;
using SharpDX;

namespace ExileTrafficking;

public class ExileTraffickingSettings : ISettings
{
    public ToggleNode Enable { get; set; } = new ToggleNode(false);

    [Menu("League override", "Leave empty to read the league from the game.")]
    public TextNode LeagueOverride { get; set; } = new TextNode("");

    [Menu("Read the mercenary from memory", "Builds the query from the encounter handler instead of the panel text. Falls back to the panel when memory has nothing.")]
    public ToggleNode PreferMemory { get; set; } = new ToggleNode(true);

    [Menu("Search on your ratings", "Switches on the skills and supports you rated Good and leaves the rest in the query but disabled, ready to tick on the site. Overrides the support count below. Falls back to it when the build has nothing rated Good.")]
    public ToggleNode SearchRated { get; set; } = new ToggleNode(true);

    [Menu("Enable # support gems in query", "How many supports per skill to switch on. 0 leaves them all off so you can widen the search yourself.")]
    public RangeNode<int> EnabledSupports { get; set; } = new RangeNode<int>(0, 0, 6);

    [Menu("Button X nudge", "Nudge from the panel's bottom-left anchor.")]
    public RangeNode<int> ButtonNudgeX { get; set; } = new RangeNode<int>(8, -2000, 2000);

    [Menu("Button Y nudge", "Nudge from the game's own button row.")]
    public RangeNode<int> ButtonNudgeY { get; set; } = new RangeNode<int>(0, -2000, 2000);

    [Menu("Highlight rated skills in the encounter panel")]
    public ToggleNode PanelHighlight { get; set; } = new ToggleNode(true);

    [Menu("Show mercenary overlay in the world")]
    public ToggleNode WorldOverlay { get; set; } = new ToggleNode(true);

    [Menu("Show a breakdown on hovered mercenary warrants", "Sits beside the game's own tooltip, on whichever side has the room.")]
    public ToggleNode WarrantTooltip { get; set; } = new ToggleNode(true);

    [Menu("Warrant trade search key", "Press while hovering a warrant to open the trade search for that mercenary.")]
    public HotkeyNodeV2 WarrantSearchKey { get; set; } = new HotkeyNodeV2(Keys.T);

    [Menu("Overlay font size")]
    public RangeNode<int> OverlayFontSize { get; set; } = new RangeNode<int>(16, 8, 48);

    [Menu("Overlay verdict line")]
    public ToggleNode OverlayVerdict { get; set; } = new ToggleNode(true);

    [Menu("Overlay X offset", "Nudge from the mercenary's head.")]
    public RangeNode<int> OverlayOffsetX { get; set; } = new RangeNode<int>(0, -1000, 1000);

    [Menu("Overlay Y offset", "Nudge from the mercenary's head. Negative moves the block up.")]
    public RangeNode<int> OverlayOffsetY { get; set; } = new RangeNode<int>(-40, -1000, 1000);

    [Menu("Archetype line colour")]
    public ColorNode HeaderColor { get; set; } = new ColorNode(Color.FromRgba(0xFF37D7FF));

    [Menu("Good colour")]
    public ColorNode GoodColor { get; set; } = new ColorNode(Color.FromRgba(0xFF6EEB82));

    [Menu("Neutral colour")]
    public ColorNode NeutralColor { get; set; } = new ColorNode(Color.FromRgba(0xFFD8D8D8));

    [Menu("Bricked colour")]
    public ColorNode BrickedColor { get; set; } = new ColorNode(Color.FromRgba(0xFF5C5CE5));

    public Dictionary<string, BuildRating> Ratings { get; set; } = new Dictionary<string, BuildRating>();
}

// the settings page. everything above is drawn by the engine off its [Menu] attributes, everything
// here is the hand-drawn part that reflection can't express
public static class SettingsUi
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

    // leaves the theme's own text colour alone for neutral, so only a real rating stands out
    private static EColor.StyleColorScope TextFor(Rating rating, ExileTraffickingSettings settings) =>
        new((ImGuiCol.Text, rating == Rating.Neutral
            ? ImGui.GetColorU32(ImGuiCol.Text)
            : EColor.U32(Ratings.Colour(rating, settings.GoodColor.Value, settings.NeutralColor.Value,
                settings.BrickedColor.Value))));

    private static bool Rate(string id, ref Rating rating)
    {
        // fade rather than desaturate the losers, so all three keep their hue and only the pick is full strength
        var clicked = Controls.ButtonGroup(id, Buttons, Array.IndexOf(Options, rating));
        if (clicked < 0) return false;

        rating = Options[clicked];
        return true;
    }

    private static string search = "";
    private static bool onlyRated;
    private static string importText = "";
    private static string importStatus = "";
    private static Dictionary<string, BuildRating> pendingImport;

    // general is the plugin's own base.DrawSettings, so the [Menu] attributes stay the one source of
    // truth for those labels and tooltips instead of getting re-typed here
    public static void Draw(ExileTraffickingSettings settings, Action general)
    {
        if (!ImGui.BeginTabBar("##et_tabs")) return;

        try
        {
            Tab("General", general);
            Tab("Ratings", () => DrawRatings(settings));
            Tab("Import / Export", () => DrawShare(settings));
        }
        finally
        {
            ImGui.EndTabBar();
        }
    }

    // a throw inside a tab still has to reach EndTabItem or the whole bar comes apart
    private static void Tab(string label, Action body)
    {
        if (!ImGui.BeginTabItem(label)) return;

        try { body(); } finally { ImGui.EndTabItem(); }
    }

    // name stretches, the rating buttons sit in a pinned column so long names cannot run under them
    private static readonly (string Name, float Width)[] Columns = { ("skill", 0f), ("rating", 78f) };

    private const ImGuiTableFlags TableFlags = ImGuiTableFlags.RowBg | ImGuiTableFlags.NoBordersInBody;

    private static void DrawRatings(ExileTraffickingSettings settings)
    {
        SyncButtons(settings);

        ImGui.SetNextItemWidth(-1f);
        ImGui.InputTextWithHint("##et_search", "Search archetypes / skills / supports...", ref search, 64);
        ImGui.Checkbox("Only rated archetypes", ref onlyRated);
        ImGui.SameLine();
        ImGui.TextDisabled($"{MercData.BuildsByName.Count(b => b.Skills.Count > 0)} archetypes");

        foreach (var build in MercData.BuildsByName)
        {
            var rated = Ratings.Count(settings.Ratings, build.Id);
            if (onlyRated && rated == 0) continue;

            var skills = Visible(build).ToList();
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

    // onlyRated filters archetypes, never their contents: an archetype you opened shows its whole pool
    private static IEnumerable<string> Visible(MercBuild build)
    {
        var buildMatches = BuildMatches(build);

        foreach (var (skill, supports) in build.Skills)
        {
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
            using (TextFor(rating, settings))
            {
                open = ImGui.TreeNodeEx($"{skill}##node", supports.Count == 0
                    ? ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.Bullet | ImGuiTreeNodeFlags.SpanFullWidth
                    : ImGuiTreeNodeFlags.SpanFullWidth);
            }

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

            using (Tables.Row(row++))
            {
                ImGui.TableNextColumn();
                using (TextFor(value, settings))
                {
                    ImGui.TreeNodeEx(support, ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.NoTreePushOnOpen |
                                              ImGuiTreeNodeFlags.SpanFullWidth);
                }

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
