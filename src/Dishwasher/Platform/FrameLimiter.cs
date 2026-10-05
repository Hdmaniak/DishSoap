// ============================================================================
// PORT (frame limiter) --  applies the Android-only FPS LOCK setting to the
// MonoGame game loop.
//
// The game computes its per-frame delta from `gameTime.ElapsedGameTime`
// (Game1.cs:1332 `Globals.frameTime = (float)gameTime.ElapsedGameTime.TotalSeconds`),
// so it is NOT tied to a hard-coded 1/60 constant: it is already a fixed-time
// -step game (MonoGame default IsFixedTimeStep=true, TargetElapsedTime=1/60).
// Changing `TargetElapsedTime` therefore changes the *rate* at which the world
// advances while each step still advances the correct amount of time, i.e. the
// game runs choppier at 30, it does not run in slow motion.
//
//   fpsLock 0 (Unlimited) -> IsFixedTimeStep = false  (variable step; on
//                            Android the EGL swap still vsyncs at 60 Hz).
//   fpsLock 30 / 60 / 120  -> IsFixedTimeStep = true, TargetElapsedTime = 1/fps
//
// Applied from Game1.Initialize (startup) and from AndroidSettingsActivate
// (immediately on change).  Revert: delete this file + the two hooks.
// ============================================================================
using System;
using Microsoft.Xna.Framework;

namespace Dishwasher
{
    public static class FrameLimiter
    {
        private static Game _game;
        private static int _applied = -1;

        /// <summary>Remember the running Game and apply the stored lock now.</summary>
        public static void Attach(Game game)
        {
            _game = game;
            _applied = -1;
            Apply();
        }

        /// <summary>Bring the game loop in line with AndroidSettings.FpsLock.
        /// Safe to call at any time (startup or while the menu is open).</summary>
        public static void Apply()
        {
            if (_game == null) return;

            int state = AndroidSettings.FpsLock;
            if (state == _applied) return; // avoid repeated TargetElapsedTime sets

            try
            {
                switch (state)
                {
                    case AndroidSettings.FPS_30:
                        SetFixed(30);
                        break;
                    case AndroidSettings.FPS_60:
                        SetFixed(60);
                        break;
                    case AndroidSettings.FPS_120:
                        SetFixed(120);
                        break;
                    default:
                        // Unlimited: variable step, no artificial cap.  The
                        // Android EGL swap (vsync) may still cap this at the
                        // panel refresh rate (~60 Hz on the A52).
                        _game.IsFixedTimeStep = false;
                        Log.Info("[fpslimit] unlimited (IsFixedTimeStep=false,"
                            + " target=" + _game.TargetElapsedTime.TotalMilliseconds.ToString("0.##") + "ms)");
                        break;
                }
                _applied = state;
            }
            catch (Exception ex)
            {
                Log.Exception("fpslimit.apply", ex);
            }
        }

        private static void SetFixed(int fps)
        {
            // Set the target before flipping IsFixedTimeStep so the loop never
            // observes a fresh fixed step with a stale target.
            _game.TargetElapsedTime = TimeSpan.FromSeconds(1.0 / fps);
            _game.IsFixedTimeStep = true;
            Log.Info("[fpslimit] fixed " + fps + " fps (TargetElapsedTime="
                + _game.TargetElapsedTime.TotalMilliseconds.ToString("0.###") + "ms)");
        }
    }
}
