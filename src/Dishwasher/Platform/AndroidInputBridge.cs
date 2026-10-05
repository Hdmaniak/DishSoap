// ============================================================================
// Android gamepad bridge  --  Input Diagnostics Specialist fix.
//
// Problem (proven by InputDiagnostics):
//   * MonoGame.Framework.Android 3.8.5.1 never calls its own
//     Microsoft.Xna.Framework.Input.GamePad.Initialize(), so GamePad.GetState(i)
//     reports IsConnected == false for every index even when a controller is
//     attached (no lazy registration happens until an input event arrives).
//   * MonoGameAndroidGameView only forwards a key/motion event to GamePad when
//     the event's InputDevice has SOURCE_GAMEPAD/JOYSTICK **and non-zero
//     VendorId/ProductId**. Events that fail that test are dropped (neither
//     GamePad nor Keyboard sees them).
//
// Fix (no MonoGame source changes, no GamePad call-site changes):
//   1. Explicitly invoke the internal GamePad.Initialize() via reflection so the
//      attached controller is registered/enumerated at PlayerIndex.One.
//   2. Reflectively drive the framework's own internal AndroidGamePad instance
//      (_buttons/_leftStick/_rightStick/_leftTrigger/_rightTrigger) from the
//      KeyEvent/MotionEvent stream the Activity receives. GamePad.GetState()
//      then returns the bridged state to the *unmodified* game code.
//
// This is additive and revertable: delete this file, remove the
// InputDiagnostics/AndroidInputBridge calls in Activity1.cs and Game1.cs.
// ============================================================================
using System;
using System.Reflection;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace Dishwasher
{
    public static class AndroidInputBridge
    {
        private static readonly Type _gamePadType = typeof(GamePad);
        private static readonly FieldInfo _padsField =
            _gamePadType.GetField("GamePads", BindingFlags.NonPublic | BindingFlags.Static);
        private static readonly Type _padType =
            _gamePadType.Assembly.GetType("Microsoft.Xna.Framework.Input.AndroidGamePad");

        public static bool Available => _padsField != null && _padType != null;

        // ---- PORT (touch): synthetic pad + merge with a real controller ----
        // Hardware button state per slot, kept separately from the touch bits so
        // a real pad and touch can both drive slot 0 without clobbering each
        // other. Only touch (below) ever adds TouchButtons.
        private static readonly Buttons[] _hwButtons = new Buttons[4];
        private static Buttons _touchButtons = Buttons.None;
        private static bool _syntheticSlot0;

        // PORT (touchpad): analog state.  Hardware and touch contributions are
        // tracked separately per slot and merged in PublishTouch, exactly like
        // the button bits, so an on-screen stick and a real controller cannot
        // clobber each other.  Precedence: if a real stick is deflected it wins
        // (see PublishTouch); otherwise the touch stick is used.
        private static readonly Vector2[] _hwLeftStick = new Vector2[4];
        private static readonly Vector2[] _hwRightStick = new Vector2[4];
        private static readonly float[] _hwLeftTrigger = new float[4];
        private static readonly float[] _hwRightTrigger = new float[4];
        private static Vector2 _touchLeftStick = Vector2.Zero;
        private static Vector2 _touchRightStick = Vector2.Zero;
        private static float _touchLeftTrigger;
        private static float _touchRightTrigger;

        /// <summary>The pulsed touch buttons currently overlaid on slot 0.</summary>
        public static Buttons TouchButtons => _touchButtons;

        // Reflectively call GamePad.Initialize() so attached pads are enumerated.
        public static bool InitializePads()
        {
            try
            {
                if (!Available) return false;
                MethodInfo init = _gamePadType.GetMethod("Initialize",
                    BindingFlags.NonPublic | BindingFlags.Static);
                init?.Invoke(null, null);
                return init != null;
            }
            catch (Exception ex) { Log.Exception("bridge.init", ex); return false; }
        }

        private static Array GetPads()
        {
            return (Array)_padsField.GetValue(null);
        }

        private static object GetPad(int slot)
        {
            Array pads = GetPads();
            if (pads == null || slot < 0 || slot >= pads.Length) return null;
            return pads.GetValue(slot);
        }

        private static object GetField(object pad, string name)
        {
            if (pad == null) return null;
            FieldInfo f = _padType.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            return f?.GetValue(pad);
        }

        private static void SetField(object pad, string name, object value)
        {
            if (pad == null) return;
            FieldInfo f = _padType.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            f?.SetValue(pad, value);
        }

        private static object CreatePad(Android.Views.InputDevice device)
        {
            ConstructorInfo ctor = _padType.GetConstructor(new Type[] { typeof(Android.Views.InputDevice) });
            return ctor?.Invoke(new object[] { device });
        }

        // Only react to controllers (or the adb "Virtual" test device).
        private static bool IsGamepadLike(Android.Views.InputDevice d)
        {
            if (d == null) return false;
            if (d.Id < 0) return true; // adb `input` virtual device
            int s = (int)d.Sources;
            if ((s & 0x401) == 0x401) return true;        // SOURCE_GAMEPAD
            if ((s & 0x1000010) == 0x1000010) return true; // SOURCE_JOYSTICK
            if ((s & 0x101) == 0x101 && d.VendorId != 0 && d.ProductId != 0) return true; // external pad/kb
            return false;
        }

        // Pick the PlayerIndex slot for the device that produced this event.
        // Matches MonoGame's own first-free-slot behaviour, but by
        // ControllerNumber-1 when Android provides one, and (for adb-injected
        // "Virtual" events, id<0) routes to index 0 so the bridge can be tested
        // without a physical button.
        private static int ResolveSlot(Android.Views.InputDevice d)
        {
            if (d == null) return -1;
            Array pads = GetPads();

            // PORT (touch): if a real gamepad appears after boot while the
            // synthetic touch pad owns slot 0, promote the real pad into slot 0
            // (so one player index still works for both) instead of pushing it
            // to a higher slot the GamerServices shim would reject.
            if (_syntheticSlot0 && d.Id >= 0 && IsPhysicalGamepad(d))
            {
                object synthetic = pads.GetValue(0);
                object existingId = GetField(synthetic, "_deviceId");
                if (!(existingId is int sid) || sid != d.Id)
                {
                    object promoted = CreatePad(d);
                    if (promoted != null)
                    {
                        pads.SetValue(promoted, 0);
                        _hwButtons[0] = Buttons.None;
                        _syntheticSlot0 = false;
                        Log.Info("[touch] real gamepad promoted to slot 0 (id=" + d.Id + ")");
                    }
                }
            }

            for (int i = 0; i < pads.Length; i++)
            {
                object p = pads.GetValue(i);
                if (p == null) continue;
                object id = GetField(p, "_deviceId");
                if (id is int existing && existing == d.Id) return i;
            }

            if (d.Id < 0) return 0; // adb `input` virtual device -> drive index 0

            int slot = -1;
            int ctrl = -1;
            try { ctrl = d.ControllerNumber; } catch (Exception) { }
            if (ctrl >= 1 && ctrl <= pads.Length) slot = ctrl - 1;
            if (slot < 0 || pads.GetValue(slot) != null)
            {
                slot = -1;
                for (int i = 0; i < pads.Length; i++)
                {
                    if (pads.GetValue(i) == null) { slot = i; break; }
                }
            }
            if (slot < 0) slot = 0;
            object created = CreatePad(d);
            if (created != null) pads.SetValue(created, slot);
            return slot;
        }

        private static object EnsurePad(int slot, Android.Views.InputDevice d)
        {
            object pad = GetPad(slot);
            if (pad == null)
            {
                pad = CreatePad(d);
                if (pad != null) GetPads().SetValue(pad, slot);
            }
            return pad;
        }

        private static Buttons MapKey(int keyCode)
        {
            switch (keyCode)
            {
                case 19: return Buttons.DPadUp;
                case 20: return Buttons.DPadDown;
                case 21: return Buttons.DPadLeft;
                case 22: return Buttons.DPadRight;
                case 23: return Buttons.A;          // DPAD_CENTER as a click
                case 96: return Buttons.A;          // KEYCODE_BUTTON_A
                case 97: return Buttons.B;
                case 99: return Buttons.X;
                case 100: return Buttons.Y;
                case 102: return Buttons.LeftShoulder;
                case 103: return Buttons.RightShoulder;
                case 104: return Buttons.LeftTrigger;
                case 105: return Buttons.RightTrigger;
                case 106: return Buttons.LeftStick;
                case 107: return Buttons.RightStick;
                case 108: return Buttons.Start;
                case 109: return Buttons.Back;
                case 110: return Buttons.BigButton;
                case 66: return Buttons.Start;      // ENTER fallback
                case 62: return Buttons.A;          // SPACE fallback
                case 4: return Buttons.Back;        // BACK key
                default: return Buttons.None;
            }
        }

        public static void RouteKey(Android.Views.KeyEvent e)
        {
            try
            {
                if (e == null || !Available) return;
                Android.Views.InputDevice d = e.Device;
                if (!IsGamepadLike(d)) return;
                Buttons mapped = MapKey((int)e.KeyCode);
                if (mapped == Buttons.None) return;

                int slot = ResolveSlot(d);
                if (slot < 0) return;
                object pad = EnsurePad(slot, d);
                if (pad == null) return;

                bool down = e.Action == Android.Views.KeyEventActions.Down
                         || e.Action == Android.Views.KeyEventActions.Multiple;
                // PORT (touch): track hardware bits separately from the synthetic
                // touch overlay, then publish the merge.
                Buttons hw = _hwButtons[slot];
                hw = down ? (hw | mapped) : (hw & ~mapped);
                _hwButtons[slot] = hw;
                if (slot == 0) PublishTouch();
                else SetField(pad, "_buttons", hw);
                SetField(pad, "_isConnected", true);
            }
            catch (Exception ex) { Log.Exception("bridge.key", ex); }
        }

        public static void RouteMotion(Android.Views.MotionEvent e)
        {
            try
            {
                if (e == null || !Available) return;
                if (e.Action != Android.Views.MotionEventActions.Move) return;
                Android.Views.InputDevice d = e.Device;
                if (!IsGamepadLike(d)) return;
                int slot = ResolveSlot(d);
                if (slot < 0) return;
                object pad = EnsurePad(slot, d);
                if (pad == null) return;

                // Same axis indices MonoGame's own AndroidGamePad.OnGenericMotionEvent uses.
                float lx = e.GetAxisValue(Android.Views.Axis.X);
                float ly = e.GetAxisValue(Android.Views.Axis.Y);
                float rx = e.GetAxisValue(Android.Views.Axis.Z);
                float ry = e.GetAxisValue(Android.Views.Axis.Rz);

                float lt = Math.Max(e.GetAxisValue(Android.Views.Axis.Brake), e.GetAxisValue(Android.Views.Axis.Ltrigger));
                float rt = Math.Max(e.GetAxisValue(Android.Views.Axis.Gas), e.GetAxisValue(Android.Views.Axis.Rtrigger));

                // PORT (touchpad): keep the hardware sticks/triggers separate
                // from the touch overlay and republish the merge.
                _hwLeftStick[slot] = new Vector2(lx, -ly);
                _hwRightStick[slot] = new Vector2(rx, -ry);
                _hwLeftTrigger[slot] = lt;
                _hwRightTrigger[slot] = rt;

                // PORT (touch): keep the hardware buttons separate from the
                // menu-touch overlay and republish the merge so a touch pulse is
                // not lost to a controller motion event.
                Buttons hw = _hwButtons[slot];
                hw = lt > 0f ? (hw | Buttons.LeftTrigger) : (hw & ~Buttons.LeftTrigger);
                hw = rt > 0f ? (hw | Buttons.RightTrigger) : (hw & ~Buttons.RightTrigger);
                _hwButtons[slot] = hw;
                if (slot == 0)
                {
                    PublishTouch();
                }
                else
                {
                    SetField(pad, "_leftStick", _hwLeftStick[slot]);
                    SetField(pad, "_rightStick", _hwRightStick[slot]);
                    SetField(pad, "_leftTrigger", lt);
                    SetField(pad, "_rightTrigger", rt);
                    SetField(pad, "_buttons", hw);
                }
                SetField(pad, "_isConnected", true);
            }
            catch (Exception ex) { Log.Exception("bridge.motion", ex); }
        }

        // ------------------------------------------------------------------
        // PORT (touch)
        // ------------------------------------------------------------------

        /// <summary>A real (physical/Bluetooth) controller, not adb's virtual
        /// device and not the touchscreen.</summary>
        private static bool IsPhysicalGamepad(Android.Views.InputDevice d)
        {
            if (d == null || d.Id < 0) return false;
            int s = (int)d.Sources;
            if ((s & 0x401) == 0x401) return true;         // SOURCE_GAMEPAD
            if ((s & 0x1000010) == 0x1000010) return true; // SOURCE_JOYSTICK
            return false;
        }

        /// <summary>Pick a real Android InputDevice to back the synthetic pad.
        /// GamePad.GetState disconnects a pad whose device id no longer exists,
        /// so the pad must reference a live device.</summary>
        private static Android.Views.InputDevice FindRealDevice()
        {
            Android.Views.InputDevice fallback = null;
            try
            {
                int[] ids = Android.Views.InputDevice.GetDeviceIds();
                for (int i = 0; i < ids.Length; i++)
                {
                    Android.Views.InputDevice d = Android.Views.InputDevice.GetDevice(ids[i]);
                    if (d == null) continue;
                    if (((int)d.Sources & 0x1002) == 0x1002) return d; // touchscreen
                    if (fallback == null) fallback = d;
                }
            }
            catch (Exception ex) { Log.Exception("bridge.finddevice", ex); }
            return fallback;
        }

        /// <summary>Ensure slot 0 always has a connected pad, even with no
        /// controller attached, so GamePad.GetState(0).IsConnected is true and
        /// touch-synthesised buttons reach the unmodified game code.</summary>
        public static bool InitializeTouchPad()
        {
            try
            {
                if (!Available) return false;
                if (GetPad(0) != null)
                {
                    // A real controller already owns slot 0; touch just merges
                    // into it (see RouteKey/RouteMotion/PublishTouch).
                    Log.Info("[touch] real pad present at slot 0; touch will merge");
                    return true;
                }

                Android.Views.InputDevice dev = FindRealDevice();
                if (dev == null) { Log.Info("[touch] no InputDevice available for synthetic pad"); return false; }

                object pad = CreatePad(dev);
                if (pad == null) pad = CreateUninitializedPad(dev);
                if (pad == null) return false;

                SetField(pad, "_isConnected", true);
                _hwButtons[0] = Buttons.None;
                GetPads().SetValue(pad, 0);
                _syntheticSlot0 = true;
                Log.Info("[touch] synthetic pad at slot 0 (device id=" + dev.Id + " name='" + dev.Name + "')");
                return true;
            }
            catch (Exception ex) { Log.Exception("touch.initpad", ex); return false; }
        }

        /// <summary>Fallback pad creation that bypasses the AndroidGamePad
        /// constructor (which touches device vibrator APIs that can throw on a
        /// touchscreen).</summary>
        private static object CreateUninitializedPad(Android.Views.InputDevice dev)
        {
            try
            {
                object pad = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(_padType);
                SetField(pad, "_deviceId", dev.Id);
                SetField(pad, "_descriptor", dev.Descriptor ?? "touch");
                SetField(pad, "_device", dev);
                SetField(pad, "_isConnected", true);
                return pad;
            }
            catch (Exception ex) { Log.Exception("touch.uninitpad", ex); return null; }
        }

        /// <summary>Publish the menu-touch button overlay on slot 0. Called by
        /// TouchControls whenever its pulsed state changes (and once per frame
        /// while a pulse is live). Preserves the hardware state on slot 0.</summary>
        public static void SetTouchButtons(Buttons buttons)
        {
            try
            {
                if (!Available) return;
                _touchButtons = buttons;
                PublishTouch();
            }
            catch (Exception ex) { Log.Exception("touch.setbuttons", ex); }
        }

        /// <summary>PORT (touchpad): publish the full on-screen-pad state
        /// (buttons, both sticks, triggers) on slot 0, merged with any real
        /// controller state. Called from TouchGamepadOverlay.</summary>
        public static void SetTouchState(Buttons buttons, Vector2 leftStick, Vector2 rightStick,
            float leftTrigger, float rightTrigger)
        {
            try
            {
                if (!Available) return;
                _touchButtons = buttons;
                _touchLeftStick = leftStick;
                _touchRightStick = rightStick;
                _touchLeftTrigger = leftTrigger;
                _touchRightTrigger = rightTrigger;
                PublishTouch();
            }
            catch (Exception ex) { Log.Exception("touch.setstate", ex); }
        }

        /// <summary>Write the merged button/axis state into slot 0's
        /// AndroidGamePad. The game reads GamePad.GetState(0) directly from
        /// these fields.</summary>
        private static void PublishTouch()
        {
            object pad = GetPad(0);
            if (pad == null) return;
            SetField(pad, "_buttons", _hwButtons[0] | _touchButtons);
            SetField(pad, "_isConnected", true);

            // Sticks: a deflected physical stick wins over the on-screen stick
            // so the two never sum into a phantom direction.  Triggers take the
            // larger of the two.
            Vector2 hl = _hwLeftStick[0];
            SetField(pad, "_leftStick", hl.LengthSquared() >= _touchLeftStick.LengthSquared() ? hl : _touchLeftStick);
            Vector2 hr = _hwRightStick[0];
            SetField(pad, "_rightStick", hr.LengthSquared() >= _touchRightStick.LengthSquared() ? hr : _touchRightStick);
            SetField(pad, "_leftTrigger", Math.Max(_hwLeftTrigger[0], _touchLeftTrigger));
            SetField(pad, "_rightTrigger", Math.Max(_hwRightTrigger[0], _touchRightTrigger));
        }

        /// <summary>Re-assert the touch overlay after MonoGame's own
        /// DispatchKeyEvent / DispatchGenericMotionEvent path has written the
        /// same AndroidGamePad.</summary>
        public static void ReapplyTouchState()
        {
            PublishTouch();
        }
    }
}
