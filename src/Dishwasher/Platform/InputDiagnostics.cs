// ============================================================================
// TEMP INPUT DIAGNOSTICS  --  added by the Input Diagnostics Specialist.
// Purely additive / read-only with respect to game logic. Every log line is
// prefixed "[input]".
//
// STATUS (final consolidation): INERT BY DEFAULT. `Enabled` is false, so every
// entry point returns immediately and the 500 ms GamePad/Keyboard poll that
// used to spam logcat (and allocate a StringBuilder every poll) never runs.
// The call sites in Game1.Update / Activity1 are guarded by `Enabled`.
//
// TO RE-ENABLE (controller investigation only): set Enabled = true below.
// Everything (device dump, per-500 ms state, key/motion events, view listener)
// then behaves as before. Set it back to false for a shipping build.
//
// What it does (no button press required):
//   * LogAndroidDevices()  - dumps Android InputDevice.GetDeviceIds() + name,
//                            sources, vendor/product, ControllerNumber as seen
//                            by THIS app process.
//   * Tick()               - throttled (500 ms / on change) dump of
//                            GamePad.GetState(i) for i=0..3 (IsConnected,
//                            buttons, triggers, sticks) + Keyboard.GetState()
//                            + Globals.mainPlayerIndex + view focus.
//   * LogKeyEvent()        - logs every KeyEvent the Activity dispatches
//                            (device id/name/sources/vendor/product/keycode).
//   * LogMotionEvent()     - throttled log of generic motion axis values.
// ============================================================================
using System;
using System.Text;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace Dishwasher
{
    public static class InputDiagnostics
    {
        // Master switch: false == shipping (no logging, no polling, no listener).
        public static bool Enabled = false;

        private static double _nextPoll;
        private static double _nextFocus;
        private static double _nextMotion;
        private static string _lastPoll = "";
        private static string _lastFocus = "";
        private static Android.Views.View _view;

        public static void AttachView(Android.Views.View view)
        {
            if (!Enabled) return;
            _view = view;
#if ANDROID
            try { view?.SetOnKeyListener(new KeyListener()); }
            catch (Exception ex) { Log.Exception("input.viewkey.attach", ex); }
#endif
        }

#if ANDROID
        // Lets us prove whether the focused game view itself receives key events
        // (MonoGameAndroidGameView.OnKeyDown sits behind this dispatch step).
        private sealed class KeyListener : Java.Lang.Object, Android.Views.View.IOnKeyListener
        {
            public bool OnKey(Android.Views.View v, Android.Views.Keycode keyCode, Android.Views.KeyEvent e)
            {
                try
                {
                    Android.Views.InputDevice d = e?.Device;
                    Log.Info("[input][viewkey] action=" + (e == null ? "?" : e.Action.ToString())
                        + " keycode=" + keyCode + "(" + (int)keyCode + ")"
                        + " devId=" + (d == null ? -1 : d.Id)
                        + " devName='" + (d == null ? "<null>" : d.Name) + "'"
                        + " vendor=0x" + (d == null ? 0 : d.VendorId).ToString("X")
                        + " product=0x" + (d == null ? 0 : d.ProductId).ToString("X"));
                }
                catch (Exception ex) { Log.Exception("input.viewkey", ex); }
                return false; // let normal dispatch continue
            }
        }
#endif


        // ---------------------------------------------------------------- devices
        public static void LogAndroidDevices(string where)
        {
            if (!Enabled) return;
#if ANDROID
            try
            {
                int[] ids = Android.Views.InputDevice.GetDeviceIds();
                Log.Info("[input][devices] " + where + ": InputDevice.GetDeviceIds() -> " + ids.Length + " device(s)");
                foreach (int id in ids)
                {
                    Android.Views.InputDevice d = Android.Views.InputDevice.GetDevice(id);
                    if (d == null)
                    {
                        Log.Info("[input][devices]   id=" + id + " <null>");
                        continue;
                    }
                    int sources = (int)d.Sources;
                    int ctrl = -1;
                    try { ctrl = d.ControllerNumber; } catch (Exception) { }
                    Log.Info("[input][devices]   id=" + id
                        + " name='" + d.Name + "'"
                        + " sources=0x" + sources.ToString("X")
                        + " vendor=0x" + d.VendorId.ToString("X")
                        + " product=0x" + d.ProductId.ToString("X")
                        + " controllerNumber=" + ctrl
                        + " descriptor='" + d.Descriptor + "'");
                }
            }
            catch (Exception ex)
            {
                Log.Exception("input.devices", ex);
            }
#endif
        }

        // ------------------------------------------------------------------ poll
        public static void Tick(GameTime gameTime)
        {
            if (!Enabled) return;
            try
            {
                double now = gameTime.TotalGameTime.TotalSeconds;

                if (now >= _nextFocus)
                {
                    _nextFocus = now + 2.0;
                    string focus = DescribeFocus();
                    if (focus != _lastFocus)
                    {
                        _lastFocus = focus;
                        Log.Info("[input][focus] " + focus + " (change)");
                    }
                }

                if (now < _nextPoll)
                {
                    return;
                }

                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < 4; i++)
                {
                    GamePadState gs = GamePad.GetState((PlayerIndex)i);
                    if (i > 0) sb.Append(" | ");
                    sb.Append("P").Append(i).Append(gs.IsConnected ? "=CONN" : "=-");
                    if (gs.IsConnected)
                    {
                        sb.Append(" btn=").Append(gs.Buttons.ToString());
                        sb.Append(" A=").Append(gs.Buttons.A == ButtonState.Pressed ? "1" : "0");
                        sb.Append(" B=").Append(gs.Buttons.B == ButtonState.Pressed ? "1" : "0");
                        sb.Append(" Start=").Append(gs.Buttons.Start == ButtonState.Pressed ? "1" : "0");
                        sb.Append(" LT=").Append(gs.Triggers.Left.ToString("0.00"));
                        sb.Append(" RT=").Append(gs.Triggers.Right.ToString("0.00"));
                        sb.Append(" LS=(").Append(gs.ThumbSticks.Left.X.ToString("0.00")).Append(",").Append(gs.ThumbSticks.Left.Y.ToString("0.00")).Append(")");
                        sb.Append(" RS=(").Append(gs.ThumbSticks.Right.X.ToString("0.00")).Append(",").Append(gs.ThumbSticks.Right.Y.ToString("0.00")).Append(")");
                    }
                }

                Keys[] keys;
                try { keys = Keyboard.GetState().GetPressedKeys(); }
                catch (Exception) { keys = new Keys[0]; }
                sb.Append(" || keys=");
                if (keys == null || keys.Length == 0) sb.Append("-");
                else { for (int i = 0; i < keys.Length; i++) { if (i > 0) sb.Append(","); sb.Append(keys[i]); } }

                sb.Append(" || mainPlayerIndex=").Append(projectDish.Globals.mainPlayerIndex);

                string line = sb.ToString();
                bool changed = line != _lastPoll;
                _nextPoll = now + 0.5;
                _lastPoll = line;
                Log.Info("[input] " + line + (changed ? "  (change)" : ""));
            }
            catch (Exception ex)
            {
                Log.Exception("input.tick", ex);
            }
        }

        private static string DescribeFocus()
        {
#if ANDROID
            if (_view == null) return "view=<null>";
            return "view.IsFocused=" + _view.IsFocused + " view.HasFocus=" + _view.HasFocus + " view.Focusable=" + _view.Focusable;
#else
            return "n/a";
#endif
        }

        // ------------------------------------------------------------- key events
        public static void LogKeyEvent(Android.Views.KeyEvent e)
        {
            if (!Enabled) return;
#if ANDROID
            try
            {
                if (e == null) return;
                Android.Views.InputDevice d = e.Device;
                int sources = d == null ? 0 : (int)d.Sources;
                Log.Info("[input][key] action=" + e.Action
                    + " keycode=" + e.KeyCode + "(" + (int)e.KeyCode + ")"
                    + " repeat=" + e.RepeatCount
                    + " devId=" + (d == null ? -1 : d.Id)
                    + " devName='" + (d == null ? "<null>" : d.Name) + "'"
                    + " sources=0x" + sources.ToString("X")
                    + " vendor=0x" + (d == null ? 0 : d.VendorId).ToString("X")
                    + " product=0x" + (d == null ? 0 : d.ProductId).ToString("X"));
            }
            catch (Exception ex)
            {
                Log.Exception("input.key", ex);
            }
#endif
        }

        // ---------------------------------------------------------- motion events
        public static void LogMotionEvent(Android.Views.MotionEvent e)
        {
            if (!Enabled) return;
#if ANDROID
            try
            {
                if (e == null) return;
                double now = Android.OS.SystemClock.ElapsedRealtime() / 1000.0;
                if (now < _nextMotion) return;
                _nextMotion = now + 0.5;
                Android.Views.InputDevice d = e.Device;
                Log.Info("[input][motion] action=" + e.Action
                    + " devId=" + (d == null ? -1 : d.Id)
                    + " devName='" + (d == null ? "<null>" : d.Name) + "'"
                    + " X=" + e.GetAxisValue(Android.Views.Axis.X).ToString("0.00")
                    + " Y=" + e.GetAxisValue(Android.Views.Axis.Y).ToString("0.00")
                    + " Z=" + e.GetAxisValue(Android.Views.Axis.Z).ToString("0.00")
                    + " RZ=" + e.GetAxisValue(Android.Views.Axis.Rz).ToString("0.00")
                    + " HAT_X=" + e.GetAxisValue(Android.Views.Axis.HatX).ToString("0.00")
                    + " HAT_Y=" + e.GetAxisValue(Android.Views.Axis.HatY).ToString("0.00")
                    + " LTRIGGER(17)=" + e.GetAxisValue(Android.Views.Axis.Ltrigger).ToString("0.00")
                    + " RTRIGGER(18)=" + e.GetAxisValue(Android.Views.Axis.Rtrigger).ToString("0.00")
                    + " GAS(22)=" + e.GetAxisValue(Android.Views.Axis.Gas).ToString("0.00")
                    + " BRAKE(23)=" + e.GetAxisValue(Android.Views.Axis.Brake).ToString("0.00"));
            }
            catch (Exception ex)
            {
                Log.Exception("input.motion", ex);
            }
#endif
        }
    }
}
