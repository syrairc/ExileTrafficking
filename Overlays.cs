using System;
using System.Collections.Generic;
using System.Linq;
using ExileCore;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.Elements.InventoryElements;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using SharpDX;
using Vector2 = System.Numerics.Vector2;

namespace ExileTrafficking;

// everything drawn with Graphics rather than ImGui: the label over a mercenary's head, the breakdown
// beside a hovered warrant, and the outlines on the encounter panel

// a stack of measured lines. both overlays lay out the same way and only differ on alignment, so the
// measuring lives here once - and it has to be measured rather than lines * a nominal height, or the
// block drifts off its anchor as the line count changes
internal sealed class TextBlock
{
    // panel colour for anything that wants a backdrop behind its lines
    public static readonly Color Backdrop = Color.FromRgba(0xD2141414);

    // no stroked text in ExileCore, so lay black down eight ways first
    private static readonly Vector2[] StrokeOffsets =
    {
        new(-1, -1), new(0, -1), new(1, -1),
        new(-1, 0), new(1, 0),
        new(-1, 1), new(0, 1), new(1, 1),
    };

    private readonly List<(string Text, Color Color, float Indent, Vector2 Size)> lines = new();

    public float Width { get; private set; }
    public float Height { get; private set; }

    public void Clear()
    {
        lines.Clear();
        Width = 0f;
        Height = 0f;
    }

    public void Add(Graphics graphics, string text, Color color, float indent = 0f)
    {
        var size = graphics.MeasureText(text);
        lines.Add((text, color, indent, size));

        Height += size.Y;
        Width = Math.Max(Width, indent + size.X);
    }

    // x is the centre line when centre aligned, the left edge otherwise
    public void Draw(Graphics graphics, float x, float top, FontAlign align)
    {
        foreach (var (text, color, indent, size) in lines)
        {
            var position = new Vector2(x + indent, top);

            foreach (var offset in StrokeOffsets)
            {
                graphics.DrawText(text, position + offset, Color.Black, align);
            }

            graphics.DrawText(text, position, color, align);
            top += size.Y;
        }
    }
}

// one hireable mercenary standing in the zone, resolved once a frame and shared by the overlays that
// want it. the area line needs the same walk the world overlay does, so doing it twice would be waste
public sealed record MercSighting(Entity Entity, MercBuild Build, bool Certain, bool Infamous,
    bool Active, IReadOnlyList<string> Skills, int Level);

public static class WorldOverlay
{
    private const float Padding = 6f;

    // merc slots are the packed ids, 1024 apart, starting here. everything below is movement and daemons
    public const int MercSkillIdBase = 32896;

    // a mercenary you have turned on is no longer an offer, so it drops off the overlay
    public static bool IsHireable(Entity entity) =>
        entity != null && entity.IsValid && !entity.IsHostile &&
        entity.Path != null && entity.Path.Contains("/Mercenaries/") && !entity.Path.Contains("Allied") &&
        entity.TryGetComponent<MinimapIcon>(out var icon) && icon?.Name == "MercenaryEncounter";

    public static List<string> SkillNames(Entity entity)
    {
        var names = new List<string>();
        if (!entity.TryGetComponent<Actor>(out var actor) || actor?.ActorSkills == null) return names;

        foreach (var skill in actor.ActorSkills)
        {
            if (skill == null || skill.Id < MercSkillIdBase) continue;

            var name = MercData.SkillFromEffect(skill.Name);
            if (name != null && !names.Contains(name)) names.Add(name);
        }

        return names;
    }

    // encounter_state 1 is the offer you can actually walk up to and talk to. the spares an infamy
    // scarab drops sit at 8 with their minimap icon hidden, and one you've already hired reads 0
    public static bool IsActive(Entity entity) =>
        entity.TryGetComponent<StateMachine>(out var machine) &&
        machine?.States?.Any(x => x.Name == "encounter_state" && x.Value == 1) == true;

    public static List<MercSighting> Sightings(GameController game)
    {
        var found = new List<MercSighting>();

        foreach (var entity in game.EntityListWrapper.OnlyValidEntities)
        {
            if (!IsHireable(entity)) continue;

            var skills = SkillNames(entity);
            if (skills.Count == 0) continue;

            // the component names the exact build row, which settles the archetype outright and is
            // the only thing that tells infamous from base - those two share a skill pool and a path.
            // inference is still the fallback for when the read comes back empty
            var hash = MercenaryMemory.EntityBuildHash(game, entity);
            var build = MercData.BuildForHash(hash);
            var certain = build != null;
            if (build == null) build = MercData.Infer(skills, entity.Path, out certain);

            var infamous = MercData.TypeOptionForHash(hash)?
                .EndsWith("Noble", StringComparison.Ordinal) == true;

            found.Add(new MercSighting(entity, build, certain, infamous, IsActive(entity), skills,
                MercData.Level(entity.Path)));
        }

        // a scarab can put three in the map, so keep them in a stable order: the real offer first,
        // then the wild ones nearest out
        return found
            .OrderByDescending(x => x.Active)
            .ThenBy(x => x.Entity.DistancePlayer)
            .ToList();
    }

    // text is centre aligned on x, so the block is as wide as its widest line
    private static readonly TextBlock Block = new();

    public static void Draw(GameController game, Graphics graphics, ExileTraffickingSettings settings,
        IReadOnlyList<MercSighting> sightings, RectangleF? panel = null)
    {
        var window = game.Window.GetWindowRectangleTimeCache;

        // 16 is the default overlay font size, treat it as scale 1.0
        using var _ = graphics.SetTextScale(settings.OverlayFontSize.Value / 16f);

        foreach (var sighting in sightings)
        {
            if (!sighting.Active && !settings.WildOverlay) continue;

            var entity = sighting.Entity;
            var skills = sighting.Skills;
            var buildId = sighting.Build?.Id;

            Block.Clear();
            Block.Add(graphics, Header(sighting, settings), settings.HeaderColor.Value);

            // a wild mercenary is not an offer, so rating its skills or calling a verdict on it is
            // noise. name and pool only
            foreach (var skill in skills)
            {
                Block.Add(graphics, skill, sighting.Active
                    ? Ratings.Colour(Ratings.Skill(settings.Ratings, buildId, skill), settings)
                    : settings.NeutralColor.Value);
            }

            if (settings.OverlayVerdict && sighting.Active)
            {
                var verdict = Ratings.Verdict(settings.Ratings, buildId, skills);
                Block.Add(graphics, Word(verdict), Ratings.Colour(verdict, settings));
            }

            // Entity.Pos is the bounding box corner, so it sits half a box north east of the model and
            // the projection turns that into a sideways slide as the camera moves. centre first, then
            // rise half a box to the top of the head
            var head = entity.BoundsCenterPosNum;
            head.Z -= (entity.GetComponent<Render>()?.BoundsNum.Z ?? 0f) / 2f;
            var origin = game.IngameState.Camera.WorldToScreen(head);
            origin.X += settings.OverlayOffsetX.Value;
            origin.Y += settings.OverlayOffsetY.Value;

            var top = origin.Y - Block.Height;
            if (origin.X < 0 || origin.X > window.Width || origin.Y < 0 || top > window.Height) continue;

            if (panel is { } rect &&
                new RectangleF(origin.X - Block.Width / 2f, top, Block.Width, Block.Height).Intersects(rect))
            {
                continue;
            }

            if (settings.OverlayBackground)
            {
                graphics.DrawBox(new RectangleF(origin.X - Block.Width / 2f - Padding, top - Padding,
                    Block.Width + Padding * 2f, Block.Height + Padding * 2f), TextBlock.Backdrop);
            }

            Block.Draw(graphics, origin.X, top, FontAlign.Center);
        }
    }

    public static string Header(MercSighting sighting, ExileTraffickingSettings settings)
    {
        var level = settings.ShowLevel ? $"  LVL {sighting.Level}" : "";

        return sighting.Build == null
            ? $"UNKNOWN{level}"
            : $"{MercData.DisplayName(sighting.Build, sighting.Infamous).ToUpperInvariant()}" +
              $"{(sighting.Certain ? "" : "?")}{level}";
    }

    private static string Word(Rating rating) => rating switch
    {
        Rating.Good => "GOOD",
        Rating.Bricked => "BRICKED",
        _ => "NEUTRAL",
    };
}

public sealed record HoveredWarrant(MemMerc Merc, RectangleF Anchor);

// hover a mercenary warrant and get the same readout the world overlay gives, parked next to the
// game's own tooltip
public static class WarrantTooltip
{
    private const float Gap = 6f;      // between the game's tooltip and ours
    private const float Padding = 6f;
    private const float Indent = 18f;

    private static readonly Color HintColor = Color.FromRgba(0xFF8A8A8A);

    // what's on a warrant never changes, so hold the last one rather than re-walking it every frame.
    // keyed on the component address and not Entity.Id - ids are not unique for items, three
    // inventory slots happily share one, and keying on that served warrants for unrelated items
    private static long lastComponent;
    private static MemMerc lastMerc;

    private static readonly TextBlock Block = new();

    public static HoveredWarrant Hovered(GameController game)
    {
        try
        {
            var hover = game?.IngameState?.UIHover;
            if (hover == null || hover.Address == 0) return null;

            // the item's tooltip is what we anchor to, and it's only up once the game has drawn it
            var tip = hover.Tooltip;
            if (tip == null || !tip.IsValid || !tip.IsVisible) return null;

            var item = hover.AsObject<NormalInventoryItem>()?.Item;
            if (item == null || item.Address == 0 || !item.IsValid) return null;

            var merc = Merc(item);
            return merc == null ? null : new HoveredWarrant(merc, tip.GetClientRect());
        }
        catch
        {
            return null;
        }
    }

    public static void Draw(Graphics graphics, ExileTraffickingSettings settings, HoveredWarrant hovered,
        RectangleF window)
    {
        var merc = hovered.Merc;
        var buildId = merc.Build?.Id;

        using var _ = graphics.SetTextScale(settings.OverlayFontSize.Value / 16f);

        Block.Clear();
        Block.Add(graphics, Header(merc, settings), settings.HeaderColor.Value);

        foreach (var skill in merc.Skills)
        {
            var name = MercData.SkillName(skill.TradeId) ?? skill.TradeId;
            Block.Add(graphics, name, Ratings.Colour(Ratings.Skill(settings.Ratings, buildId, name), settings));

            foreach (var id in skill.SupportTradeIds)
            {
                var support = MercData.SupportName(id) ?? id;
                Block.Add(graphics, support,
                    Ratings.Colour(Ratings.Support(settings.Ratings, buildId, name, support), settings), Indent);
            }
        }

        Block.Add(graphics, $"press [{settings.WarrantSearchKey.Value.Key}] to search trade", HintColor);

        var w = Block.Width + Padding * 2f;
        var h = Block.Height + Padding * 2f;

        // sit on whichever side of the game's tooltip has the room
        var x = hovered.Anchor.Center.X > window.Width / 2f
            ? hovered.Anchor.Left - Gap - w
            : hovered.Anchor.Right + Gap;

        x = Math.Clamp(x, 0f, Math.Max(0f, window.Width - w));
        var y = Math.Clamp(hovered.Anchor.Top, 0f, Math.Max(0f, window.Height - h));

        graphics.DrawBox(new RectangleF(x, y, w, h), TextBlock.Backdrop);
        Block.Draw(graphics, x + Padding, y + Padding, FontAlign.Left);
    }

    private static MemMerc Merc(Entity item)
    {
        // resolve the component first, every frame. it's one dictionary lookup, and it's what rules
        // out the currency/scarab/map you're actually hovering before any cached value gets a say
        var component = MercenaryMemory.Component(item);
        if (component == null || component.Address == 0) return null;

        if (component.Address == lastComponent) return lastMerc;

        var merc = component.Merc;
        if (merc == null) return null;

        lastComponent = component.Address;
        lastMerc = merc;
        return merc;
    }

    private static string Header(MemMerc merc, ExileTraffickingSettings settings)
    {
        var level = settings.ShowLevel ? $"  LVL {merc.Level}" : "";

        var build = merc.Build;
        if (build == null) return $"UNKNOWN BUILD {merc.BuildHash}{level}";

        // infamous rows fold to the same build, the trade option is the only thing that keeps the
        // two apart, and it's the name the game prints
        var infamous = MercData.TypeOptionForHash(merc.BuildHash)?.EndsWith("Noble", StringComparison.Ordinal) == true;

        return $"{MercData.DisplayName(build, infamous).ToUpperInvariant()}{level}";
    }
}

public static class AreaMercenaryOverlay
{
    private const float Padding = 6f;

    private static readonly Color BuildsColor = Color.FromRgba(0xFFB8B8B8);
    private static readonly TextBlock Block = new();

    public static void Draw(Graphics graphics, ExileTraffickingSettings settings, MercClass merc,
        MercSighting sighting, RectangleF window)
    {
        using var _ = graphics.SetTextScale(settings.AreaTextScale.Value);

        var builds = MercData.ClassBuilds(merc.Id);

        // once the mercenary is actually standing there the class is guesswork nobody needs, so the
        // line narrows to the one archetype and its own rating rather than the class's best
        var verdict = sighting?.Build != null
            ? Ratings.Build(settings.Ratings, sighting.Build.Id)
            : Ratings.Best(settings.Ratings, builds.Select(x => x.Id));

        Block.Clear();
        if (sighting != null)
        {
            Block.Add(graphics, WorldOverlay.Header(sighting, settings), settings.HeaderColor.Value);

            if (settings.AreaSkills)
            {
                foreach (var skill in sighting.Skills)
                {
                    Block.Add(graphics, skill,
                        Ratings.Colour(Ratings.Skill(settings.Ratings, sighting.Build?.Id, skill), settings));
                }
            }
        }
        else
        {
            Block.Add(graphics, $"{merc.House} {merc.Archetype}", settings.HeaderColor.Value);
            if (builds.Count > 0) Block.Add(graphics, string.Join(", ", builds.Select(x => x.Name)), BuildsColor);
        }

        var right = window.Width - settings.AreaOffsetX.Value;
        var top = (float)settings.AreaOffsetY.Value;
        var box = new RectangleF(right - Block.Width - Padding, top - Padding, Block.Width + Padding * 2f,
            Block.Height + Padding * 2f);

        // an unrated class looks exactly as it did before the tint existed
        var style = verdict == Rating.Neutral ? "Off" : settings.AreaRatingStyle.Value;
        var tint = Ratings.Colour(verdict, settings);

        graphics.DrawBox(box, style is "Background" or "Both" ? Wash(tint) : TextBlock.Backdrop);
        if (style is "Border" or "Both") graphics.DrawFrame(box, tint, 2);

        Block.Draw(graphics, right, top, FontAlign.Right);
    }

    // full strength behind the text is unreadable, so keep the hue and drop it to backdrop darkness
    private static Color Wash(Color color) =>
        new((byte)(color.R * 0.35f), (byte)(color.G * 0.35f), (byte)(color.B * 0.35f), TextBlock.Backdrop.A);
}

// rated skills and supports get a frame drawn round the game's own panel widgets
public static class PanelHighlight
{
    public static void Draw(Graphics graphics, MercSnapshot snapshot, ExileTraffickingSettings settings)
    {
        var build = MercData.BuildForArchetype(snapshot.Archetype);
        if (build == null) return;

        foreach (var skill in snapshot.Skills)
        {
            Outline(graphics, skill.Row, Ratings.Skill(settings.Ratings, build.Id, skill.Name), settings);

            foreach (var support in skill.Supports)
            {
                var rating = Ratings.Support(settings.Ratings, build.Id, skill.Name, support.Name);
                Outline(graphics, support.Icon, rating, settings);
            }
        }
    }

    private static void Outline(Graphics graphics, Element element, Rating rating,
        ExileTraffickingSettings settings)
    {
        if (rating == Rating.Neutral || element == null || !element.IsValid) return;

        graphics.DrawFrame(element.GetClientRect(), Ratings.Colour(rating, settings), 2);
    }
}
