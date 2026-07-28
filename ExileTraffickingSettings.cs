using ExileCore.Shared.Attributes;
using ExileCore.Shared.Interfaces;
using ExileCore.Shared.Nodes;

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
}
