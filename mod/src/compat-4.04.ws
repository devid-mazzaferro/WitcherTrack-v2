/***********************************************************************/
/**  modWitcherTrack - game-version layer for 4.04
/**
/**  The reporter, witchertrack.ws, is one file for every game version.
/**  The few calls whose shape changed between 4.04 and 5.0 live here,
/**  behind functions with the same signatures as their 5.0 twins in
/**  compat-5.0.ws, and patch_mod.py ships whichever matches the scripts
/**  it patched, as local/witchertrack_compat.ws. WitcherScript has no
/**  preprocessor, so this is the only way to keep a single reporter.
/**
/**  Every body below is the expression the reporter used inline up to
/**  v1.2.2, which is what ran on 4.04: an area is an EAreaName integer,
/**  and a Gwent card carries its own `index`.
/***********************************************************************/


/// Which build of the reporter this is, for the `ping` record.
function WT_GameTarget() : string
{
	return "4.04";
}


/*----------------------------------------------------------------------
  Gwent
  SCardDefinition.index : int
  CR4GwintManager.HasCardInCollection(index : int) : bool
----------------------------------------------------------------------*/

function WT_CardIndex( gm : CR4GwintManager, card : SCardDefinition ) : int
{
	return card.index;
}

function WT_OwnsCard( gm : CR4GwintManager, card : SCardDefinition ) : bool
{
	return gm.HasCardInCollection( card.index );
}


/*----------------------------------------------------------------------
  Areas
  SAreaMapPinInfo.areaType : int
  CCommonMapManager.GetCurrentArea(), compared with areaType
----------------------------------------------------------------------*/

function WT_AreaNumber( mm : CCommonMapManager, area : SAreaMapPinInfo ) : int
{
	return area.areaType;
}

function WT_IsCurrentArea( mm : CCommonMapManager, area : SAreaMapPinInfo ) : bool
{
	return area.areaType == mm.GetCurrentArea();
}
