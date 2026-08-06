using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace ExileTrafficking;

public sealed class MercBuild
{
    public string Id { get; init; }
    public string Name { get; init; }
    public string Infamous { get; init; }
    public string Class { get; init; }
    public string Path { get; init; }
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Skills { get; init; }
}

public static class MercData
{
    private static readonly Dictionary<string, string> SkillIds;
    private static readonly Dictionary<string, string> SupportIds;
    private static readonly Dictionary<string, string> ArchetypeIds;
    private static readonly Dictionary<string, string> Effects;
    private static readonly Dictionary<string, MercBuild> BuildsById;
    private static readonly Dictionary<string, MercBuild> BuildsByArchetype;

    public static IReadOnlyDictionary<string, MercBuild> Builds => BuildsById;
    public static IReadOnlyList<MercBuild> BuildsByName { get; }

    static MercData()
    {
        var root = Load() ?? new JObject();
        SkillIds = Strings(root, "skills");
        SupportIds = Strings(root, "supports");
        ArchetypeIds = Strings(root, "archetypes");
        Effects = Strings(root, "grantedEffects");
        BuildsById = ReadBuilds(root);
        BuildsByName = BuildsById.Values.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();

        BuildsByArchetype = new Dictionary<string, MercBuild>(StringComparer.OrdinalIgnoreCase);
        foreach (var build in BuildsById.Values)
        {
            BuildsByArchetype[build.Name] = build;
            if (!string.IsNullOrEmpty(build.Infamous)) BuildsByArchetype[build.Infamous] = build;
        }
    }

    public static string SkillId(string tradeName) => Get(SkillIds, tradeName);
    public static string SupportId(string tradeName) => Get(SupportIds, tradeName);
    public static string ArchetypeId(string display) => Get(ArchetypeIds, display);
    public static string SkillFromEffect(string grantedEffectId) => Get(Effects, grantedEffectId);

    public static MercBuild BuildForArchetype(string display) =>
        !string.IsNullOrWhiteSpace(display) && BuildsByArchetype.TryGetValue(display.Trim(), out var build)
            ? build
            : null;

    public static MercBuild Infer(IReadOnlyCollection<string> skillNames, string metadataPath)
    {
        if (skillNames == null || skillNames.Count == 0) return null;

        var matches = BuildsById.Values
            .Where(b => skillNames.All(b.Skills.ContainsKey))
            .ToList();
        if (matches.Count <= 1) return matches.FirstOrDefault();

        var path = PathName(metadataPath);
        return matches.FirstOrDefault(b => b.Path == path) ?? matches[0];
    }

    // "Metadata/Monsters/Mercenaries/MercenaryShadow2@58" -> "MercenaryShadow2"
    public static string PathName(string metadataPath)
    {
        if (string.IsNullOrEmpty(metadataPath)) return null;
        var name = metadataPath[(metadataPath.LastIndexOf('/') + 1)..];
        var at = name.IndexOf('@');
        return at < 0 ? name : name[..at];
    }

    public static int Level(string metadataPath)
    {
        var at = metadataPath?.LastIndexOf('@') ?? -1;
        return at >= 0 && int.TryParse(metadataPath[(at + 1)..], out var level) ? level : 0;
    }

    private static string Get(Dictionary<string, string> table, string key) =>
        !string.IsNullOrWhiteSpace(key) && table.TryGetValue(key.Trim(), out var value) ? value : null;

    private static Dictionary<string, string> Strings(JObject root, string name) =>
        root[name]?.ToObject<Dictionary<string, string>>() ?? new Dictionary<string, string>();

    private static Dictionary<string, MercBuild> ReadBuilds(JObject root)
    {
        var builds = new Dictionary<string, MercBuild>(StringComparer.OrdinalIgnoreCase);
        if (root["builds"] is not JObject node) return builds;

        foreach (var (id, value) in node)
        {
            var skills = value["skills"]?.ToObject<Dictionary<string, List<string>>>()
                         ?? new Dictionary<string, List<string>>();
            builds[id] = new MercBuild
            {
                Id = id,
                Name = (string)value["name"],
                Infamous = (string)value["infamous"] ?? "",
                Class = (string)value["class"],
                Path = (string)value["path"],
                Skills = skills.ToDictionary(x => x.Key, x => (IReadOnlyList<string>)x.Value),
            };
        }

        return builds;
    }

    // typeof(MercData).Assembly, not GetExecutingAssembly: under the test host those differ
    private static JObject Load()
    {
        try
        {
            using var stream = typeof(MercData).Assembly
                .GetManifestResourceStream("ExileTrafficking.mercdata.json");
            using var reader = new StreamReader(stream);
            return JObject.Parse(reader.ReadToEnd());
        }
        catch
        {
            return null;
        }
    }
}
