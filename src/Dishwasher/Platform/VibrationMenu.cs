// ============================================================================
// PORT (rumble): the game's OWN options row ("HELP & OPTIONS > SETTINGS",
// MainMenu case 34, row 1) now cycles the four vibration routing modes
// OFF / PHONE / CONTROLLER / BOTH instead of the original ON/OFF.
//
// Mapping to the game's own field (kept consistent with its logic):
//   OFF        <=> Settings.norumble = true
//   PHONE      <=> Settings.norumble = false  AND  AndroidSettings.Vibration = PHONE
//   CONTROLLER <=> Settings.norumble = false  AND  AndroidSettings.Vibration = CONTROLLER
//   BOTH       <=> Settings.norumble = false  AND  AndroidSettings.Vibration = BOTH
//
// The Android target is persisted in android_settings.sav (default BOTH for
// old saves); Settings.norumble continues to persist in the game's own
// settings.sav exactly as before. This is a partial-class extension so the
// original MainMenu.cs only holds the two small, marked call sites.
//
// Revert: delete this file and restore the two `// PORT rumble` sites in
// GameSource/projectDish/MainMenu.cs.
// ============================================================================
using Dishwasher;

namespace projectDish
{
    public partial class MainMenu
    {
        private StringContainer[] _vibModeStr;

        private void EnsureVibrationText()
        {
            if (_vibModeStr != null) return;
            // Order must match AndroidSettings.VIB_* constants.
            _vibModeStr = new StringContainer[AndroidSettings.VIB_STATES]
            {
                new StringContainer("vibration: off"),
                new StringContainer("vibration: phone"),
                new StringContainer("vibration: controller"),
                new StringContainer("vibration: both")
            };
        }

        /// <summary>Combined UI state: game OFF, or the Android target when the
        /// game allows vibration.</summary>
        private int CurrentVibrationState(Settings settings)
        {
            if (settings != null && settings.norumble) return AndroidSettings.VIB_OFF;
            int v = AndroidSettings.Vibration;
            // VIB_OFF is not a valid "on" target; treat a stray 0 as the default.
            if (v == AndroidSettings.VIB_OFF) v = AndroidSettings.VIB_DEFAULT;
            return v;
        }

        /// <summary>Label for options row 1.</summary>
        private StringContainer VibrationLabel(Settings settings)
        {
            EnsureVibrationText();
            return _vibModeStr[CurrentVibrationState(settings)];
        }

        /// <summary>Cycle OFF -> PHONE -> CONTROLLER -> BOTH for this player's
        /// Settings, persist the Android target and apply immediately.</summary>
        private void CycleVibration(Settings settings)
        {
            int next = (CurrentVibrationState(settings) + 1) % AndroidSettings.VIB_STATES;
            if (settings != null)
            {
                if (next == AndroidSettings.VIB_OFF)
                {
                    settings.norumble = true;
                }
                else
                {
                    settings.norumble = false;
                    AndroidSettings.SetVibration(next);
                }
            }
            AndroidRumble.ApplyMode();
            Log.Info("[rumble] game-row state=" + next
                + " norumble=" + (settings != null && settings.norumble)
                + " target=" + AndroidSettings.Vibration);
        }
    }
}
