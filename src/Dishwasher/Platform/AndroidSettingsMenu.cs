// ============================================================================
// PORT (android settings) --  "ANDROID SETTINGS" screen.
//
// This is a partial-class extension of the game's own MainMenu, so it reuses
// the exact same visual style / navigation / text system (drawOption,
// hitButton, DrawMenuHeading) WITHOUT putting Android-only screen code into
// the original GameSource/projectDish/MainMenu.cs.  The original file only
// holds six small, clearly-marked dispatch hooks:
//
//   1. class MainMenu is declared `partial`
//   2. main menu draw (case 0): the ANDROID SETTINGS row + wrap bounds
//   3. main menu A-press (case 0): route the new row to this screen
//   4. B/back switch: route the new level back to the main menu
//   5. A-press switch (keyJump): call AndroidSettingsActivate()
//   6. DrawButtons: case ANDROID_SETTINGS_LEVEL -> DrawAndroidSettingsScreen()
//   plus getCurScreen()/drawOption()/hitButton() share the settings layout.
//
// Everything else lives here.  Revert: delete this file + AndroidSettings.cs
// + the marked hooks.
//
// Frame limiting is applied by Platform/FrameLimiter.cs; the counter is drawn
// by Platform/PerfOverlay.cs.
// ============================================================================
using Dishwasher;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace projectDish
{
    public partial class MainMenu
    {
        /// <summary>Menu-state id for the Android settings screen.  Chosen in
        /// the unused 42+ range; levelSelOption[] is sized 64.</summary>
        public const int ANDROID_SETTINGS_LEVEL = 42;

        private const int ANDROID_ROW_FPS = 0;
        private const int ANDROID_ROW_FPSCOUNTER = 1;
        private const int ANDROID_ROW_TOUCH = 2;
        private const int ANDROID_ROW_EDIT = 3;
        private const int ANDROID_ROW_BACK = 4;
        private const int ANDROID_ROW_COUNT = 5;

        // Built lazily (not in a field initializer): StringContainer's special
        // -symbol parsing depends on Globals.spriteFontMode, which is only set
        // during content load.  Literals are lowercase for the same reason the
        // game's own programmatic labels are (A/B/X/Y glyphs are reserved);
        // the renderer upper-cases them.
        private StringContainer _androidSettingsLabel;
        private StringContainer _androidTitleStr;
        private StringContainer[] _androidFpsStr;
        private StringContainer[] _androidShowFpsStr;
        private StringContainer[] _androidTouchStr;
        private StringContainer _androidEditStr;

        private void EnsureAndroidText()
        {
            if (_androidSettingsLabel != null) return;
            _androidSettingsLabel = new StringContainer("android settings");
            _androidTitleStr = new StringContainer("android settings");
            _androidFpsStr = new StringContainer[AndroidSettings.FPS_LOCK_STATES]
            {
                new StringContainer("fps lock: unlimited"),
                new StringContainer("fps lock: 30"),
                new StringContainer("fps lock: 60"),
                new StringContainer("fps lock: 120")
            };
            _androidShowFpsStr = new StringContainer[2]
            {
                new StringContainer("show fps counter: off"),
                new StringContainer("show fps counter: on")
            };
            // OFF / ON -- single full-controller on-screen pad (see
            // Platform/TouchGamepadOverlay.cs).  No trademarked art.
            _androidTouchStr = new StringContainer[2]
            {
                new StringContainer("touch controls: off"),
                new StringContainer("touch controls: on")
            };
            _androidEditStr = new StringContainer("edit touch layout");
        }

        /// <summary>Label for the top-level main-menu row.  Also triggers the
        /// lazy text build at a point where spriteFontMode is final.</summary>
        private StringContainer GetAndroidSettingsLabel()
        {
            EnsureAndroidText();
            return _androidSettingsLabel;
        }

        /// <summary>True for menu levels that use the "settings list" layout
        /// (the original Options screen, 34, and the Android settings screen).
        /// Used by drawOption/hitButton so both look identical.</summary>
        private bool IsSettingsLikeLayout()
        {
            return level == 34 || level == ANDROID_SETTINGS_LEVEL;
        }

        /// <summary>Render the Android settings screen.  Called from
        /// DrawButtons' switch on `level`.</summary>
        private void DrawAndroidSettingsScreen(Texture2D spriteTex, Texture2D TextTex, Text text,
            SpriteBatch sprite, Texture2D comicText, Texture2D jTextTex, float tAdd)
        {
            EnsureAndroidText();
            DrawMenuHeading(new Vector2(445f + tAdd, 60f), 1.8f, 0.3f, _androidTitleStr, jTextTex, sprite, text);
            if (selOption < 0)
            {
                selOption = ANDROID_ROW_COUNT - 1;
            }
            if (selOption > ANDROID_ROW_COUNT - 1)
            {
                selOption = 0;
            }
            drawOption(ANDROID_ROW_FPS, _androidFpsStr[AndroidSettings.FpsLock],
                sprite, spriteTex, comicText, text, jTextTex);
            drawOption(ANDROID_ROW_FPSCOUNTER,
                _androidShowFpsStr[AndroidSettings.ShowFps ? 1 : 0],
                sprite, spriteTex, comicText, text, jTextTex);
            drawOption(ANDROID_ROW_TOUCH,
                _androidTouchStr[AndroidSettings.TouchOn ? 1 : 0],
                sprite, spriteTex, comicText, text, jTextTex);
            drawOption(ANDROID_ROW_EDIT, _androidEditStr,
                sprite, spriteTex, comicText, text, jTextTex);
            drawOption(ANDROID_ROW_BACK, Globals.maintext._backStr,
                sprite, spriteTex, comicText, text, jTextTex);
        }

        /// <summary>A-press on the Android settings screen.  Called from the
        /// game's keyJump switch.</summary>
        private void AndroidSettingsActivate()
        {
            EnsureAndroidText();
            switch (selOption)
            {
                case ANDROID_ROW_FPS:
                    // Cycles Unlimited -> 30 -> 60 -> 120 and applies the new
                    // lock to the game loop immediately.
                    AndroidSettings.SetFpsLock((AndroidSettings.FpsLock + 1) % AndroidSettings.FPS_LOCK_STATES);
                    Dishwasher.FrameLimiter.Apply();
                    break;
                case ANDROID_ROW_FPSCOUNTER:
                    AndroidSettings.SetShowFps(!AndroidSettings.ShowFps);
                    break;
                case ANDROID_ROW_TOUCH:
                    // Toggle OFF/ON; applied immediately (drawn + touch-routed
                    // only in active gameplay).
                    {
                        bool next = !AndroidSettings.TouchOn;
                        AndroidSettings.SetTouchControls(next);
                        TouchGamepadOverlay.On = next;
                        Log.Info("[touchpad] " + (next ? "ON" : "OFF"));
                    }
                    break;
                case ANDROID_ROW_EDIT:
                    // Enter the drag-to-arrange editor (renders over this menu,
                    // consumes all touches, BACK/its BACK button exits).
                    TouchGamepadOverlay.EnterEditor();
                    Sound.playCue("sword1");
                    break;
                case ANDROID_ROW_BACK:
                    hitButton(selOption, Globals.maintext._backStr);
                    AndroidSettings.Save();
                    transType = 1;
                    transFrame = 0f;
                    transGoal = 0;
                    break;
            }
        }

        /// <summary>B / back on the Android settings screen.  Called from the
        /// game's keyGrab switch.</summary>
        private void AndroidSettingsBack()
        {
            AndroidSettings.Save();
            transType = 1;
            transFrame = 0f;
            transGoal = 0;
        }
    }
}
