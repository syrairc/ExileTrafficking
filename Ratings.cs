using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
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
    public Rating Rating { get; set; }
    public Dictionary<string, SkillRating> Skills { get; set; } = new();
}

public static class Ratings
{
    public static Rating Build(Dictionary<string, BuildRating> store, string buildId) =>
        store != null && store.TryGetValue(buildId ?? "", out var build) ? build.Rating : Rating.Neutral;

    public static void SetBuild(Dictionary<string, BuildRating> store, string buildId, Rating rating)
    {
        if (store == null || string.IsNullOrEmpty(buildId)) return;

        if (!store.TryGetValue(buildId, out var build))
        {
            if (rating == Rating.Neutral) return;
            store[buildId] = build = new BuildRating();
        }

        build.Rating = rating;
        if (rating == Rating.Neutral && build.Skills.Count == 0) store.Remove(buildId);
    }

    // good beats bricked, so one archetype worth stopping for still colours the whole class
    public static Rating Best(Dictionary<string, BuildRating> store, IEnumerable<string> buildIds)
    {
        var best = Rating.Neutral;
        foreach (var id in buildIds ?? Enumerable.Empty<string>())
        {
            var rating = Build(store, id);
            if (rating == Rating.Good) return Rating.Good;
            if (rating == Rating.Bricked) best = Rating.Bricked;
        }

        return best;
    }

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
            ? (build.Rating == Rating.Neutral ? 0 : 1) +
              build.Skills.Values.Sum(x => (x.Rating == Rating.Neutral ? 0 : 1) + x.Supports.Count)
            : 0;

    public static Color Colour(Rating rating, Color good, Color neutral, Color bricked) => rating switch
    {
        Rating.Good => good,
        Rating.Bricked => bricked,
        _ => neutral,
    };

    // the overlays all want the same three settings nodes, so unpacking them here saves each one a helper
    public static Color Colour(Rating rating, ExileTraffickingSettings settings) =>
        Colour(rating, settings.GoodColor.Value, settings.NeutralColor.Value, settings.BrickedColor.Value);

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

        if (build.Skills.Count == 0 && build.Rating == Rating.Neutral) store.Remove(buildId);
    }
}

// import/export for the ratings above, so a build's picks can be handed to someone else
public static class ShareCode
{
    public const string Prefix = "ET1:";

    // way above a full 36-archetype table, just here to stop a crafted string inflating unbounded
    private const int MaxDecodedBytes = 256 * 1024;

    // wire shape: {"v":1,"b":{buildId:{skill:[rating,{support:rating}]}},"a":{buildId:rating}}
    // "a" arrived after "b" and stays optional, so codes written before it still read back fine
    private class Payload
    {
        [JsonProperty("v")] public int Version { get; set; } = 1;
        [JsonProperty("b")] public Dictionary<string, Dictionary<string, object[]>> Builds { get; set; } = new();
        [JsonProperty("a")] public Dictionary<string, int> Archetypes { get; set; } = new();
    }

    public static string Encode(Dictionary<string, BuildRating> store, string buildId = null)
    {
        var payload = new Payload();
        foreach (var (id, build) in store ?? new Dictionary<string, BuildRating>())
        {
            if (buildId != null && id != buildId) continue;

            var skills = new Dictionary<string, object[]>();
            foreach (var (skill, entry) in build.Skills)
            {
                skills[skill] = new object[]
                {
                    (int)entry.Rating,
                    entry.Supports.ToDictionary(x => x.Key, x => (int)x.Value),
                };
            }

            if (skills.Count > 0) payload.Builds[id] = skills;
            if (build.Rating != Rating.Neutral) payload.Archetypes[id] = (int)build.Rating;
        }

        var json = JsonConvert.SerializeObject(payload);
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, true))
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            deflate.Write(bytes, 0, bytes.Length);
        }

        return Prefix + Convert.ToBase64String(output.ToArray())
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    public static Dictionary<string, BuildRating> Decode(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;

        code = code.Trim();
        if (!code.StartsWith(Prefix, StringComparison.Ordinal)) return null;

        try
        {
            var body = code[Prefix.Length..].Replace('-', '+').Replace('_', '/');
            body = body.PadRight(body.Length + (4 - body.Length % 4) % 4, '=');

            using var input = new MemoryStream(Convert.FromBase64String(body));
            using var deflate = new DeflateStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = deflate.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (output.Length + read > MaxDecodedBytes) return null;
                output.Write(buffer, 0, read);
            }

            var payload = JsonConvert.DeserializeObject<Payload>(Encoding.UTF8.GetString(output.ToArray()));
            if (payload?.Builds == null || payload.Version != 1) return null;

            var store = new Dictionary<string, BuildRating>();
            foreach (var (id, skills) in payload.Builds)
            {
                foreach (var (skill, pair) in skills)
                {
                    Ratings.SetSkill(store, id, skill, ClampRating(Convert.ToInt32(pair[0])));

                    var supports = JsonConvert.DeserializeObject<Dictionary<string, int>>(
                        JsonConvert.SerializeObject(pair[1]));
                    foreach (var (support, rating) in supports ?? new Dictionary<string, int>())
                    {
                        Ratings.SetSupport(store, id, skill, support, ClampRating(rating));
                    }
                }
            }

            foreach (var (id, rating) in payload.Archetypes ?? new Dictionary<string, int>())
            {
                Ratings.SetBuild(store, id, ClampRating(rating));
            }

            return store;
        }
        catch
        {
            return null;
        }
    }

    // anything outside the enum's real range settles on neutral rather than persisting garbage
    private static Rating ClampRating(int value) =>
        value == (int)Rating.Good ? Rating.Good : value == (int)Rating.Bricked ? Rating.Bricked : Rating.Neutral;

    public static void Apply(Dictionary<string, BuildRating> store,
        Dictionary<string, BuildRating> incoming, bool replace)
    {
        if (store == null || incoming == null) return;

        foreach (var (id, build) in incoming)
        {
            if (replace) store.Remove(id);

            if (build.Rating != Rating.Neutral) Ratings.SetBuild(store, id, build.Rating);

            foreach (var (skill, entry) in build.Skills)
            {
                if (entry.Rating != Rating.Neutral) Ratings.SetSkill(store, id, skill, entry.Rating);
                foreach (var (support, rating) in entry.Supports)
                {
                    Ratings.SetSupport(store, id, skill, support, rating);
                }
            }
        }
    }

    public static int RatingCount(Dictionary<string, BuildRating> store) =>
        store?.Keys.Sum(id => Ratings.Count(store, id)) ?? 0;
}
