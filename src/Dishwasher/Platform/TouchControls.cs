// ============================================================================
// PORT (touch) -- MENU touch controls for the Android port.
//
// The game only reads Microsoft.Xna.Framework.Input.GamePad. There is no
// touch path in the original XNA 3.0 title. This class turns MotionEvents the
// Activity receives into the same *gamepad button edges* the menus already
// understand (via AndroidInputBridge, which drives MonoGame's internal
// AndroidGamePad). No game logic is changed.
//
// Gesture map (menus only, no controller required):
//   * Tap on a menu ROW  -> move the cursor to that row and press A (activate
//                           it). MenuTouchTargets records each row's rect at
//                           draw time; a tap is hit-tested here. Tapping BACK
//                           goes back like any other row.
//   * Tap on empty space -> nothing (no row hit), so stray taps do not fire.
//   * Tap on a screen with no rows (title / intro / message box) -> A confirm,
//                           preserving the old tap==A for those screens.
//   * Swipe up / down    -> D-pad up / down  (secondary cursor movement)
//   * Swipe left / right -> D-pad left/right
//   * Two-finger tap     -> B / cancel       (Buttons.B -> character.keyGrab)
//   * Android BACK key / gesture -> B / cancel (Activity1 DispatchKeyEvent
//                                                 keycode 4 + OnBackPressed)
//
// Scope: this class OWNS the menu gestures; active-gameplay touches are
// delegated to Platform/TouchGamepadOverlay.cs (the optional on-screen pad),
// and only when Game1.portGameplayActive is true.  Outside gameplay and menus
// touches are ignored.
//
// Synthesis produces a short *pulse* (a few game frames) of press then release
// so each gesture is exactly one clean down/up edge. The menus use edge
// detection, so one pulse == one navigation and there is no auto-repeat.
//
// The synthetic pad itself (so GamePad.GetState(0).IsConnected is true with no
// controller) lives in AndroidInputBridge.InitializeTouchPad(); this class only
// owns the gesture recognition and the pulsed button state.
//
// Revert: delete this file, remove the TouchControls hooks in Activity1.cs and
// the single hook in Game1.Update.
// ============================================================================
using System;
using Microsoft.Xna.Framework.Input;

namespace Dishwasher
{
    public static class TouchControls
    {
        // Master switch. Android-only; no per-frame cost when false.
        public static bool Enabled = true;

        /// <summary>Log raw/logical touch coordinates (coordinate-space check).</summary>
        public static bool Verbose = false;

        // A pulse is held for this many game frames. 2+ guarantees the game's
        // per-frame poll sees a down edge and then a release, even if a frame is
        // dropped; because the menus are edge-triggered it still moves once.
        private const int PulseFrames = 3;

        // ---- pulsed button state (only the buttons we ever synthesize) ----
        private static int _aFrames, _bFrames, _upFrames, _downFrames, _leftFrames, _rightFrames;

        // PORT (touchpad): comic SPEED UP is a HOLD (A stays pressed while the
        // finger is down); SKIP is a tap (one B edge).  Only active when TOUCH
        // CONTROLS is ON.
        private static bool _comicHoldA;
        private static int _comicPtr = -1;

        // ---- gesture state ----
        private const float SwipeThresholdLogical = 55f; // logical (game px) travel
        private const float TapSlopLogical = 45f;        // max travel for a tap
        private const long TapMaxMs = 400;               // max duration for a tap
        private const long BackDebounceMs = 150;         // ignore double-back

        private static float _startX, _startY;
        private static long _downTime;
        private static bool _active;
        private static bool _multi;
        private static bool _swipeFired;
        private static long _lastBackMs = -10000;

        // ---- direct tap-to-activate (PORT) ----
        // On a tap the UI thread parks the logical tap point here; the game
        // thread (Tick) hit-tests it against the row rects MenuTouchTargets
        // recorded during the last menu Draw and, on a hit, asks MainMenu.Update
        // to select + activate that row.
        private static readonly object _tapLock = new object();
        private static bool _tapPending;
        private static float _tapX, _tapY;

        public static void Initialize()
        {
            AndroidInputBridge.InitializeTouchPad();
            TouchGamepadOverlay.Initialize(); // PORT (touchpad)
            Log.Info("[touch] menu controls initialised, verbose=" + Verbose);
        }

        // ------------------------------------------------------- menu context
        // true when the game is NOT in active gameplay. Game1.gameMode: 4 =
        // loader/boot, 1+ = menus/screens, 0 = in-game (Game1.cs:51, 829, 898,
        // 1048). A paused game (gameMode 0 + Globals.paused) is a menu too.
        private static bool MenuContext()
        {
            return projectDish.Game1.gameMode != 0 || projectDish.Globals.paused;
        }

        // ---------------------------------------------------------------- pulses
        private static Buttons Current()
        {
            Buttons b = Buttons.None;
            if (_aFrames > 0 || _comicHoldA) b |= Buttons.A;
            if (_bFrames > 0) b |= Buttons.B;
            if (_upFrames > 0) b |= Buttons.DPadUp;
            if (_downFrames > 0) b |= Buttons.DPadDown;
            if (_leftFrames > 0) b |= Buttons.DPadLeft;
            if (_rightFrames > 0) b |= Buttons.DPadRight;
            return b;
        }

        private static void Push()
        {
            AndroidInputBridge.SetTouchButtons(Current());
        }

        private static void Pulse(Buttons b)
        {
            if ((b & Buttons.A) != 0) _aFrames = PulseFrames;
            if ((b & Buttons.B) != 0) _bFrames = PulseFrames;
            if ((b & Buttons.DPadUp) != 0) _upFrames = PulseFrames;
            if ((b & Buttons.DPadDown) != 0) _downFrames = PulseFrames;
            if ((b & Buttons.DPadLeft) != 0) _leftFrames = PulseFrames;
            if ((b & Buttons.DPadRight) != 0) _rightFrames = PulseFrames;
            Push();
            if (Verbose) Log.Info("[touch] pulse " + b);
        }

        // Called once per game frame from Game1.Update (PORT hook).
        public static void Tick()
        {
            if (!Enabled) return;
            TouchGamepadOverlay.Tick(); // PORT (touchpad): gameplay overlay
            ProcessPendingTap(); // PORT (touch): direct tap-to-activate

            // PORT (touchpad): safety -- never leave A held if the comic ends
            // while a SPEED UP hold is still active.
            if (!projectDish.Game1.portComicPlaying && _comicHoldA)
            {
                _comicHoldA = false;
                Push();
            }

            bool changed = false;
            if (_aFrames > 0) { _aFrames--; changed = true; }
            if (_bFrames > 0) { _bFrames--; changed = true; }
            if (_upFrames > 0) { _upFrames--; changed = true; }
            if (_downFrames > 0) { _downFrames--; changed = true; }
            if (_leftFrames > 0) { _leftFrames--; changed = true; }
            if (_rightFrames > 0) { _rightFrames--; changed = true; }
            if (changed) Push();
        }

        // ------------------------------------------------- tap-to-activate (PORT)
        // Runs on the game thread from Tick(). Takes the most recent tap point
        // and hit-tests it against the row rectangles recorded by the last menu
        // Draw:
        //   * gameMode == 1 (MainMenu is the active screen) and a row is hit
        //                               -> stash its index; MainMenu.Update will
        //                                  move selOption there and raise the
        //                                  keyJump edge (cursor move + A).
        //   * gameMode == 1, no row hit -> ignore (stray tap does not misfire).
        //   * gameMode == 1 but the screen recorded no rows at all (title /
        //     intro / message box) -> legacy tap == A confirm.
        //   * gameMode != 1 (e.g. a paused gameplay screen whose pause menu is
        //     not drawn through MainMenu.drawOption) -> legacy tap == A.
        private static void ProcessPendingTap()
        {
            // Never let a row hit linger past the frame it was produced in.
            MenuTouchTargets.SetPendingRow(-1);

            float x, y;
            bool pending;
            lock (_tapLock)
            {
                pending = _tapPending;
                _tapPending = false;
                x = _tapX;
                y = _tapY;
            }
            if (!pending) return;

            // MainMenu.Update/Draw only run in gameMode 1, so only then are the
            // recorded row rectangles meaningful for row selection.
            bool menuScreen = projectDish.Game1.gameMode == 1;
            int row;
            if (menuScreen && MenuTouchTargets.HitTest(x, y, out row))
            {
                MenuTouchTargets.SetPendingRow(row);
                // PORT (touch): include the logical tap point so the supervised
                // run can verify the canvas<->screen mapping from logcat.
                Log.Info("[touch] tap -> select row " + row
                    + " (logical " + x.ToString("0") + "," + y.ToString("0")
                    + " physical " + (x * WidescreenConfig.Scale).ToString("0")
                    + "," + (y * WidescreenConfig.Scale).ToString("0") + ")");
            }
            else if (!menuScreen || MenuTouchTargets.Count == 0)
            {
                Pulse(Buttons.A); // non-row screen: legacy tap == A confirm
            }
            else if (Verbose)
            {
                Log.Info("[touch] tap ignored (no row at "
                    + x.ToString("0") + "," + y.ToString("0") + ")");
            }
        }

        // Called on the UI thread when a tap is recognised. Stores the logical
        // (game-pixel) tap point for ProcessPendingTap on the game thread. The
        // point is the DOWN position, matching the tap slop check.
        private static void QueueTap(float x, float y)
        {
            lock (_tapLock)
            {
                _tapX = x;
                _tapY = y;
                _tapPending = true;
            }
            if (Verbose) Log.Info("[touch] TAP queued at (" + x.ToString("0") + "," + y.ToString("0") + ")");
        }

        // ----------------------------------------------------------- back button
        // Android BACK -> B (the game's menu cancel). Returns true when the BACK
        // press was handled here (i.e. we are in a menu and ConsumeBack should
        // not finish the Activity); false in gameplay so the platform default
        // still applies. Debounced so a key + the OnBackPressed fallback cannot
        // double-fire.
        public static bool HandleBack()
        {
            if (!Enabled) return false;
            // PORT (touchpad): while the layout editor is open, BACK exits it
            // (discarding unsaved changes) and is consumed.
            if (TouchGamepadOverlay.EditorActive)
            {
                TouchGamepadOverlay.ExitEditor();
                return true;
            }
            if (!MenuContext()) return false;
            long now = Android.OS.SystemClock.ElapsedRealtime();
            if (now - _lastBackMs >= BackDebounceMs)
            {
                _lastBackMs = now;
                Pulse(Buttons.B);
                Log.Info("[touch] BACK -> B");
            }
            return true;
        }

        // Activity1 calls this again after MonoGame's own dispatch so touch bits
        // survive the framework writing the same AndroidGamePad.
        public static void Reapply()
        {
            if (!Enabled) return;
            AndroidInputBridge.SetTouchButtons(Current());
        }

        // --------------------------------------------------------------- touches
        // PORT (touchpad): returns true when the event was consumed by the
        // gameplay overlay (so Activity1 can swallow it); false otherwise.
        // Routing uses the SAME predicate (Game1.portGameplayActive) that gates
        // the overlay draw, so a touch can never reach the pad over a menu.
        public static bool OnTouch(Android.Views.MotionEvent e)
        {
            if (!Enabled || e == null) return false;
            // PORT (touchpad): the layout editor is an explicit, documented
            // exception to the gameplay-only rule -- it consumes all touches.
            if (TouchGamepadOverlay.EditorActive) return TouchGamepadOverlay.OnEditorTouch(e);
            // PORT (touchpad): comics/cutscenes are only touched when TOUCH
            // CONTROLS is ON (OFF => exactly the shipped behaviour).
            if (TouchGamepadOverlay.On && projectDish.Game1.portComicPlaying) return ComicTouch(e);
            if (TouchGamepadOverlay.On && projectDish.Game1.portTouchPadContext)
            {
                // TOUCH CONTROLS is ON and we are in-level (including in-game
                // menus): the pad takes precedence and fully overrides the
                // tap-to-select path, so the two can never both fire.
                return TouchGamepadOverlay.OnTouch(e);
            }
            if (!MenuContext()) return false;
#if ANDROID
            try
            {
                switch (e.ActionMasked)
                {
                    case Android.Views.MotionEventActions.Down:
                        _active = true;
                        _multi = e.PointerCount > 1;
                        _swipeFired = false;
                        _startX = Logical(e.GetX());
                        _startY = Logical(e.GetY());
                        _downTime = Android.OS.SystemClock.ElapsedRealtime();
                        Push();
                        if (Verbose)
                            Log.Info("[touch] DOWN raw=(" + e.GetX().ToString("0") + "," + e.GetY().ToString("0")
                                + ") logical=(" + _startX.ToString("0") + "," + _startY.ToString("0") + ")");
                        break;

                    case Android.Views.MotionEventActions.PointerDown:
                        _multi = true;
                        if (Verbose) Log.Info("[touch] POINTER_DOWN pointers=" + e.PointerCount);
                        break;

                    case Android.Views.MotionEventActions.Move:
                        if (_active && !_multi && !_swipeFired)
                        {
                            float dx = Logical(e.GetX()) - _startX;
                            float dy = Logical(e.GetY()) - _startY;
                            if (Math.Abs(dx) >= SwipeThresholdLogical || Math.Abs(dy) >= SwipeThresholdLogical)
                            {
                                if (Math.Abs(dx) > Math.Abs(dy))
                                    Pulse(dx > 0f ? Buttons.DPadRight : Buttons.DPadLeft);
                                else
                                    Pulse(dy > 0f ? Buttons.DPadDown : Buttons.DPadUp);
                                _swipeFired = true;
                            }
                        }
                        break;

                    case Android.Views.MotionEventActions.Up:
                        if (_multi)
                        {
                            Pulse(Buttons.B); // two-finger tap = cancel
                        }
                        else if (_active && !_swipeFired)
                        {
                            float dx = Logical(e.GetX()) - _startX;
                            float dy = Logical(e.GetY()) - _startY;
                            long dt = Android.OS.SystemClock.ElapsedRealtime() - _downTime;
                            if (dx * dx + dy * dy <= TapSlopLogical * TapSlopLogical && dt <= TapMaxMs)
                            {
                                // PORT (touch): direct tap-to-activate. Do not
                                // blindly press A at the old cursor; park the
                                // tap point so Tick can hit-test the drawn menu
                                // rows and select+activate the one under it.
                                QueueTap(_startX, _startY);
                            }
                            else if (Verbose)
                            {
                                Log.Info("[touch] UP ignored (dx=" + dx.ToString("0") + " dy=" + dy.ToString("0")
                                    + " dt=" + dt + "ms)");
                            }
                        }
                        Reset();
                        break;

                    case Android.Views.MotionEventActions.Cancel:
                        Reset();
                        break;
                }
            }
            catch (Exception ex) { Log.Exception("touch.event", ex); }
#endif
            return false;
        }

        private static void Reset()
        {
            _active = false;
            _multi = false;
            _swipeFired = false;
        }

        // PORT (touchpad): comic/cutscene touch -- ONLY when TOUCH CONTROLS is
        // ON, and only OUR two buttons (taps elsewhere do nothing).  With the
        // setting OFF this path is never taken, so comics behave exactly as the
        // shipped build.  The game's own A/B prompts are suppressed while ON
        // (Game1.Draw), so ours are the only controls.
        //   SPEED UP = HOLD -> Buttons.A held while the finger is down
        //   SKIP      = TAP  -> one Buttons.B edge
        // Verified mapping (Game1.cs:3261-3270): A -> strip.nextThing();
        // B -> strip.frame = end.
        private static bool ComicTouch(Android.Views.MotionEvent e)
        {
#if ANDROID
            try
            {
                switch (e.ActionMasked)
                {
                    case Android.Views.MotionEventActions.Down:
                        _comicPtr = e.GetPointerId(e.ActionIndex);
                        int b = TouchGamepadOverlay.ComicButtonDown(Logical(e.GetX()), Logical(e.GetY()));
                        if (b == 1)
                        {
                            _comicHoldA = true; // SPEED UP: hold A
                            Push();
                            Log.Info("[touch] comic SPEED UP (hold)");
                        }
                        return true; // consume; a tap elsewhere does nothing

                    case Android.Views.MotionEventActions.PointerDown:
                        return true; // extra fingers ignored

                    case Android.Views.MotionEventActions.Up:
                    case Android.Views.MotionEventActions.PointerUp:
                        {
                            if (e.GetPointerId(e.ActionIndex) != _comicPtr) return true;
                            int d = TouchGamepadOverlay.ComicDown;
                            float ux = Logical(e.GetX());
                            float uy = Logical(e.GetY());
                            TouchGamepadOverlay.ClearComicDown();
                            _comicPtr = -1;
                            if (d == 1 || _comicHoldA)
                            {
                                _comicHoldA = false; // release the hold
                                Push();
                                Log.Info("[touch] comic SPEED UP release");
                            }
                            else if (d == 2 && TouchGamepadOverlay.HitComicButton(ux, uy) == 2)
                            {
                                Pulse(Buttons.B); // SKIP: tap
                                Log.Info("[touch] comic SKIP (tap)");
                            }
                            return true;
                        }

                    case Android.Views.MotionEventActions.Move:
                        return true;

                    case Android.Views.MotionEventActions.Cancel:
                        TouchGamepadOverlay.ClearComicDown();
                        _comicPtr = -1;
                        if (_comicHoldA) { _comicHoldA = false; Push(); }
                        return true;
                }
            }
            catch (Exception ex) { Log.Exception("touch.comic", ex); }
#endif
            return true;
        }

        // Android delivers MotionEvents to the Activity in *window* space. The
        // window spans the physical panel, but the game's SurfaceView is scaled
        // by WidescreenConfig.Scale. Direction is scale-invariant; thresholds are
        // expressed in the game's logical pixels, so normalise here.
        private static float Logical(float v)
        {
            if (WidescreenConfig.Enabled && WidescreenConfig.Scale > 0f)
                return v / WidescreenConfig.Scale;
            return v;
        }
    }
}
