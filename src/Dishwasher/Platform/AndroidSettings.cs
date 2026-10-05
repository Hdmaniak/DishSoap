// ============================================================================
// PORT (android settings) --  Android-only settings tree.
//
// This is deliberately SEPARATE from the game's own projectDish.Settings /
// `settings.sav`.  Android-only preferences live in their own file so the
// original game settings layout/indices stay pristine and the two never
// interfere:
//
//     <app-private>/TheDishwasher/android_settings.sav      (XML, XmlSerializer)
//
// The directory is the same app-private container the game already uses
// (`StorageContainer.Path` == Environment.SpecialFolder.Personal +
// "/TheDishwasher", see Platform/Shim_Storage.cs) but the *file* is new.
//
// Consumers:
//   * FPS LOCK is applied to the game loop by Platform/FrameLimiter.cs
//     (IsFixedTimeStep / TargetElapsedTime), at startup and on change.
//   * SHOW FPS COUNTER is read by Platform/PerfOverlay.cs, which measures
//     the presented frame rate and draws it on screen.
//
// Revert: delete this file, Platform/AndroidSettingsMenu.cs,
//         Platform/FrameLimiter.cs and Platform/PerfOverlay.cs, and the
//         clearly-marked `PORT (android settings)` / `PORT (fps overlay)` /
//         `PORT FRAME LIMITER` hooks in MainMenu.cs + Game1.cs +
//         the Activity1.cs / SaveAutosave.cs calls.  Nothing else changes.
// ============================================================================
using System;
using System.IO;
using System.Xml.Serialization;

namespace Dishwasher
{
    /// <summary>
    /// Persistent Android-only settings.  Static, lazily loaded; never touches
    /// the game's Settings/settings.sav.
    /// </summary>
    public static class AndroidSettings
    {
        // FPS LOCK mapping.  Order must match the labels built in
        // Platform/AndroidSettingsMenu.cs (fps lock: unlimited/30/60/120).
        public const int FPS_UNLIMITED = 0;
        public const int FPS_30 = 1;
        public const int FPS_60 = 2;
        public const int FPS_120 = 3;
        public const int FPS_LOCK_STATES = 4;

        // VIBRATION routing (added in version 2): which output(s) the game's
        // rumble value is sent to.  Order is the UI cycle order.
        //   OFF        -- nothing vibrates.
        //   PHONE      -- the Android device vibrator only.
        //   CONTROLLER -- the connected gamepad only.
        //   BOTH       -- both.
        public const int VIB_OFF = 0;
        public const int VIB_PHONE = 1;
        public const int VIB_CONTROLLER = 2;
        public const int VIB_BOTH = 3;
        public const int VIB_STATES = 4;

        // Default for a save that predates the VIBRATION row (or a corrupt
        // value): BOTH.  Rationale: it is a strict superset of the old
        // behaviour (the device always vibrated before) and it also drives a
        // reachable gamepad, so nothing that used to work stops working and
        // the new capability is on by default.  Users who dislike the phone
        // buzz can move to CONTROLLER.
        public const int VIB_DEFAULT = VIB_BOTH;

        // Persisted format version.  2 = VIBRATION added; older files (version
        // < 2) have no <vibration> element, so they default to VIB_DEFAULT
        // instead of the int default 0 (= OFF).
        public const int VERSION = 2;

        // TOUCH CONTROLS: single ON/OFF full-controller overlay
        // (Platform/TouchGamepadOverlay.cs).  Default OFF.
        public const string FileName = "android_settings.sav";

        /// <summary>Serialized payload.  Unknown/missing elements are tolerated
        /// by XmlSerializer (=&gt; forward/backward compatible).</summary>
        [Serializable]
        public struct Data
        {
            public int version;
            public int fpsLock;
            // Added for SHOW FPS COUNTER.  XmlSerializer leaves a missing
            // element at its default (= false), so old files load fine.
            public bool showFps;
            // Added for TOUCH CONTROLS (true = ON).  Missing element loads as
            // false (OFF), so old files load fine.
            public bool touchOn;
            // Added for the TOUCH LAYOUT EDITOR: normalised block centres,
            // "x0,y0;x1,y1;...".  Missing/empty => the shipped default layout.
            public string touchLayout;
            // Added for ANDROID SETTINGS > VIBRATION (VIB_OFF..VIB_BOTH).  A
            // missing element loads as 0 (= OFF), so LoadInternal gates it on
            // <version> and forces VIB_DEFAULT for old files.
            public int vibration;
        }

        private static readonly object _lock = new object();
        private static bool _loaded;
        private static Data _data;
        private static string _path;

        /// <summary>Full path of the Android settings file (same app-private
        /// directory as settings.sav).</summary>
        public static string FilePath
        {
            get
            {
                if (_path == null)
                {
                    string dir = new Microsoft.Xna.Framework.Storage.StorageContainer().Path;
                    _path = Path.Combine(dir, FileName);
                }
                return _path;
            }
        }

        /// <summary>Current FPS LOCK value (0..3); triggers a load on first use.</summary>
        public static int FpsLock
        {
            get { lock (_lock) { if (!_loaded) LoadInternal(); return _data.fpsLock; } }
        }

        /// <summary>Current SHOW FPS COUNTER value (default false); triggers a
        /// load on first use.</summary>
        public static bool ShowFps
        {
            get { lock (_lock) { if (!_loaded) LoadInternal(); return _data.showFps; } }
        }

        /// <summary>Current TOUCH CONTROLS value (true = ON); triggers a load
        /// on first use.</summary>
        public static bool TouchOn
        {
            get { lock (_lock) { if (!_loaded) LoadInternal(); return _data.touchOn; } }
        }

        /// <summary>Serialized normalised touch-layout block centres (may be
        /// null/empty => shipped default); triggers a load on first use.</summary>
        public static string TouchLayoutRaw
        {
            get { lock (_lock) { if (!_loaded) LoadInternal(); return _data.touchLayout; } }
        }

        /// <summary>Current VIBRATION routing (VIB_OFF..VIB_BOTH); triggers a
        /// load on first use.</summary>
        public static int Vibration
        {
            get { lock (_lock) { if (!_loaded) LoadInternal(); return _data.vibration; } }
        }

        /// <summary>Clamp an arbitrary value to a valid FPS LOCK state.</summary>
        public static int ClampFps(int value)
        {
            if (value < 0 || value >= FPS_LOCK_STATES) return FPS_UNLIMITED;
            return value;
        }

        /// <summary>Clamp an arbitrary value to a valid VIBRATION state.</summary>
        public static int ClampVib(int value)
        {
            if (value < 0 || value >= VIB_STATES) return VIB_DEFAULT;
            return value;
        }

        /// <summary>Set + persist FPS LOCK (written on change).</summary>
        public static void SetFpsLock(int value)
        {
            lock (_lock)
            {
                if (!_loaded) LoadInternal();
                _data.fpsLock = ClampFps(value);
            }
            Save();
        }

        /// <summary>Set + persist SHOW FPS COUNTER (written on change).</summary>
        public static void SetShowFps(bool value)
        {
            lock (_lock)
            {
                if (!_loaded) LoadInternal();
                _data.showFps = value;
            }
            Save();
        }

        /// <summary>Set + persist the TOUCH CONTROLS switch (written on change).</summary>
        public static void SetTouchControls(bool value)
        {
            lock (_lock)
            {
                if (!_loaded) LoadInternal();
                _data.touchOn = value;
            }
            Save();
        }

        /// <summary>Set + persist the normalised touch-layout string (written on
        /// SAVE / RESET from the layout editor).</summary>
        public static void SetTouchLayout(string value)
        {
            lock (_lock)
            {
                if (!_loaded) LoadInternal();
                _data.touchLayout = value;
            }
            Save();
        }

        /// <summary>Set + persist the VIBRATION routing (written on change).
        /// Applied immediately by AndroidRumble.ApplyMode().</summary>
        public static void SetVibration(int value)
        {
            lock (_lock)
            {
                if (!_loaded) LoadInternal();
                _data.vibration = ClampVib(value);
            }
            Save();
        }

        /// <summary>Read the file at startup.  Safe to call more than once.</summary>
        public static void Load()
        {
            lock (_lock) { LoadInternal(); }
        }

        /// <summary>Write the current values.  Safe if never loaded (loads first).</summary>
        public static void Save()
        {
            lock (_lock)
            {
                if (!_loaded) LoadInternal();
                try
                {
                    string path = FilePath;
                    string dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    using (FileStream fs = File.Open(path, FileMode.Create))
                    {
                        XmlSerializer ser = new XmlSerializer(typeof(Data));
                        ser.Serialize(fs, _data);
                    }
                }
                catch (Exception ex)
                {
                    Log.Exception("android-settings.save", ex);
                }
            }
        }

        private static void LoadInternal()
        {
            _loaded = true;
            Data data = default(Data);
            try
            {
                string path = FilePath;
                if (File.Exists(path))
                {
                    using (FileStream fs = File.Open(path, FileMode.Open, FileAccess.Read))
                    {
                        XmlSerializer ser = new XmlSerializer(typeof(Data));
                        data = (Data)ser.Deserialize(fs);
                    }
                }
            }
            catch (Exception ex)
            {
                // Corrupt/unreadable file: fall back to defaults in memory.
                Log.Exception("android-settings.load", ex);
                data = default(Data);
            }
            data.fpsLock = ClampFps(data.fpsLock);
            // VIBRATION was added after the original format: a file without a
            // <version> (or version < 2) predates the field, so default it
            // rather than reading the struct's int default 0 (= OFF).
            if (data.version < VERSION) data.vibration = VIB_DEFAULT;
            data.vibration = ClampVib(data.vibration);
            data.version = VERSION;
            _data = data;
        }
    }
}
