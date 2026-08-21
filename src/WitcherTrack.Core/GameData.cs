namespace WitcherTrack.Core;

/// <summary>
/// Facts about The Witcher 3 that the game reports incompletely, and that therefore have
/// to be stated once and reviewed rather than inferred at runtime.
/// </summary>
/// <remarks>
/// Everything here is derived from the game's own script sources and verified against a
/// completed savegame. It is deliberately small: anything the engine can answer for
/// itself is asked at runtime instead.
/// </remarks>
public static class GameData
{
    /// <summary>
    /// Map pin types that can actually be cleared, and so belong in a completion total.
    /// </summary>
    /// <remarks>
    /// The game marks a cleared point of interest by disabling its pin. Most pin types are
    /// never disabled because they are not something you finish - signposts, notice boards,
    /// blacksmiths, boat moorings, player stashes. Counting those would put a completion
    /// run permanently short of a total it can never reach.
    /// <para>
    /// Established from a fully completed savegame: every type listed here was disabled for
    /// every one of its pins, and every type left out was disabled for none of them.
    /// </para>
    /// <para>
    /// Two of these pins can occupy almost the same spot yet both be genuine, separately
    /// counted objectives - confirmed against a savefile, directly in-game, for
    /// <c>q104_rat_nest1</c> and <c>q104_rat_nest2</c> (ten units apart): the pause map
    /// renders a single icon for the pair, but both nests exist and both must be cleared.
    /// A pin count that looks one-over relative to what a player can see on their own map is
    /// not necessarily a duplicate to exclude - check whether it is this kind of pair before
    /// assuming it is one of the reward-choice pins in <see cref="NonCountingPoiIds"/>.
    /// </para>
    /// </remarks>
    public static readonly IReadOnlySet<string> ClearablePinTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "BanditCamp",
        "BanditCampfire",
        "BossAndTreasure",
        "Contraband",
        "DungeonCrawl",
        "MonsterNest",
        "PlaceOfPower",
        "RescuingTown",
        "SpoilsOfWar",
        "TreasureHuntMappin",

        // Blood and Wine adds its own pin types, none of which appear on the continent used
        // to derive the list above. Established the same way, from a Toussaint save that had
        // finished the expansion: every pin of these five types was disabled, and none of
        // WitcherHouse, PlayerStashDiscoverable, Bookshelf, Bed, AlchemyTable or
        // MutagenDismantle ever were - those stayed out for the same reason the base-game
        // furniture and crafting-station types did.
        "WineContract",
        "Plegmund",
        "InfestedVineyard",
        "KnightErrant",
        "Hideout",
    };

    /// <summary>
    /// Individual point-of-interest identifiers that never disable, regardless of whether the
    /// content they belong to has been finished.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A handful of <c>TreasureHuntMappin</c> entries are not the chest itself but bookkeeping
    /// pins for a treasure hunt's alternative rewards - the same hunt reports one pin per
    /// possible item (<c>th1003_mp_armor_set</c>, <c>_mp_crossbow</c>, <c>_mp_silver_sword</c>,
    /// <c>_mp_steel_sword</c>, and similarly named <c>_mp_upgrade_*</c> variants for others).
    /// None of these ever move to a disabled state in the engine, no matter which of the
    /// alternatives the quest actually awarded.
    /// </para>
    /// <para>
    /// Confirmed against two independent playthroughs, one a fully completed run: the exact
    /// same thirty-eight identifiers were reported <c>not_done</c> in both, despite the two
    /// saves otherwise disagreeing on almost everything else. Pins that are simply uncleared
    /// vary between two different players; pins that are structurally incapable of clearing
    /// do not. Left in <see cref="ClearablePinTypes"/> alongside real chest pins, they would
    /// hold every run at a percentage no playthrough can ever reach.
    /// </para>
    /// <para>
    /// <c>poi_gor_d_18_mp_post</c> went back and forth twice before settling here. First
    /// excluded on the never-clears signature alone - <c>not_done</c> in every one of six log
    /// uploads, including a Toussaint save otherwise at 100%. Un-excluded after a coordinate
    /// check against a community map extraction that compared it to the wrong neighbour,
    /// <c>poi_gor_d_20_mp</c> (four hundred units away), and concluded from that distance that
    /// it was a genuine, separate Hidden Treasure simply not yet found in any logged save.
    /// Re-excluded after checking the same extraction against its actual physical neighbour
    /// instead: it sits roughly one tenth of a unit from <c>poi_gor_d_18_mp</c>
    /// (BanditCampfire, <c>done</c>) in both a savefile-derived script log and the extraction
    /// independently - the same near-zero-distance, shared-base-id, <c>_post</c>-suffix
    /// signature already confirmed for its immediate sibling, <c>poi_gor_d_17_mp_post</c>,
    /// below. Never clearing is necessary but not sufficient for this list - it also has to
    /// not be a real, merely-unvisited pin, and the only reliable way to tell the two apart is
    /// checking the correct neighbour's coordinates, not just any coordinate in the same area.
    /// </para>
    /// <para>
    /// The same duplicate-pin pattern turned out to be much wider in Toussaint: eight more
    /// ids, all with a <c>_post</c>, <c>_b</c> or <c>_ban</c> suffix, were <c>not_done</c> in
    /// every one of six logs while a sibling id at (or within a few units of) the exact same
    /// coordinates was <c>done</c> in the same logs - a second database entry for a site
    /// already cleared under its primary pin, not an unfinished objective. Confirmed
    /// independently against a completed savefile's own map directly: no unsatisfied pin of
    /// any kind remains anywhere - these nine plus the one above account for the entire
    /// ten-pin gap between the tool's old total and the savefile's own count:
    /// <c>poi_bar_a_02_mp_post</c> (47u from <c>poi_bar_a_02_mp</c>, BanditCamp),
    /// <c>poi_bar_a_12_mp_b</c> (6u from <c>poi_bar_a_12_mp</c>, KnightErrant),
    /// <c>poi_bar_a_13_mp_ban</c> (not_done in all six; no coordinate-matched sibling found,
    /// kept on naming and disable-pattern alone),
    /// <c>poi_gor_a_09_mp_b</c> (10u from <c>poi_gor_a_09_mp</c>, KnightErrant),
    /// <c>poi_gor_d_07_mp_b</c> (8u from <c>poi_gor_d_07_mp</c>, KnightErrant),
    /// <c>poi_gor_d_17_mp_post</c> (same coordinates as <c>poi_gor_d_17_mp</c>, BanditCampfire),
    /// <c>poi_ved_a_07_mp_post</c> (same coordinates as <c>poi_ved_a_07_mp</c>, BanditCampfire),
    /// <c>poi_ved_a_08_mp_post</c> (same coordinates as <c>poi_ved_a_08_mp</c>, BanditCampfire).
    /// </para>
    /// <para>
    /// The suffix is a clue, not the rule: <c>poi_bar_a_09_mp</c> is the mirror image of the
    /// eight above - it is the plain, unsuffixed BanditCampfire pin that never disables,
    /// while its own <c>_post</c> sibling half a unit away (a BossAndTreasure pin) clears
    /// normally. Found by dropping the naming assumption and testing every countable point
    /// of interest directly: across six logs from saves otherwise at the same 434/436, this
    /// was the only id reported <c>not_done</c> in all six with no exception anywhere.
    /// </para>
    /// <para>
    /// <c>poi_bar_a_08_mp_post</c> is a different kind of duplicate again, confirmed by
    /// testing the mechanic directly against a savefile: five Toussaint sites register as both a
    /// BanditCampfire and a BossAndTreasure pin at the same coordinates, and clearing the
    /// site resolves both registrations together, not just one. Four of the five have a
    /// phantom half that never disables and were handled above by that test; this fifth
    /// pair - <c>poi_bar_a_08_mp</c> and <c>poi_bar_a_08_mp_post</c> - is <c>done</c> on
    /// both sides in every one of six logs, so the never-clears test could not find it. One
    /// side of every such pair still has to be dropped or the site is counted twice; kept
    /// the plain BanditCampfire id, as with the other four.
    /// </para>
    /// <para>
    /// <c>ep1_poi09_mp</c>/<c>ep1_poi09_mp_bugfix</c> and <c>ep1_poi23_mp</c>/
    /// <c>ep1_poi23_mp_bugfix</c> are the same always-done-together duplicate, found by
    /// testing directly against a savefile whether any BanditCampfire pins share
    /// coordinates: these are the only two pairs at zero units apart out of thirty-three
    /// Velen/No Man's Land BanditCampfire pins (the next closest unrelated pair is fifty-two
    /// units away). Both
    /// are Hearts of Stone content placed on the base map, so they count toward the
    /// zone-wide Velen total even though their <c>dlc</c> tag is <c>hos</c>. The <c>_bugfix</c>
    /// half reads as the one CD Projekt Red intended to keep, so the plain id is the one
    /// dropped here.
    /// </para>
    /// <para>
    /// <c>mq2006_chest1_mappin</c> and <c>mq2006_chest2_mappin</c> were on this list from
    /// the original two-playthrough evidence and looked, by the naming, like a reward-choice
    /// pair such as the <c>th####_mp_*</c> ids above. They are not: each was traced, against a
    /// savefile, by coordinates and quest wiki - <c>chest1</c> sits by Fayrlund and is the pin
    /// with the Hidden Treasure icon for "X Marks the Spot"; <c>chest2</c> sits 1,631 units away by
    /// Dorve Ruins and belongs to the unrelated quest "Shortcut". They only share an internal
    /// <c>mq2006</c> quest-group prefix, not a location or a reward choice. Both are real,
    /// separately completable treasures; removed from this list on that basis, even though
    /// neither had shown <c>done</c> in any log so far - on the reward-choice pins above that
    /// pattern meant "structurally cannot clear", but here it more likely means neither
    /// obscure hunt had actually been finished in any of the logged saves yet.
    /// </para>
    /// </remarks>
    public static readonly IReadOnlySet<string> NonCountingPoiIds = new HashSet<string>(StringComparer.Ordinal)
    {
        "poi_bar_a_02_mp_post",
        "poi_bar_a_12_mp_b",
        "poi_bar_a_13_mp_ban",
        "poi_gor_a_09_mp_b",
        "poi_gor_d_07_mp_b",
        "poi_gor_d_17_mp_post",
        "poi_gor_d_18_mp_post",
        "poi_ved_a_07_mp_post",
        "poi_ved_a_08_mp_post",
        "poi_bar_a_09_mp",
        "poi_bar_a_08_mp_post",
        "ep1_poi09_mp",
        "ep1_poi23_mp",
        "mp_lw_sk55_treasure_hunt",
        "mp_lw_sk58_treasure_hunt",
        "th1003_mp_armor_set",
        "th1003_mp_crossbow",
        "th1003_mp_silver_sword",
        "th1003_mp_steel_sword",
        "th1005_mp_armor_set",
        "th1005_mp_silver_sword",
        "th1005_mp_steel_sword",
        "th1005_mp_upgrade_2a",
        "th1005_mp_upgrade_2b",
        "th1005_mp_upgrade_2c",
        "th1005_mp_upgrade_3a",
        "th1005_mp_upgrade_3c",
        "th1007_mp_armor_set",
        "th1007_mp_crossbow",
        "th1007_mp_silver_sword",
        "th1007_mp_steel_sword",
        "th1007_mp_upgrade_1",
        "th1007_mp_upgrade_1b",
        "th1007_mp_upgrade_1c",
        "th1007_mp_upgrade_1d",
        "th1007_mp_upgrade_1e",
        "th1007_mp_upgrade_1f",
        "th1007_mp_upgrade_2a",
        "th1007_mp_upgrade_2b",
        "th1007_mp_upgrade_2c",
        "th1007_mp_upgrade_3a",
        "th1007_mp_upgrade_3b",
        "th1007_mp_upgrade_3c",
        "th1009_mp_upgrade_1b_a2",
        "th1009_mp_upgrade_1b_b2",
        "th1009_mp_upgrade_1b_c2",
        "th1009_mp_upgrade_2b_a2",
        "th1009_mp_upgrade_2b_b2",
        "th1009_mp_upgrade_2b_c2",
    };

    /// <summary>
    /// What players call each map pin type, keyed by the engine's own name for it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The game does not name the points a completion run has to clear. Of four hundred and
    /// seventy countable pins exactly one carries a localisation string, and the couple of
    /// hundred that do have one are signposts and notice boards - none of which count. So
    /// the category is the only label there is, and the engine's own spelling of it
    /// (<c>RescuingTown</c>, <c>BossAndTreasure</c>) is not what a player would recognise.
    /// </para>
    /// <para>
    /// The mapping was derived by reconciling pin-type counts against a hand-counted,
    /// category-by-category tally taken from a savefile, for the base game and Blood and
    /// Wine independently.
    /// Twelve of these agree to within two pins on <em>both</em> lists, which is the same
    /// margin the already-settled categories show. Three do not, and are marked below: they
    /// are the most likely reading rather than a confirmed one. Getting one of those three
    /// wrong mislabels a row in the interface; it cannot affect any total, because counting
    /// keys on the pin type itself and never on this name.
    /// </para>
    /// <para>
    /// <c>RescuingTown</c> is the clearest illustration of why the engine name cannot be
    /// shown directly: it is what the game calls an Abandoned Site, matching 17 against 17
    /// in the base game and 11 against 11 in Toussaint.
    /// </para>
    /// </remarks>
    public static readonly IReadOnlyDictionary<string, string> PinTypeNames =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // Agree on both lists.
            ["RescuingTown"] = "Abandoned Site",
            ["MonsterNest"] = "Monster Nest",
            ["PlaceOfPower"] = "Place of Power",
            ["Contraband"] = "Smugglers' Cache",
            ["SpoilsOfWar"] = "Spoils of War",
            ["BanditCamp"] = "Person in Distress",
            ["DungeonCrawl"] = "Monster Den",
            ["InfestedVineyard"] = "Infested Vineyard",
            ["WineContract"] = "Vintner's Contract",
            ["Hideout"] = "Hanse Base",
            ["KnightErrant"] = "Knight Errant",
            ["Plegmund"] = "The Prophet Lebioda's Footsteps",

            // Close but not exact against a savefile's own map tally, with a residual of one
            // or two pins per zone that has never resolved further: Guarded Treasure and
            // Bandit Camp are both exact in the base game and over by several in Toussaint,
            // and Hidden Treasure lines up to within one pin per zone once the ids in
            // NonCountingPoiIds are subtracted out (see that set's remarks - the raw
            // TreasureHuntMappin count includes those phantom bookkeeping pins).
            ["BossAndTreasure"] = "Guarded Treasure",
            ["BanditCampfire"] = "Bandit Camp",
            ["TreasureHuntMappin"] = "Hidden Treasure",

            // Confirmed directly against a savefile: these two pins are not
            // part of the 100% completion total. Kept out of ClearablePinTypes above, so
            // this entry only affects the label if a raw dump is ever displayed uninterpreted.
            ["SignalingStake"] = "Signal Stake",
        };

    /// <summary>
    /// The player-facing name of a map pin type, falling back to the engine's own name with
    /// its words separated when the type is not one of the catalogued ones.
    /// </summary>
    /// <remarks>
    /// The fallback matters after a game update: a pin type nobody has classified yet shows
    /// as "Some New Type" rather than vanishing or showing blank.
    /// </remarks>
    public static string PinTypeName(string? pinType)
    {
        if (string.IsNullOrWhiteSpace(pinType))
        {
            return "Point of Interest";
        }

        if (PinTypeNames.TryGetValue(pinType, out string? name))
        {
            return name;
        }

        var spaced = new System.Text.StringBuilder(pinType.Length + 8);

        for (int i = 0; i < pinType.Length; i++)
        {
            if (i > 0 && char.IsUpper(pinType[i]) && !char.IsUpper(pinType[i - 1]))
            {
                spaced.Append(' ');
            }

            spaced.Append(pinType[i]);
        }

        return spaced.ToString();
    }

    /// <summary>
    /// A pin type that is clearable in general, but does not count within one specific
    /// content pack.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Blood and Wine's <c>TreasureHuntMappin</c> pins used to be excluded entirely here, on
    /// the theory that the expansion's own treasure hunts are already counted through the
    /// journal and these pins were a further, uncounted duplicate of that. A real save's own
    /// per-pin state disproved it: of the fifteen such pins, fourteen were individually
    /// disabled - not left permanently open the way the base game's phantom reward-choice
    /// pins are (see <see cref="NonCountingPoiIds"/>) - and the same savefile's own
    /// hand-counted category breakdown lists exactly fourteen Hidden Treasure among the
    /// ninety-three Blood and Wine points of interest. They are real, individually-clearable
    /// objectives, not a duplicate of the journal count.
    /// </para>
    /// <para>
    /// Empty for now, kept rather than removed because the DLC-scoped exception is real
    /// machinery - the base game's <c>TreasureHuntMappin</c> keeps a much larger set of
    /// permanently-open reward-choice pins, handled instead through
    /// <see cref="NonCountingPoiIds"/> because those are specific identifiers, not a whole
    /// pin type within a content pack.
    /// </para>
    /// </remarks>
    public static readonly IReadOnlySet<(string PinType, string Dlc)> ClearableExceptForDlc =
        new HashSet<(string, string)>();

    /// <summary>
    /// The map area index that identifies Toussaint's world file
    /// (<c>dlc\bob\data\levels\bob\bob.w2w</c> - "bob" being the project's internal name for
    /// Blood and Wine), as reported by <c>CCommonMapManager.GetAreaMapPins()</c>.
    /// </summary>
    /// <remarks>
    /// Unlike Hearts of Stone, which adds its content to the base continent's own world file
    /// and so cannot be told apart from base-game points of interest by area alone, Blood and
    /// Wine loads an entirely separate map - so every point of interest reported under this
    /// area index belongs to it unambiguously, with no per-entry curation needed.
    /// <para>
    /// This index comes from one savefile's tracked session, not from the game's source: it is the
    /// position <c>GetAreaMapPins()</c> happened to return Toussaint at, and that position is
    /// not guaranteed to be stable across game versions or even across sessions. Treat a
    /// content pack attribution built from it as a good default, not a certainty, until it has
    /// been cross-checked against another dump.
    /// </para>
    /// </remarks>
    public const int ToussaintAreaType = 11;

    /// <summary>
    /// Diagrams and formulae that belong to a content pack, mapped to it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The game tells the reporter which content pack a quest came from and says nothing at
    /// all about a schematic, so every diagram and formula would otherwise be filed under
    /// the base game - correct for a 300% run, which counts all of them, and wrong for the
    /// per-pack modes, which would ask for expansion diagrams during a base-game run and
    /// for none at all during an expansion run.
    /// </para>
    /// <para>
    /// The identifier is the key, not the display name, and that is not a preference: the
    /// game hands out the same localised name for two different schematics. "Diagram:
    /// Toussaint knight's armor" is reported for both <c>Knight Geralt Armor 3 schematic</c>
    /// and <c>Knight Geralt A Armor 3 schematic</c>, and the same collision repeats for the
    /// gauntlets, boots and trousers. The A variants are the tourney set - the community
    /// list names all eight, four "Toussaint knight's" and four "Toussaint knight's tourney",
    /// and the catalogue holds exactly eight identifiers - so a set keyed on names would
    /// silently count four of the eight.
    /// </para>
    /// <para>
    /// Sixty-five Blood and Wine diagrams and twenty-nine Hearts of Stone ones are listed.
    /// Both lists were collected by hand and checked against the catalogue: every name in
    /// them resolves to an identifier the game has actually reported, and none is left over.
    /// </para>
    /// </remarks>
    public static readonly IReadOnlyDictionary<string, string> SchematicContentPacks = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        // Grandmaster witcher gear - the fourth and fifth upgrades of the four school sets.
        ["Witcher Lynx Jacket Upgrade schematic 4"] = "baw",
        ["Witcher Lynx Gloves Upgrade schematic 5"] = "baw",
        ["Witcher Lynx Boots Upgrade schematic 5"] = "baw",
        ["Witcher Lynx Pants Upgrade schematic 5"] = "baw",
        ["Lynx School steel sword Upgrade schematic 4"] = "baw",
        ["Lynx School silver sword Upgrade schematic 4"] = "baw",
        ["Witcher Gryphon Jacket Upgrade schematic 4"] = "baw",
        ["Witcher Gryphon Gloves Upgrade schematic 5"] = "baw",
        ["Witcher Gryphon Boots Upgrade schematic 5"] = "baw",
        ["Witcher Gryphon Pants Upgrade schematic 5"] = "baw",
        ["Gryphon School steel sword Upgrade schematic 4"] = "baw",
        ["Gryphon School silver sword Upgrade schematic 4"] = "baw",
        ["Witcher Bear Jacket Upgrade schematic 4"] = "baw",
        ["Witcher Bear Gloves Upgrade schematic 5"] = "baw",
        ["Witcher Bear Boots Upgrade schematic 5"] = "baw",
        ["Witcher Bear Pants Upgrade schematic 5"] = "baw",
        ["Bear School steel sword Upgrade schematic 4"] = "baw",
        ["Bear School silver sword Upgrade schematic 4"] = "baw",
        ["Witcher Wolf Jacket Upgrade schematic 4"] = "baw",
        ["Witcher Wolf Gloves Upgrade schematic 5"] = "baw",
        ["Witcher Wolf Boots Upgrade schematic 5"] = "baw",
        ["Witcher Wolf Pants Upgrade schematic 5"] = "baw",
        ["Wolf School steel sword Upgrade schematic 4"] = "baw",
        ["Wolf School silver sword Upgrade schematic 4"] = "baw",

        // The Manticore set, internally the Red Wolf school.
        ["Witcher Red Wolf Jacket schematic 1"] = "baw",
        ["Witcher Red Wolf Gloves schematic 1"] = "baw",
        ["Witcher Red Wolf Boots schematic 1"] = "baw",
        ["Witcher Red Wolf Pants schematic 1"] = "baw",
        ["Red Wolf School steel sword schematic 1"] = "baw",
        ["Red Wolf School silver sword schematic 1"] = "baw",

        // Toussaint knight's gear; the A variants are the tourney set.
        ["Knight Geralt Armor 3 schematic"] = "baw",
        ["Knight Geralt Gloves 3 schematic"] = "baw",
        ["Knight Geralt Boots 3 schematic"] = "baw",
        ["Knight Geralt Pants 3 schematic"] = "baw",
        ["Knight Geralt A Armor 3 schematic"] = "baw",
        ["Knight Geralt A Gloves 3 schematic"] = "baw",
        ["Knight Geralt A Boots 3 schematic"] = "baw",
        ["Knight Geralt A Pants 3 schematic"] = "baw",
        ["Knights Geralt steel sword 3 schematic"] = "baw",

        // Ducal guard gear; the A variants are the Color Guardsman's and the Captain's.
        ["Guard Lvl1 Armor 3 schematic"] = "baw",
        ["Guard Lvl1 Gloves 3 schematic"] = "baw",
        ["Guard Lvl1 Boots 3 schematic"] = "baw",
        ["Guard Lvl1 Pants 3 schematic"] = "baw",
        ["Guard Lvl1 steel sword 3 schematic"] = "baw",
        ["Guard Lvl1 A Armor 3 schematic"] = "baw",
        ["Guard Lvl1 A Gloves 3 schematic"] = "baw",
        ["Guard Lvl1 A Boots 3 schematic"] = "baw",
        ["Guard Lvl1 A Pants 3 schematic"] = "baw",
        ["Guard Lvl2 Armor 3 schematic"] = "baw",
        ["Guard Lvl2 Gloves 3 schematic"] = "baw",
        ["Guard Lvl2 Boots 3 schematic"] = "baw",
        ["Guard Lvl2 Pants 3 schematic"] = "baw",
        ["Guard Lvl2 steel sword 3 schematic"] = "baw",
        ["Guard Lvl2 A Armor 3 schematic"] = "baw",
        ["Guard Lvl2 A Gloves 3 schematic"] = "baw",
        ["Guard Lvl2 A Boots 3 schematic"] = "baw",
        ["Guard Lvl2 A Pants 3 schematic"] = "baw",

        // Toussaint blades sold or found on their own.
        ["Toussaint steel sword 3 schematic"] = "baw",
        ["Hanza steel sword 3 schematic"] = "baw",

        // The serpentine swords of Hanse Faramond and the Viroledan.
        ["Serpent Steel Sword schematic 1"] = "baw",
        ["Serpent Steel Sword schematic 2"] = "baw",
        ["Serpent Steel Sword schematic 3"] = "baw",
        ["Serpent Silver Sword schematic 1"] = "baw",
        ["Serpent Silver Sword schematic 2"] = "baw",
        ["Serpent Silver Sword schematic 3"] = "baw",

        // The Runewright's glyphs, in all three grades.
        ["Glyph binding lesser schematic"] = "hos",
        ["Glyph binding schematic"] = "hos",
        ["Glyph binding greater schematic"] = "hos",
        ["Glyph mending lesser schematic"] = "hos",
        ["Glyph mending schematic"] = "hos",
        ["Glyph mending greater schematic"] = "hos",
        ["Glyph reinforcement lesser schematic"] = "hos",
        ["Glyph reinforcement schematic"] = "hos",
        ["Glyph reinforcement greater schematic"] = "hos",
        ["Glyph warding lesser schematic"] = "hos",
        ["Glyph warding schematic"] = "hos",
        ["Glyph warding greater schematic"] = "hos",

        // The two runestones the expansion added; note the internal spelling of pyerog.
        ["Rune pierog schematic"] = "hos",
        ["Rune tvarog schematic"] = "hos",

        // The Ofieri set, internally Ofir.
        ["Crafted Ofir Armor schematic"] = "hos",
        ["Crafted Ofir Gloves schematic"] = "hos",
        ["Crafted Ofir Boots schematic"] = "hos",
        ["Crafted Ofir Pants schematic"] = "hos",
        ["Crafted Ofir Steel Sword schematic"] = "hos",

        // The Order of the Flaming Rose set, internally Burning Rose.
        ["Crafted Burning Rose Armor schematic"] = "hos",
        ["Crafted Burning Rose Gloves schematic"] = "hos",
        ["Crafted Burning Rose Sword schematic"] = "hos",

        // The Viper set and its two swords, carrying the game's own EP1 prefix.
        ["EP1 Witcher Armor schematic"] = "hos",
        ["EP1 Witcher Gloves schematic"] = "hos",
        ["EP1 Witcher Boots schematic"] = "hos",
        ["EP1 Witcher Pants schematic"] = "hos",
        ["EP1 Viper School steel sword schematic"] = "hos",
        ["EP1 Crafted Witcher Silver Sword schematic"] = "hos",

        // Sold by the Ofieri merchant, and the one schematic that belongs to no set.
        ["Concealment Kit schematic"] = "hos",
    };

    /// <summary>
    /// Gwent cards that are not part of the base-game collection.
    /// </summary>
    /// <remarks>
    /// <para>
    /// "Collect 'Em All" asks for 120 card types, all from the base game. The faction field
    /// separates the Skellige deck that Blood and Wine added, but these cards are filed
    /// under Neutral or under a base faction and cannot be told apart that way, so they are
    /// listed here.
    /// </para>
    /// <para>
    /// Expansion characters: Olgierd, Gaunter O'Dimm and his Darkness, and the Toad Prince
    /// come from Hearts of Stone; the Lady of the Lake and Visenna from Blood and Wine.
    /// Schirru is a Hearts of Stone addition to an existing faction, which is why the
    /// faction cannot catch it. Roach is a reward card rather than one you collect. The two
    /// GOG cards are a store promotion, and cow and mushroom are unused definitions.
    /// </para>
    /// <para>
    /// Removing these leaves exactly 120 types, which is the number the quest asks for.
    /// </para>
    /// </remarks>
    public static readonly IReadOnlySet<string> NonBaseGwentTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "cow",
        "gog_ciri",
        "gog_geralt",
        "lady_of_the_lake",
        "mrmirror",
        "mrmirror_foglet",
        "mushroom",
        "olgierd",
        "roach",
        "schirru",
        "toad",
        "visenna",
    };

    /// <summary>
    /// Points of interest whose own pin state cannot be trusted, mapped to a quest whose
    /// completion proves them done regardless of what <c>IsEntityMapPinDisabled()</c> says.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two points of interest were found, across every uploaded savefile with no
    /// exception, reading <c>not_done</c> while the quest that requires looting them read
    /// <c>done</c> in the same savefile - a contradiction, since neither quest below can
    /// complete without the chest in question being taken. See <c>KNOWN-ISSUES.md</c> for
    /// the full evidence trail (coordinates, sibling checks, cross-savefile verification)
    /// behind each one:
    /// </para>
    /// <list type="bullet">
    ///   <item><c>mq2006_chest1_mappin</c> ("X Marks the Spot") is proven by
    ///         <c>MQ2006 Bergeton's Treasure 1BE06D7D-4CE755B0-FA87E0B2-570B6DC9</c>.</item>
    ///   <item><c>mq2006_chest2_mappin</c> ("Shortcut") is proven by
    ///         <c>lw_sk_poi_050 485B407E-42A71E25-DBF39795-2451430F</c>.</item>
    /// </list>
    /// <para>
    /// <c>hs22_mp_nml</c>, near the Widows' Grotto signpost, was briefly added here on the
    /// theory that it does not spawn into the map-pin manager until
    /// <c>mq2048_msg_in_a_bottle</c> ("From a Land Far, Far Away") resolves. That theory
    /// was wrong - the two are not actually connected - and was removed once corrected.
    /// A savefile where this pin reads <c>not_done</c> is reporting a genuine gap, not a
    /// detection bug: the pin is legitimately not yet found. Do not re-add it on
    /// proximity to that quest alone; it would need its own independent proof, the same
    /// standard the two chests above were held to.
    /// </para>
    /// <para>
    /// This is a targeted correction for two specifically proven cases, not a general
    /// "trust the quest over the pin" rule - most <c>TreasureHuntMappin</c> pins have no
    /// tracked quest at all, and for the ones that do, the pin is normally the more
    /// reliable of the two signals. A broader chest-to-quest mapping across every Hidden
    /// Treasure is plausible in principle, but would need the same per-entry coordinate
    /// and quest-tag evidence as these two before being trusted, not an assumption that
    /// the naming or timing pattern generalises.
    /// </para>
    /// <para>
    /// That evidence is now collected rather than assumed: <see cref="QuestPinLink"/>
    /// derives a link where a chest pin and a treasure hunt complete together, with no
    /// other chest and no other hunt between them. It is deliberately not a proximity
    /// rule - a chest metres from where an unrelated quest was handed in proves nothing.
    /// These two stay listed here regardless: they were each proven individually, they
    /// predate the sequence data, and a run that has not reached them derives nothing.
    /// </para>
    /// <para>
    /// Applied by <see cref="StateResolver"/> after the event log is resolved but before
    /// a manual override is applied, so a player correction can still override this if a
    /// future game update ever makes it wrong.
    /// </para>
    /// </remarks>
    public static readonly IReadOnlyDictionary<string, string> PoiProvenByQuest =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["mq2006_chest1_mappin"] = "MQ2006 Bergeton's Treasure 1BE06D7D-4CE755B0-FA87E0B2-570B6DC9",
            ["mq2006_chest2_mappin"] = "lw_sk_poi_050 485B407E-42A71E25-DBF39795-2451430F",
        };

    /// <summary>
    /// Journal entries that are reported as quests but cannot be completed, and so must
    /// not sit in a total the run can never reach.
    /// </summary>
    /// <remarks>
    /// Both were found by asking why a fully completed playthrough still reported three
    /// unfinished quests. Neither is a quest the player can act on:
    /// <list type="bullet">
    ///   <item><c>[metaquest] Search for ugly</c> - an internal wrapper. The square
    ///         brackets are the game's own marking for entries of this sort.</item>
    ///   <item><c>mq1058 Lynx Witcher Fake</c> - a decoy contract entry that exists
    ///         alongside the real one.</item>
    /// </list>
    /// The third of those three is handled by <see cref="QuestExclusionGroups"/> instead,
    /// because it is a real quest with two mutually exclusive halves.
    /// </remarks>
    public static readonly IReadOnlySet<string> NonCountingQuestTags = new HashSet<string>(StringComparer.Ordinal)
    {
        "[metaquest] Search for ugly",
        "mq1058 Lynx Witcher Fake",
    };

    /// <summary>
    /// Quests that come in mutually exclusive variants, mapped to the group they share.
    /// </summary>
    /// <remarks>
    /// <c>mq7006_the_paths_of_destiny</c> and its <c>_p2</c> counterpart are two halves of
    /// the same Blood and Wine quest: finishing one leaves the other permanently inactive.
    /// The pair therefore contributes one to the total, not two.
    /// </remarks>
    public static readonly IReadOnlyDictionary<string, string> QuestExclusionGroups = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["mq7006_the_paths_of_destiny"] = "baw_paths_of_destiny",
        ["mq7006_the_paths_of_destiny_p2"] = "baw_paths_of_destiny",
    };

    /// <summary>
    /// Maps a Gwent card index onto its card type.
    /// </summary>
    /// <remarks>
    /// <para>
    /// "Collect 'Em All" asks for one of each card type, not one of every card: there are
    /// three Ghouls in the game and any one of them satisfies the objective. The game
    /// reports indices, and duplicates sometimes share an index and sometimes do not, so
    /// the grouping has to come from the card names.
    /// </para>
    /// <para>
    /// Taken from the name-to-index table in the game's own <c>gwintManager.ws</c>, with the
    /// trailing copy number removed. Indices absent from this table are definitions with no
    /// card behind them and are ignored.
    /// </para>
    /// </remarks>
    public static readonly IReadOnlyDictionary<int, string> GwentCardTypes = new Dictionary<int, string>
    {
        [0] = "dummy",
        [1] = "horn",
        [2] = "scorch",
        [3] = "frost",
        [4] = "fog",
        [5] = "rain",
        [6] = "clear_sky",
        [7] = "geralt",
        [8] = "vesemir",
        [9] = "yennefer",
        [10] = "ciri",
        [11] = "triss",
        [12] = "dandelion",
        [13] = "zoltan",
        [14] = "emiel",
        [15] = "villen",
        [16] = "avallach",
        [17] = "olgierd",
        [18] = "mrmirror",
        [19] = "mrmirror_foglet",
        [20] = "cow",
        [22] = "mushroom",
        [23] = "skellige_storm",
        [24] = "lady_of_the_lake",
        [25] = "visenna",
        [26] = "gog_geralt",
        [27] = "gog_ciri",
        [28] = "roach",
        [100] = "vernon",
        [101] = "natalis",
        [102] = "esterad",
        [103] = "philippa",
        [105] = "thaler",
        [107] = "siegfried",
        [109] = "dijkstra",
        [116] = "stennis",
        [121] = "trebuchet",
        [125] = "poor_infantry",
        [126] = "poor_infantry",
        [127] = "poor_infantry",
        [130] = "crinfrid",
        [140] = "catapult",
        [146] = "ballista",
        [150] = "kaedwen",
        [151] = "kaedwen",
        [160] = "blue_stripes",
        [170] = "siege_tower",
        [175] = "dun_banner_medic",
        [200] = "letho",
        [201] = "menno",
        [202] = "moorvran",
        [203] = "tibor",
        [205] = "albrich",
        [206] = "assire",
        [207] = "cynthia",
        [208] = "fringilla",
        [209] = "morteisen",
        [210] = "rainfarn",
        [211] = "renuald",
        [212] = "rotten",
        [213] = "shilard",
        [214] = "stefan",
        [215] = "sweers",
        [217] = "vanhemar",
        [218] = "vattier",
        [219] = "vreemde",
        [220] = "cahir",
        [221] = "puttkammer",
        [230] = "archer_support",
        [231] = "archer_support",
        [235] = "black_archer",
        [236] = "black_archer",
        [240] = "heavy_zerri",
        [241] = "zerri",
        [245] = "impera_brigade",
        [250] = "nausicaa",
        [255] = "combat_engineer",
        [260] = "young_emissary",
        [261] = "young_emissary",
        [265] = "siege_support",
        [300] = "eithne",
        [301] = "saskia",
        [302] = "isengrim",
        [303] = "iorveth",
        [305] = "dennis",
        [306] = "milva",
        [307] = "ida",
        [308] = "filavandrel",
        [309] = "yaevinn",
        [310] = "toruviel",
        [311] = "riordain",
        [312] = "ciaran",
        [313] = "barclay",
        [320] = "havekar_support",
        [321] = "havekar_support",
        [322] = "havekar_support",
        [325] = "vrihedd_brigade",
        [326] = "vrihedd_brigade",
        [330] = "dol_infantry",
        [331] = "dol_infantry",
        [332] = "dol_infantry",
        [335] = "dol_dwarf",
        [336] = "dol_dwarf",
        [337] = "dol_dwarf",
        [340] = "mahakam",
        [341] = "mahakam",
        [342] = "mahakam",
        [343] = "mahakam",
        [344] = "mahakam",
        [350] = "elf_skirmisher",
        [351] = "elf_skirmisher",
        [352] = "elf_skirmisher",
        [355] = "vrihedd_cadet",
        [360] = "dol_archer",
        [365] = "havekar_nurse",
        [366] = "havekar_nurse",
        [367] = "havekar_nurse",
        [368] = "schirru",
        [400] = "draug",
        [401] = "kayran",
        [402] = "imlerith",
        [403] = "leshen",
        [405] = "forktail",
        [407] = "earth_elemental",
        [410] = "fiend",
        [413] = "plague_maiden",
        [415] = "griffin",
        [417] = "werewolf",
        [420] = "botchling",
        [423] = "frightener",
        [425] = "ice_giant",
        [427] = "endrega",
        [430] = "harpy",
        [433] = "cockatrice",
        [435] = "gargoyle",
        [437] = "celaeno_harpy",
        [440] = "grave_hag",
        [443] = "fire_elemental",
        [445] = "fogling",
        [447] = "wyvern",
        [450] = "arachas_behemoth",
        [451] = "arachas",
        [452] = "arachas",
        [453] = "arachas",
        [455] = "nekker",
        [456] = "nekker",
        [457] = "nekker",
        [460] = "ekkima",
        [461] = "fleder",
        [462] = "garkain",
        [463] = "bruxa",
        [464] = "katakan",
        [470] = "ghoul",
        [471] = "ghoul",
        [472] = "ghoul",
        [475] = "crone_brewess",
        [476] = "crone_weavess",
        [477] = "crone_whispess",
        [478] = "toad",
        [500] = "crach_an_craite",
        [501] = "hjalmar",
        [502] = "cerys",
        [503] = "ermion",
        [504] = "draig",
        [505] = "holger_blackhand",
        [506] = "madman_lugos",
        [507] = "donar_an_hindar",
        [508] = "udalryk",
        [509] = "birna_bran",
        [510] = "blueboy_lugos",
        [511] = "svanrige",
        [512] = "olaf",
        [513] = "berserker",
        [515] = "young_berserker",
        [517] = "clan_an_craite_warrior",
        [518] = "clan_tordarroch_armorsmith",
        [519] = "clan_heymaey_skald",
        [520] = "light_drakkar",
        [521] = "war_drakkar",
        [522] = "clan_brokvar_archer",
        [523] = "clan_drummond_shieldmaiden",
        [524] = "clan_dimun_pirate",
        [525] = "cock",
        [526] = "clan_drummond_shieldmaiden",
        [527] = "clan_drummond_shieldmaiden",
        [1002] = "foltest_bronze",
        [1003] = "foltest_silver",
        [1004] = "foltest_gold",
        [1005] = "foltest_platinium",
        [2002] = "emhyr_bronze",
        [2003] = "emhyr_silver",
        [2004] = "emhyr_gold",
        [2005] = "emhyr_platinium",
        [3002] = "francesca_bronze",
        [3003] = "francesca_silver",
        [3004] = "francesca_gold",
        [3005] = "francesca_platinium",
        [4002] = "eredin_bronze",
        [4003] = "eredin_silver",
        [4004] = "eredin_gold",
        [4005] = "eredin_platinium",
        [5001] = "king_bran_bronze",
        [5002] = "king_bran_copper",
    };

    /// <summary>
    /// True when a Gwent card counts toward the base-game collection: it has a real card
    /// behind it, is not part of the Skellige deck, and was not added by an expansion.
    /// </summary>
    public static bool IsBaseGameGwentCard(int index, string? faction, out string? type)
    {
        type = null;

        if (index >= 1000 || string.Equals(faction, "skellige", StringComparison.Ordinal))
        {
            return false;
        }

        if (!GwentCardTypes.TryGetValue(index, out string? name) || NonBaseGwentTypes.Contains(name))
        {
            return false;
        }

        type = name;
        return true;
    }
}
