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
- **Warrant tooltip** - hover a mercenary warrant in your inventory or stash and the full build
  shows beside the game's own tooltip: archetype, level, every skill and every rolled support,
  coloured by your ratings. A keybind on that tooltip opens the trade search for that warrant.
- **Share strings** - export one archetype or the whole table as an `ET1:` string, import with a
  replace-or-merge prompt.

The queries are built from the game's own mercenary descriptor rather than from panel text, which is
where the support gems come from - the encounter panel and the world entity do not expose them.
Turn that off with "Read the mercenary from memory" and it falls back to reading the panel.

## Donations

This plugin would not be possible without the hard work of the ExileCore/ExileAPI developers. If you want to support plugin development, donate to them. See below for donation information.

- ExileAPI: https://github.com/exApiTools/ExileApi-Compiled
- ExileCore2: https://github.com/exCore2/ExileCore2
