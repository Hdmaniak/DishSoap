// ============================================================================
// BluetoothHidRumble -- C# facade over the Java helper
// Platform/Java/RumbleHidHost.java, plus the per-controller HID report table.
//
// Android < 12 exposes no gamepad vibrator (InputDevice.getVibrator() returns
// NullVibrator for a Bluetooth pad because the kernel evdev node has no
// force-feedback).  The only route to a Bluetooth controller on API < 31 is
// the platform HID host's @hide output-report method BluetoothHidHost.sendData,
// which the Java helper invokes after applying the standard
// VMRuntime.setHiddenApiExemptions bypass.
//
// CONTROLLER AWARENESS: the transport (sendData) is generic, but the *report
// bytes* are controller-specific.  BuildReport() maps interface vendor/product
// (or name) to the correct output report.  Only the Xbox One (model 1708 /
// Xbox One S) report is implemented, ported from SDL3's SDL_hidapi_xboxone.c;
// DualShock 4 is a clearly-marked extension point (see BuildReport).
//
// On API >= 31 this file is unused: AndroidRumble drives the controller's
// VibratorManager, which is controller-agnostic.
// ============================================================================
using System;
using Android.Content;
using Java.Lang;
using Java.Lang.Reflect;

namespace Dishwasher
{
    public static class BluetoothHidRumble
    {
        private static Class _cls;
        private static Method _init;
        private static Method _send;
        private static Method _ready;
        private static bool _loaded;
        private static bool _readyCached;
        private static long _readyCheckedAt = long.MinValue;
        private static long _lastSendLog;

        private static bool EnsureLoaded()
        {
            if (_loaded) return _cls != null;
            _loaded = true;
            try
            {
                _cls = Class.ForName("com.recomp.dishwasher.RumbleHidHost");
                _init = _cls.GetMethod("init", Class.FromType(typeof(Context)));
                _send = _cls.GetMethod("send",
                    Class.FromType(typeof(Java.Lang.String)),
                    Class.FromType(typeof(Java.Lang.String)));
                _ready = _cls.GetMethod("isReady");
                Log.Info("[rumble][bthid] helper loaded");
            }
            catch (System.Exception ex) { Log.Exception("rumble.bthid.load", ex); _cls = null; }
            return _cls != null;
        }

        /// <summary>Bind the HID host profile (API &lt; 31 only). Safe once.</summary>
        public static void Initialize(Context ctx)
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(31)) return;
            if (!EnsureLoaded()) return;
            try { _init?.Invoke(null, ctx); }
            catch (System.Exception ex) { Log.Exception("rumble.bthid.init", ex); }
        }

        /// <summary>True once the HID host proxy is bound and sendData() is
        /// reflectable. Cached ~500 ms because the per-frame rumble path polls
        /// it.</summary>
        public static bool Available
        {
            get
            {
                if (!EnsureLoaded() || _ready == null) return false;
                long now = System.Environment.TickCount64;
                // NB: a long.MinValue sentinel here overflowed `now - sentinel`
                // negative, so the cache returned a stale false forever and the
                // controller sink was never enabled. Guard explicitly.
                if (_readyCheckedAt != long.MinValue && now - _readyCheckedAt < 500) return _readyCached;
                _readyCheckedAt = now;
                try
                {
                    object r = _ready.Invoke(null, null);
                    _readyCached = r is Java.Lang.Boolean b && b.BooleanValue();
                }
                catch (System.Exception ex) { _readyCached = false; Log.Exception("rumble.bthid.ready", ex); }
                return _readyCached;
            }
        }

        /// <summary>Send a controller rumble report (motors 0..100). A zero
        /// report stops the motors. Returns false if the controller has no
        /// verified report or the HID stack rejected it.</summary>
        public static bool SendRumble(string deviceNameHint, int vendorId, int productId, byte low, byte high)
        {
            if (!EnsureLoaded() || _send == null) return false;

            string hex = BuildReport(vendorId, productId, deviceNameHint, low, high);
            if (hex == null)
            {
                long n0 = System.Environment.TickCount64;
                if (n0 - _lastSendLog >= 5000)
                {
                    _lastSendLog = n0;
                    Log.Info("[rumble][bthid] no verified report for dev='"
                        + (deviceNameHint ?? "?") + "' vid=0x" + vendorId.ToString("X4")
                        + " pid=0x" + productId.ToString("X4") + " (skipped)");
                }
                return false;
            }

            try
            {
                object r = _send.Invoke(null,
                    new Java.Lang.String(deviceNameHint ?? string.Empty),
                    new Java.Lang.String(hex));
                bool ok = r is Java.Lang.Boolean b && b.BooleanValue();
                long now = System.Environment.TickCount64;
                if (now - _lastSendLog >= 1000)
                {
                    _lastSendLog = now;
                    Log.Info("[rumble][bthid] send dev='" + (deviceNameHint ?? "?")
                        + "' vid=0x" + vendorId.ToString("X4") + " pid=0x" + productId.ToString("X4")
                        + " hex=" + hex + " -> " + ok);
                }
                return ok;
            }
            catch (System.Exception ex) { Log.Exception("rumble.bthid.send", ex); return false; }
        }

        // ---- per-controller report table ----------------------------------

        /// <summary>Return the output report bytes as a hex string for the given
        /// controller, or null if no verified report exists.</summary>
        private static string BuildReport(int vendorId, int productId, string name, byte low, byte high)
        {
            if (IsXboxOne(vendorId, productId, name))
            {
                // SDL3 SDL_hidapi_xboxone.c, Bluetooth branch:
                //   { 0x03, 0x0F, LT, RT, LM, RM, 0xFF, 0x00, 0xEB }
                // motors 0..100. (LT/RT unused here.)
                return "030F0000" + low.ToString("X2") + high.ToString("X2") + "FF00EB";
            }

            // ---- EXTENSION POINT: Sony DualShock 4 (VID 0x054c) -------------
            // Deliberately NOT implemented: the DS4 Bluetooth effects report
            // (report id 0x11) is 78 bytes, requires the controller to be in
            // "enhanced mode" first, and needs a CRC-32 trailer over the hidp
            // header (0xA2) + report bytes. See SDL_hidapi_ps4.c
            // (HIDAPI_DriverPS4_InternalSendJoystickEffect). Add a branch here
            // returning the correct hex once verified on a real DS4; no other
            // plumbing changes are needed.
            // -----------------------------------------------------------------

            return null;
        }

        private static bool IsXboxOne(int vendorId, int productId, string name)
        {
            if (vendorId == 0x045e) // Microsoft
            {
                switch (productId)
                {
                    case 0x02e0: // Xbox One S (Bluetooth)
                    case 0x02fd: // Xbox Wireless Controller (model 1708)
                    case 0x0b00:
                    case 0x0b05:
                    case 0x0b0a:
                    case 0x0b0b:
                    case 0x0b12:
                    case 0x0b13:
                    case 0x0b20:
                        return true;
                }
            }
            return !string.IsNullOrEmpty(name)
                && name.IndexOf("xbox", System.StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
