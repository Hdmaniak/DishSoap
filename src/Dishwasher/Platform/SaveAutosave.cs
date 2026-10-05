// ============================================================================
// PORT AUTOSAVE  --  Android lifecycle save hook (revertable).
//
// Why: the Xbox 360 original is *not* continuously autosaving.  It persists
// progress only at explicit in-game moments:
//   * story campaign level complete   Map.cs:2149-2155  Globals.player.Write()
//   * arcade  level complete          Map.cs:2008-2013  Globals.player.Write()
//   * online arcade end               HUD.cs:3245        Globals.player.Write()
//   * arena win -> "Save and Quit"    MainMenu.cs:2846/2854 Globals.player.Write()
//   * backing out of the settings UI  MainMenu.cs:1412/2745 Globals.settings.Write()
//
// On Android the user can background or swipe the app away at any time, so any
// progress since the last level completion would be lost.  Activity1.OnPause /
// OnStop now invoke the game's *own* save routines (no new serialization), so
// the same `profile.sav` / `settings.sav` the game already produces are made
// durable when the app goes to the background.
//
// Revert: delete this file and the two `SaveAutosave.Save(...)` calls in
//         Activity1.cs (OnPause / OnStop).  Nothing else changes.
// ============================================================================
using System;
using System.Threading;

namespace Dishwasher
{
    public static class SaveAutosave
    {
        private static int _busy;

        /// <summary>
        /// Calls the game's existing Player.Write()/Settings.Write() routines.
        /// Safe to call from the Android UI thread (Activity.OnPause/OnStop).
        /// </summary>
        public static void Save(string reason)
        {
            // Re-entrancy guard: OnPause and OnStop can both fire for one
            // background transition.  Player.Write() itself is idempotent.
            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            {
                return;
            }
            try
            {
                // PORT (android settings): persist the Android-only settings
                // tree on background too.  Independent of the game's
                // profile/device guards below, so it always round-trips.
                try { AndroidSettings.Save(); }
                catch (Exception ex) { Log.Exception("autosave.androidsettings", ex); }

                var player = projectDish.Globals.player;

                // Never overwrite a good save before the profile has been read
                // (a fresh Player with default (-1) grades would destroy
                // progress).  Mirrors the guards inside Player.Write().
                if (player == null || player.device == null) return;
                if (projectDish.Globals.trial) return;
                if (projectDish.Globals.deviceFailed) return;
                if (!projectDish.Globals.hasDevice) return;
                if (!projectDish.Globals.initialRead) return;

                try
                {
                    var settings = projectDish.Globals.settings;
                    if (settings != null) settings.Write(player);
                }
                catch (Exception ex) { Log.Exception("autosave.settings", ex); }

                player.Write();

                // Co-op player (only when a second local player is signed in).
                try
                {
                    var coop = projectDish.Globals.coopPlayer;
                    if (coop != null && coop.device != null)
                    {
                        coop.Write(coop: true);
                    }
                }
                catch (Exception ex) { Log.Exception("autosave.coop", ex); }

                Log.Info("[autosave] saved on " + reason
                    + " playSeconds=" + player.playSeconds);
            }
            catch (Exception ex)
            {
                Log.Exception("autosave", ex);
            }
            finally
            {
                Interlocked.Exchange(ref _busy, 0);
            }
        }
    }
}
