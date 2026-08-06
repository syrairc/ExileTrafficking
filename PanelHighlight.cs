using ExileCore;
using ExileCore.PoEMemory;
using SharpDX;

namespace ExileTrafficking;

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

        var color = rating == Rating.Good ? settings.GoodColor.Value : settings.BrickedColor.Value;
        graphics.DrawFrame(element.GetClientRect(), color, 2);
    }
}
