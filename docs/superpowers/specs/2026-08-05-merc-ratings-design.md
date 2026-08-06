# ExileTrafficking - mercenary skill ratings, panel highlighting, world overlay

Date: 2026-08-05

## Summary

Three additions to the plugin, each behind its own toggle:

1. A ratings tree the user fills in: **merc type -> skill -> support**, each entry rated
   good / neutral / bricked. A support rating only applies under the skill it is filed under.
2. **Panel highlighting**: coloured outline boxes on skill rows and support icons inside the
   mercenary encounter window, driven by those ratings.
3. **World overlay**: stroked text above a hireable mercenary in the zone, before the dialog is
   opened, showing its archetype, level, skills coloured by rating, and a rolled-up verdict.

Plus an import/export share string for the ratings, per-archetype and whole-table.

## Research findings

Verified against the running game (bridge) and the game data tables on 2026-08-05.

### Entity side

A hireable mercenary in the zone, e.g. `Metadata/Monsters/Mercenaries/MercenaryShadow2@58`:

- `Actor.ActorSkills` **is populated before any interaction**. The six mercenary skills are the
  entries with `Id >= 32896`, spaced 1024 apart. Their `Name` is the `GrantedEffects.Id`, e.g.
  `BladefallMercenary`.
- `MinimapIcon.Name == "MercenaryEncounter"` marks a hireable one. Allied mercenaries (yours, or
  another player's) carry an `Allied` suffix in the metadata path.
- `Render.Name` is the personal name ("Dryan, of House Azadi"). The `@58` suffix on the path is the
  level.
- **The archetype is not on the entity.** `Monster` and `Preload` are empty,
  `ObjectMagicProperties.Mods` holds only `CannotBeAugmented` / `MonsterNoDropsOrExperience`,
  `Stats` is 40 combat stats, and `PlayerClass` / `Mercenary` / `InteractionAction` are not
  implemented in ExileCore. The metadata path gives the class (`MercenaryShadow2`), not the build.
- **Supports are not readable from the entity.** `Inventories` is a stub in ExileCore. The world
  overlay therefore shows skills only.

### Data tables

- `MercenarySkills.Id` is a foreignrow into `GrantedEffects`; its `Name` is the exact trade/panel
  display name. This is the only correct source for mercenary variant skills:
  `LightningStrikeFireMercenary` is **"Flamebolt Strike"**, while ExileCore's own
  `ActiveSkill.DisplayName` reports "Lightning Strike".
- `MercenaryBuilds.Id` matches the trade archetype id already stored in `mercdata.json`
  (`Crit1HShadowPhysSpell`), `BuildName` matches the panel display name ("Bladecaster"), and
  `Infamous` flags the Noble variant. `Skill1` / `Skill2` / `Skill3` together give the build's skill
  pool.
- `MercenarySkills.PossibleSupports` gives the support pool per skill (0 to 31, average 15).
- Scale: 36 base builds, up to 16 skills each, 266 distinct supports.

### Archetype inference

Matching a live merc's six skills against build pools works. The test case
`TemporalAnomalyMercenary, LightningWarpPhysMercenary, BladefallMercenary, BladeVortexMercenary,
FlameDashMercenary, DashMercenary` matched `Crit1HShadowPhysSpell` (Bladecaster) 6/6.

The only collision is base vs Infamous: `Crit1HShadowPhysSpell` and `Crit1HShadowPhysSpellNoble`
have identical pools and nothing on the entity separates them. **Decision: the ratings tree lists
the 36 base builds only, and an Infamous merc shares its base build's ratings.** The trade search is
unaffected, since it reads the archetype off the panel where the distinction is visible.

## Design

### 1. Data generation

`mercdata.json` keeps its current `skills` / `supports` / `archetypes` tables from the trade API and
gains two sections generated from the dat exports:

```json
"builds": {
  "Crit1HShadowPhysSpell": {
    "name": "Bladecaster",
    "infamous": "Infamous Bladecaster",
    "class": "Crit1HShadow",
    "skills": {
      "Bladefall": ["Lesser Spell Cascade", "Spell Cascade", "..."],
      "Clutches of the Damned": []
    }
  }
},
"grantedEffects": { "BladefallMercenary": "Bladefall" }
```

- `builds` holds base builds only. `infamous` is the display name of the Noble variant, so a panel
  read of "Infamous Bladecaster" resolves back to this entry.
- `grantedEffects` maps a `GrantedEffects.Id` to a trade display name, which is how world-entity
  skills get their names.
- `class` is the `MercenaryClasses.Id`, used as a tie-break during inference.

`tools/gen-mercdata.py` gains a step that reads the `pathofexile-dat` JSON exports (path passed as
an argument, defaulting to the poe-dat skill's export directory) and merges these sections in. Its
existing output path incorrectly points at `src/mercdata.json`; the file lives at the repo root, so
that is corrected in the same change.

### 2. Ratings store

```
Rating = Good | Neutral | Bricked
buildId -> { skillName -> { rating, supportName -> rating } }
```

Sparse: only non-neutral entries are persisted. Anything absent reads as `Neutral`. Keys are the
trade display names already used everywhere else in the plugin, so panel text, dat data and stored
ratings all share one vocabulary. Stored in the plugin's settings JSON.

### 3. Settings UI

A `Ratings` tab, rendered from a `DrawSettings` override, laid out as one long tree:

- A search box filtering archetypes, skills and supports by name.
- An "only show rated" toggle.
- All 36 archetypes as collapsing headers, each expanding to its skill pool, each skill expanding to
  a table of its real support pool.
- Each skill and support row carries a tri-state good / neutral / bricked control.
- Each archetype header carries an export button (see share string).

An `Import / Export` tab holds the whole-table export and the import box.

ExileImgui is vendored into the repo as `ExileImgui/`, matching the other plugins in this workspace.
No csproj change is needed - the default compile globs pick it up. `Controls.Segmented` provides the
tri-state control and `Combo` the filterable combo.

### 4. Panel highlighting

The existing `ReadPanel` walk already resolves skill rows and support icons, and every element
exposes `GetClientRect()`. Highlighting draws:

- a coloured outline rectangle around each skill row, from that skill's rating under the panel's
  archetype;
- a coloured outline rectangle around each support icon, from that support's rating under that
  skill.

Neutral draws nothing. The panel's archetype maps to a build id through `builds`, taking the
`infamous` alias into account.

### 5. World overlay

Each frame, for entities that are on screen, have `MinimapIcon.Name == "MercenaryEncounter"` and
whose metadata path does not contain `Allied`:

1. Read `Actor.ActorSkills`, keep entries with `Id >= 32896`, map each `Name` through
   `grantedEffects` to a trade name.
2. Find the build whose skill pool is a superset of that set. If more than one matches, prefer the
   one whose `class` matches the class in the metadata path; if still ambiguous, take the first and
   mark the archetype uncertain.
3. Parse the level from the `@N` suffix.
4. Draw above the entity, each line stroked (drawn offset in black, then in the rating colour):

```
Bladecaster  lvl 58
Clutches of the Damned
Bloody Warp
Bladefall
Blade Vortex
Flame Dash
Dash
BRICKED
```

The verdict is worst-wins over the skill ratings: any bricked skill gives `BRICKED`, otherwise any
good gives `GOOD`, otherwise `NEUTRAL`. Unrated skills count as neutral. Support ratings do not
contribute, since supports are unreadable at this stage.

### 6. Share string

`ET1:` followed by base64url of the deflate-compressed compact JSON of the ratings, with ratings
encoded as `1` / `-1` (neutral entries are absent):

```json
{"v":1,"b":{"Crit1HShadowPhysSpell":{"Bladefall":[1,{"Greater Spell Cascade":1,"Lesser Brutality":-1}]}}}
```

- Per-archetype export button in each archetype header; whole-table export on the Import / Export
  tab. Both copy to the clipboard.
- Import parses the string, then shows a summary ("3 archetypes, 41 ratings") with **Replace** and
  **Merge** buttons. Replace wipes the scope the string covers; Merge keeps existing entries and
  lets incoming values win on conflict. Nothing is applied until one is pressed.
- A malformed or truncated string reports an error and changes nothing.

### 7. Settings

- Master toggle for panel highlighting.
- Master toggle for the world overlay.
- Three rating colours (good / neutral / bricked), shared by both features.
- Overlay font scale.
- Overlay verdict line on/off.

The existing Trade Search button, league override, support-count and nudge settings are unchanged.

### 8. File layout

The plugin is currently one 260-line file. It splits into:

| File | Holds |
|---|---|
| `ExileTrafficking.cs` | plugin entry, panel detection, `ReadPanel`, trade search |
| `MercData.cs` | embedded table loading, build/skill/support lookups, archetype inference |
| `Ratings.cs` | rating enum, sparse store, lookups, verdict roll-up |
| `RatingsUi.cs` | `DrawSettings` tree, search, tri-state rows |
| `ShareCode.cs` | encode/decode of the `ET1:` string |
| `PanelHighlight.cs` | outline boxes over the encounter window |
| `WorldOverlay.cs` | entity scan, skill resolution, stroked drawing |
| `ExileImgui/` | vendored UI helpers |

## Out of scope

- Reading supports from a world entity. Not exposed by ExileCore; would need new memory work on the
  unimplemented `Mercenary` component.
- Distinguishing base from Infamous on a world entity.
- Any change to the trade search query builder.
