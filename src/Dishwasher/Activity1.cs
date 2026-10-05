using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using Android.Widget;
using Microsoft.Xna.Framework;
using System;

namespace Dishwasher
{
    [Activity(
        Label = "@string/app_name",
        MainLauncher = true,
        // Approved shield-art launcher icon. @mipmap/ic_launcher is the adaptive
        // icon (API 26+) with legacy mipmap PNG fallbacks; regenerate the whole
        // set with docs/assets/generate-icon-from-art.py.
        Icon = "@mipmap/ic_launcher",
        RoundIcon = "@mipmap/ic_launcher_round",
        AlwaysRetainTaskState = true,
        LaunchMode = LaunchMode.SingleInstance,
        // PORT (orientation, 2026-10-03): the game is a 16:9 landscape title.
        // `UserLandscape` allows BOTH landscape directions, so rotating the
        // phone 180° flips the display and keeps the image upright — while
        // still forbidding portrait. It deliberately RESPECTS the user's
        // system auto-rotate / rotation-lock preference: when auto-rotate is on
        // it follows the sensor within landscape; when rotation is locked it
        // uses the user's chosen landscape. (`SensorLandscape` would ignore the
        // lock; only fall back to it if UserLandscape misbehaves.)
        ScreenOrientation = ScreenOrientation.UserLandscape,
        ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.Keyboard | ConfigChanges.KeyboardHidden | ConfigChanges.ScreenSize
    )]
    public class Activity1 : AndroidGameActivity
    {
        private projectDish.Game1 _game;
        private View _view;
        // PORT (orientation, 2026-10-03): keep the widescreen wrapper and the
        // chosen logical size so a display rotation can re-assert the view
        // transform without creating a second FrameLayout or re-adding the
        // SurfaceView (which would double-wrap it).
        private FrameLayout _root;
        private int _logicalW;
        private float _wsScale;
        // PORT (public build): first-run import screen controller.
        private Dishwasher.Import.ImportController _import;

        protected override void OnCreate(Bundle bundle)
        {
            base.OnCreate(bundle);

            // --- PORT WIDESCREEN: let the window extend under the display
            //     cutout (the punch-hole) so the 1.5x-scaled game view can
            //     cover the entire 2400x1080 panel instead of stopping at the
            //     cutout inset. API 28+. ---
#pragma warning disable CA1416 // version guard below; API 28+ path
            try
            {
                if (Build.VERSION.SdkInt >= BuildVersionCodes.P)
                {
                    Window.Attributes.LayoutInDisplayCutoutMode =
                        LayoutInDisplayCutoutMode.ShortEdges;
                }
            }
            catch (Exception ex) { Dishwasher.Log.Exception("ws.cutout", ex); }
#pragma warning restore CA1416
            // --- END PORT WIDESCREEN ---

            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                Dishwasher.Log.Exception("unhandled",
                    e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString()));

            AndroidContentBootstrap.Prepare();

            // PORT (android settings): read the Android-only settings file at
            // startup (separate from the game's settings.sav).
            Dishwasher.AndroidSettings.Load();

            // PORT (public build): in the content-free public build, require a
            // validated on-device content tree before booting the game. The
            // internal build (IsPublic == false) always skips straight through.
            if (Dishwasher.PublicBuild.IsPublic && !Dishwasher.PublicBuild.ContentReady())
            {
                _import = new Dishwasher.Import.ImportController(this, RestartForGame);
                _import.Show();
                return;
            }

            StartGame();
        }

        // PORT (public build): once the import screen has verified + derived the
        // content, restart the Activity so the game boots through the normal
        // OnCreate->OnResume lifecycle. Starting the Game directly from the
        // import screen skips MonoGame's activity-resumed callback (it already
        // fired while no Game existed), which leaves the GL loop unstarted.
        private void RestartForGame()
        {
            Dishwasher.Log.Info("[import] content ready; restarting into game");
            Recreate();
        }

        // PORT (public build): the original OnCreate body, also reached after the
        // post-import restart.
        private void StartGame()
        {
            _game = new projectDish.Game1();
            _view = _game.Services.GetService(typeof(View)) as View;

            // --- TEMP INPUT DIAGNOSTICS (Input Diagnostics Specialist) ---
            Dishwasher.InputDiagnostics.AttachView(_view);
            Dishwasher.InputDiagnostics.LogAndroidDevices("Activity1.OnCreate");
            // --- END TEMP INPUT DIAGNOSTICS ---

            // --- PORT WIDESCREEN (moved into ApplyWidescreen below so it can be
            //     re-asserted on a display rotation). ---
            ApplyWidescreen(first: true);
            // --- END PORT WIDESCREEN ---

            // PORT: hide the system bars (immersive) for a game-like 16:9 view.
            // Must run after SetContentView so the decor view (and its
            // WindowInsetsController) exists.
            HideSystemBars();

            // --- INPUT FIX: enumerate attached controllers (MonoGame never does) ---
            bool initOk = Dishwasher.AndroidInputBridge.InitializePads();
            Dishwasher.Log.Info("[input][bridge] GamePad.Initialize() invoked=" + initOk
                + " available=" + Dishwasher.AndroidInputBridge.Available);
            Dishwasher.InputDiagnostics.LogAndroidDevices("after GamePad.Initialize()");
            // --- END INPUT FIX ---

            // --- PORT (touch): create the synthetic touch pad (slot 0) so menus
            //     are usable with no controller attached. ---
            Dishwasher.TouchControls.Initialize();
            // --- END PORT (touch) ---

            // --- PORT (rumble): value-aware vibration engine. Needs the
            //     Activity context for the device-vibrator fallback (API<31). ---
            Dishwasher.AndroidRumble.Initialize(this);
            // --- END PORT (rumble) ---

            _game.Run();
        }

        // PORT (public build): route SAF picker results to the import screen.
        protected override void OnActivityResult(int requestCode, Result resultCode, Intent data)
        {
            base.OnActivityResult(requestCode, resultCode, data);
            _import?.OnActivityResult(requestCode, resultCode, data);
        }

        // --- PORT WIDESCREEN: render the game into a logical backbuffer whose
        //     height stays 720 (so vertical world units / character scale are
        //     unchanged) and whose width is widened to the panel aspect. The
        //     SurfaceView is measured at that logical size, so MonoGame's
        //     DisplayMode / viewport / render targets are all logical; a
        //     uniform view scale then fills the physical panel with no
        //     letterbox and no distortion.
        //
        //     `first` is true only from OnCreate: the logical size is chosen
        //     once and the game initialises against it. A 180° rotation keeps
        //     the same app-local landscape window size, so a later call only
        //     re-asserts the existing transform (in place) and never creates a
        //     second FrameLayout or re-adds the SurfaceView.
        //
        //     NB: Resources.DisplayMetrics excludes the display cutout / system
        //     bars (it reported 2168), so use the REAL panel size. ---
        private void ApplyWidescreen(bool first)
        {
            int physW;
            int physH;
            try
            {
                if (Build.VERSION.SdkInt >= BuildVersionCodes.R)
                {
#pragma warning disable CA1416 // guarded by the SDK check above
                    var real = WindowManager.CurrentWindowMetrics.Bounds;
                    physW = Math.Max(real.Width(), real.Height());
                    physH = Math.Min(real.Width(), real.Height());
#pragma warning restore CA1416
                }
                else
                {
#pragma warning disable CA1422 // legacy size query for API < 30
                    var real = new Android.Graphics.Point();
                    WindowManager.DefaultDisplay.GetRealSize(real);
                    physW = Math.Max(real.X, real.Y);
                    physH = Math.Min(real.X, real.Y);
#pragma warning restore CA1422
                }
            }
            catch (Exception)
            {
                var dm = Resources.DisplayMetrics;
                physW = Math.Max(dm.WidthPixels, dm.HeightPixels);
                physH = Math.Min(dm.WidthPixels, dm.HeightPixels);
            }
            float wsScale = (float)physH / 720f;
            int logicalW = (int)Math.Round(physW / wsScale);
            logicalW = (logicalW / 2) * 2; // even: render targets half to integer

            if (first || !WidescreenConfig.Enabled)
            {
                WidescreenConfig.LogicalWidth = logicalW;
                WidescreenConfig.LogicalHeight = 720;
                WidescreenConfig.Scale = wsScale;
                WidescreenConfig.Enabled = true;
            }
            else if (logicalW != WidescreenConfig.LogicalWidth
                || Math.Abs(wsScale - WidescreenConfig.Scale) > 0.0001f)
            {
                // Never expected for a 180° rotation (same panel, same
                // app-local landscape size). If it ever happens, keep the size
                // the game already initialised against so the render targets /
                // world stay consistent, and log the discrepancy.
                Dishwasher.Log.Info("[ws] panel changed after init (panel=" + physW + "x" + physH
                    + " logical would be " + logicalW + " scale=" + wsScale.ToString("0.####")
                    + ") keeping " + WidescreenConfig.LogicalWidth + "x720 scale="
                    + WidescreenConfig.Scale.ToString("0.####"));
            }
            _logicalW = WidescreenConfig.LogicalWidth;
            _wsScale = WidescreenConfig.Scale;

            if (_root == null)
            {
                _root = new FrameLayout(this);
                _root.SetBackgroundColor(Android.Graphics.Color.Black);
                _root.AddView(_view, new FrameLayout.LayoutParams(_logicalW, 720));
                SetContentView(_root);
            }

            // Idempotent: update the existing layout params in place and
            // re-assert pivot (0,0) + uniform scale. Never adds another child.
            var lp = _view.LayoutParameters as FrameLayout.LayoutParams;
            if (lp != null && (lp.Width != _logicalW || lp.Height != 720))
            {
                lp.Width = _logicalW;
                lp.Height = 720;
                _view.LayoutParameters = lp;
            }
            _view.PivotX = 0f;
            _view.PivotY = 0f;
            _view.ScaleX = _wsScale;
            _view.ScaleY = _wsScale;

            Dishwasher.Log.Info("[ws] panel=" + physW + "x" + physH
                + " logical=" + _logicalW + "x720 scale=" + _wsScale.ToString("0.####")
                + " view=" + _view.Width + "x" + _view.Height
                + (first ? " (initial)" : " (re-apply)"));
        }
        // --- END PORT WIDESCREEN ---

        // --- PORT: immersive fullscreen (hide status + navigation bars) ---
#pragma warning disable CA1416 // version checks below; API 30+ path is guarded
        private void HideSystemBars()
        {
            try
            {
                if (Build.VERSION.SdkInt >= BuildVersionCodes.R)
                {
                    Window.SetDecorFitsSystemWindows(false);
                    var controller = Window.InsetsController;
                    if (controller != null)
                    {
                        controller.Hide(WindowInsets.Type.SystemBars());
                        controller.SystemBarsBehavior =
                            (int)WindowInsetsControllerBehavior.ShowTransientBarsBySwipe;
                    }
                }
                else
                {
#pragma warning disable CS0618 // SystemUiVisibility is deprecated but required below API 30
                    Window.DecorView.SystemUiVisibility = (StatusBarVisibility)(
                        SystemUiFlags.HideNavigation | SystemUiFlags.Fullscreen |
                        SystemUiFlags.ImmersiveSticky | SystemUiFlags.LayoutFullscreen |
                        SystemUiFlags.LayoutHideNavigation);
#pragma warning restore CS0618
                }
            }
            catch (Exception ex)
            {
                Dishwasher.Log.Exception("immersive", ex);
            }
        }
#pragma warning restore CA1416

        public override void OnWindowFocusChanged(bool hasFocus)
        {
            base.OnWindowFocusChanged(hasFocus);
            if (hasFocus)
            {
                HideSystemBars();
            }
            else
            {
                Dishwasher.AndroidRumble.StopAll();   // PORT (rumble): focus loss
            }
        }
        // --- END PORT: immersive fullscreen ---

        // --- PORT (orientation, 2026-10-03): the activity handles the
        //     Orientation/ScreenSize config change (it is NOT recreated), so
        //     re-assert the widescreen view transform + cutout + immersive
        //     state after the display rotates 180° under UserLandscape. A
        //     landscape→landscape flip usually leaves the app-local size (and
        //     therefore the logical backbuffer/scale) unchanged, so this is a
        //     harmless no-op then; it exists so a genuine window/inset change
        //     on some vendor build cannot leave the SurfaceView mis-scaled.
        //     ApplyWidescreen is idempotent (never double-wraps the view). ---
        public override void OnConfigurationChanged(Android.Content.Res.Configuration newConfig)
        {
            base.OnConfigurationChanged(newConfig);
            try
            {
#pragma warning disable CA1416 // guarded by the SDK check below; API 28+ path
                if (Build.VERSION.SdkInt >= BuildVersionCodes.P)
                {
                    Window.Attributes.LayoutInDisplayCutoutMode =
                        LayoutInDisplayCutoutMode.ShortEdges;
                }
#pragma warning restore CA1416
                ApplyWidescreen(first: false);
                HideSystemBars();
                Dishwasher.Log.Info("[orient] onConfigurationChanged orientation="
                    + newConfig.Orientation + " logical=" + WidescreenConfig.LogicalWidth
                    + "x720 scale=" + WidescreenConfig.Scale.ToString("0.####"));
            }
            catch (Exception ex)
            {
                Dishwasher.Log.Exception("orient.configchanged", ex);
            }
        }
        // --- END PORT (orientation) ---

        // --- PORT AUTOSAVE: make progress durable when the app is backgrounded
        //     or swiped away. Calls the game's own Player.Write()/
        //     Settings.Write() (see Platform/SaveAutosave.cs); those are the
        //     same routines the 360 build uses at level completion. Runs after
        //     base.OnPause/OnStop so the game loop is already suspending.
        //     Revert: delete these two overrides. ---
        protected override void OnPause()
        {
            base.OnPause();
            Dishwasher.AndroidRumble.StopAll();   // PORT (rumble): never leave a motor held
            Dishwasher.SaveAutosave.Save("OnPause");
        }

        protected override void OnStop()
        {
            base.OnStop();
            Dishwasher.AndroidRumble.StopAll();   // PORT (rumble): never leave a motor held
            Dishwasher.SaveAutosave.Save("OnStop");
        }
        // --- END PORT AUTOSAVE ---

        // --- TEMP INPUT DIAGNOSTICS + INPUT BRIDGE ---
        public override bool DispatchKeyEvent(KeyEvent e)
        {
            // --- TEMP RENDER DIAGNOSTICS: volume keys switch experiment mode.
            //     Gated so the harness is inert by default: when
            //     RenderDiagnostics.Enabled == false the volume keys are not
            //     swallowed and reach the system as normal. ---
            if (Dishwasher.RenderDiagnostics.Enabled && e != null && e.Action == KeyEventActions.Down)
            {
                int kc = (int)e.KeyCode;
                if (kc == (int)Keycode.VolumeUp) { Dishwasher.RenderDiagnostics.Bump(1); return true; }
                if (kc == (int)Keycode.VolumeDown) { Dishwasher.RenderDiagnostics.Bump(-1); return true; }
            }
            // --- END TEMP RENDER DIAGNOSTICS ---

            // --- PORT (touch): Android BACK / back-gesture = the game's menu
            //     cancel (B), but only while in a menu. TouchControls.HandleBack
            //     returns false in gameplay, in which case the platform default
            //     (finish the Activity) still applies. ---
            if (e != null && (int)e.KeyCode == (int)Keycode.Back
                && e.Action == KeyEventActions.Down
                && Dishwasher.TouchControls.HandleBack())
            {
                return true;
            }
            // --- END PORT (touch BACK) ---

            Dishwasher.AndroidInputBridge.RouteKey(e);          // INPUT FIX
            Dishwasher.InputDiagnostics.LogKeyEvent(e);
            bool handled = base.DispatchKeyEvent(e);
            // Keep the synthetic touch overlay on top of whatever MonoGame's own
            // key path just wrote into the same AndroidGamePad.
            Dishwasher.AndroidInputBridge.ReapplyTouchState();  // PORT (touch)
            return handled;
        }

        // --- PORT (touch): deliver touch screens to the gesture recogniser.
        //     Never consumed, so MonoGame still sees the events if it wants
        //     them. ---
        public override bool DispatchTouchEvent(MotionEvent e)
        {
            // PORT (touchpad): the gameplay overlay consumes its own touches so
            // they never reach the framework; menu gestures return false and
            // fall through to the normal path.
            if (Dishwasher.TouchControls.OnTouch(e)) return true;
            return base.DispatchTouchEvent(e);
        }
        // --- END PORT (touch) ---

        // --- PORT (touch): fallback for Android BACK when it is not seen as a
        //     KeyEvent (predictive back / gesture navigation). ---
        public override void OnBackPressed()
        {
            // PORT (public build): on the import screen BACK should leave the app.
            if (_import != null)
            {
                base.OnBackPressed();
                return;
            }
            if (!Dishwasher.TouchControls.HandleBack())
            {
                base.OnBackPressed();
            }
        }
        // --- END PORT (touch) ---

        public override bool DispatchGenericMotionEvent(MotionEvent e)
        {
            Dishwasher.AndroidInputBridge.RouteMotion(e);       // INPUT FIX
            Dishwasher.InputDiagnostics.LogMotionEvent(e);
            return base.DispatchGenericMotionEvent(e);
        }
        // --- END TEMP INPUT DIAGNOSTICS + INPUT BRIDGE ---
    }
}
