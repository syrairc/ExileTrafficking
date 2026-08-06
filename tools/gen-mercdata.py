#!/usr/bin/env python3
"""Regenerate mercdata.json from GGG's live trade endpoints.

Run this when a patch adds mercenary skills, supports, or archetypes. Output goes to
mercdata.json at the repo root, which the plugin embeds.

    python tools/gen-mercdata.py

Keys are the exact display strings the game's mercenary encounter panel shows, so the plugin can
look up straight from panel text. Support keys have the " (Tier N)" suffix stripped, because the
panel tooltip never shows it and the tier is already implied by the Lesser/plain/Greater prefix.
"""

import json
import os
import urllib.request

UA = "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:140.0) Gecko/20100101 Firefox/140.0"
STATS = "https://www.pathofexile.com/api/trade/data/stats"
ITEMS = "https://www.pathofexile.com/api/trade/data/items"
OUT = os.path.join(os.path.dirname(__file__), "..", "mercdata.json")

DAT = os.environ.get(
    "POE_DAT_EXPORT",
    os.path.expanduser(r"~\.claude\skills\poe-dat\.data\poe1\export\English"),
)


def dat(name):
    with open(os.path.join(DAT, name + ".json"), encoding="utf-8") as f:
        return json.load(f)


def fetch(url):
    req = urllib.request.Request(url, headers={"User-Agent": UA})
    with urllib.request.urlopen(req, timeout=60) as r:
        return json.load(r)


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


def main():
    stats = fetch(STATS)
    items = fetch(ITEMS)

    merc = next(g for g in stats["result"] if g["id"] == "mercenary")["entries"]
    skills = {e["text"]: e["id"] for e in merc if ".skill_" in e["id"]}
    supports = {}
    for e in merc:
        if ".support_" not in e["id"]:
            continue
        base = e["text"].split(" (Tier")[0]
        # GGG ships one genuine duplicate (Gilded Extra Targets, both Tier 3). First wins.
        supports.setdefault(base, e["id"])

    archetypes = {}
    for group in items["result"]:
        for e in group["entries"]:
            if e.get("disc") != "mercenary_warrant":
                continue
            name = e["text"].removeprefix("Mercenary Warrant (").removesuffix(")")
            archetypes[name] = e["type"]

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


if __name__ == "__main__":
    main()
