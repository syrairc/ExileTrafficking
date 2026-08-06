using System.Collections.Generic;
using ExileCore.Shared.Attributes;
using ExileCore.Shared.Interfaces;
using ExileCore.Shared.Nodes;
using SharpDX;

namespace ExileTrafficking;

public class ExileTraffickingSettings : ISettings
{
    public ToggleNode Enable { get; set; } = new ToggleNode(false);

    [Menu("League override", "Leave empty to read the league from the game.")]
    public TextNode LeagueOverride { get; set; } = new TextNode("");

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

    [Menu("Overlay font size")]
    public RangeNode<int> OverlayFontSize { get; set; } = new RangeNode<int>(16, 8, 48);

    [Menu("Overlay verdict line")]
    public ToggleNode OverlayVerdict { get; set; } = new ToggleNode(true);

    [Menu("Good colour")]
    public ColorNode GoodColor { get; set; } = new ColorNode(Color.FromRgba(0xFF6EEB82));

    [Menu("Neutral colour")]
    public ColorNode NeutralColor { get; set; } = new ColorNode(Color.FromRgba(0xFFD8D8D8));

    [Menu("Bricked colour")]
    public ColorNode BrickedColor { get; set; } = new ColorNode(Color.FromRgba(0xFF5C5CE5));

    public Dictionary<string, BuildRating> Ratings { get; set; } = new Dictionary<string, BuildRating>();
}
