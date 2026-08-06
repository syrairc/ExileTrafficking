using System.Collections.Generic;
using System.Numerics;
using ExileCore;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using SharpDX;
using Vector2 = System.Numerics.Vector2;

namespace ExileTrafficking;

public static class WorldOverlay
{
    // merc slots are the packed ids, 1024 apart, starting here. everything below is movement and daemons
    public const int MercSkillIdBase = 32896;

    private static readonly Vector2[] StrokeOffsets =
    {
        new(-1, -1), new(0, -1), new(1, -1),
        new(-1, 0), new(1, 0),
        new(-1, 1), new(0, 1), new(1, 1),
    };

    public static bool IsHireable(Entity entity) =>
        entity != null && entity.IsValid &&
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

    public static void Draw(GameController game, Graphics graphics, ExileTraffickingSettings settings)
    {
        var window = game.Window.GetWindowRectangleTimeCache;

        // 16 is the default overlay font size, treat it as scale 1.0
        var scale = settings.OverlayFontSize.Value / 16f;
        using var _ = graphics.SetTextScale(scale);
        var lineHeight = graphics.MeasureText("X").Y;

        foreach (var entity in game.EntityListWrapper.OnlyValidEntities)
        {
            if (!IsHireable(entity)) continue;

            var skills = SkillNames(entity);
            if (skills.Count == 0) continue;

            var build = MercData.Infer(skills, entity.Path);
            var buildId = build?.Id;

            var head = entity.PosNum;
            head.Z -= entity.GetComponent<Render>()?.BoundsNum.Z ?? 0f;
            var origin = game.IngameState.Camera.WorldToScreen(head);
            if (origin.X < 0 || origin.Y < 0 || origin.X > window.Width || origin.Y > window.Height) continue;

            var y = origin.Y - (skills.Count + 2) * lineHeight;

            var header = build == null
                ? $"Unknown  lvl {MercData.Level(entity.Path)}"
                : $"{build.Name}  lvl {MercData.Level(entity.Path)}";
            y = Line(graphics, header, new Vector2(origin.X, y), settings.NeutralColor.Value);

            foreach (var skill in skills)
            {
                var rating = Ratings.Skill(settings.Ratings, buildId, skill);
                y = Line(graphics, skill, new Vector2(origin.X, y), Colour(rating, settings));
            }

            if (!settings.OverlayVerdict) continue;

            var verdict = Ratings.Verdict(settings.Ratings, buildId, skills);
            Line(graphics, Word(verdict), new Vector2(origin.X, y), Colour(verdict, settings));
        }
    }

    private static string Word(Rating rating) => rating switch
    {
        Rating.Good => "GOOD",
        Rating.Bricked => "BRICKED",
        _ => "NEUTRAL",
    };

    private static Color Colour(Rating rating, ExileTraffickingSettings settings) => rating switch
    {
        Rating.Good => settings.GoodColor.Value,
        Rating.Bricked => settings.BrickedColor.Value,
        _ => settings.NeutralColor.Value,
    };

    // no stroked text in ExileCore, so lay black down eight ways first
    private static float Line(Graphics graphics, string text, Vector2 position, Color color)
    {
        foreach (var offset in StrokeOffsets)
        {
            graphics.DrawText(text, position + offset, Color.Black, FontAlign.Center);
        }

        var drawn = graphics.DrawText(text, position, color, FontAlign.Center);
        return position.Y + drawn.Y;
    }
}
