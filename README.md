# ExileTrafficking

Adds a trade search button next to a mercenary panel, matching that mercenary's archetype and skills.

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
data tables. The generator, `tools/gen-mercdata.py`, is not shipped with the plugin - it lives in
the maintainer's workspace alongside this repo. Both commands below are run from there:

    node bin/dat.mjs table MercenaryBuilds MercenarySkills MercenarySupports MercenaryClasses GrantedEffects MonsterVarieties --game 1
    python tools/gen-mercdata.py

The first command is the `pathofexile-dat` CLI; point `POE_DAT_EXPORT` at its export folder if it is
not in the default location.

## Donations

This plugin would not be possible without the hard work of the ExileCore/ExileAPI developers. If you want to support plugin development, donate to them. See below for donation information.

- ExileAPI: https://github.com/exApiTools/ExileApi-Compiled
- ExileCore2: https://github.com/exCore2/ExileCore2

