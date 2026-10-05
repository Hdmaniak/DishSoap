// ============================================================================
// PORT (exit)  --  make the EXIT menu entry actually terminate on Android.
//
// MonoGame's Android platform implements Game.Exit() as:
//     ((Activity)Game.Activity).MoveTaskToBack(true);   // AndroidGamePlatform.Exit
// i.e. selecting EXIT only sends the task to the background. The Activity and
// its (stopped) MonoGame loop stay alive; because Activity1 is
// LaunchMode.SingleInstance with AlwaysRetainTaskState, relaunching returns to
// that same live task -- the stale "already loaded" screen instead of a cold
// start.
//
// EXIT now persists progress through the game's own save routines (the same
// Player.Write()/Settings.Write() the 360 build uses) and then finishes +
// removes the task and stops the process, so the next launch is a genuine cold
// start. The save is synchronous and completes before the kill, so skipping the
// normal OnPause/OnStop callbacks at this point is safe.
//
// Revert: delete this file and restore `case 4: Exit();` in Game1.cs.
// ============================================================================
using System;
using Android.OS;
using Microsoft.Xna.Framework;

namespace Dishwasher
{
    internal static class PortExit
    {
        public static void Quit()
        {
            // 1. Persist first, via the existing autosave path (synchronous).
            SaveAutosave.Save("Exit");

            // 2. Never leave a motor held while we go away.
            try { AndroidRumble.StopAll(); } catch (Exception ex) { Log.Exception("exit.rumble", ex); }

            var activity = Game.Activity;
            if (activity == null)
            {
                Log.Error("[exit] no Activity; cannot finish task");
                return;
            }

            Log.Info("[exit] EXIT selected: save done, finishing task pid="
                + Process.MyPid());

            // Finish on the UI thread, then stop the process so the task cannot
            // be resumed from a cached state on the next launch.
            activity.RunOnUiThread(() =>
            {
                try
                {
                    if (!activity.IsFinishing)
                    {
                        if (Build.VERSION.SdkInt >= BuildVersionCodes.Lollipop)
                        {
                            activity.FinishAndRemoveTask();
                        }
                        else
                        {
                            activity.Finish();
                        }
                    }
                }
                catch (Exception ex) { Log.Exception("exit.finish", ex); }

                try { Process.KillProcess(Process.MyPid()); }
                catch (Exception ex) { Log.Exception("exit.kill", ex); }
            });
        }
    }
}
