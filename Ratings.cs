using System.Collections.Generic;
using System.Linq;
using SharpDX;

namespace ExileTrafficking;

public enum Rating
{
    Neutral = 0,
    Good = 1,
    Bricked = -1,
}

public class SkillRating
{
    public Rating Rating { get; set; }
    public Dictionary<string, Rating> Supports { get; set; } = new();
}

public class BuildRating
{
    public Dictionary<string, SkillRating> Skills { get; set; } = new();
}

public static class Ratings
{
    public static Rating Skill(Dictionary<string, BuildRating> store, string buildId, string skill) =>
        Find(store, buildId, skill)?.Rating ?? Rating.Neutral;

    public static Rating Support(Dictionary<string, BuildRating> store, string buildId, string skill, string support)
    {
        var entry = Find(store, buildId, skill);
        return entry != null && entry.Supports.TryGetValue(support, out var rating) ? rating : Rating.Neutral;
    }

    public static void SetSkill(Dictionary<string, BuildRating> store, string buildId, string skill, Rating rating)
    {
        var entry = rating == Rating.Neutral ? Find(store, buildId, skill) : Create(store, buildId, skill);
        if (entry == null) return;

        entry.Rating = rating;
        Prune(store, buildId, skill);
    }

    public static void SetSupport(Dictionary<string, BuildRating> store, string buildId, string skill,
        string support, Rating rating)
    {
        var entry = rating == Rating.Neutral ? Find(store, buildId, skill) : Create(store, buildId, skill);
        if (entry == null) return;

        if (rating == Rating.Neutral) entry.Supports.Remove(support);
        else entry.Supports[support] = rating;

        Prune(store, buildId, skill);
    }

    public static int Count(Dictionary<string, BuildRating> store, string buildId) =>
        store != null && store.TryGetValue(buildId ?? "", out var build)
            ? build.Skills.Values.Sum(x => (x.Rating == Rating.Neutral ? 0 : 1) + x.Supports.Count)
            : 0;

    public static Color Colour(Rating rating, Color good, Color neutral, Color bricked) => rating switch
    {
        Rating.Good => good,
        Rating.Bricked => bricked,
        _ => neutral,
    };

    public static Rating Verdict(Dictionary<string, BuildRating> store, string buildId, IEnumerable<string> skills)
    {
        var worst = Rating.Neutral;
        foreach (var skill in skills ?? Enumerable.Empty<string>())
        {
            var rating = Skill(store, buildId, skill);
            if (rating == Rating.Bricked) return Rating.Bricked;
            if (rating == Rating.Good) worst = Rating.Good;
        }

        return worst;
    }

    private static SkillRating Find(Dictionary<string, BuildRating> store, string buildId, string skill) =>
        store != null && store.TryGetValue(buildId ?? "", out var build) &&
        build.Skills.TryGetValue(skill ?? "", out var entry)
            ? entry
            : null;

    private static SkillRating Create(Dictionary<string, BuildRating> store, string buildId, string skill)
    {
        if (store == null || string.IsNullOrEmpty(buildId) || string.IsNullOrEmpty(skill)) return null;

        if (!store.TryGetValue(buildId, out var build)) store[buildId] = build = new BuildRating();
        if (!build.Skills.TryGetValue(skill, out var entry)) build.Skills[skill] = entry = new SkillRating();
        return entry;
    }

    // sparse store, an all-neutral branch is the same as no branch
    private static void Prune(Dictionary<string, BuildRating> store, string buildId, string skill)
    {
        if (!store.TryGetValue(buildId, out var build)) return;

        if (build.Skills.TryGetValue(skill, out var entry) &&
            entry.Rating == Rating.Neutral && entry.Supports.Count == 0)
        {
            build.Skills.Remove(skill);
        }

        if (build.Skills.Count == 0) store.Remove(buildId);
    }
}
