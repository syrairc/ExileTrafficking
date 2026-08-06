using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Newtonsoft.Json;

namespace ExileTrafficking;

public static class ShareCode
{
    public const string Prefix = "ET1:";

    // wire shape: {"v":1,"b":{buildId:{skill:[rating,{support:rating}]}}}
    private class Payload
    {
        [JsonProperty("v")] public int Version { get; set; } = 1;
        [JsonProperty("b")] public Dictionary<string, Dictionary<string, object[]>> Builds { get; set; } = new();
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
            using var reader = new StreamReader(deflate, Encoding.UTF8);
            var payload = JsonConvert.DeserializeObject<Payload>(reader.ReadToEnd());
            if (payload?.Builds == null) return null;

            var store = new Dictionary<string, BuildRating>();
            foreach (var (id, skills) in payload.Builds)
            {
                foreach (var (skill, pair) in skills)
                {
                    Ratings.SetSkill(store, id, skill, (Rating)Convert.ToInt32(pair[0]));

                    var supports = JsonConvert.DeserializeObject<Dictionary<string, int>>(
                        JsonConvert.SerializeObject(pair[1]));
                    foreach (var (support, rating) in supports ?? new Dictionary<string, int>())
                    {
                        Ratings.SetSupport(store, id, skill, support, (Rating)rating);
                    }
                }
            }

            return store;
        }
        catch
        {
            return null;
        }
    }

    public static void Apply(Dictionary<string, BuildRating> store,
        Dictionary<string, BuildRating> incoming, bool replace)
    {
        if (store == null || incoming == null) return;

        foreach (var (id, build) in incoming)
        {
            if (replace) store.Remove(id);

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
