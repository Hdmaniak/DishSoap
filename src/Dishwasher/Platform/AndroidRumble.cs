// ============================================================================
// Android rumble  --  Haptics / Rumble Specialist.
//
// The game drives vibration through XNA's GamePad.SetVibration(...) (four call
// sites in Game1.cs, once per frame whenever a player index is live).
//
// On Android, MonoGame.Framework.Android 3.8.5.1 implements that as
// GamePad.PlatformSetVibration(), which IGNORES leftMotor/rightMotor and
// unconditionally calls Vibrator.Vibrate(500 ms) -- including for (0,0) -- so
// on API >= 31 it is a constant buzz and on API < 31 it is silent.
//
// This file replaces those call sites with a value-aware, ROUTED path:
//
//   game rumble 0..1  ->  AndroidRumble.Set(index, left, right)
//                          -> CONTROLLER sink (the gamepad):
//                               API >= 31 : InputDevice.VibratorManager
//                               API <  31 : InputDevice.Vibrator, else the
//                                           Bluetooth HID output-report path
//                                           (Platform/Java/RumbleHidHost.java)
//                          -> PHONE sink (the Android device vibrator)
//
// The routing is the ANDROID SETTINGS > VIBRATION row
// (OFF / PHONE / CONTROLLER / BOTH; see Platform/AndroidSettingsMenu.cs).
// A request of 0 cancels; pause/stop/focus-loss cancel; and the game's own
// VIBRATION setting (Settings.norumble) overrides everything (nothing fires).
//
// Revert: delete this file + Platform/BluetoothHidRumble.cs +
// Platform/Java/RumbleHidHost.java, restore the four GamePad.SetVibration()
// calls in Game1.cs, and remove the AndroidRumble hooks in Activity1.cs /
// AndroidSettingsMenu.cs and the BLUETOOTH permissions.
// ============================================================================
using System;
using System.Reflection;
using Android.Content;
using Android.OS;
using Microsoft.Xna.Framework.Input;

namespace Dishwasher
{
    public static class AndroidRumble
    {
        private const int OneShotMs = 250;
        private const int ReissueIntervalMs = 150;
        private const float Epsilon = 0.01f;

        private static readonly Type GamePadType = typeof(GamePad);
        private static readonly FieldInfo PadsField =
            GamePadType.GetField("GamePads", BindingFlags.NonPublic | BindingFlags.Static);
        private static readonly Type PadType =
            GamePadType.Assembly.GetType("Microsoft.Xna.Framework.Input.AndroidGamePad");
        private static readonly FieldInfo PadDeviceField = PadType?.GetField(
            "_device", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        /// <summary>Per-player-index controller sink state.</summary>
        private sealed class CtrlSlot
        {
            public int DeviceId = int.MinValue;
            public bool Resolved;
            public string DeviceName;
            public int VendorId;
            public int ProductId;
            public Vibrator[] Vibs;   // the gamepad's own motor(s), when exposed
            public bool UseBtHid;     // API<31 Bluetooth HID output-report fallback
            public float LastValue = -1f;
            public long LastIssue;
        }

        private static readonly CtrlSlot[] Slots = new CtrlSlot[4];
        private static readonly object Gate = new object();

        private static Context _ctx;
        private static Vibrator _phone;
        private static VibratorManager _phoneVm;
        private static bool _initialized;
        private static float _phoneLast = -1f;
        private static long _phoneLastIssue;
        private static long _lastLog;

        public static void Initialize(Context ctx)
        {
            lock (Gate)
            {
                try
                {
                    _ctx = ctx;
                    if (OperatingSystem.IsAndroidVersionAtLeast(31))
                    {
                        _phoneVm = ctx.GetSystemService(Context.VibratorManagerService) as VibratorManager;
                        _phone = _phoneVm?.DefaultVibrator;
                    }
                    else
                    {
                        _phone = ctx.GetSystemService(Context.VibratorService) as Vibrator;
                    }
                    _initialized = true;
                    Log.Info("[rumble] init sdk=" + (int)Build.VERSION.SdkInt
                        + " phoneHas=" + (_phone != null && _phone.HasVibrator));
                    // API < 31 only: bind the HID host for the Bluetooth fallback.
                    BluetoothHidRumble.Initialize(ctx);
                }
                catch (Exception ex) { Log.Exception("rumble.init", ex); }
            }
        }

        /// <summary>Called when the VIBRATION row changes: cancel everything so
        /// the new routing starts from silence.</summary>
        public static void ApplyMode()
        {
            StopAll();
            Log.Info("[rumble] mode=" + ModeName(AndroidSettings.Vibration));
        }

        /// <summary>Replacement for GamePad.SetVibration(index, left, right).</summary>
        public static void Set(int index, float left, float right)
        {
            try
            {
                if (!_initialized || index < 0 || index >= Slots.Length) return;

                // The game's own VIBRATION setting wins over the Android row:
                // when the game disables rumble nothing may fire anywhere.
                if (index == 0)
                {
                    try
                    {
                        if (projectDish.Globals.settings != null && projectDish.Globals.settings.norumble)
                        {
                            StopAll();
                            return;
                        }
                    }
                    catch (Exception) { /* settings not ready yet */ }
                }

                int mode = AndroidSettings.Vibration;
                bool wantPhone = mode == AndroidSettings.VIB_PHONE || mode == AndroidSettings.VIB_BOTH;
                bool wantCtrl = mode == AndroidSettings.VIB_CONTROLLER || mode == AndroidSettings.VIB_BOTH;
                float value = Math.Max(left, right);

                if (mode == AndroidSettings.VIB_OFF || value <= Epsilon)
                {
                    StopController(index);
                    StopPhone();
                    return;
                }

                CtrlSlot s = Slots[index] ?? (Slots[index] = new CtrlSlot());
                long now = System.Environment.TickCount64;

                if (wantCtrl)
                {
                    Resolve(s, GetDevice(index));
                    if (Math.Abs(value - s.LastValue) >= 0.02f || now - s.LastIssue >= ReissueIntervalMs)
                    {
                        s.LastValue = value;
                        s.LastIssue = now;
                        if (s.Vibs != null && s.Vibs.Length > 0)
                        {
                            IssueVibrators(s.Vibs, left, right);
                        }
                        else if (s.UseBtHid)
                        {
                            BluetoothHidRumble.SendRumble(
                                s.DeviceName, s.VendorId, s.ProductId,
                                Byte100(left), Byte100(right));
                        }
                    }
                }
                else StopController(index);

                if (wantPhone)
                {
                    if (Math.Abs(value - _phoneLast) >= 0.02f || now - _phoneLastIssue >= ReissueIntervalMs)
                    {
                        _phoneLast = value;
                        _phoneLastIssue = now;
                        IssuePhone(0.6f * left + 0.4f * right);
                    }
                }
                else StopPhone();

                if (now - _lastLog >= 1000)
                {
                    _lastLog = now;
                    string ctrl = (s.Vibs != null && s.Vibs.Length > 0)
                        ? "vib"
                        : (s.UseBtHid ? (BluetoothHidRumble.Available ? "bt" : "bt-init") : "none");
                    Log.Info("[rumble] mode=" + ModeName(mode) + " slot=" + index
                        + " L=" + left.ToString("0.00") + " R=" + right.ToString("0.00")
                        + " ctrl=" + ctrl + " phone=" + wantPhone);
                }
            }
            catch (Exception ex) { Log.Exception("rumble.set", ex); }
        }

        public static void StopAll()
        {
            for (int i = 0; i < Slots.Length; i++) StopController(i);
            StopPhone();
        }

        private static void StopController(int index)
        {
            CtrlSlot s = Slots[index];
            if (s == null) return;
            bool wasActive = s.LastValue > Epsilon;
            if (s.Vibs != null)
            {
                foreach (Vibrator v in s.Vibs)
                {
                    try { v?.Cancel(); } catch (Exception) { }
                }
            }
            if (wasActive && s.UseBtHid)
            {
                BluetoothHidRumble.SendRumble(s.DeviceName, s.VendorId, s.ProductId, 0, 0);
            }
            s.LastValue = 0f;
        }

        private static void StopPhone()
        {
            bool wasActive = _phoneLast > Epsilon;
            _phoneLast = 0f;
            if (!wasActive) return;
            try { _phone?.Cancel(); } catch (Exception) { }
            if (OperatingSystem.IsAndroidVersionAtLeast(31))
            {
                try { _phoneVm?.DefaultVibrator?.Cancel(); } catch (Exception) { }
            }
        }

        // ---- controller resolution ----------------------------------------

        private static void Resolve(CtrlSlot s, Android.Views.InputDevice dev)
        {
            // If this slot is still the synthetic touch pad (the real pad is
            // only promoted into slot 0 when its first event arrives), fall
            // back to any connected physical gamepad so its rumble sink stays
            // reachable for player 0.
            if ((dev == null || !IsPhysicalGamepad(dev)) && s == Slots[0])
            {
                Android.Views.InputDevice g = FindGamepadDevice();
                if (g != null) dev = g;
            }

            int id = dev?.Id ?? int.MinValue;
            bool haveBackend = (s.Vibs != null && s.Vibs.Length > 0) || s.UseBtHid;
            if (s.Resolved && s.DeviceId == id && haveBackend) return;

            s.DeviceId = id;
            s.Resolved = true;
            s.DeviceName = dev?.Name;
            s.VendorId = dev?.VendorId ?? 0;
            s.ProductId = dev?.ProductId ?? 0;
            s.Vibs = null;
            s.UseBtHid = false;

            if (dev != null && IsPhysicalGamepad(dev))
            {
                if (OperatingSystem.IsAndroidVersionAtLeast(31))
                {
                    try
                    {
                        VibratorManager vm = dev.VibratorManager;
                        int[] ids = vm?.GetVibratorIds();
                        if (ids != null && ids.Length > 0)
                        {
                            Vibrator[] list = ids.Length >= 2
                                ? new[] { vm.GetVibrator(ids[0]), vm.GetVibrator(ids[1]) }
                                : new[] { vm.GetVibrator(ids[0]) };
                            if (HasVibrator(list)) s.Vibs = list;
                        }
                    }
                    catch (Exception ex) { Log.Exception("rumble.devvib", ex); }
                }
                else
                {
                    try
                    {
                        Vibrator v = dev.Vibrator;
                        if (v != null && v.HasVibrator) s.Vibs = new[] { v };
                    }
                    catch (Exception ex) { Log.Exception("rumble.devvib", ex); }
                    // Attempt the Bluetooth HID transport for any physical
                    // gamepad on API<31. SendRumble() is a safe no-op that logs
                    // when the HID host isn't ready or the controller has no
                    // verified report, so don't gate on the transient proxy
                    // state here.
                    if (s.Vibs == null) s.UseBtHid = true;
                }
            }

            Log.Info("[rumble] resolve dev='" + (dev?.Name ?? "<none>") + "' id=" + id
                + " vid=0x" + s.VendorId.ToString("X4") + " pid=0x" + s.ProductId.ToString("X4")
                + " mode=" + ModeName(AndroidSettings.Vibration)
                + " vibs=" + (s.Vibs?.Length ?? 0) + " btHid=" + s.UseBtHid
                + " sdk=" + (int)Build.VERSION.SdkInt);
        }

        private static Android.Views.InputDevice GetDevice(int index)
        {
            try
            {
                Array pads = PadsField?.GetValue(null) as Array;
                object pad = pads?.GetValue(index);
                if (pad == null || PadDeviceField == null) return null;
                return PadDeviceField.GetValue(pad) as Android.Views.InputDevice;
            }
            catch (Exception) { return null; }
        }

        private static bool IsPhysicalGamepad(Android.Views.InputDevice d)
        {
            if (d == null || d.Id < 0) return false;
            int sources = (int)d.Sources;
            if ((sources & 0x401) == 0x401) return true;          // SOURCE_GAMEPAD
            if ((sources & 0x1000010) == 0x1000010) return true;  // SOURCE_JOYSTICK
            return false;
        }

        /// <summary>First connected physical gamepad (any slot).</summary>
        private static Android.Views.InputDevice FindGamepadDevice()
        {
            try
            {
                int[] ids = Android.Views.InputDevice.GetDeviceIds();
                foreach (int id in ids)
                {
                    Android.Views.InputDevice d = Android.Views.InputDevice.GetDevice(id);
                    if (d != null && IsPhysicalGamepad(d)) return d;
                }
            }
            catch (Exception) { }
            return null;
        }

        private static bool HasVibrator(Vibrator[] list)
        {
            foreach (Vibrator v in list)
            {
                try { if (v != null && v.HasVibrator) return true; } catch (Exception) { }
            }
            return false;
        }

        // ---- output -------------------------------------------------------

        private static void IssueVibrators(Vibrator[] vibs, float left, float right)
        {
            if (vibs.Length >= 2)
            {
                Issue(vibs[0], left);    // low-frequency motor
                Issue(vibs[1], right);   // high-frequency motor
            }
            else
            {
                Issue(vibs[0], 0.6f * left + 0.4f * right);  // single-motor blend
            }
        }

        private static void IssuePhone(float intensity)
        {
            if (intensity <= Epsilon) { StopPhone(); return; }
            Vibrator v = _phone;
            if (OperatingSystem.IsAndroidVersionAtLeast(31)) v = _phoneVm?.DefaultVibrator ?? _phone;
            Issue(v, intensity);
        }

        private static void Issue(Vibrator v, float intensity)
        {
            if (v == null) return;
            try
            {
                if (intensity <= Epsilon) { v.Cancel(); return; }
                int amp = Amp(intensity);
                if (OperatingSystem.IsAndroidVersionAtLeast(26))
                {
                    v.Vibrate(VibrationEffect.CreateOneShot(OneShotMs, amp));
                }
                else
                {
                    v.Vibrate(OneShotMs);
                }
            }
            catch (Exception ex) { Log.Exception("rumble.issue", ex); }
        }

        private static int Amp(float intensity)
        {
            int amp = (int)Math.Round(Math.Min(1f, Math.Max(0f, intensity)) * 255f);
            if (amp < 1) amp = 1;
            if (amp > 255) amp = 255;
            return amp;
        }

        private static byte Byte100(float v)
        {
            int i = (int)Math.Round(Math.Min(1f, Math.Max(0f, v)) * 100f);
            if (i < 0) i = 0;
            if (i > 100) i = 100;
            return (byte)i;
        }

        private static string ModeName(int mode)
        {
            switch (mode)
            {
                case AndroidSettings.VIB_OFF: return "OFF";
                case AndroidSettings.VIB_PHONE: return "PHONE";
                case AndroidSettings.VIB_CONTROLLER: return "CONTROLLER";
                case AndroidSettings.VIB_BOTH: return "BOTH";
                default: return "?";
            }
        }
    }
}
