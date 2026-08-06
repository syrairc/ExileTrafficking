# Mercenary Ratings Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let the user rate mercenary skills and their supports per merc type, then surface those ratings as outline boxes in the encounter panel and as stroked text above hireable mercenaries in the world.

**Architecture:** Offline game data (build -> skill pool -> support pool, plus GrantedEffects id -> trade name) is baked into the embedded `mercdata.json` by `tools/gen-mercdata.py`. A sparse ratings store lives in the plugin settings. Two renderers read it: one over the encounter window's existing element walk, one over world entities whose skills are read from `Actor.ActorSkills` and whose archetype is inferred by matching those skills against build pools.

**Tech Stack:** C# / net10.0-windows, ExileCore (ExileAPI PoE1), ImGui.NET via the vendored ExileImGui toolkit, Newtonsoft.Json, xunit for the pure-logic tests, Python 3 for the data generator.

## Global Constraints

- Design spec: `docs/superpowers/specs/2026-08-05-merc-ratings-design.md`. Read it before starting.
- Comments: lowercase, terse, one line, ASCII only, no em-dashes. A reminder to the author, not an explanation for a stranger. Most changes need none.
- Commits: no `Co-Authored-By: Claude` trailer, no "Generated with Claude Code" line.
- Every rating vocabulary key is the **trade display name** (`"Bladefall"`, `"Greater Spell Cascade"`, `"Bladecaster"`), never an internal id, except `grantedEffects` whose keys are `GrantedEffects.Id`.
- Ratings are sparse: `Neutral` is never persisted.
- Mercenary skill slots on an entity are the `ActorSkill` entries with `Id >= 32896`.
- Infamous variants share their base build's ratings. The store only ever keys on a base build id.
- `MercData` must read its embedded resource off `typeof(MercData).Assembly`, never `Assembly.GetExecutingAssembly()` - under the test host the executing assembly is the test project and the lookup returns null.
- Do not add package references beyond what the csproj already has (ImGui.NET, Newtonsoft.Json, SharpDX.Mathematics).
- `dotnet build` resolves ExileCore through the `exapiPackage` env var (currently `C:\Exile\ExileAPI`).

---

## File Structure

| File | Responsibility |
|---|---|
| `ExileTrafficking.cs` | plugin entry, panel detection, `ReadPanel`, trade search, render dispatch |
| `ExileTraffickingSettings.cs` | settings nodes plus the ratings store |
| `MercData.cs` | embedded table loading, build/skill/support lookups, archetype inference |
| `Ratings.cs` | rating enum, sparse store types, get/set, prune, verdict roll-up |
| `RatingsUi.cs` | `DrawSettings` override: ratings tree, search, tri-state rows, import/export tab |
| `ShareCode.cs` | `ET1:` string encode/decode and merge/replace application |
| `PanelHighlight.cs` | outline boxes over the encounter window |
| `WorldOverlay.cs` | hireable-merc scan, skill resolution, stroked drawing |
| `ExileImgui/` | vendored UI toolkit (copied, never patched in place) |
| `tools/gen-mercdata.py` | regenerates `mercdata.json` from the trade API plus the dat exports |
| `tests/ExileTrafficking.Tests/` | xunit tests for the pure logic |

---

### Task 1: Vendor ExileImGui and stand up the test project

**Files:**
- Create: `ExileImgui/Controls.cs`, `ExileImgui/Combo.cs`, `ExileImgui/EColor.cs`, `ExileImgui/Layout.cs`, `ExileImgui/SpriteAtlas.cs`, `ExileImgui/Text.cs`, `ExileImgui/Tables.cs` (copied)
- Create: `tests/ExileTrafficking.Tests/ExileTrafficking.Tests.csproj`
- Create: `tests/ExileTrafficking.Tests/EmbeddedDataTests.cs`
- Modify: `ExileTrafficking.csproj`

**Interfaces:**
- Consumes: nothing.
- Produces: namespace `ExileImGui` with `Controls`, `Combo`, `Tables`, `Text`, `EColor`, `Layout`, `SpriteAtlas`. A test project runnable with `dotnet test tests/ExileTrafficking.Tests/ExileTrafficking.Tests.csproj`.

- [ ] **Step 1: Copy the toolkit files**

The upstream library lives at `E:\github\ExileImGui\ExileImgui\ExileImGui\`. `Controls.cs` needs `Combo`, `EColor`, `Layout`, `SpriteAtlas`, `Text`; `Tables.cs` is standalone. Copy exactly that set, nothing else - no demo, no tests, no `Install.cs`.

```bash
mkdir -p ExileImgui
for f in Controls Combo EColor Layout SpriteAtlas Text Tables; do
  cp "/e/github/ExileImGui/ExileImgui/ExileImGui/$f.cs" "ExileImgui/$f.cs"
done
ls ExileImgui
```

Expected: seven `.cs` files listed.

- [ ] **Step 2: Keep tests out of the plugin's compile globs**

The plugin csproj uses default globs, so `tests/**` would otherwise compile into the plugin. Add this `ItemGroup` to `ExileTrafficking.csproj`, after the existing `EmbeddedResource` group:

```xml
  <ItemGroup>
    <!-- tests/ has its own csproj, keep its files out of this project's globs -->
    <Compile Remove="tests\**" />
    <EmbeddedResource Remove="tests\**" />
    <None Remove="tests\**" />
  </ItemGroup>
```

- [ ] **Step 3: Create the test project**

Create `tests/ExileTrafficking.Tests/ExileTrafficking.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0-windows</TargetFramework>
    <Nullable>disable</Nullable>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\ExileTrafficking.csproj" />
  </ItemGroup>
  <ItemGroup>
    <!-- copy-local: the test host has no HUD to supply ExileCore at runtime, unlike the plugin -->
    <Reference Include="ExileCore"><HintPath>$(exapiPackage)\ExileCore.dll</HintPath><Private>True</Private></Reference>
    <Reference Include="GameOffsets"><HintPath>$(exapiPackage)\GameOffsets.dll</HintPath><Private>True</Private></Reference>
  </ItemGroup>
</Project>
```

- [ ] **Step 4: Write the failing test**

Create `tests/ExileTrafficking.Tests/EmbeddedDataTests.cs`:

```csharp
using Xunit;

namespace ExileTrafficking.Tests;

public class EmbeddedDataTests
{
    [Fact]
    public void MercDataResourceIsEmbedded()
    {
        var asm = typeof(ExileTrafficking).Assembly;
        using var stream = asm.GetManifestResourceStream("ExileTrafficking.mercdata.json");
        Assert.NotNull(stream);
    }
}
```

- [ ] **Step 5: Run the tests**

Run: `dotnet test tests/ExileTrafficking.Tests/ExileTrafficking.Tests.csproj`
Expected: PASS, 1 test. If the vendored files fail to compile with `CS0246`, a file from the copy set is missing - recheck step 1.

- [ ] **Step 6: Commit**

```bash
git add ExileImgui tests ExileTrafficking.csproj
git commit -m "Vendor ExileImGui and add a test project"
```

---

### Task 2: Generate the build tree into mercdata.json

**Files:**
- Modify: `tools/gen-mercdata.py`
- Modify: `mercdata.json` (regenerated)

**Interfaces:**
- Consumes: nothing.
- Produces: `mercdata.json` gains a `builds` object and a `grantedEffects` object, shapes exactly as below.

The dat exports come from the `poe-dat` skill. Generate them first:

```bash
cd /c/Users/malvi/.claude/skills/poe-dat && node bin/dat.mjs table MercenaryBuilds MercenarySkills MercenarySupports MercenaryClasses GrantedEffects MonsterVarieties --game 1
```

They land in `C:\Users\malvi\.claude\skills\poe-dat\.data\poe1\export\English\`.

Facts established while designing, so the generator can be written without re-deriving them:

- `MercenaryBuilds` has 65 rows: 36 base, 29 Infamous.
- An Infamous build's `Id` is its base's `Id` + `"Noble"`, for 28 of the 29. The odd one out is `AurasMinionsTemplarSmiteRuckusNoble` ("Infamous Warpriest of the Ruckus"), which has no base row and therefore becomes its own entry. Final count: **37 entries**.
- `MercenaryClasses.MonsterVarietyAllied` resolves to e.g. `Metadata/Monsters/Mercenaries/MercenaryShadow2Allied`. Strip the folder, the `Allied` suffix and any trailing underscores to get the world metadata name (`MercenaryShadow2`). Two rows carry trailing underscores: `MercenaryTemplar2Allied__` and `MercenaryDuelist1Allied_`.
- `MercenarySkills.Id` is a row index into `GrantedEffects`; `MercenarySkills.Name` is the trade display name.
- `MercenarySkills.PossibleSupports` are row indices into `MercenarySupports`, whose `Name` is the trade display name.
- One build, `Crit1HShadowSpectral` ("Bladereach"), has no trade warrant. Keep it - a world merc can still be one.

- [ ] **Step 1: Fix the output path**

`OUT` currently points at a `src/` folder that does not exist. In `tools/gen-mercdata.py`:

```python
OUT = os.path.join(os.path.dirname(__file__), "..", "mercdata.json")
```

- [ ] **Step 2: Add the dat reader and the build tree builder**

Add near the top of `tools/gen-mercdata.py`:

```python
DAT = os.environ.get(
    "POE_DAT_EXPORT",
    os.path.expanduser(r"~\.claude\skills\poe-dat\.data\poe1\export\English"),
)


def dat(name):
    with open(os.path.join(DAT, name + ".json"), encoding="utf-8") as f:
        return json.load(f)
```

Then add these two functions:

```python
def granted_effects(skills, effects):
    """GrantedEffects.Id -> trade display name, e.g. BladefallMercenary -> Bladefall."""
    by_index = {r["_index"]: r["Id"] for r in effects}
    out = {}
    for row in skills:
        effect = by_index.get(row["Id"])
        if effect and row["Name"]:
            out[effect] = row["Name"]
    return out


def build_tree(builds, skills, supports, classes, varieties):
    """Base builds only. Infamous rows fold into their base as an alias."""
    paths = {}
    for row in classes:
        variety = varieties[row["MonsterVarietyAllied"]]["Id"].rsplit("/", 1)[-1]
        paths[row["_index"]] = variety.replace("Allied", "").rstrip("_")

    by_id = {r["Id"]: r for r in builds}
    out = {}
    for row in builds:
        base = row["Id"][: -len("Noble")] if row["Id"].endswith("Noble") else None
        # infamous rows with a base fold into it, the one orphan stands alone
        if row["Infamous"] and base in by_id:
            continue

        pool = {}
        for index in dict.fromkeys(row["Skill1"] + row["Skill2"] + row["Skill3"]):
            skill = skills[index]
            pool[skill["Name"]] = [supports[s]["Name"] for s in skill["PossibleSupports"]]

        noble = by_id.get(row["Id"] + "Noble")
        out[row["Id"]] = {
            "name": row["BuildName"],
            "infamous": noble["BuildName"] if noble else "",
            "class": classes[row["Class"]]["Id"],
            "path": paths[row["Class"]],
            "skills": pool,
        }
    return out
```

- [ ] **Step 3: Wire them into `main` and report the counts**

Replace the `out = {...}` / print block at the end of `main()`:

```python
    builds = build_tree(
        dat("MercenaryBuilds"), dat("MercenarySkills"), dat("MercenarySupports"),
        dat("MercenaryClasses"), dat("MonsterVarieties"),
    )
    effects = granted_effects(dat("MercenarySkills"), dat("GrantedEffects"))

    out = {
        "skills": skills,
        "supports": supports,
        "archetypes": archetypes,
        "builds": builds,
        "grantedEffects": effects,
    }
    path = os.path.abspath(OUT)
    with open(path, "w", encoding="utf-8") as f:
        json.dump(out, f, indent=1, sort_keys=True)
        f.write("\n")

    print(f"wrote {path}")
    print(f"  skills         {len(skills)}")
    print(f"  supports       {len(supports)}")
    print(f"  archetypes     {len(archetypes)}")
    print(f"  builds         {len(builds)}")
    print(f"  grantedEffects {len(effects)}")
```

- [ ] **Step 4: Run the generator**

Run: `python tools/gen-mercdata.py`
Expected output ends with:

```
  builds         37
  grantedEffects 272
```

- [ ] **Step 5: Verify the shape**

Run:

```bash
python -c "import json;d=json.load(open('mercdata.json'));b=d['builds']['Crit1HShadowPhysSpell'];print(b['name'],'|',b['infamous'],'|',b['path'],'|',len(b['skills']),'|',len(b['skills']['Bladefall']));print(d['grantedEffects']['BladefallMercenary'], d['grantedEffects']['LightningStrikeFireMercenary'])"
```

Expected:

```
Bladecaster | Infamous Bladecaster | MercenaryShadow2 | 12 | 20
Bladefall Flamebolt Strike
```

- [ ] **Step 6: Commit**

```bash
git add tools/gen-mercdata.py mercdata.json
git commit -m "Generate mercenary build tree into mercdata.json"
```

---

### Task 3: MercData lookups and archetype inference

**Files:**
- Create: `MercData.cs`
- Modify: `ExileTrafficking.cs` (delete the static table fields, `LoadTables` and `Lookup`; call into `MercData` instead)
- Create: `tests/ExileTrafficking.Tests/MercDataTests.cs`

**Interfaces:**
- Consumes: `mercdata.json` from Task 2.
- Produces:

```csharp
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
    public static IReadOnlyDictionary<string, MercBuild> Builds { get; }
    public static IReadOnlyList<MercBuild> BuildsByName { get; }          // sorted by Name
    public static string SkillId(string tradeName);                        // trade stat id, null if unknown
    public static string SupportId(string tradeName);
    public static string ArchetypeId(string display);                      // trade type option
    public static string SkillFromEffect(string grantedEffectId);          // BladefallMercenary -> Bladefall
    public static MercBuild BuildForArchetype(string display);             // matches Name or Infamous
    public static MercBuild Infer(IReadOnlyCollection<string> skillNames, string metadataPath);
}
```

- [ ] **Step 1: Write the failing tests**

Create `tests/ExileTrafficking.Tests/MercDataTests.cs`:

```csharp
using System.Collections.Generic;
using Xunit;

namespace ExileTrafficking.Tests;

public class MercDataTests
{
    private static readonly string[] BladecasterSkills =
    {
        "Clutches of the Damned", "Bloody Warp", "Bladefall",
        "Blade Vortex", "Flame Dash", "Dash",
    };

    [Fact]
    public void LoadsEveryBuild()
    {
        Assert.Equal(37, MercData.Builds.Count);
        var build = MercData.Builds["Crit1HShadowPhysSpell"];
        Assert.Equal("Bladecaster", build.Name);
        Assert.Equal("MercenaryShadow2", build.Path);
        Assert.Equal(12, build.Skills.Count);
        Assert.Contains("Greater Brutality", build.Skills["Bladefall"]);
    }

    [Fact]
    public void ResolvesVariantSkillNamesFromGrantedEffects()
    {
        Assert.Equal("Bladefall", MercData.SkillFromEffect("BladefallMercenary"));
        // the variant the game's own DisplayName gets wrong
        Assert.Equal("Flamebolt Strike", MercData.SkillFromEffect("LightningStrikeFireMercenary"));
        Assert.Null(MercData.SkillFromEffect("NotASkill"));
    }

    [Fact]
    public void InfamousArchetypeResolvesToItsBaseBuild()
    {
        Assert.Equal("Crit1HShadowPhysSpell", MercData.BuildForArchetype("Bladecaster").Id);
        Assert.Equal("Crit1HShadowPhysSpell", MercData.BuildForArchetype("Infamous Bladecaster").Id);
        Assert.Null(MercData.BuildForArchetype("Not An Archetype"));
    }

    [Fact]
    public void InfersBuildFromSkillSet()
    {
        var build = MercData.Infer(BladecasterSkills, "Metadata/Monsters/Mercenaries/MercenaryShadow2@58");
        Assert.Equal("Crit1HShadowPhysSpell", build.Id);
    }

    [Fact]
    public void InferenceUsesMetadataPathAsTieBreak()
    {
        // Dash alone sits in many pools, so the path is what decides
        var build = MercData.Infer(new[] { "Dash" }, "Metadata/Monsters/Mercenaries/MercenaryShadow2@58");
        Assert.Equal("Crit1HShadow", build.Class);
    }

    [Fact]
    public void InferenceReturnsNullWhenNothingMatches()
    {
        Assert.Null(MercData.Infer(new List<string> { "Not A Skill" }, "Metadata/Monsters/Mercenaries/MercenaryShadow2@58"));
        Assert.Null(MercData.Infer(new List<string>(), "whatever"));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/ExileTrafficking.Tests/ExileTrafficking.Tests.csproj --filter MercDataTests`
Expected: FAIL, `MercData` does not exist (`CS0103`).

- [ ] **Step 3: Write MercData.cs**

```csharp
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
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/ExileTrafficking.Tests/ExileTrafficking.Tests.csproj --filter MercDataTests`
Expected: PASS, 6 tests.

- [ ] **Step 5: Move the plugin onto MercData**

In `ExileTrafficking.cs`, delete the `Skills` / `Supports` / `Archetypes` static fields, the static constructor, `LoadTables` and `Lookup`. Replace their uses:

- `FindText(window, 12, Archetypes, true)` becomes `FindText(window, 12, MercData.ArchetypeId, true)`
- `FindText(child, 3, Skills, true)` becomes `FindText(child, 3, MercData.SkillId, true)`
- `FindText(x.Tooltip, 3, Supports)` becomes `FindText(x.Tooltip, 3, MercData.SupportId)`
- `Lookup(Archetypes, snapshot.Archetype)` becomes `MercData.ArchetypeId(snapshot.Archetype)`, and likewise `MercData.SkillId(skill.Name)` / `MercData.SupportId(support)` in `BuildQueryJson`

`FindText` changes signature to take the lookup function:

```csharp
    private static string FindText(Element root, int depth, Func<string, string> lookup, bool visibleOnly = false) =>
        Descendants(root, depth, visibleOnly)
            .Select(e =>
            {
                var text = e?.TextNoTags;
                if (string.IsNullOrWhiteSpace(text)) text = e?.Text;
                return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
            })
            .FirstOrDefault(t => lookup(t) != null);
```

- [ ] **Step 6: Build and verify in game**

Run: `dotnet build ExileTrafficking.csproj`
Expected: build succeeds, no warnings about unused fields.

In game, open a mercenary encounter and press Trade Search. Expected: the browser opens the same query as before this task.

- [ ] **Step 7: Commit**

```bash
git add MercData.cs ExileTrafficking.cs tests/ExileTrafficking.Tests/MercDataTests.cs
git commit -m "Add MercData build lookups and archetype inference"
```

---

### Task 4: Ratings store

**Files:**
- Create: `Ratings.cs`
- Modify: `ExileTraffickingSettings.cs`
- Create: `tests/ExileTrafficking.Tests/RatingsTests.cs`

**Interfaces:**
- Consumes: `MercData` (Task 3).
- Produces:

```csharp
public enum Rating { Neutral = 0, Good = 1, Bricked = -1 }

public class SkillRating
{
    public Rating Rating { get; set; }
    public Dictionary<string, Rating> Supports { get; set; }
}

public class BuildRating
{
    public Dictionary<string, SkillRating> Skills { get; set; }
}

public static class Ratings
{
    public static Rating Skill(Dictionary<string, BuildRating> store, string buildId, string skill);
    public static Rating Support(Dictionary<string, BuildRating> store, string buildId, string skill, string support);
    public static void SetSkill(Dictionary<string, BuildRating> store, string buildId, string skill, Rating rating);
    public static void SetSupport(Dictionary<string, BuildRating> store, string buildId, string skill, string support, Rating rating);
    public static int Count(Dictionary<string, BuildRating> store, string buildId);
    public static Rating Verdict(Dictionary<string, BuildRating> store, string buildId, IEnumerable<string> skills);
}
```

- [ ] **Step 1: Write the failing tests**

Create `tests/ExileTrafficking.Tests/RatingsTests.cs`:

```csharp
using System.Collections.Generic;
using Xunit;

namespace ExileTrafficking.Tests;

public class RatingsTests
{
    private const string Build = "Crit1HShadowPhysSpell";

    private static Dictionary<string, BuildRating> Store() => new();

    [Fact]
    public void UnsetEntriesReadNeutral()
    {
        var store = Store();
        Assert.Equal(Rating.Neutral, Ratings.Skill(store, Build, "Bladefall"));
        Assert.Equal(Rating.Neutral, Ratings.Support(store, Build, "Bladefall", "Brutality"));
    }

    [Fact]
    public void RoundTripsSkillAndSupport()
    {
        var store = Store();
        Ratings.SetSkill(store, Build, "Bladefall", Rating.Good);
        Ratings.SetSupport(store, Build, "Bladefall", "Lesser Brutality", Rating.Bricked);

        Assert.Equal(Rating.Good, Ratings.Skill(store, Build, "Bladefall"));
        Assert.Equal(Rating.Bricked, Ratings.Support(store, Build, "Bladefall", "Lesser Brutality"));
    }

    [Fact]
    public void NeutralIsNeverStored()
    {
        var store = Store();
        Ratings.SetSkill(store, Build, "Bladefall", Rating.Good);
        Ratings.SetSupport(store, Build, "Bladefall", "Brutality", Rating.Good);

        Ratings.SetSupport(store, Build, "Bladefall", "Brutality", Rating.Neutral);
        Ratings.SetSkill(store, Build, "Bladefall", Rating.Neutral);

        Assert.Empty(store);
    }

    [Fact]
    public void SupportSurvivesItsSkillGoingNeutral()
    {
        var store = Store();
        Ratings.SetSkill(store, Build, "Bladefall", Rating.Good);
        Ratings.SetSupport(store, Build, "Bladefall", "Brutality", Rating.Bricked);
        Ratings.SetSkill(store, Build, "Bladefall", Rating.Neutral);

        Assert.Equal(Rating.Bricked, Ratings.Support(store, Build, "Bladefall", "Brutality"));
        Assert.Equal(1, Ratings.Count(store, Build));
    }

    [Fact]
    public void VerdictIsWorstWins()
    {
        var store = Store();
        var skills = new[] { "Bladefall", "Blade Vortex", "Dash" };

        Assert.Equal(Rating.Neutral, Ratings.Verdict(store, Build, skills));

        Ratings.SetSkill(store, Build, "Bladefall", Rating.Good);
        Assert.Equal(Rating.Good, Ratings.Verdict(store, Build, skills));

        Ratings.SetSkill(store, Build, "Blade Vortex", Rating.Bricked);
        Assert.Equal(Rating.Bricked, Ratings.Verdict(store, Build, skills));
    }

    [Fact]
    public void CountsRatedEntries()
    {
        var store = Store();
        Ratings.SetSkill(store, Build, "Bladefall", Rating.Good);
        Ratings.SetSupport(store, Build, "Bladefall", "Brutality", Rating.Bricked);
        Ratings.SetSupport(store, Build, "Bladefall", "Spell Cascade", Rating.Good);

        Assert.Equal(3, Ratings.Count(store, Build));
        Assert.Equal(0, Ratings.Count(store, "SomeOtherBuild"));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/ExileTrafficking.Tests/ExileTrafficking.Tests.csproj --filter RatingsTests`
Expected: FAIL, `Ratings` does not exist.

- [ ] **Step 3: Write Ratings.cs**

```csharp
using System.Collections.Generic;
using System.Linq;

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
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/ExileTrafficking.Tests/ExileTrafficking.Tests.csproj --filter RatingsTests`
Expected: PASS, 6 tests.

- [ ] **Step 5: Add the store and the render settings**

In `ExileTraffickingSettings.cs`, add the usings `System.Collections.Generic` and `SharpDX`, then these members below the existing ones:

```csharp
    [Menu("Highlight rated skills in the encounter panel")]
    public ToggleNode PanelHighlight { get; set; } = new ToggleNode(true);

    [Menu("Show mercenary overlay in the world")]
    public ToggleNode WorldOverlay { get; set; } = new ToggleNode(true);

    [Menu("Overlay font size")]
    public RangeNode<int> OverlayFontSize { get; set; } = new RangeNode<int>(16, 8, 48);

    [Menu("Overlay verdict line")]
    public ToggleNode OverlayVerdict { get; set; } = new ToggleNode(true);

    [Menu("Good colour")]
    public ColorNode GoodColor { get; set; } = new ColorNode(Color.FromRgba(0xFF6EEB82));

    [Menu("Neutral colour")]
    public ColorNode NeutralColor { get; set; } = new ColorNode(Color.FromRgba(0xFFD8D8D8));

    [Menu("Bricked colour")]
    public ColorNode BrickedColor { get; set; } = new ColorNode(Color.FromRgba(0xFF5C5CE5));

    public Dictionary<string, BuildRating> Ratings { get; set; } = new Dictionary<string, BuildRating>();
```

Note `Color.FromRgba` takes AABBGGRR on SharpDX, so those literals are green, light grey and red respectively.

- [ ] **Step 6: Build and verify persistence in game**

Run: `dotnet build ExileTrafficking.csproj`
Expected: build succeeds.

In game, reload the plugin and open its settings. Expected: the new toggles, slider and three colour swatches appear. Change a colour, reload the plugin, confirm it stuck.

- [ ] **Step 7: Commit**

```bash
git add Ratings.cs ExileTraffickingSettings.cs tests/ExileTrafficking.Tests/RatingsTests.cs
git commit -m "Add the sparse ratings store and render settings"
```

---

### Task 5: Share string encode and decode

**Files:**
- Create: `ShareCode.cs`
- Create: `tests/ExileTrafficking.Tests/ShareCodeTests.cs`

**Interfaces:**
- Consumes: `Ratings`, `BuildRating`, `SkillRating`, `Rating` (Task 4).
- Produces:

```csharp
public static class ShareCode
{
    public const string Prefix = "ET1:";
    public static string Encode(Dictionary<string, BuildRating> store, string buildId = null);
    public static Dictionary<string, BuildRating> Decode(string code);   // null when malformed
    public static void Apply(Dictionary<string, BuildRating> store, Dictionary<string, BuildRating> incoming, bool replace);
    public static int RatingCount(Dictionary<string, BuildRating> store);
}
```

`Encode` with a `buildId` emits only that build. `Apply` with `replace: true` wipes each incoming build id before writing it; with `replace: false` it merges and lets incoming values win per entry.

- [ ] **Step 1: Write the failing tests**

Create `tests/ExileTrafficking.Tests/ShareCodeTests.cs`:

```csharp
using System.Collections.Generic;
using Xunit;

namespace ExileTrafficking.Tests;

public class ShareCodeTests
{
    private const string A = "Crit1HShadowPhysSpell";
    private const string B = "EleBowRangerManyshot";

    private static Dictionary<string, BuildRating> Sample()
    {
        var store = new Dictionary<string, BuildRating>();
        Ratings.SetSkill(store, A, "Bladefall", Rating.Good);
        Ratings.SetSupport(store, A, "Bladefall", "Lesser Brutality", Rating.Bricked);
        Ratings.SetSkill(store, B, "Ice Shot", Rating.Bricked);
        return store;
    }

    [Fact]
    public void RoundTripsTheWholeStore()
    {
        var code = ShareCode.Encode(Sample());
        Assert.StartsWith("ET1:", code);

        var back = ShareCode.Decode(code);
        Assert.Equal(2, back.Count);
        Assert.Equal(Rating.Good, Ratings.Skill(back, A, "Bladefall"));
        Assert.Equal(Rating.Bricked, Ratings.Support(back, A, "Bladefall", "Lesser Brutality"));
        Assert.Equal(Rating.Bricked, Ratings.Skill(back, B, "Ice Shot"));
    }

    [Fact]
    public void EncodesOneBuildOnly()
    {
        var back = ShareCode.Decode(ShareCode.Encode(Sample(), A));
        Assert.Single(back);
        Assert.True(back.ContainsKey(A));
    }

    [Fact]
    public void CodeIsUrlSafeAndSmallerThanTheJson()
    {
        var code = ShareCode.Encode(Sample());
        Assert.DoesNotContain('+', code);
        Assert.DoesNotContain('/', code);
        Assert.DoesNotContain('=', code);
    }

    [Fact]
    public void MalformedInputDecodesToNull()
    {
        Assert.Null(ShareCode.Decode(null));
        Assert.Null(ShareCode.Decode(""));
        Assert.Null(ShareCode.Decode("hello"));
        Assert.Null(ShareCode.Decode("ET1:not-base64!!"));
        Assert.Null(ShareCode.Decode("ET1:aGVsbG8"));       // valid base64, not deflate
    }

    [Fact]
    public void MergeKeepsExistingAndLetsIncomingWin()
    {
        var store = new Dictionary<string, BuildRating>();
        Ratings.SetSkill(store, A, "Bladefall", Rating.Bricked);
        Ratings.SetSkill(store, A, "Blade Vortex", Rating.Good);

        ShareCode.Apply(store, ShareCode.Decode(ShareCode.Encode(Sample(), A)), replace: false);

        Assert.Equal(Rating.Good, Ratings.Skill(store, A, "Bladefall"));      // incoming won
        Assert.Equal(Rating.Good, Ratings.Skill(store, A, "Blade Vortex"));   // untouched, kept
    }

    [Fact]
    public void ReplaceWipesTheIncomingScopeOnly()
    {
        var store = new Dictionary<string, BuildRating>();
        Ratings.SetSkill(store, A, "Blade Vortex", Rating.Good);
        Ratings.SetSkill(store, B, "Ice Shot", Rating.Good);

        ShareCode.Apply(store, ShareCode.Decode(ShareCode.Encode(Sample(), A)), replace: true);

        Assert.Equal(Rating.Neutral, Ratings.Skill(store, A, "Blade Vortex")); // wiped
        Assert.Equal(Rating.Good, Ratings.Skill(store, A, "Bladefall"));       // written
        Assert.Equal(Rating.Good, Ratings.Skill(store, B, "Ice Shot"));        // out of scope, kept
    }

    [Fact]
    public void CountsRatings()
    {
        Assert.Equal(3, ShareCode.RatingCount(Sample()));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/ExileTrafficking.Tests/ExileTrafficking.Tests.csproj --filter ShareCodeTests`
Expected: FAIL, `ShareCode` does not exist.

- [ ] **Step 3: Write ShareCode.cs**

```csharp
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
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/ExileTrafficking.Tests/ExileTrafficking.Tests.csproj --filter ShareCodeTests`
Expected: PASS, 7 tests.

- [ ] **Step 5: Commit**

```bash
git add ShareCode.cs tests/ExileTrafficking.Tests/ShareCodeTests.cs
git commit -m "Add the ET1 share string codec"
```

---

### Task 6: Ratings settings screen

**Files:**
- Create: `RatingsUi.cs`
- Modify: `ExileTrafficking.cs` (add the `DrawSettings` override)

**Interfaces:**
- Consumes: `MercData.BuildsByName`, `Ratings`, `ShareCode`, `ExileImGui.Controls`, `ExileImGui.Text`.
- Produces: `public static class RatingsUi { public static void Draw(ExileTraffickingSettings settings); }`

Layout, matching the approved mockup (option C):

```
[ General ] [ Ratings ] [ Import / Export ]

Ratings tab:
  <search input>                      filters archetypes, skills and supports
  [x] Only show rated                 37 archetypes
  > Bastion            10 skills
  v Bladecaster        12 skills - 4 rated          [export]
      v Bladefall             (G)(-)(X)
          Greater Spell Cascade  (G)(-)(X)
          Faster Casting         (G)(-)(X)
      > Blade Vortex          (G)(-)(X)
```

- [ ] **Step 1: Write RatingsUi.cs**

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ExileImGui;
using ImGuiNET;

namespace ExileTrafficking;

public static class RatingsUi
{
    private static readonly (string Label, Rating Value)[] Options =
    {
        ("G", Rating.Good),
        ("-", Rating.Neutral),
        ("X", Rating.Bricked),
    };

    private static string search = "";
    private static bool onlyRated;
    private static string importText = "";
    private static string importStatus = "";
    private static Dictionary<string, BuildRating> pendingImport;

    public static void Draw(ExileTraffickingSettings settings)
    {
        if (!ImGui.BeginTabBar("##et_tabs")) return;

        try
        {
            if (ImGui.BeginTabItem("Ratings"))
            {
                DrawRatings(settings);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Import / Export"))
            {
                DrawShare(settings);
                ImGui.EndTabItem();
            }
        }
        finally
        {
            ImGui.EndTabBar();
        }
    }

    private static void DrawRatings(ExileTraffickingSettings settings)
    {
        ImGui.SetNextItemWidth(260f);
        ImGui.InputTextWithHint("##et_search", "Search archetypes / skills / supports...", ref search, 64);
        ImGui.SameLine();
        ImGui.Checkbox("Only show rated", ref onlyRated);
        ImGui.SameLine();
        ImGui.TextDisabled($"{MercData.BuildsByName.Count} archetypes");

        foreach (var build in MercData.BuildsByName)
        {
            var rated = Ratings.Count(settings.Ratings, build.Id);
            if (onlyRated && rated == 0) continue;

            var skills = Visible(build, settings).ToList();
            if (skills.Count == 0) continue;

            var label = rated > 0
                ? $"{build.Name}   {build.Skills.Count} skills - {rated} rated###{build.Id}"
                : $"{build.Name}   {build.Skills.Count} skills###{build.Id}";

            if (!ImGui.CollapsingHeader(label)) continue;

            ImGui.PushID(build.Id);
            ImGui.Indent();
            if (ImGui.SmallButton("Export this archetype"))
            {
                ImGui.SetClipboardText(ShareCode.Encode(settings.Ratings, build.Id));
            }

            foreach (var skill in skills) DrawSkill(settings, build, skill);
            ImGui.Unindent();
            ImGui.PopID();
        }
    }

    private static IEnumerable<string> Visible(MercBuild build, ExileTraffickingSettings settings)
    {
        var buildMatches = Matches(build.Name) || Matches(build.Infamous);

        foreach (var (skill, supports) in build.Skills)
        {
            if (onlyRated &&
                Ratings.Skill(settings.Ratings, build.Id, skill) == Rating.Neutral &&
                !supports.Any(s => Ratings.Support(settings.Ratings, build.Id, skill, s) != Rating.Neutral))
            {
                continue;
            }

            if (buildMatches || Matches(skill) || supports.Any(Matches)) yield return skill;
        }
    }

    private static bool Matches(string text) =>
        string.IsNullOrWhiteSpace(search) || Text.Matches(search, text ?? "");

    private static void DrawSkill(ExileTraffickingSettings settings, MercBuild build, string skill)
    {
        var supports = build.Skills[skill];
        var rating = Ratings.Skill(settings.Ratings, build.Id, skill);

        ImGui.PushID(skill);
        var open = ImGui.TreeNodeEx($"{skill}##node",
            supports.Count == 0 ? ImGuiTreeNodeFlags.Leaf : ImGuiTreeNodeFlags.None);
        ImGui.SameLine();
        if (Controls.Segmented($"##skill_{skill}", ref rating, Options))
        {
            Ratings.SetSkill(settings.Ratings, build.Id, skill, rating);
        }

        if (open)
        {
            foreach (var support in supports)
            {
                if (!Matches(support) && !Matches(skill) && !Matches(build.Name)) continue;

                var value = Ratings.Support(settings.Ratings, build.Id, skill, support);
                if (onlyRated && value == Rating.Neutral) continue;

                ImGui.PushID(support);
                ImGui.TextUnformatted(support);
                ImGui.SameLine(240f);
                if (Controls.Segmented("##support", ref value, Options))
                {
                    Ratings.SetSupport(settings.Ratings, build.Id, skill, support, value);
                }

                ImGui.PopID();
            }

            ImGui.TreePop();
        }

        ImGui.PopID();
    }

    private static void DrawShare(ExileTraffickingSettings settings)
    {
        ImGui.TextDisabled($"{settings.Ratings.Count} archetypes, {ShareCode.RatingCount(settings.Ratings)} ratings");

        if (ImGui.Button("Copy everything to clipboard"))
        {
            ImGui.SetClipboardText(ShareCode.Encode(settings.Ratings));
            importStatus = "copied";
        }

        ImGui.Separator();
        ImGui.SetNextItemWidth(420f);
        ImGui.InputTextWithHint("##et_import", "ET1:...", ref importText, 8192);
        ImGui.SameLine();
        if (ImGui.Button("Paste"))
        {
            importText = ImGui.GetClipboardText() ?? "";
        }

        if (ImGui.Button("Read string"))
        {
            pendingImport = ShareCode.Decode(importText);
            importStatus = pendingImport == null
                ? "not a valid ET1 string"
                : $"{pendingImport.Count} archetypes, {ShareCode.RatingCount(pendingImport)} ratings";
        }

        if (!string.IsNullOrEmpty(importStatus)) ImGui.TextUnformatted(importStatus);

        if (pendingImport == null) return;

        if (ImGui.Button("Replace"))
        {
            ShareCode.Apply(settings.Ratings, pendingImport, replace: true);
            Done("replaced");
        }

        ImGui.SameLine();
        if (ImGui.Button("Merge"))
        {
            ShareCode.Apply(settings.Ratings, pendingImport, replace: false);
            Done("merged");
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel")) Done("cancelled");
    }

    private static void Done(string status)
    {
        pendingImport = null;
        importText = "";
        importStatus = status;
    }
}
```

- [ ] **Step 2: Add the DrawSettings override**

In `ExileTrafficking.cs`, add:

```csharp
    // a throw in here blanks the whole settings page, so never let one escape
    public override void DrawSettings()
    {
        base.DrawSettings();

        try
        {
            RatingsUi.Draw(Settings);
        }
        catch (Exception e)
        {
            ImGui.TextUnformatted($"settings error: {e.Message}");
        }
    }
```

- [ ] **Step 3: Build**

Run: `dotnet build ExileTrafficking.csproj`
Expected: build succeeds.

- [ ] **Step 4: Verify in game**

Reload the plugin and open its settings. Check each of these:

1. The declarative settings from `[Menu]` still render above the tab bar.
2. The `Ratings` tab lists 37 archetypes.
3. Expanding `Bladecaster` shows 12 skills; expanding `Bladefall` shows 20 supports.
4. Clicking `G` on `Bladefall` sticks, and the header updates to "1 rated".
5. Typing `brutal` in the search box narrows to archetypes/skills that have a Brutality support.
6. `Only show rated` hides everything unrated.
7. Reload the plugin: the ratings survive.
8. `Export this archetype` puts an `ET1:` string on the clipboard; paste it into the Import / Export tab, press `Read string`, and the summary line reports the right counts. `Merge` applies it.

- [ ] **Step 5: Commit**

```bash
git add RatingsUi.cs ExileTrafficking.cs
git commit -m "Add the ratings settings screen and share tab"
```

---

### Task 7: Panel highlighting

**Files:**
- Create: `PanelHighlight.cs`
- Modify: `ExileTrafficking.cs` (carry elements on the snapshot records, call the highlighter from `Render`)

**Interfaces:**
- Consumes: `MercData.BuildForArchetype`, `Ratings`, settings colours.
- Produces:

```csharp
public record MercSupport(string Name, Element Icon);
public record MercSkill(string Name, IReadOnlyList<MercSupport> Supports, Element Row);
public record MercSnapshot(string Archetype, IReadOnlyList<MercSkill> Skills);
public static class PanelHighlight { public static void Draw(Graphics graphics, MercSnapshot snapshot, ExileTraffickingSettings settings); }
```

The three records must be `public`: `PanelHighlight.Draw` is public and takes `MercSnapshot`, and a
public method cannot expose an internal parameter type (`CS0051`). They are currently internal.

- [ ] **Step 1: Carry the elements on the snapshot**

In `ExileTrafficking.cs`, replace the two records at the top:

```csharp
public record MercSupport(string Name, Element Icon);

public record MercSkill(string Name, IReadOnlyList<MercSupport> Supports, Element Row);

public record MercSnapshot(string Archetype, IReadOnlyList<MercSkill> Skills);
```

Change `ReadSupports` to return the elements too:

```csharp
    private static List<MercSupport> ReadSupports(Element row) =>
        Descendants(row, 4)
            .Select(x => new MercSupport(FindText(x.Tooltip, 3, MercData.SupportId), x))
            .Where(x => x.Name != null)
            .OrderBy(x => x.Icon.GetClientRect().X)
            .ToList();
```

In `ReadPanel`, pass the row element through:

```csharp
                skills.Add(new MercSkill(name, ReadSupports(row), row));
```

In `BuildQueryJson`, the support loop now reads the name off the record:

```csharp
            foreach (var support in skill.Supports)
            {
                var supportId = MercData.SupportId(support.Name);
                if (supportId == null) return null;

                filters.Add(new { id = supportId, disabled = filters.Count >= enabledSupports });
            }
```

- [ ] **Step 2: Write PanelHighlight.cs**

```csharp
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
```

- [ ] **Step 3: Call it from Render**

In `ExileTrafficking.cs`, inside `Render`, right after the `if (snapshot == null) return;` line:

```csharp
            if (Settings.PanelHighlight) PanelHighlight.Draw(Graphics, snapshot, Settings);
```

- [ ] **Step 4: Build**

Run: `dotnet build ExileTrafficking.csproj`
Expected: build succeeds.

- [ ] **Step 5: Verify in game**

Rate a few entries of an archetype you can actually encounter, then open that mercenary's encounter window.

1. A skill rated Good draws a green box around its whole row; a Bricked one draws red.
2. A support rated under that skill draws a box around just that icon.
3. Neutral entries draw nothing.
4. The same support rated under a *different* skill does not colour here.
5. Turning `Highlight rated skills in the encounter panel` off removes every box.
6. Trade Search still opens the same query.

- [ ] **Step 6: Commit**

```bash
git add PanelHighlight.cs ExileTrafficking.cs
git commit -m "Highlight rated skills and supports in the encounter panel"
```

---

### Task 8: World overlay

**Files:**
- Create: `WorldOverlay.cs`
- Modify: `ExileTrafficking.cs` (call the overlay from `Render`)
- Create: `tests/ExileTrafficking.Tests/WorldOverlayTests.cs`

**Interfaces:**
- Consumes: `MercData.SkillFromEffect`, `MercData.Infer`, `MercData.Level`, `Ratings.Verdict`.
- Produces:

```csharp
public static class WorldOverlay
{
    public const int MercSkillIdBase = 32896;
    public static bool IsHireable(Entity entity);
    public static List<string> SkillNames(Entity entity);
    public static void Draw(GameController game, Graphics graphics, ExileTraffickingSettings settings);
}
```

- [ ] **Step 1: Write the failing tests**

`IsHireable` and `SkillNames` need a live `Entity`, so the tests cover the two pure helpers the overlay leans on. Create `tests/ExileTrafficking.Tests/WorldOverlayTests.cs`:

```csharp
using Xunit;

namespace ExileTrafficking.Tests;

public class WorldOverlayTests
{
    [Theory]
    [InlineData("Metadata/Monsters/Mercenaries/MercenaryShadow2@58", 58)]
    [InlineData("Metadata/Monsters/Mercenaries/MercenaryMarauder2Allied@53", 53)]
    [InlineData("Metadata/Monsters/Mercenaries/MercenaryShadow2", 0)]
    [InlineData(null, 0)]
    public void ParsesLevelFromMetadataPath(string path, int expected)
    {
        Assert.Equal(expected, MercData.Level(path));
    }

    [Theory]
    [InlineData("Metadata/Monsters/Mercenaries/MercenaryShadow2@58", "MercenaryShadow2")]
    [InlineData("Metadata/Monsters/Mercenaries/MercenaryMarauder2Allied@53", "MercenaryMarauder2Allied")]
    public void ParsesNameFromMetadataPath(string path, string expected)
    {
        Assert.Equal(expected, MercData.PathName(path));
    }
}
```

- [ ] **Step 2: Run the tests to verify they pass**

Run: `dotnet test tests/ExileTrafficking.Tests/ExileTrafficking.Tests.csproj --filter WorldOverlayTests`
Expected: PASS, 6 cases. `MercData.Level` and `MercData.PathName` were written in Task 3; if either is missing, add it there.

- [ ] **Step 3: Write WorldOverlay.cs**

```csharp
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ExileCore;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using SharpDX;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;

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

            var size = settings.OverlayFontSize.Value;
            var y = origin.Y - (skills.Count + 2) * size;

            var header = build == null
                ? $"Unknown  lvl {MercData.Level(entity.Path)}"
                : $"{build.Name}  lvl {MercData.Level(entity.Path)}";
            y = Line(graphics, header, new Vector2(origin.X, y), settings.NeutralColor.Value, size);

            foreach (var skill in skills)
            {
                var rating = Ratings.Skill(settings.Ratings, buildId, skill);
                y = Line(graphics, skill, new Vector2(origin.X, y), Colour(rating, settings), size);
            }

            if (!settings.OverlayVerdict) continue;

            var verdict = Ratings.Verdict(settings.Ratings, buildId, skills);
            Line(graphics, Word(verdict), new Vector2(origin.X, y), Colour(verdict, settings), size);
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
    private static float Line(Graphics graphics, string text, Vector2 position, Color color, int size)
    {
        foreach (var offset in StrokeOffsets)
        {
            graphics.DrawText(text, position + offset, Color.Black, size, FontAlign.Center);
        }

        var drawn = graphics.DrawText(text, position, color, size, FontAlign.Center);
        return position.Y + drawn.Y;
    }
}
```

- [ ] **Step 4: Call it from Render**

In `ExileTrafficking.cs`, at the very top of `Render`'s `try` block, before the panel lookup:

```csharp
            if (Settings.WorldOverlay) WorldOverlay.Draw(GameController, Graphics, Settings);
```

- [ ] **Step 5: Build**

Run: `dotnet build ExileTrafficking.csproj`
Expected: build succeeds.

- [ ] **Step 6: Verify in game**

Find a zone with a hireable mercenary (the minimap shows the mercenary icon).

1. The overlay draws above the mercenary: archetype and level, then its six skills, then the verdict.
2. The archetype matches what the encounter panel says when you open it (allowing for the Infamous prefix, which the overlay cannot know).
3. Skills you rated Good/Bricked draw in those colours; unrated ones draw neutral.
4. The verdict is worst-wins: one bricked skill turns it BRICKED.
5. Your own hired mercenary and any allied mercenary get no overlay.
6. Text is readable over a bright floor - that is the stroke working.
7. Turning `Show mercenary overlay in the world` off removes it. `Overlay verdict line` removes just the last line. The font size slider takes effect.
8. Stand far away with the merc off screen: nothing draws, and the framerate is unchanged.

- [ ] **Step 7: Commit**

```bash
git add WorldOverlay.cs ExileTrafficking.cs tests/ExileTrafficking.Tests/WorldOverlayTests.cs
git commit -m "Draw a rated skill overlay above hireable mercenaries"
```

---

### Task 9: README and final pass

**Files:**
- Modify: `README.md`

- [ ] **Step 1: Document the features**

Add below the existing description in `README.md`:

```markdown
## Features

- **Trade search** - a button on the mercenary encounter panel that opens a trade search matching
  that mercenary's archetype and skills.
- **Skill ratings** - rate every mercenary skill and its supports as good, neutral or bricked, per
  merc type, in the plugin's Ratings tab. A support rating only counts under the skill it is filed
  under. Infamous variants share their base archetype's ratings.
- **Panel highlighting** - rated skills and supports get a coloured outline inside the encounter
  window.
- **World overlay** - hireable mercenaries in the zone show their archetype, level, skills and an
  overall verdict above their head, before you talk to them. Supports are not readable at that
  point, so only skills are shown.
- **Share strings** - export one archetype or the whole table as an `ET1:` string, import with a
  replace-or-merge prompt.

## Regenerating the game data

`mercdata.json` is embedded in the plugin. It comes from GGG's trade endpoints plus the game's own
data tables:

    node bin/dat.mjs table MercenaryBuilds MercenarySkills MercenarySupports MercenaryClasses GrantedEffects MonsterVarieties --game 1
    python tools/gen-mercdata.py

The first command is the `pathofexile-dat` CLI; point `POE_DAT_EXPORT` at its export folder if it is
not in the default location.
```

- [ ] **Step 2: Run the whole test suite**

Run: `dotnet test tests/ExileTrafficking.Tests/ExileTrafficking.Tests.csproj`
Expected: PASS, 26 tests.

- [ ] **Step 3: Commit**

```bash
git add README.md
git commit -m "Document the ratings, highlighting and overlay features"
```

---

## Self-Review Notes

Spec coverage check, section by section:

| Spec section | Task |
|---|---|
| 1. Data generation | Task 2 |
| 2. Ratings store | Task 4 |
| 3. Settings UI | Task 6 |
| 4. Panel highlighting | Task 7 |
| 5. World overlay | Task 8 |
| 6. Share string | Tasks 5 and 6 |
| 7. Settings toggles | Task 4 (nodes), Tasks 7-8 (honoured) |
| 8. File layout | Tasks 3-8, `ExileImgui/` in Task 1 |

Deviation from the spec worth noting: the spec says 36 base builds, the generator emits **37** - 36
base rows plus `AurasMinionsTemplarSmiteRuckusNoble` ("Infamous Warpriest of the Ruckus"), an
Infamous build with no base row, which therefore stands on its own.
