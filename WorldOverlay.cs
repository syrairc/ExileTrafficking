using System;
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

    public static void Draw(GameController game, Graphics graphics, ExileTraffickingSettings settings,
        RectangleF? panel = null)
    {
        var window = game.Window.GetWindowRectangleTimeCache;

        // 16 is the default overlay font size, treat it as scale 1.0
        var scale = settings.OverlayFontSize.Value / 16f;
        using var _ = graphics.SetTextScale(scale);

        foreach (var entity in game.EntityListWrapper.OnlyValidEntities)
        {
            if (!IsHireable(entity)) continue;

            var skills = SkillNames(entity);
            if (skills.Count == 0) continue;

            var build = MercData.Infer(skills, entity.Path, out var certain);
            var buildId = build?.Id;

            Block.Clear();
            Add(graphics, build == null
                ? $"UNKNOWN  LVL {MercData.Level(entity.Path)}"
                : $"{build.Name.ToUpperInvariant()}{(certain ? "" : "?")}  LVL {MercData.Level(entity.Path)}",
                settings.HeaderColor.Value);

            foreach (var skill in skills)
            {
                Add(graphics, skill, Colour(Ratings.Skill(settings.Ratings, buildId, skill), settings));
            }

            if (settings.OverlayVerdict)
            {
                var verdict = Ratings.Verdict(settings.Ratings, buildId, skills);
                Add(graphics, Word(verdict), Colour(verdict, settings));
            }

            // Entity.Pos is the bounding box corner, so it sits half a box north east of the model and
            // the projection turns that into a sideways slide as the camera moves. centre first, then
            // rise half a box to the top of the head
            var head = entity.BoundsCenterPosNum;
            head.Z -= (entity.GetComponent<Render>()?.BoundsNum.Z ?? 0f) / 2f;
            var origin = game.IngameState.Camera.WorldToScreen(head);
            origin.X += settings.OverlayOffsetX.Value;
            origin.Y += settings.OverlayOffsetY.Value;

            // measured, not lines * a nominal height: the block is laid out with the same numbers it
            // reserves, so its bottom line stays a fixed gap above the head whatever the skill count
            float height = 0f, width = 0f;
            foreach (var (_, _, size) in Block)
            {
                height += size.Y;
                width = Math.Max(width, size.X);
            }

            var top = origin.Y - height;
            if (origin.X < 0 || origin.X > window.Width || origin.Y < 0 || top > window.Height) continue;

            if (panel is { } rect &&
                new RectangleF(origin.X - width / 2f, top, width, height).Intersects(rect))
            {
                continue;
            }

            var y = top;
            foreach (var (text, color, size) in Block)
            {
                Line(graphics, text, new Vector2(origin.X, y), color);
                y += size.Y;
            }
        }
    }

    // text is centre aligned on x, so the block is as wide as its widest line
    private static readonly List<(string Text, Color Color, Vector2 Size)> Block = new();

    private static void Add(Graphics graphics, string text, Color color) =>
        Block.Add((text, color, graphics.MeasureText(text)));

    private static Color Colour(Rating rating, ExileTraffickingSettings settings) =>
        Ratings.Colour(rating, settings.GoodColor.Value, settings.NeutralColor.Value, settings.BrickedColor.Value);

    private static string Word(Rating rating) => rating switch
    {
        Rating.Good => "GOOD",
        Rating.Bricked => "BRICKED",
        _ => "NEUTRAL",
    };

    // no stroked text in ExileCore, so lay black down eight ways first
    private static void Line(Graphics graphics, string text, Vector2 position, Color color)
    {
        foreach (var offset in StrokeOffsets)
        {
            graphics.DrawText(text, position + offset, Color.Black, FontAlign.Center);
        }

        graphics.DrawText(text, position, color, FontAlign.Center);
    }
}
