/***********************************************************************/
/**  modWitcherTrack - game-version layer for 5.0 and later
/**
/**  The reporter, witchertrack.ws, is one file for every game version.
/**  The few calls whose shape changed between 4.04 and 5.0 live here,
/**  behind functions with the same signatures as their 4.04 twins in
/**  compat-4.04.ws, and patch_mod.py ships whichever matches the scripts
/**  it patched, as local/witchertrack_compat.ws. WitcherScript has no
/**  preprocessor, so this is the only way to keep a single reporter.
/**
/**  Two things changed shape in 5.0, and both are kept to the numbers
/**  4.04 reported, so a log from either version means the same thing:
/**
/**   - an area is a name ('AN_Velen') where it was an EAreaName integer:
/**     SAreaMapPinInfo.areaType : int became areaName : name, and
/**     CCommonMapManager.GetCurrentArea() returns that name;
/**   - a Gwent card is keyed by name: SCardDefinition lost its `index`
/**     field, HasCardInCollection takes the card's name, and the index
/**     comes from CR4GwintManager.GetIndex( card ).
/***********************************************************************/


/// Which build of the reporter this is, for the `ping` record.
function WT_GameTarget() : string
{
	return "5.0";
}


/*----------------------------------------------------------------------
  Gwent
  CR4GwintManager.GetIndex(card : SCardDefinition) : int
  CR4GwintManager.HasCardInCollection(cardName : name) : bool
  SCardDefinition.cardName : name

  The records keep carrying the index, because that is what the tracker
  folds into a card type (GameData.GwentCardTypes) and what run.json
  already holds. GetIndex() is the number the game itself hands the card
  art as the card's "index" (gwintBaseMenu.ws), and the `index` attribute
  of def_gwint_cards_final.xml, unchanged from 4.04 for every card the
  catalogue counts.
----------------------------------------------------------------------*/

function WT_CardIndex( gm : CR4GwintManager, card : SCardDefinition ) : int
{
	return gm.GetIndex( card );
}

function WT_OwnsCard( gm : CR4GwintManager, card : SCardDefinition ) : bool
{
	return gm.HasCardInCollection( card.cardName );
}


/*----------------------------------------------------------------------
  Areas
  CCommonMapManager.GetCurrentArea() : name
  CCommonMapManager.GetDeprecatedAreaName(area : int) : name
  SAreaMapPinInfo.areaName : name
----------------------------------------------------------------------*/

/// The number the records have always carried for an area entry - see
/// WT_AreaIndex for how it is recovered from 5.0's name.
function WT_AreaNumber( mm : CCommonMapManager, area : SAreaMapPinInfo ) : int
{
	return WT_AreaIndex( mm, area.areaName );
}

/// Whether the player is in this area entry right now. Both sides are names,
/// with Toussaint's two spellings folded together first, because vanilla
/// itself accepts either - see WT_CanonicalArea.
function WT_IsCurrentArea( mm : CCommonMapManager, area : SAreaMapPinInfo ) : bool
{
	return WT_CanonicalArea( area.areaName ) == WT_CanonicalArea( mm.GetCurrentArea() );
}

/// Toussaint answers to two names. Wherever vanilla asks which area it is in, it
/// tests for both 'AN_Dlc_Bob' and 'AN_Bob' (sound.ws, journalQuestMenu.ws,
/// mapMenu.ws), and CCommonMapManager.GetDeprecatedAreaName knows only the second,
/// so the first is folded into it before any comparison. Every other name passes
/// through untouched.
function WT_CanonicalArea( areaName : name ) : name
{
	if( areaName == 'AN_Dlc_Bob' )
	{
		return 'AN_Bob';
	}

	return areaName;
}

/// The number an area was reported under before 5.0, recovered from its name.
///
/// Up to 4.04 SAreaMapPinInfo carried `areaType : int` and GetCurrentArea()
/// returned the same EAreaName integer. 5.0 replaced both with names - 'AN_Velen',
/// 'AN_Bob'. The `poi` records and the `area_raw` diagnostic keep the integer,
/// because the tracker reads it as one (CatalogBuilder attributes every pin under
/// area 11 to Blood and Wine), and a log from either version has to mean the same.
///
/// CCommonMapManager.GetDeprecatedAreaName( int ) is the game's own table from the
/// old integers to the new names - it is how quest conditions authored against
/// EAreaName are still evaluated (questWorld.ws) - and it answers '' past its last
/// entry. Walking it until it runs out inverts it with no copy of ours to drift.
///
/// A name the table does not know, such as an area 5.0 added, comes back as -1: a
/// full dump still includes it, no positive area filter selects it, the tracker
/// attributes it to no content pack, and the world path printed beside it still
/// says which world it is.
function WT_AreaIndex( mm : CCommonMapManager, areaName : name ) : int
{
	var wanted    : name;
	var candidate : name;
	var i         : int;

	wanted = WT_CanonicalArea( areaName );

	i = 0;
	candidate = mm.GetDeprecatedAreaName( i );
	while( candidate != '' )
	{
		if( candidate == wanted )
		{
			return i;
		}

		i += 1;
		candidate = mm.GetDeprecatedAreaName( i );
	}

	return -1;
}
