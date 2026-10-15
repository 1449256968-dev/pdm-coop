using System.Collections.Generic;
using UnityEngine.Events;
using UnityEngine.UI;

public static class CoopRoomSettingsGuard
{
    private static readonly HashSet<string> GeneralRoomMethods = new HashSet<string>
    {
        "UpdateGameDifficulty",
        "UpdatePlayerMovementSpeedMultiplier",
        "UpdatePlayerAllyMovementSpeedMultiplier",
        "UpdatePlayerEnemyMovementSpeedMultiplier",
        "UpdatePlayerNeutralMovementSpeedMultiplier",
        "UpdateAllMovementSpeedMultiplier",
        "UpdateCombatDifficulty",
        "UpdateDifficultyIncreaseMultiplier",
        "UpdatePlayerStatsMultiplier",
        "UpdatePauseOnTF",
        "UpdatePauseOnNearbyTF",
        "UpdateNeverEndGame",
        "UpdatePlayerMonsterAutoEscape",
        "UpdatePlayerHasTFPriority",
        "UpdateUseModForms",
        "UpdateUseAltWorkerBeeImageset",
        "UpdateUseAltFrogImageset",
        "UpdateUseBimboAltYuantiImageset",
        "UpdateUseDryadAltImageset",
        "UpdateUseCheerleaderAltImageset",
        "UpdateUseNudeLithositeHostsImageset",
        "UpdateUseAltFallenMagicalGirlImageset",
        "UpdateUseAltProgenitorCloneImageset",
        "UpdateFavourUnseenContent",
        "UpdateContentCyclingMode",
        "UpdateIncludeGenericImageSet",
        "UpdateDisableMaleCultist",
        "UpdateTomboyRabbitPrince",
        "UpdateUseOldCupidImages",
        "UpdateDisableAIDashing",
        "UpdateImageSetEnabled",
        "UpdateFallenCupidThrallImagesetSelection",
        "UpdateRealStatueImagesetSelection",
        "UpdateLivingStatueImagesetSelection",
        "UpdateLadyStatueImagesetSelection"
    };

    public static bool IsRoomControl(Selectable control)
    {
        UnityEventBase callbacks = null;
        if (control is Slider) callbacks = ((Slider)control).onValueChanged;
        else if (control is Toggle) callbacks = ((Toggle)control).onValueChanged;
        else if (control is Dropdown) callbacks = ((Dropdown)control).onValueChanged;
        else if (control is InputField) callbacks = ((InputField)control).onValueChanged;
        else if (control is Button) callbacks = ((Button)control).onClick;
        if (callbacks == null) return false;
        for (int i = 0; i < callbacks.GetPersistentEventCount(); i++)
            if (callbacks.GetPersistentTarget(i) is GeneralSettingsUI &&
                GeneralRoomMethods.Contains(callbacks.GetPersistentMethodName(i))) return true;
        return false;
    }
}
