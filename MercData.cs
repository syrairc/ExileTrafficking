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
    public IReadOnlyList<string> Infamous { get; init; }
    public string Class { get; init; }
    public string Path { get; init; }
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Skills { get; init; }
}

public sealed record MercClass(string Id, string House, string Archetype);

public static class MercData
{
    // MercenaryClasses.dat, in row order - the preload hands you an index into this. house and
    // archetype live in the row's icon paths rather than mercdata.json, so they're spelled out, but
    // the build list is derived below so it tracks the data instead of drifting from it.
    // archetype alone never identifies a class: every one but Scion has two classes in the same
    // house, so the builds are the part that actually pins it down.
    private static readonly MercClass[] ClassRows =
    {
        new("ElementalWitch", "Cyaxan", "Witch"),
        new("ChaosMinionWitch", "Cyaxan", "Witch"),
        new("TrapsMinesShadow", "Azadi", "Shadow"),
        new("Crit1HShadow", "Azadi", "Shadow"),
        new("MeleeAOEMarauder", "Keita", "Marauder"),
        new("MeleeStrikesMarauder", "Keita", "Marauder"),
        new("MiscScion", "Bardiya", "Scion"),
        new("AurasMinionsTemplar", "Keita", "Templar"),
        new("PhysConvertTemplar", "Keita", "Templar"),
        new("NonEleBowRanger", "Cyaxan", "Ranger"),
        new("EleBowRanger", "Cyaxan", "Ranger"),
        new("PhysicalDuelist", "Azadi", "Duelist"),
        new("MeleeAOEStrikeDuelist", "Azadi", "Duelist"),
    };

    public static MercClass ClassAt(int index) =>
        index >= 0 && index < ClassRows.Length ? ClassRows[index] : null;

    // every build that class can roll, 2 or 3 of them. infamous variants are aliases on these rather
    // than builds of their own, so they don't need filtering out
    public static IReadOnlyList<MercBuild> ClassBuilds(string classId) =>
        BuildsById.Values
            .Where(x => x.Class == classId)
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static IReadOnlyList<string> BuildsInClass(string classId) =>
        ClassBuilds(classId).Select(x => x.Name).ToList();

    private static readonly Dictionary<string, string> SkillIds;
    private static readonly Dictionary<string, string> SupportIds;
    private static readonly Dictionary<string, string> SkillNamesById;
    private static readonly Dictionary<string, string> SupportNamesById;
    private static readonly Dictionary<string, string> ArchetypeIds;
    private static readonly Dictionary<string, string> Effects;
    private static readonly Dictionary<string, MercBuild> BuildsById;
    private static readonly Dictionary<string, MercBuild> BuildsByArchetype;
    private static readonly Dictionary<ushort, MercBuild> BuildsByHash;
    private static readonly Dictionary<ushort, string> TypeOptionsByHash;

    public static IReadOnlyDictionary<string, MercBuild> Builds => BuildsById;
    public static IReadOnlyList<MercBuild> BuildsByName { get; }
    public static IReadOnlyCollection<string> ArchetypeNames => ArchetypeIds.Keys;

    static MercData()
    {
        var root = Load() ?? new JObject();
        SkillIds = Strings(root, "skills");
        SupportIds = Strings(root, "supports");
        SkillNamesById = Reverse(SkillIds);
        SupportNamesById = Reverse(SupportIds);
        ArchetypeIds = Strings(root, "archetypes");
        Effects = Strings(root, "grantedEffects");
        BuildsById = ReadBuilds(root);
        BuildsByName = BuildsById.Values.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();

        BuildsByArchetype = new Dictionary<string, MercBuild>(StringComparer.OrdinalIgnoreCase);
        foreach (var build in BuildsById.Values)
        {
            BuildsByArchetype[build.Name] = build;
            foreach (var alias in build.Infamous) BuildsByArchetype[alias] = build;
        }

        BuildsByHash = new Dictionary<ushort, MercBuild>();
        foreach (var (hash, id) in Strings(root, "buildHashes"))
        {
            if (ushort.TryParse(hash, out var key) && BuildsById.TryGetValue(id, out var build))
            {
                BuildsByHash[key] = build;
            }
        }

        TypeOptionsByHash = new Dictionary<ushort, string>();
        foreach (var (hash, option) in Strings(root, "typeOptions"))
        {
            if (ushort.TryParse(hash, out var key)) TypeOptionsByHash[key] = option;
        }
    }

    public static string SkillId(string tradeName) => Get(SkillIds, tradeName);
    public static string SupportId(string tradeName) => Get(SupportIds, tradeName);
    public static string ArchetypeId(string display) => Get(ArchetypeIds, display);
    // ratings are keyed by display name, the memory path only ever sees trade ids
    public static string SkillName(string tradeId) => Get(SkillNamesById, tradeId);
    public static string SupportName(string tradeId) => Get(SupportNamesById, tradeId);
    // a merc skill has two granted effects: the hired one the table is keyed by
    // (ShieldCrushMercenary) and the encounter one an offer in the world actually casts
    // (ShieldCrushMercenaryEncounter). same skill, so fall through to the base id
    public static string SkillFromEffect(string grantedEffectId) =>
        Get(Effects, grantedEffectId) ??
        (grantedEffectId != null && grantedEffectId.EndsWith(EncounterSuffix, StringComparison.Ordinal)
            ? Get(Effects, grantedEffectId[..^"Encounter".Length])
            : null);

    private const string EncounterSuffix = "MercenaryEncounter";

    public static MercBuild BuildForHash(ushort hash) =>
        BuildsByHash.TryGetValue(hash, out var build) ? build : null;

    // infamous rows share a base build but are a separate warrant on trade, so this must not fold
    public static string TypeOptionForHash(ushort hash) =>
        TypeOptionsByHash.TryGetValue(hash, out var option) ? option : null;

    // infamous rows fold to the same build, so the alias is the only name that tells the two apart.
    // a build with no alias listed still has to read as infamous rather than silently as its base
    public static string DisplayName(MercBuild build, bool infamous) =>
        !infamous ? build?.Name ?? "" : build?.Infamous.FirstOrDefault() ?? $"Infamous {build?.Name}";

    public static MercBuild BuildForArchetype(string display) =>
        !string.IsNullOrWhiteSpace(display) && BuildsByArchetype.TryGetValue(display.Trim(), out var build)
            ? build
            : null;

    public static MercBuild Infer(IReadOnlyCollection<string> skillNames, string metadataPath) =>
        Infer(skillNames, metadataPath, out _);

    public static MercBuild Infer(IReadOnlyCollection<string> skillNames, string metadataPath, out bool certain)
    {
        certain = true;
        if (skillNames == null || skillNames.Count == 0) return null;

        var matches = BuildsById.Values
            .Where(b => skillNames.All(b.Skills.ContainsKey))
            .ToList();
        if (matches.Count <= 1) return matches.FirstOrDefault();

        var path = PathName(metadataPath);
        var byPath = matches.Where(b => b.Path == path).ToList();
        if (byPath.Count == 1) return byPath[0];

        // path didn't narrow it to one, genuinely ambiguous
        certain = false;
        return byPath.FirstOrDefault() ?? matches[0];
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

    // first name wins, a couple of ids are shared by more than one display string
    private static Dictionary<string, string> Reverse(Dictionary<string, string> table)
    {
        var flipped = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, id) in table)
        {
            if (!string.IsNullOrWhiteSpace(id)) flipped.TryAdd(id, name);
        }

        return flipped;
    }

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
            var infamous = value["infamous"]?.ToObject<List<string>>() ?? new List<string>();
            builds[id] = new MercBuild
            {
                Id = id,
                Name = (string)value["name"],
                Infamous = infamous,
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
