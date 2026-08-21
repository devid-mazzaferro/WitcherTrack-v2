/***********************************************************************/
/**  modWitcherTrack  v1.2.1  - reporter for WitcherTrack
/**
/**  Reports what the player has completed, so the tracker can read game
/**  state directly instead of running OCR over the on-screen popups.
/**
/**  This file is additive: it declares new functions and nothing else.
/**  Seven one-line calls into three vanilla files are what actually invoke
/**  them; they are listed in HOOKS.md and each one only appends a log
/**  line. No vanilla behaviour is changed, no value is altered, nothing
/**  is short-circuited, nothing is written to disk.
/**
/**  Output format, one record per line:
/**      WT|v1|<kind>|<id>|<state>[|<extra>...]
/**  where <kind> = meta | quest | diagram | formula | poi | gwent | at
/**
/**  NOTE ON IDENTIFIERS: `state` and `quest` are reserved words in
/**  WitcherScript, so they are never used as parameter or variable
/**  names here even where they would read best.
/**
/**  NOTE ON ENCODING: the game's script compiler expects UTF-16 LE with
/**  exactly one byte-order mark, like the vanilla sources. This file is
/**  the UTF-8 master kept for review; the packaged mod contains the
/**  converted copy.
/***********************************************************************/


/*----------------------------------------------------------------------
  Emitting records
----------------------------------------------------------------------*/

function WT_Emit( kind : string, id : string, stateText : string )
{
	LogChannel( 'WT', "WT|v1|" + kind + "|" + id + "|" + stateText );
}

function WT_Emit2( kind : string, id : string, stateText : string, extra : string )
{
	LogChannel( 'WT', "WT|v1|" + kind + "|" + id + "|" + stateText + "|" + extra );
}

/// Emits a record with a trailing display name. The name is always the last
/// field, so the reader can rejoin anything after the known ones and a name
/// containing a separator cannot corrupt the record.
function WT_EmitNamed( kind : string, id : string, stateText : string, extra : string, displayName : string )
{
	LogChannel( 'WT', "WT|v1|" + kind + "|" + id + "|" + stateText + "|" + extra + "|" + displayName );
}

function WT_Meta( key : string, val : string )
{
	LogChannel( 'WT', "WT|v1|meta|" + key + "|" + val );
}

/// Records where the player was standing when something was completed.
///
/// A point of interest carries its own coordinates, because the game knows where
/// the pin is. Nothing else does: a quest is a journal entry and a Gwent card is
/// an inventory row, and neither has a place attached. The only moment a place
/// can be attached to them is the moment they happen, which is here.
///
/// Emitted as its own record rather than as extra fields on the completion it
/// accompanies, for two reasons. The completion records already have a shape the
/// reader parses positionally, and a dump uses the same shape - so widening them
/// would mean widening the dump too, where a position would be a lie: a dump
/// re-asserts hundreds of entries at once and the player is standing in exactly
/// one place. This record is only ever written live.
///
/// It carries no identifier either, only a place. Saying merely "the player is
/// here, now" and letting the tracker attach it to whatever it sees complete in
/// the same breath is both simpler and more capable: the tracker already works
/// out what newly finished, so a Gwent card gets a position for free even though
/// the sweep that reports cards re-lists the whole collection and cannot say
/// which of them is new.
///
/// The world matters as much as the coordinates. They are local to whichever
/// world is streamed in, so an X/Y without one cannot be placed - see
/// KNOWN-ISSUES.md and the `poi` records, which carry theirs for the same reason.
function WT_EmitAt()
{
	var mm    : CCommonMapManager;
	var areas : array< SAreaMapPinInfo >;
	var here  : Vector;
	var world : string;
	var i     : int;

	if( !thePlayer )
	{
		return;
	}

	mm = theGame.GetCommonMapManager();
	if( !mm )
	{
		return;
	}

	// The world file for the area the player is currently in, taken from the same
	// area list WT_DumpPins walks, so no new way of naming a world is introduced.
	world = "";
	areas = mm.GetAreaMapPins();
	for( i = 0; i < areas.Size(); i += 1 )
	{
		if( areas[ i ].areaType == mm.GetCurrentArea() )
		{
			world = areas[ i ].worldPath;
			break;
		}
	}

	// Without a world the coordinates cannot be placed, so there is nothing worth
	// writing - better a missing position than one attributed to the wrong region.
	if( world == "" )
	{
		return;
	}

	here = thePlayer.GetWorldPosition();

	LogChannel( 'WT', "WT|v1|at|" + FloatToString( here.X )
	                 + "|" + FloatToString( here.Y ) + "|" + world );
}

function WT_StatusToString( s : EJournalStatus ) : string
{
	if( s == JS_Success )  return "done";
	if( s == JS_Active )   return "active";
	if( s == JS_Failed )   return "failed";
	if( s == JS_Inactive ) return "inactive";
	return "unknown";
}

/// Quest category, straight from the engine rather than guessed from the
/// identifier. This is what the tracker groups its counters by.
function WT_QuestTypeToString( t : eQuestType ) : string
{
	if( t == Story )        return "story";
	if( t == Chapter )      return "chapter";
	if( t == Side )         return "side";
	if( t == MonsterHunt )  return "contract";
	if( t == TreasureHunt ) return "treasure";
	return "other";
}

/// Gwent faction. Skellige is the deck Blood and Wine added, so this is
/// what separates the base-game collection - the one the "Collect 'Em All"
/// quest asks for - from the expansion cards.
function WT_GwintFactionToString( f : eGwintFaction ) : string
{
	if( f == GwintFaction_Neutral )        return "neutral";
	if( f == GwintFaction_NothernKingdom ) return "northern";
	if( f == GwintFaction_Nilfgaard )      return "nilfgaard";
	if( f == GwintFaction_Scoiatael )      return "scoiatael";
	if( f == GwintFaction_NoMansLand )     return "monsters";
	if( f == GwintFaction_Skellige )       return "skellige";
	return "unknown";
}

/// Which content pack an entry belongs to. CJournalQuest.GetContentType()
/// is the game's own expansion index - vanilla calls it epIndex and uses
/// it to pick the Hearts of Stone and Blood and Wine quest icons - so the
/// attribution needs no curation on the tracker's side.
function WT_ContentTypeToString( c : EJournalContentType ) : string
{
	var index : int;

	index = (int)c;

	if( index == 0 ) return "base";
	if( index == 1 ) return "hos";
	if( index == 2 ) return "baw";

	return "ep" + IntToString( index );
}


/*----------------------------------------------------------------------
  Quests
  CWitcherJournalManager.GetActivatedOfType('CJournalQuest', out)
  CJournalBase.GetUniqueScriptTag() : name
  CJournalManager.GetEntryStatus(entry) : EJournalStatus

  GetActivatedOfType returns only entries the player has encountered.
  Quests never started simply do not appear, which is correct: the
  denominator comes from the tracker's catalogue and absence means
  not done.
----------------------------------------------------------------------*/

function WT_DumpQuests() : int
{
	var jm         : CWitcherJournalManager;
	var entries    : array< CJournalBase >;
	var questEntry : CJournalQuest;
	var i, n       : int;

	n = 0;
	jm = theGame.GetJournalManager();
	if( !jm )
	{
		WT_Meta( "error", "no journal manager" );
		return 0;
	}

	jm.GetActivatedOfType( 'CJournalQuest', entries );

	for( i = 0; i < entries.Size(); i += 1 )
	{
		questEntry = (CJournalQuest)entries[ i ];
		if( questEntry )
		{
			// Content pack and category come from the engine, so the tracker
			// does not have to infer either from the identifier.
			WT_EmitNamed( "quest",
			              NameToString( questEntry.GetUniqueScriptTag() ),
			              WT_StatusToString( jm.GetEntryStatus( questEntry ) ),
			              WT_ContentTypeToString( questEntry.GetContentType() ) + "|" +
			              WT_QuestTypeToString( questEntry.GetType() ),
			              GetLocStringById( questEntry.GetTitleStringId() ) );
			n += 1;
		}
	}

	return n;
}


/*----------------------------------------------------------------------
  Crafting diagrams and alchemy formulae
  W3PlayerWitcher.GetCraftingSchematicsNames() : array<name>
  W3PlayerWitcher.GetAlchemyRecipes()          : array<name>

  These return what the player knows, so the catalogue does not have to
  be compiled into the mod.
----------------------------------------------------------------------*/

function WT_DumpDiagrams( witcher : W3PlayerWitcher ) : int
{
	var names : array< name >;
	var dm    : CDefinitionsManagerAccessor;
	var i     : int;

	// The player is passed in because the OnSpawned hook runs while the player
	// entity is still being created, and GetWitcherPlayer() is not resolvable
	// yet at that point. Inside OnSpawned the entity is `this`, so the caller
	// hands it over directly. Everywhere else the lookup still works.
	if( !witcher )
	{
		witcher = GetWitcherPlayer();
	}

	if( !witcher )
	{
		WT_Meta( "error", "no player" );
		return 0;
	}

	dm = theGame.GetDefinitionsManager();

	names = witcher.GetCraftingSchematicsNames();
	for( i = 0; i < names.Size(); i += 1 )
	{
		WT_EmitNamed( "diagram", NameToString( names[ i ] ), "done", "",
		              GetLocStringByKeyExt( dm.GetItemLocalisationKeyName( names[ i ] ) ) );
	}

	return names.Size();
}

function WT_DumpFormulae( witcher : W3PlayerWitcher ) : int
{
	var names : array< name >;
	var dm    : CDefinitionsManagerAccessor;
	var i     : int;

	if( !witcher )
	{
		witcher = GetWitcherPlayer();
	}

	if( !witcher )
	{
		WT_Meta( "error", "no player" );
		return 0;
	}

	dm = theGame.GetDefinitionsManager();

	names = witcher.GetAlchemyRecipes();
	for( i = 0; i < names.Size(); i += 1 )
	{
		WT_EmitNamed( "formula", NameToString( names[ i ] ), "done", "",
		              GetLocStringByKeyExt( dm.GetItemLocalisationKeyName( names[ i ] ) ) );
	}

	return names.Size();
}


/*----------------------------------------------------------------------
  Points of interest
  CCommonMapManager.GetAreaMapPins() : array<SAreaMapPinInfo>
  CCommonMapManager.GetEntityMapPins(worldPath) : array<SEntityMapPinInfo>
  IsEntityMapPinDisabled / IsEntityMapPinDiscovered / IsEntityMapPinKnown

  Semantics confirmed in commonMapManager.ws: the vanilla `onlyEnabled`
  filter discards disabled pins, which is how the game itself marks a
  point as exhausted. So disabled means cleared.

  There are thousands of pins, so areaFilter allows dumping one area at
  a time. Use the full dump on demand, not in a polling loop.
----------------------------------------------------------------------*/

function WT_DumpPins( areaFilter : int, everything : bool ) : int
{
	var mm       : CCommonMapManager;
	var areas    : array< SAreaMapPinInfo >;
	var pins     : array< SEntityMapPinInfo >;
	var seen     : array< string >;
	var i, j, n  : int;
	var tag      : name;
	var pinState : string;
	var pos      : Vector;

	n = 0;
	mm = theGame.GetCommonMapManager();
	if( !mm )
	{
		WT_Meta( "error", "no map manager" );
		return 0;
	}

	areas = mm.GetAreaMapPins();

	// Diagnostic: list every area the manager reports, before any filtering
	// or deduplication. Kept even though the mystery it was added for is
	// solved (see below and KNOWN-ISSUES.md) - it is cheap, and it is what
	// proved the area list itself was never the problem.
	WT_Meta( "area_count", IntToString( areas.Size() ) );
	for( i = 0; i < areas.Size(); i += 1 )
	{
		WT_Meta( "area_raw", IntToString( i ) + "|" + IntToString( areas[ i ].areaType ) + "|" + areas[ i ].worldPath );
	}

	for( i = 0; i < areas.Size(); i += 1 )
	{
		// Several area entries share a world, so the same pins would otherwise be
		// reported more than once - roughly half of a full dump was duplicates.
		if( ( areaFilter < 0 || areas[ i ].areaType == areaFilter )
		    && !seen.Contains( areas[ i ].worldPath ) )
		{
			seen.PushBack( areas[ i ].worldPath );

			// Toussaint (dlc\bob\...\bob.w2w) is always present in `areas` above,
			// in every script log checked, regardless of where the player is - but
			// GetEntityMapPins() for it comes back empty until the player has
			// physically streamed that world in at least once this session, then
			// keeps answering. Confirmed from a log with four consecutive full
			// reports in one session: 0, 0, 0, then 229 Toussaint pins the moment
			// the player set foot there. No other region (including everything
			// from Hearts of Stone, which lives inside the base-game world files
			// and so is loaded from the start) showed the same gap. Combined with
			// the resolver treating each full report as a superseding snapshot,
			// this is what makes Toussaint's totals read as 0 before a first visit
			// and drop out again after leaving - see KNOWN-ISSUES.md.
			pins.Clear();
			pins = mm.GetEntityMapPins( areas[ i ].worldPath );

			for( j = 0; j < pins.Size(); j += 1 )
			{
				tag = pins[ j ].entityName;
				pos = pins[ j ].entityPosition;

				if( mm.IsEntityMapPinDisabled( tag ) )
					pinState = "done";
				else if( mm.IsEntityMapPinDiscovered( tag ) )
					pinState = "discovered";
				else if( mm.IsEntityMapPinKnown( tag ) )
					pinState = "known";
				else
					pinState = "not_done";

				// A report on load lists only what is cleared. The tracker reads a
				// framed report as the whole truth, so a pin it does not mention is
				// not done - which is exactly what the omitted ones are, and what
				// keeps loading an earlier save moving progress back down. Listing
				// the other four thousand every load is what made a long run's log
				// grow into hundreds of megabytes. `wt_pins()` and `wt_dump()` still
				// list everything, because building the catalogue needs the entries
				// that are *not* done as much as the ones that are.
				if( !everything && pinState != "done" )
				{
					continue;
				}

				// X/Y trail the DLC-area index so a location can be plotted on a map -
				// two pins sharing (near enough) the same coordinates are the same spot
				// on the ground, whatever their entityType says. Z is left out: nothing
				// here needs elevation, and it would just be one more field to parse.
				//
				// worldPath trails X/Y for the same reason areaType does: the tracker
				// already asks this manager for one world file at a time (see the loop
				// above and KNOWN-ISSUES.md's Toussaint entry), so it already knows the
				// answer - the coordinates themselves reset per streamed world and are
				// not unique across White Orchard, Velen+Novigrad, Skellige and Kaer
				// Morhen, so without this a position alone cannot say which of them a
				// point belongs to.
				WT_EmitNamed( "poi",
				              NameToString( tag ),
				              pinState,
				              NameToString( pins[ j ].entityType ) + "|" + IntToString( areas[ i ].areaType )
				                + "|" + FloatToString( pos.X ) + "|" + FloatToString( pos.Y )
				                + "|" + areas[ i ].worldPath,
				              GetLocStringByKeyExt( StrLower( "map_location_" + NameToString( tag ) ) ) );
				n += 1;
			}
		}
	}

	return n;
}


/*----------------------------------------------------------------------
  Gwent
  CR4GwintManager.GetCardDefs() : array<SCardDefinition>
  CR4GwintManager.HasCardInCollection(index : int) : bool
----------------------------------------------------------------------*/

function WT_DumpGwent( everything : bool ) : int
{
	var gm        : CR4GwintManager;
	var defs      : array< SCardDefinition >;
	var i, n      : int;
	var cardState : string;

	n = 0;
	gm = theGame.GetGwintManager();
	if( !gm )
	{
		WT_Meta( "error", "no gwint manager" );
		return 0;
	}

	defs = gm.GetCardDefs();
	for( i = 0; i < defs.Size(); i += 1 )
	{
		if( gm.HasCardInCollection( defs[ i ].index ) )
			cardState = "done";
		else
			cardState = "not_done";

		// As in WT_DumpPins: on load, only what is owned.
		if( !everything && cardState != "done" )
		{
			continue;
		}

		// The faction is reported so the base-game collection can be counted
		// separately from the Skellige deck.
		WT_Emit2( "gwent",
		          IntToString( defs[ i ].index ),
		          cardState,
		          WT_GwintFactionToString( defs[ i ].faction ) );
		n += 1;
	}

	// What was actually written, not the size of the deck - the counts in the
	// log are there to be checked against the records beside them.
	return n;
}


/*======================================================================
  Hook entry points

  These are what the five inserted calls invoke. WT_OnPlayerSpawned is
  the important one: OnSpawned fires every time the player entity is
  created, which includes every savegame load, so the tracker receives a
  complete snapshot exactly when it needs one - right after the player
  reloads an earlier save. That is the case an OCR-based tracker can
  never recover from.
======================================================================*/

/// Called from W3PlayerWitcher.OnSpawned.
function WT_OnPlayerSpawned( witcher : W3PlayerWitcher )
{
	WT_Meta( "hook", "player_spawned" );

	// The full report, points of interest included. This is the snapshot the
	// tracker uses to resynchronise after a reload, so it has to describe
	// everything - and it means no console or network client is needed to
	// collect map pins.
	WT_ReportFull( witcher );
}

/// Called from CR4HudModuleJournalUpdate.AddQuestUpdate.
function WT_OnQuestUpdate( journalQuest : CJournalQuest )
{
	var jm         : CWitcherJournalManager;
	var tag        : string;
	var statusText : string;

	if( !journalQuest )
	{
		return;
	}

	jm = theGame.GetJournalManager();
	if( !jm )
	{
		return;
	}

	tag = NameToString( journalQuest.GetUniqueScriptTag() );
	statusText = WT_StatusToString( jm.GetEntryStatus( journalQuest ) );

	// The place goes out first, always. The tracker holds the last place it was
	// told and hands it to whatever completes next, so a place reported after the
	// completion arrives one record too late and lands on the following one.
	//
	// Only where a quest *finished*. A quest updates many times as its steps tick
	// over, and all but the last of those happen somewhere that is not where it
	// ends - recording each would leave a trail of places the quest merely passed
	// through.
	if( statusText == "done" )
	{
		WT_EmitAt();
	}

	WT_Emit( "quest", tag, statusText );

	// Clearing a point of interest usually closes a quest entry with it, and
	// nothing else announces that the pin went out. See WT_Sweep.
	WT_Sweep();

	// The Gwent collection quest, kept as a second look rather than as the
	// mechanism. It was the mechanism in v0.11, on the evidence of a savefile
	// where three cards bought mid-session moved nothing until a reload while
	// this quest updated at the moment of purchase - but a full run has since
	// shown that update happens when the quest first goes active and then not
	// again: two updates across a session that went from 26 cards to 35, with
	// every one of those nine arriving only on the next load. WT_OnGwentCardAdded
	// is what reports a card now. This costs nothing on the rare occasion the
	// quest does update, and re-reads the collection when it does.
	//
	// The two collection quests are tagged `Card Game Meta: Gather All` and
	// `cg700_all_cards`, both script tags rather than localised titles, so
	// matching on "card" holds in any language.
	if( StrContains( StrLower( tag ), "card" ) )
	{
		// The sweep cannot say which card is new, but the tracker can, and this is
		// where whoever sold or found it was standing.
		WT_EmitAt();

		WT_SweepOwnedCards();
	}
}

/// Called from CR4Player.AddGwentCard, inside the game's own test that the
/// card name resolved to a real index.
///
/// This is the live signal for a Gwent card. Every other route was tried
/// first: there is no journal notification for a card, the collection quest
/// updates once and then stops (see WT_OnQuestUpdate), and the map-pin sweep
/// has nothing to do with cards. This function is where the game itself
/// decides a card is being added, so there is nowhere earlier to look.
///
/// The index is reported rather than swept, which is the one place this file
/// departs from reading state back. It is not a guess: it is the value the
/// game is about to hand to AddCardToCollection, inside its own check that the
/// name resolved to something. Sweeping the *collection* here would re-list all
/// of it - a hundred and fifty lines late in a run - on every single card, and
/// the report on load re-reads it anyway. The map pins are a different question
/// and are swept, as they are everywhere else; see the call below.
function WT_OnGwentCardAdded( cardIndex : int )
{
	WT_EmitAt();
	WT_Emit( "gwent", IntToString( cardIndex ), "done" );

	// The card is trusted; the map pins still are not. This call was missing
	// because the two were conflated: the paragraph above argues against
	// re-listing the *collection*, and WT_Sweep does not touch the collection -
	// it reads back map pins, exactly as every other live hook does.
	//
	// Without it, a card was the one live event that asked the game nothing. A
	// point of interest cleared with nothing else happening afterwards then had
	// to wait for an unrelated notification, or for the next load, before anyone
	// looked. Reported against a savefile where cards were taken after a bandit
	// camp was cleared and the camp stayed uncounted.
	WT_Sweep();
}

/// Called from CR4HudModuleJournalUpdate.AddCraftingSchematicUpdate.
/// This is the exact function that draws the "New crafting diagram"
/// popup, so it reports what the old OCR pipeline was trying to read off
/// the screen - as an identifier instead of pixels.
function WT_OnCraftingSchematicUpdate( schematicName : name )
{
	WT_EmitAt();
	WT_Emit( "diagram", NameToString( schematicName ), "done" );

	// A diagram is very often what was inside the chest a guarded treasure
	// was guarding, so this is the moment that pin goes out. See WT_Sweep.
	WT_Sweep();
}

/// Called from CR4HudModuleJournalUpdate.AddAlchemySchematicUpdate.
function WT_OnAlchemySchematicUpdate( schematicName : name )
{
	WT_EmitAt();
	WT_Emit( "formula", NameToString( schematicName ), "done" );

	// As above: loot is what marks most points of interest cleared.
	WT_Sweep();
}

/// Sweeps every streamed-in world and reports the points of interest the
/// game now considers cleared.
///
/// This exists because the vanilla map-pin notification cannot report a
/// completion. AddMapPinUpdate is the "a new pin appeared on your map"
/// message: proven against a captured session, it fires with pin *type*
/// names ("BossAndTreasure", "PlaceOfPower") rather than entity names,
/// and only ever with a discovered/known state - never done. There is no
/// vanilla notification for a pin being cleared, and CCommonMapManager is
/// native, so there is nothing else to hook. Reading the state back
/// deliberately is the only way to see it.
///
/// Only cleared pins are emitted, and every cleared pin, wherever it is.
///
/// Restricting this to the player's surroundings was tried and dropped. It is
/// much cheaper, but it assumes a point of interest is reported in the moment
/// it is cleared, and that is not how a route is actually played: a place is
/// passed, left, and finished later, and by then the hook that would report it
/// fires somewhere else entirely. Sweeping everything means it does not matter
/// where the player is when a hook happens to fire - the next one picks up
/// whatever has been cleared since, from anywhere.
///
/// The cost was measured rather than guessed, over every captured session on
/// file: about 155 sweeps across those, which scales to roughly 50-150 MB of
/// log over a full 300% run. That is real, but it is the same order as the
/// game's own debug channels, which are ~85% of the file and not ours to turn
/// off.
function WT_SweepDonePins() : int
{
	var mm       : CCommonMapManager;
	var areas    : array< SAreaMapPinInfo >;
	var pins     : array< SEntityMapPinInfo >;
	var seen     : array< string >;
	var i, j, n  : int;
	var tag      : name;

	n = 0;
	mm = theGame.GetCommonMapManager();
	if( !mm )
	{
		return 0;
	}

	areas = mm.GetAreaMapPins();

	for( i = 0; i < areas.Size(); i += 1 )
	{
		// Same deduplication as WT_DumpPins: several area entries share a
		// world, and asking for one twice reports every pin in it twice.
		if( !seen.Contains( areas[ i ].worldPath ) )
		{
			seen.PushBack( areas[ i ].worldPath );

			pins.Clear();
			pins = mm.GetEntityMapPins( areas[ i ].worldPath );

			for( j = 0; j < pins.Size(); j += 1 )
			{
				tag = pins[ j ].entityName;

				if( !mm.IsEntityMapPinDisabled( tag ) )
				{
					continue;
				}

				WT_Emit( "poi", NameToString( tag ), "done" );
				n += 1;
			}
		}
	}

	return n;
}

/// Reports every Gwent card currently in the collection.
///
/// Same reasoning as WT_SweepDonePins, and the same lack of an alternative:
/// there has never been a notification for gaining a card, so until now the
/// collection was only ever read on load. Buying one does update the
/// "Card Game Meta: Gather All" quest, though, and a quest update is a hook
/// this already runs on - which is what makes reading the collection back at
/// that moment enough, with no new hook into the vanilla scripts.
///
/// The whole deck is a couple of hundred definitions, so this is far cheaper
/// than the pin sweep beside it. Only owned cards are emitted, in the same
/// shape the full dump uses, so nothing downstream has to tell the two apart.
function WT_SweepOwnedCards() : int
{
	var gm    : CR4GwintManager;
	var defs  : array< SCardDefinition >;
	var i, n  : int;

	n = 0;
	gm = theGame.GetGwintManager();
	if( !gm )
	{
		return 0;
	}

	defs = gm.GetCardDefs();
	for( i = 0; i < defs.Size(); i += 1 )
	{
		if( gm.HasCardInCollection( defs[ i ].index ) )
		{
			WT_Emit2( "gwent",
			          IntToString( defs[ i ].index ),
			          "done",
			          WT_GwintFactionToString( defs[ i ].faction ) );
			n += 1;
		}
	}

	return n;
}

/// Everything the live hooks read back rather than are told about. Kept as
/// one call so the hooks below stay a single line and anything added later
/// lands in one place.
function WT_Sweep()
{
	WT_SweepDonePins();
}

/// Called from W3EffectManager.OnBuffAdded, from inside the block the game
/// itself already guards with `owner == thePlayer && IsBuffShrine(effectType)`.
///
/// Places of Power were the one thing the other hooks could never see. Using
/// one grants an ability point and nothing else: no quest step, no diagram, no
/// formula, no pin notification - so no hook ran, and the pin stayed unreported
/// until the next load. It does, however, apply a buff, and the game names
/// those per sign - W3Effect_ShrineAxii and its four siblings, caught in a
/// captured session at the moment of use.
///
/// Hooking the game's own player-and-shrine test rather than testing for it
/// here means this cannot fire on anything else, and there is no effect-type
/// list of ours to fall out of date. One sweep per Place of Power.
function WT_OnShrineBuff()
{
	WT_Sweep();
}

/// Called from W3PlayerWitcher.Meditate, once meditation has actually begun.
///
/// This one reports nothing of its own: it is a heartbeat. Every other live
/// hook is a notification about something else that happens to prompt a
/// re-read, so a point of interest the game has already put out waits for the
/// next unrelated thing to happen before anyone asks. Meditation is frequent,
/// and it happens in the gaps - after a camp is cleared, before whatever comes
/// next - which is exactly where that wait is longest.
///
/// Hooked at the beginning of meditation rather than at its end, because the
/// end lives in the meditation state files, which this mod does not ship, and
/// the one end-of-meditation callback that does live in playerWitcher.ws
/// (MeditationRestoring) cannot be shown from the files here to run only once
/// per meditation. A hook that might fire per simulated hour would put a full
/// sweep in the log for each of them.
function WT_OnMeditation()
{
	WT_Sweep();
}

/// Called from CR4HudModuleJournalUpdate.AddMapPinUpdate.
/// A pin notification means the pin changed; the authoritative state is
/// read back from the map manager rather than assumed.
function WT_OnMapPinUpdate( mapPinName : name )
{
	var mm       : CCommonMapManager;
	var pinState : string;

	mm = theGame.GetCommonMapManager();
	if( !mm )
	{
		return;
	}

	if( mm.IsEntityMapPinDisabled( mapPinName ) )
		pinState = "done";
	else if( mm.IsEntityMapPinDiscovered( mapPinName ) )
		pinState = "discovered";
	else
		pinState = "known";

	WT_Emit( "poi", NameToString( mapPinName ), pinState );

	// The name above is usually a pin *type*, not an entity, so the line just
	// emitted rarely matches anything the catalogue holds - it is kept because
	// it costs nothing and is what proved that. The sweep is what actually
	// reports a cleared pin.
	WT_Sweep();
}


/*======================================================================
  exec functions - callable from the debug console, or remotely with
  rw3d_cli when the game is launched with -net:

      rw3d_cli.exe exec "wt_ping()"
      rw3d_cli.exe exec "wt_dump()"
======================================================================*/

exec function wt_ping()
{
	WT_Meta( "ping", "modWitcherTrack v1.2.1" );
}

exec function wt_quests()
{
	WT_Meta( "begin", "quests" );
	WT_Meta( "count_quests", IntToString( WT_DumpQuests() ) );
	WT_Meta( "end", "quests" );
}

exec function wt_crafting()
{
	WT_Meta( "begin", "crafting" );
	WT_Meta( "count_diagrams", IntToString( WT_DumpDiagrams( GetWitcherPlayer() ) ) );
	WT_Meta( "count_formulae", IntToString( WT_DumpFormulae( GetWitcherPlayer() ) ) );
	WT_Meta( "end", "crafting" );
}

exec function wt_pins( optional areaFilter : int )
{
	if( areaFilter == 0 )
		areaFilter = -1;

	WT_Meta( "begin", "pins" );
	WT_Meta( "count_pins", IntToString( WT_DumpPins( areaFilter, true ) ) );
	WT_Meta( "end", "pins" );
}

exec function wt_gwent()
{
	WT_Meta( "begin", "gwent" );
	WT_Meta( "count_gwent", IntToString( WT_DumpGwent( true ) ) );
	WT_Meta( "end", "gwent" );
}

/// Reports every point of interest the game currently considers cleared and
/// every Gwent card in the collection, and prints how many of each that was.
/// This is the sweep the live hooks run; calling it by hand is how to check
/// whether a pin has gone out or a card has landed yet, without reloading.
exec function wt_sweep()
{
	WT_Meta( "count_done_pins", IntToString( WT_SweepDonePins() ) );
	WT_Meta( "count_owned_cards", IntToString( WT_SweepOwnedCards() ) );
}

/// Full snapshot. Not for tight polling: there are thousands of pins.
exec function wt_dump()
{
	WT_ReportFull( GetWitcherPlayer() );
}

/// Everything except points of interest. Cheap enough to run on load.
exec function wt_light()
{
	WT_ReportLight( GetWitcherPlayer() );
}


/*----------------------------------------------------------------------
  Report bodies.

  These are kept separate from the exec functions above so that the hook
  entry points call an ordinary function. Whether one exec function may
  call another is not worth relying on, and this costs nothing.
----------------------------------------------------------------------*/

function WT_ReportFull( witcher : W3PlayerWitcher )
{
	WT_Meta( "begin", "full" );
	WT_Meta( "count_quests",   IntToString( WT_DumpQuests()   ) );
	WT_Meta( "count_diagrams", IntToString( WT_DumpDiagrams( witcher ) ) );
	WT_Meta( "count_formulae", IntToString( WT_DumpFormulae( witcher ) ) );
	WT_Meta( "count_gwent",    IntToString( WT_DumpGwent( false ) ) );
	WT_Meta( "count_pins",     IntToString( WT_DumpPins( -1, false ) ) );
	WT_Meta( "end", "full" );
}

function WT_ReportLight( witcher : W3PlayerWitcher )
{
	WT_Meta( "begin", "light" );
	WT_Meta( "count_quests",   IntToString( WT_DumpQuests()   ) );
	WT_Meta( "count_diagrams", IntToString( WT_DumpDiagrams( witcher ) ) );
	WT_Meta( "count_formulae", IntToString( WT_DumpFormulae( witcher ) ) );
	WT_Meta( "count_gwent",    IntToString( WT_DumpGwent( false ) ) );
	WT_Meta( "end", "light" );
}
