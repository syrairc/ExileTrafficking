using System.Collections.Generic;
using System.Linq;
using ExileCore;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Interfaces;

namespace ExileTrafficking;

// one of a mercenary's skills plus whatever supports rolled on it. kept as HASH16s rather than
// names because that's what the trade site keys on, names are a mercdata.json lookup away
public sealed class MemSkill
{
    public ushort Hash { get; init; }
    public IReadOnlyList<ushort> Supports { get; init; } = new List<ushort>();

    public string TradeId => $"mercenary.skill_{Hash}";
    public IEnumerable<string> SupportTradeIds => Supports.Select(x => $"mercenary.support_{x}");
}

// a whole mercenary, however you got at it. Id and Active only mean anything on a roster entry,
// the offer and the item both leave them at zero
public sealed class MemMerc
{
    public int Level { get; init; }
    public ushort BuildHash { get; init; }
    public uint Id { get; init; }
    public bool Active { get; init; }
    public IReadOnlyList<MemSkill> Skills { get; init; } = new List<MemSkill>();

    public MercBuild Build => MercData.BuildForHash(BuildHash);
}

public static class MercenaryMemory
{
    // the merc currently on offer. null until you open the ui
    public static MemMerc Encounter(GameController game)
    {
        try
        {
            var handler = Handler(game, MercenaryNative.EncounterRulesetId);
            return handler == 0 ? null : MercenaryNative.ReadEncounter(game.Memory, handler);
        }
        catch
        {
            return null;
        }
    }

    // the mercs you've hired. 
    public static IReadOnlyList<MemMerc> Roster(GameController game)
    {
        try
        {
            var handler = Handler(game, MercenaryNative.AreaRulesetId);
            return handler == 0
                ? new List<MemMerc>()
                : MercenaryNative.ReadRoster(game.Memory, handler);
        }
        catch
        {
            return new List<MemMerc>();
        }
    }

    // a mercenary warrant/contract item. same descriptor the handlers carry.
    public static MemMerc Contract(Entity item)
    {
        try
        {
            return Component(item)?.Merc;
        }
        catch
        {
            return null;
        }
    }

    // core has no wrapper for MercenaryContract yet so GetComponent<T> can't find it, but the address
    // is sitting in CacheComp under the native name and we can build our own object on top of it
    public static MercenaryContract Component(Entity item) =>
        item != null && item.CacheComp.TryGetValue("MercenaryContract", out var address) && address != 0
            ? item.GetObject<MercenaryContract>(address)
            : null;

    // which mercenary class the server put in this zone, known at load rather than on engaging. -1
    // when there isn't one
    public static int AreaClass(GameController game)
    {
        try
        {
            return MercenaryNative.ReadAreaClass(game.Memory, game?.IngameState?.Data?.Address ?? 0);
        }
        catch
        {
            return -1;
        }
    }

    // MechanicHandlers is every league mechanic's state object in one list, each tagged with its
    // Rulesets.dat row. that row index is the only handle we get, so it's how we pick ours out.
    public static long Handler(GameController game, int rulesetId) =>
        game?.IngameState?.ServerData?.MechanicHandlers?
            .FirstOrDefault(x => x != null && x.Id == rulesetId)?.Address ?? 0;
}

#region exileapi additions
// offsets for core, if desired

public static class MercenaryNative
{
    // Rulesets.dat row indices. ServerData.MechanicHandlers[].Id is that row index
    public const int EncounterRulesetId = 161; // MercenaryEncounterRules, the merc you're about to fight
    public const int AreaRulesetId = 162;      // MercenaryAreaRules, the mercs you've hired

    // ClientMercenaryEncounter, 472 bytes. anything below 0x150 is shared ruleset base junk.
    public const int EncounterMerc = 0x158;    // MercenaryInfo, only trust it when HasMerc is set
    public const int EncounterHasMerc = 0x1C8;

    // ClientMercenaryArea, 400 bytes. plain std::vector of roster entries, sorted by id.
    public const int AreaRosterFirst = 0x158;
    public const int AreaRosterLast = 0x160;

    // roster entry is a MercenaryInfo with three extra fields glued on the end
    public const int RosterEntrySize = 112;
    public const int RosterEntryId = 0x60;     // the server addresses mercs by this
    public const int RosterEntryActive = 0x68;

    // MercenaryInfo, 96 bytes. same struct turns up in the encounter handler, in the roster, and
    // inside a mercenary contract item's extra data.
    public const int MercLevel = 0x00;         // u8 on the wire, widened to u32 in memory
    public const int MercBuildRow = 0x38;      // MercenaryBuilds.dat row
    public const int MercSkillsFirst = 0x48;   // std::vector<skill>
    public const int MercSkillsLast = 0x50;
    public const int MercSkillSize = 40;
    public const int SkillRow = 0x00;          // MercenarySkills.dat row
    public const int SkillSupportsFirst = 0x10;
    public const int SkillSupportsLast = 0x18;
    public const int SkillSupportSize = 16;
    public const int SupportRow = 0x00;        // MercenarySupports.dat row

    // MercenaryContract component, off a warrant/contract item. vtable then owner then the
    // descriptor just inlined, no pointer chase. reader is ItemExtraData_ReadMercenaryInfo.
    public const int ContractInfo = 0x10;

    // HASH16 inside each loaded .dat row. it's the u16 the network protocol keys rows by, and 
    // skills and supports it doubles as the trade site's id number (mercenary.skill_<hash>).
    // each one is the 2-byte hole the row layout leaves between two string pointers.
    public const int BuildRowHash = 140;       // row stride 243
    public const int SkillRowHash = 104;       // row stride 114
    public const int SupportRowHash = 76;      // row stride 122

    // MercenaryPlugin, an area-generation plugin off IngameState.Data. nothing to do with the ruleset
    // handlers above: this one is filled at zone load, so it tells you the class before the mercenary
    // has spawned, let alone been engaged. it holds one MercenaryClasses.dat row and the row index is
    // the class, so the resolve is pointer arithmetic against the table rather than a name lookup.
    public const int AreaPluginsFirst = 0xE0;
    public const int AreaPluginsLast = 0xE8;
    public const int PluginNameHash = 0x08;
    public const ushort MercenaryPluginHash = 0x7F7D;
    public const int PluginClassRow = 0x18;
    public const int PluginClassAsset = 0x20;
    public const int AssetData = 0x28;         // the parsed table, its first two fields are the rows
    public const int ClassRowSize = 148;

    // a stale offset reads garbage rather than failing, so cap every count before looping on it
    public const int MaxRoster = 64;
    public const int MaxSkills = 32;
    public const int MaxSupports = 16;
    public const int MaxAreaPlugins = 64;

    // there's only ever one offer, so it's a descriptor inlined at a fixed offset rather than a
    // vector. the flag is the only thing separating a live offer from whatever was in that memory
    // beforehand, which is stale text often enough that reading it blind would look convincing
    public static MemMerc ReadEncounter(IMemory memory, long handler) =>
        memory.Read<byte>(handler + EncounterHasMerc) == 0
            ? null
            : ReadMerc(memory, handler + EncounterMerc);

    // hired mercs are a plain vector, one descriptor each with id and the active flag tacked on the
    // end. the server addresses mercs by that id so it's worth carrying even though we only display
    // not sure if there is any use in porting this stuff, not used in the plugin either
    public static List<MemMerc> ReadRoster(IMemory memory, long handler)
    {
        var roster = new List<MemMerc>();

        foreach (var entry in Walk(memory, handler + AreaRosterFirst, handler + AreaRosterLast,
                     RosterEntrySize, MaxRoster))
        {
            roster.Add(ReadMerc(memory, entry, memory.Read<uint>(entry + RosterEntryId),
                memory.Read<byte>(entry + RosterEntryActive) != 0));
        }

        return roster;
    }

    // the shared bit. offer, roster entry and warrant item all end up here with a pointer to the same
    // 96 byte descriptor, only the id/active tail differs. the build comes back as its row's HASH16
    // rather than a name because that's what both the trade query and mercdata.json key on.
    // i don't resolve the human readable names here - check MercData.BuildForHash() and MercData.SkillForHash()
    // if you need that
    public static MemMerc ReadMerc(IMemory memory, long info, uint id = 0, bool active = false)
    {
        var buildRow = memory.Read<long>(info + MercBuildRow);

        return new MemMerc
        {
            Id = id,
            Active = active,
            Level = memory.Read<int>(info + MercLevel),
            BuildHash = buildRow == 0 ? (ushort)0 : memory.Read<ushort>(buildRow + BuildRowHash),
            Skills = ReadSkills(memory, info),
        };
    }

    // two nested vectors: the merc's skills, and each skill's supports. a null row means
    // the client's dat lookup missed and it writes that silently rather than throwing, so skip the slot
    private static List<MemSkill> ReadSkills(IMemory memory, long info)
    {
        var skills = new List<MemSkill>();

        foreach (var entry in Walk(memory, info + MercSkillsFirst, info + MercSkillsLast, MercSkillSize, MaxSkills))
        {
            var row = memory.Read<long>(entry + SkillRow);
            if (row == 0) continue;

            var supports = new List<ushort>();
            foreach (var slot in Walk(memory, entry + SkillSupportsFirst, entry + SkillSupportsLast,
                         SkillSupportSize, MaxSupports))
            {
                var supportRow = memory.Read<long>(slot + SupportRow);
                if (supportRow != 0) supports.Add(memory.Read<ushort>(supportRow + SupportRowHash));
            }

            skills.Add(new MemSkill { Hash = memory.Read<ushort>(row + SkillRowHash), Supports = supports });
        }

        return skills;
    }

    // the area's mercenary class as a MercenaryClasses.dat row index, or -1 when the zone has no
    // mercenary in it. the row has to sit on a stride boundary inside the table
    public static int ReadAreaClass(IMemory memory, long data)
    {
        if (data == 0) return -1;

        foreach (var slot in Walk(memory, data + AreaPluginsFirst, data + AreaPluginsLast, 8, MaxAreaPlugins))
        {
            var plugin = memory.Read<long>(slot);
            if (plugin == 0 || memory.Read<ushort>(plugin + PluginNameHash) != MercenaryPluginHash) continue;

            var row = memory.Read<long>(plugin + PluginClassRow);
            var asset = memory.Read<long>(plugin + PluginClassAsset);
            if (row == 0 || asset == 0) return -1;

            var table = memory.Read<long>(asset + AssetData);
            if (table == 0) return -1;

            var rowsFirst = memory.Read<long>(table);
            var rowsLast = memory.Read<long>(table + 8);
            if (rowsFirst == 0 || rowsLast <= rowsFirst) return -1;

            var offset = row - rowsFirst;
            if (offset < 0 || offset % ClassRowSize != 0) return -1;

            var index = offset / ClassRowSize;
            return index < (rowsLast - rowsFirst) / ClassRowSize ? (int)index : -1;
        }

        return -1;
    }

    // every list in here is a std::vector, which on the wire is just begin/end/capacity pointers with
    // no count stored anywhere, so the element count is the span divided by the stride. that also
    // means a stale offset doesn't fail, it hands you two unrelated pointers and a count in the
    // millions, which is what max is guarding against
    private static IEnumerable<long> Walk(IMemory memory, long firstAt, long lastAt, int stride, int max)
    {
        var first = memory.Read<long>(firstAt);
        var last = memory.Read<long>(lastAt);
        if (first == 0 || last < first) yield break;

        var count = (last - first) / stride;
        if (count > max) yield break;

        for (var i = 0; i < count; i++) yield return first + i * stride;
    }
}

// the warrant component itself.
public class MercenaryContract : Component
{
    // the blob moves when the item's extra data gets re-read, so dont cache the component itself
    public MemMerc Merc => Address == 0
        ? null
        : MercenaryNative.ReadMerc(M, Address + MercenaryNative.ContractInfo);
}
#endregion
