// ============================================================================
// PORT (touchpad) -- optional on-screen gameplay gamepad + layout editor.
//
// The user asked for an optional full-controller overlay on the game view,
// selectable in ANDROID SETTINGS as TOUCH CONTROLS: OFF / ON (default OFF,
// persisted in android_settings.sav), plus an EDIT TOUCH LAYOUT screen where
// the controls are dragged around as whole BLOCKS and saved.
//
//   LEFT  (top->bottom): LT, LB | D-pad (up/left/right/down chevrons) | L3 |
//                        large left analog stick
//   RIGHT (top->bottom): RB, RT | large right analog stick | face diamond
//                        Y(top) X(left) B(right) A(bottom) | R3
//
// Blocks (the draggable units): LT/LB pair, D-pad, L3, left stick, RB/RT pair,
// right stick, A/B/X/Y cluster, R3.  Positions are stored NORMALISED (0..1 of
// the logical backbuffer) so the layout survives any resolution/aspect/widescreen
// change; the default positions are exactly the shipped reference layout, so an
// existing player sees no change until they open the editor.
//
// --- visibility rule --------------------------------------------------------
// OFF => fully inert (no draw, no routing; menu tap-to-select unchanged).
// ON  => drawn AND touch-routed while `Game1.portTouchPadContext` (in-level:
//        live gameplay + the in-game menus over it), NOT during comics/cutscenes
//        or the death cinematic.  Front-end screens (gameMode != 0) never show
//        it.  The edit screen is the ONE documented exception: while
//        `EditorActive` the pad is shown and interactive over a dimmed backdrop,
//        regardless of the gameplay predicate; closing the editor restores the
//        normal rule.  While the editor is open it consumes all touches, and the
//        Android BACK key exits the editor.
//
// --- input seam -------------------------------------------------------------
// The game only reads GamePad; it never reads touch.  This overlay synthesises
// buttons, both sticks and triggers and pushes them through
// AndroidInputBridge.SetTouchState() into MonoGame's internal AndroidGamePad at
// slot 0.  Hardware + touch are merged (a deflected physical stick wins).
//
// Revert: delete this file + the touchOn/touchLayout fields & menu rows in
// Platform and the `PORT (touchpad)` hooks in Game1.cs, TouchControls.cs and
// Activity1.cs.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using projectDish;

namespace Dishwasher
{
    public static class TouchGamepadOverlay
    {
        /// <summary>True when the on-screen pad is enabled (ANDROID SETTINGS).
        /// Does NOT imply it is currently shown -- see Game1.portTouchPadContext.</summary>
        public static bool On = false;

        /// <summary>True while the layout editor is open (explicit exception to
        /// the gameplay-only rule).</summary>
        public static bool EditorActive = false;

        // Control kinds.
        private const int K_FACE = 0;
        private const int K_DPAD = 1;
        private const int K_BUMPER = 2;
        private const int K_TRIGGER = 3;
        private const int K_STICKBTN = 4;
        private const int K_STICK = 5;
        private const int K_SYS = 6;   // START / BACK

        // Face roles.
        private const int ROLE_A = 0;
        private const int ROLE_B = 1;
        private const int ROLE_X = 2;
        private const int ROLE_Y = 3;

        private const int TRIG_LEFT = 1;
        private const int TRIG_RIGHT = 2;
        private const int STICK_LEFT = 1;
        private const int STICK_RIGHT = 2;

        // Blocks (draggable units).
        private const int BlockCount = 10;
        private const int BLK_LTLB = 0;
        private const int BLK_DPAD = 1;
        private const int BLK_L3 = 2;
        private const int BLK_LSTICK = 3;
        private const int BLK_RBRT = 4;
        private const int BLK_RSTICK = 5;
        private const int BLK_FACE = 6;
        private const int BLK_R3 = 7;
        private const int BLK_START = 8;
        private const int BLK_BACK = 9;

        // Shipped reference centres, normalised to the logical backbuffer.
        private static readonly Vector2[] DefaultCenters = new Vector2[BlockCount]
        {
            new Vector2(0.100f, 0.10f), // LT/LB
            new Vector2(0.115f, 0.36f), // D-pad
            new Vector2(0.050f, 0.80f), // L3
            new Vector2(0.155f, 0.72f), // left stick
            new Vector2(0.900f, 0.10f), // RB/RT
            new Vector2(0.845f, 0.42f), // right stick
            new Vector2(0.885f, 0.75f), // A/B/X/Y cluster
            new Vector2(0.950f, 0.90f), // R3
            new Vector2(0.440f, 0.24f), // START / PAUSE
            new Vector2(0.560f, 0.24f), // BACK / inventory
        };

        private static Vector2[] _centers = (Vector2[])DefaultCenters.Clone();
        private static float[] _scales = NewDefaultScales();
        private static Vector2[] _editorBackup;
        private static float[] _editorScaleBackup;

        private const float ScaleMin = 0.5f;
        private const float ScaleMax = 2.0f;
        private static float[] NewDefaultScales()
        {
            float[] s = new float[BlockCount];
            for (int i = 0; i < s.Length; i++) s[i] = 1f;
            return s;
        }
        private static float ClampScale(float s)
        {
            if (float.IsNaN(s) || s <= 0f) return 1f;
            return s < ScaleMin ? ScaleMin : (s > ScaleMax ? ScaleMax : s);
        }

        private struct Ctrl
        {
            public int kind;
            public Buttons buttons;
            public int role;
            public int trigger;
            public int stick;
            public int dir;
            public int block;
            public Vector2 center;
            public float radius;
            public string label;
        }

        private static Ctrl[] _ctrls;
        private static float _builtW = -1f, _builtH = -1f;
        private static int _stickLIndex = -1, _stickRIndex = -1;

        // ------------------------------------------------------------ input state
        private const int MaxPointers = 32;
        private const float StickDeadzone = 0.15f;

        private static readonly object _lock = new object();
        private static readonly int[] _ptrCtrl = new int[MaxPointers];
        private static readonly bool[] _down = new bool[32];
        private static Vector2 _lDir, _rDir, _lOut, _rOut;
        private static float _lMag, _rMag;
        private static bool _anyBound;

        // ---------------------------------------------------------- editor state
        private static int _dragBlock = -1;
        private static bool _dragMoved;
        private static int _selected = -1;
        private static Vector2 _dragOffset;          // logical px
        private static Rectangle _btnSave, _btnReset, _btnBack; // logical px
        private static Rectangle _btnMinus, _btnPlus;           // logical px
        private static Rectangle _comicSpeedUp, _comicSkip;     // logical px
        private static int _comicDown;               // 0 none, 1 speed-up, 2 skip

        // ------------------------------------------------------------ draw cache
        private static GraphicsDevice _gd;
        private static Texture2D _ringSmall, _ringBig, _knob, _disc, _tri, _pixel;
        private static readonly Dictionary<string, Texture2D> _labels = new Dictionary<string, Texture2D>();
        private static GraphicsDevice _labelGd;

        private static bool _shownLast;

#if DEBUG
        private static Buttons _logButtons = Buttons.None;
        private static bool _logStickL, _logStickR;
#endif

        static TouchGamepadOverlay()
        {
            for (int i = 0; i < MaxPointers; i++) _ptrCtrl[i] = -1;
        }

        public static void Initialize()
        {
            On = AndroidSettings.TouchOn;
            LoadCenters(AndroidSettings.TouchLayoutRaw);
            Log.Info("[touchpad] initialized, on=" + On + " blocks=" + _centers.Length);
        }

        // =====================================================================
        //  PERSISTENCE  (normalised block centres)
        // =====================================================================
        private static string SerializeCenters()
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < _centers.Length; i++)
            {
                if (i > 0) sb.Append(';');
                sb.Append(_centers[i].X.ToString("0.####", CultureInfo.InvariantCulture));
                sb.Append(',');
                sb.Append(_centers[i].Y.ToString("0.####", CultureInfo.InvariantCulture));
                sb.Append(',');
                sb.Append(_scales[i].ToString("0.###", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        private static void LoadCenters(string s)
        {
            _centers = (Vector2[])DefaultCenters.Clone();
            _scales = NewDefaultScales();
            ParseInto(s);
            Invalidate();
        }

        // Accepts "x,y[,s];…" for 8 or 10 blocks.  Missing scale => 1.0.
        private static void ParseInto(string s)
        {
            if (string.IsNullOrEmpty(s)) return;
            string[] parts = s.Split(';');
            if (parts.Length != 8 && parts.Length != BlockCount) return;
            for (int i = 0; i < parts.Length; i++)
            {
                string[] vals = parts[i].Split(',');
                if (vals.Length < 2 || vals.Length > 3) return;
                float x, y;
                if (!float.TryParse(vals[0], NumberStyles.Float, CultureInfo.InvariantCulture, out x)) return;
                if (!float.TryParse(vals[1], NumberStyles.Float, CultureInfo.InvariantCulture, out y)) return;
                _centers[i] = new Vector2(Clamp01(x), Clamp01(y));
                if (vals.Length == 3)
                {
                    float sc;
                    if (float.TryParse(vals[2], NumberStyles.Float, CultureInfo.InvariantCulture, out sc))
                        _scales[i] = ClampScale(sc);
                }
            }
        }

        private static float Clamp01(float v) { return v < 0f ? 0f : (v > 1f ? 1f : v); }

        private static void Invalidate()
        {
            _ctrls = null;
            _builtW = -1f;
            _builtH = -1f;
        }

        // =====================================================================
        //  LAYOUT / GEOMETRY  (shared by draw + hit-test)
        // =====================================================================
        private static float ScreenW()
        {
            if (WidescreenConfig.Enabled && WidescreenConfig.LogicalWidth > 0) return WidescreenConfig.LogicalWidth;
            if (Globals.screenSize.X > 0f) return Globals.screenSize.X;
            return 1600f;
        }

        private static float ScreenH()
        {
            if (WidescreenConfig.Enabled && WidescreenConfig.LogicalHeight > 0) return WidescreenConfig.LogicalHeight;
            if (Globals.screenSize.Y > 0f) return Globals.screenSize.Y;
            return 720f;
        }

        private static Vector2 BlockCenterPx(int block, float w, float h)
        {
            Vector2 n = _centers[block];
            return new Vector2(n.X * w, n.Y * h);
        }

        private static Vector2 BlockHalfNormalised(int block, float w, float h)
        {
            float ctlR = h * 0.045f, dpadR = h * 0.050f, stickR = h * 0.160f;
            float pairHalf = w * 0.045f, dstep = h * 0.10f, fstep = h * 0.095f;
            float hx, hy;
            switch (block)
            {
                case BLK_LTLB:
                case BLK_RBRT: hx = (pairHalf + ctlR) / w; hy = ctlR / h; break;
                case BLK_DPAD: hx = (dstep + dpadR) / w; hy = (dstep + dpadR) / h; break;
                case BLK_FACE: hx = (fstep + dpadR) / w; hy = (fstep + dpadR) / h; break;
                case BLK_LSTICK:
                case BLK_RSTICK: hx = stickR / w; hy = stickR / h; break;
                case BLK_START:
                case BLK_BACK: hx = (h * 0.058f) / w; hy = 0.058f; break;
                default: hx = ctlR / w; hy = ctlR / h; break; // L3, R3
            }
            float sc = _scales[block]; // hit-box grows/shrinks with the block
            return new Vector2(hx * sc, hy * sc);
        }

        private static Vector2 ClampCenter(int block, Vector2 c, float w, float h)
        {
            Vector2 he = BlockHalfNormalised(block, w, h);
            float x = he.X >= 0.5f ? 0.5f : MathHelper.Clamp(c.X, he.X, 1f - he.X);
            float y = he.Y >= 0.5f ? 0.5f : MathHelper.Clamp(c.Y, he.Y, 1f - he.Y);
            return new Vector2(x, y);
        }

        private static void EnsureBuilt()
        {
            float w = ScreenW(), h = ScreenH();
            if (_ctrls != null && _builtW == w && _builtH == h) return;

            float ctlR = h * 0.045f;
            float dpadR = h * 0.050f;
            float stickR = h * 0.160f;
            float pairHalf = w * 0.045f;
            float dstep = h * 0.10f;
            float fstep = h * 0.095f;

            List<Ctrl> list = new List<Ctrl>(24);

            // LT/LB pair
            float kLT = _scales[BLK_LTLB];
            Vector2 c = BlockCenterPx(BLK_LTLB, w, h);
            AddCircle(list, K_TRIGGER, Buttons.None, 0, TRIG_LEFT, 0, 0, BLK_LTLB, c + new Vector2(-pairHalf * kLT, 0f), ctlR * kLT, "LT");
            AddCircle(list, K_BUMPER, Buttons.LeftShoulder, 0, 0, 0, 0, BLK_LTLB, c + new Vector2(pairHalf * kLT, 0f), ctlR * kLT, "LB");

            // D-pad
            float kD = _scales[BLK_DPAD];
            c = BlockCenterPx(BLK_DPAD, w, h);
            AddCircle(list, K_DPAD, Buttons.DPadUp, 0, 0, 0, 0, BLK_DPAD, c + new Vector2(0f, -dstep * kD), dpadR * kD, "");
            AddCircle(list, K_DPAD, Buttons.DPadLeft, 0, 0, 0, 2, BLK_DPAD, c + new Vector2(-dstep * kD, 0f), dpadR * kD, "");
            AddCircle(list, K_DPAD, Buttons.DPadRight, 0, 0, 0, 3, BLK_DPAD, c + new Vector2(dstep * kD, 0f), dpadR * kD, "");
            AddCircle(list, K_DPAD, Buttons.DPadDown, 0, 0, 0, 1, BLK_DPAD, c + new Vector2(0f, dstep * kD), dpadR * kD, "");

            // L3 + left stick
            c = BlockCenterPx(BLK_L3, w, h);
            AddCircle(list, K_STICKBTN, Buttons.LeftStick, 0, 0, 0, 0, BLK_L3, c, ctlR * _scales[BLK_L3], "L3");
            c = BlockCenterPx(BLK_LSTICK, w, h);
            AddCircle(list, K_STICK, Buttons.None, 0, 0, STICK_LEFT, 0, BLK_LSTICK, c, stickR * _scales[BLK_LSTICK], "");

            // RB/RT
            float kRT = _scales[BLK_RBRT];
            c = BlockCenterPx(BLK_RBRT, w, h);
            AddCircle(list, K_BUMPER, Buttons.RightShoulder, 0, 0, 0, 0, BLK_RBRT, c + new Vector2(-pairHalf * kRT, 0f), ctlR * kRT, "RB");
            AddCircle(list, K_TRIGGER, Buttons.None, 0, TRIG_RIGHT, 0, 0, BLK_RBRT, c + new Vector2(pairHalf * kRT, 0f), ctlR * kRT, "RT");

            // right stick
            c = BlockCenterPx(BLK_RSTICK, w, h);
            AddCircle(list, K_STICK, Buttons.None, 0, 0, STICK_RIGHT, 0, BLK_RSTICK, c, stickR * _scales[BLK_RSTICK], "");

            // face cluster
            float kF = _scales[BLK_FACE];
            c = BlockCenterPx(BLK_FACE, w, h);
            AddCircle(list, K_FACE, Buttons.Y, ROLE_Y, 0, 0, 0, BLK_FACE, c + new Vector2(0f, -fstep * kF), dpadR * kF, "Y");
            AddCircle(list, K_FACE, Buttons.X, ROLE_X, 0, 0, 0, BLK_FACE, c + new Vector2(-fstep * kF, 0f), dpadR * kF, "X");
            AddCircle(list, K_FACE, Buttons.B, ROLE_B, 0, 0, 0, BLK_FACE, c + new Vector2(fstep * kF, 0f), dpadR * kF, "B");
            AddCircle(list, K_FACE, Buttons.A, ROLE_A, 0, 0, 0, BLK_FACE, c + new Vector2(0f, fstep * kF), dpadR * kF, "A");

            // R3
            c = BlockCenterPx(BLK_R3, w, h);
            AddCircle(list, K_STICKBTN, Buttons.RightStick, 0, 0, 0, 0, BLK_R3, c, ctlR * _scales[BLK_R3], "R3");

            // START / PAUSE + BACK (the system buttons the reference omitted)
            float sysR = h * 0.058f;
            c = BlockCenterPx(BLK_START, w, h);
            AddCircle(list, K_SYS, Buttons.Start, 0, 0, 0, 0, BLK_START, c, sysR * _scales[BLK_START], "START");
            c = BlockCenterPx(BLK_BACK, w, h);
            AddCircle(list, K_SYS, Buttons.Back, 0, 0, 0, 0, BLK_BACK, c, sysR * _scales[BLK_BACK], "BACK");

            _ctrls = list.ToArray();
            _builtW = w;
            _builtH = h;
            _stickLIndex = IndexOfStick(STICK_LEFT);
            _stickRIndex = IndexOfStick(STICK_RIGHT);

            // editor buttons (top-centre): [−] [+] SAVE RESET BACK
            float bw = w * 0.11f, bh = h * 0.105f, by = h * 0.035f, sw = w * 0.06f;
            _btnMinus = RectCentered(w * 0.22f, by + bh / 2f, sw, bh);
            _btnPlus = RectCentered(w * 0.29f, by + bh / 2f, sw, bh);
            _btnSave = RectCentered(w * 0.42f, by + bh / 2f, bw, bh);
            _btnReset = RectCentered(w * 0.54f, by + bh / 2f, bw, bh);
            _btnBack = RectCentered(w * 0.66f, by + bh / 2f, bw, bh);

            // comic SPEED UP / SKIP buttons (bottom-centre)
            float cbw = w * 0.17f, cbh = h * 0.14f, cby = h * 0.82f;
            _comicSpeedUp = RectCentered(w * 0.40f, cby, cbw, cbh);
            _comicSkip = RectCentered(w * 0.60f, cby, cbw, cbh);
        }

        private static Rectangle RectCentered(float cx, float cy, float w, float h)
        {
            return new Rectangle((int)(cx - w / 2f), (int)(cy - h / 2f), (int)w, (int)h);
        }

        private static int IndexOfStick(int id)
        {
            for (int i = 0; i < _ctrls.Length; i++)
                if (_ctrls[i].kind == K_STICK && _ctrls[i].stick == id) return i;
            return -1;
        }

        private static void AddCircle(List<Ctrl> list, int kind, Buttons buttons, int role,
            int trigger, int stick, int dir, int block, Vector2 center, float r, string label)
        {
            Ctrl c = new Ctrl();
            c.kind = kind; c.buttons = buttons; c.role = role; c.trigger = trigger;
            c.stick = stick; c.dir = dir; c.block = block;
            c.center = center; c.radius = r; c.label = label;
            list.Add(c);
        }

        // =====================================================================
        //  TOUCH ROUTING  (gameplay pad)
        // =====================================================================
        public static bool OnTouch(Android.Views.MotionEvent e)
        {
            if (!On || e == null) return false;
            EnsureBuilt();
            try
            {
                switch (e.ActionMasked)
                {
                    case Android.Views.MotionEventActions.Down:
                    case Android.Views.MotionEventActions.PointerDown:
                        {
                            if (e.ActionMasked == Android.Views.MotionEventActions.Down)
                                UnbindAll();
                            int i = e.ActionIndex;
                            int pid = e.GetPointerId(i);
                            float x = Logical(e.GetX(i));
                            float y = Logical(e.GetY(i));
                            int hit = HitTest(x, y);
                            if (hit >= 0)
                            {
                                Bind(pid, hit, x, y);
                                Publish();
#if DEBUG
                                Log.Info("[touchpad] DOWN ptr " + pid + " -> " + Describe(hit));
#endif
                                return true;
                            }
                            return false;
                        }

                    case Android.Views.MotionEventActions.Move:
                        {
                            bool consumed = false;
                            for (int i = 0; i < e.PointerCount; i++)
                            {
                                int pid = e.GetPointerId(i);
                                lock (_lock)
                                {
                                    int ci = pid >= 0 && pid < MaxPointers ? _ptrCtrl[pid] : -1;
                                    if (ci < 0) continue;
                                    consumed = true;
                                    if (ci == _stickLIndex || ci == _stickRIndex)
                                        SetStick(ci, Logical(e.GetX(i)), Logical(e.GetY(i)));
                                }
                            }
                            if (consumed) Publish();
                            return consumed;
                        }

                    case Android.Views.MotionEventActions.Up:
                    case Android.Views.MotionEventActions.PointerUp:
                        {
                            int pid = e.GetPointerId(e.ActionIndex);
                            bool was = Unbind(pid);
                            if (was) Publish();
#if DEBUG
                            if (was) Log.Info("[touchpad] UP ptr " + pid);
#endif
                            return was;
                        }

                    case Android.Views.MotionEventActions.Cancel:
                        UnbindAll();
                        Publish();
                        return true;
                }
            }
            catch (Exception ex) { Log.Exception("touchpad.event", ex); }
            return false;
        }

        public static void Tick()
        {
            if (EditorActive) return; // editor manages its own state

            bool show = Game1.portTouchPadContext && On;
            if (show != _shownLast)
            {
                _shownLast = show;
                Log.Info("[touchpad] overlay " + (show ? "ACTIVE" : "inactive")
                    + " gameMode=" + Game1.gameMode + " paused=" + Globals.paused);
            }

            if (show)
            {
                EnsureBuilt();
                Publish();
            }
            else if (_anyBound)
            {
                UnbindAll();
                Publish();
            }
        }

        private static void Bind(int pid, int ctrl, float x, float y)
        {
            if (pid < 0 || pid >= MaxPointers) return;
            lock (_lock)
            {
                int prev = _ptrCtrl[pid];
                if (prev >= 0 && prev != ctrl) ClearStick(prev);
                _ptrCtrl[pid] = ctrl;
                _anyBound = true;
                if (ctrl == _stickLIndex || ctrl == _stickRIndex) SetStick(ctrl, x, y);
            }
        }

        private static bool Unbind(int pid)
        {
            if (pid < 0 || pid >= MaxPointers) return false;
            lock (_lock)
            {
                int ci = _ptrCtrl[pid];
                if (ci < 0) return false;
                _ptrCtrl[pid] = -1;
                ClearStick(ci);
                return true;
            }
        }

        private static void UnbindAll()
        {
            lock (_lock)
            {
                for (int i = 0; i < MaxPointers; i++) _ptrCtrl[i] = -1;
                _lDir = _rDir = Vector2.Zero; _lMag = _rMag = 0f; _lOut = _rOut = Vector2.Zero;
                _anyBound = false;
            }
        }

        private static void ClearStick(int ci)
        {
            if (ci == _stickLIndex) { _lDir = Vector2.Zero; _lMag = 0f; _lOut = Vector2.Zero; }
            else if (ci == _stickRIndex) { _rDir = Vector2.Zero; _rMag = 0f; _rOut = Vector2.Zero; }
        }

        private static void SetStick(int ci, float x, float y)
        {
            Ctrl c = _ctrls[ci];
            Vector2 d = new Vector2(x - c.center.X, y - c.center.Y);
            float len = d.Length();
            Vector2 dir = Vector2.Zero;
            float mag = 0f;
            if (len > 0.001f) { dir = d / len; mag = Math.Min(len / c.radius, 1f); }
            float outMag = mag <= StickDeadzone ? 0f : (mag - StickDeadzone) / (1f - StickDeadzone);
            Vector2 outv = new Vector2(dir.X * outMag, -dir.Y * outMag);
            if (ci == _stickLIndex) { _lDir = dir; _lMag = mag; _lOut = outv; }
            else if (ci == _stickRIndex) { _rDir = dir; _rMag = mag; _rOut = outv; }
        }

        private static void Publish()
        {
            Buttons b = Buttons.None;
            float lt = 0f, rt = 0f;
            Vector2 left = Vector2.Zero, right = Vector2.Zero;
            bool any = false;

            lock (_lock)
            {
                Array.Clear(_down, 0, _down.Length);
                for (int p = 0; p < MaxPointers; p++)
                {
                    int ci = _ptrCtrl[p];
                    if (ci < 0 || ci >= _ctrls.Length) continue;
                    any = true;
                    _down[ci] = true;
                    Ctrl c = _ctrls[ci];
                    switch (c.kind)
                    {
                        case K_STICK:
                            if (c.stick == STICK_LEFT) left = _lOut; else right = _rOut;
                            break;
                        case K_TRIGGER:
                            if (c.trigger == TRIG_LEFT) lt = 1f; else rt = 1f;
                            break;
                        default:
                            b |= c.buttons;
                            break;
                    }
                }
                _anyBound = any;
            }

            AndroidInputBridge.SetTouchState(b, left, right, lt, rt);

#if DEBUG
            if (b != _logButtons)
            {
                _logButtons = b;
                Log.Info("[touchpad] buttons=" + (b == Buttons.None ? "none" : b.ToString()));
            }
            bool sl = left.LengthSquared() > 0.0001f;
            if (sl != _logStickL) { _logStickL = sl; Log.Info("[touchpad] Lstick " + (sl ? "dir=" + left.X.ToString("0.00") + "," + left.Y.ToString("0.00") : "centered")); }
            bool sr = right.LengthSquared() > 0.0001f;
            if (sr != _logStickR) { _logStickR = sr; Log.Info("[touchpad] Rstick " + (sr ? "dir=" + right.X.ToString("0.00") + "," + right.Y.ToString("0.00") : "centered")); }
#endif
        }

        private static string Describe(int i)
        {
            Ctrl c = _ctrls[i];
            switch (c.kind)
            {
                case K_STICK: return c.stick == STICK_LEFT ? "Lstick" : "Rstick";
                case K_TRIGGER: return c.trigger == TRIG_LEFT ? "LT" : "RT";
                case K_BUMPER: return c.buttons == Buttons.LeftShoulder ? "LB" : "RB";
                case K_STICKBTN: return c.buttons == Buttons.LeftStick ? "L3" : "R3";
                case K_DPAD:
                    return c.dir == 0 ? "dpad-up" : c.dir == 1 ? "dpad-down" : c.dir == 2 ? "dpad-left" : "dpad-right";
                default: return c.buttons.ToString();
            }
        }

        private static int HitTest(float x, float y)
        {
            int best = -1;
            float bestN = float.MaxValue;
            for (int i = 0; i < _ctrls.Length; i++)
            {
                Ctrl c = _ctrls[i];
                float dx = x - c.center.X, dy = y - c.center.Y;
                float d2 = dx * dx + dy * dy;
                float hitR = c.radius * (c.kind == K_STICK ? 1.10f : 1.30f);
                if (d2 > hitR * hitR) continue;
                float n = d2 / (c.radius * c.radius);
                if (n < bestN) { bestN = n; best = i; }
            }
            return best;
        }

        // =====================================================================
        //  EDITOR
        // =====================================================================
        public static void EnterEditor()
        {
            _editorBackup = (Vector2[])_centers.Clone();
            _editorScaleBackup = (float[])_scales.Clone();
            _dragBlock = -1;
            _selected = -1;
            EditorActive = true;
            Invalidate();
            Log.Info("[touchpad] editor opened");
        }

        public static void ExitEditor()
        {
            if (_editorBackup != null) _centers = _editorBackup;
            if (_editorScaleBackup != null) _scales = _editorScaleBackup;
            _dragBlock = -1;
            _selected = -1;
            EditorActive = false;
            Invalidate();
            Log.Info("[touchpad] editor closed (unsaved changes discarded)");
        }

        public static void SaveEditor()
        {
            AndroidSettings.SetTouchLayout(SerializeCenters());
            _editorBackup = (Vector2[])_centers.Clone();
            _editorScaleBackup = (float[])_scales.Clone();
            _dragBlock = -1;
            _selected = -1;
            EditorActive = false;
            Invalidate();
            Log.Info("[touchpad] editor SAVED: " + SerializeCenters());
        }

        public static void ResetEditor()
        {
            _centers = (Vector2[])DefaultCenters.Clone();
            _scales = NewDefaultScales();
            _dragBlock = -1;
            AndroidSettings.SetTouchLayout(SerializeCenters());
            // RESET persists immediately, so BACK must not revert to the
            // pre-reset layout: refresh the undo snapshot too.
            _editorBackup = (Vector2[])_centers.Clone();
            _editorScaleBackup = (float[])_scales.Clone();
            Invalidate();
            Log.Info("[touchpad] editor RESET to default (saved)");
        }

        public static bool OnEditorTouch(Android.Views.MotionEvent e)
        {
            if (!EditorActive || e == null) return false;
            EnsureBuilt();
            try
            {
                switch (e.ActionMasked)
                {
                    case Android.Views.MotionEventActions.Down:
                    case Android.Views.MotionEventActions.PointerDown:
                        {
                            float x = Logical(e.GetX(e.ActionIndex));
                            float y = Logical(e.GetY(e.ActionIndex));

                            Rectangle save = _btnSave; save.Inflate(12, 12);
                            Rectangle reset = _btnReset; reset.Inflate(12, 12);
                            Rectangle back = _btnBack; back.Inflate(12, 12);
                            Rectangle minus = _btnMinus; minus.Inflate(12, 12);
                            Rectangle plus = _btnPlus; plus.Inflate(12, 12);
                            if (save.Contains((int)x, (int)y)) { SaveEditor(); return true; }
                            if (reset.Contains((int)x, (int)y)) { ResetEditor(); return true; }
                            if (back.Contains((int)x, (int)y)) { ExitEditor(); return true; }
                            if (minus.Contains((int)x, (int)y)) { AdjustScale(-1); return true; }
                            if (plus.Contains((int)x, (int)y)) { AdjustScale(+1); return true; }

                            int b = NearestBlock(x, y);
                            _selected = b;
                            if (b >= 0)
                            {
                                Vector2 pc = BlockCenterPx(b, ScreenW(), ScreenH());
                                _dragBlock = b;
                                _dragMoved = false;
                                _dragOffset = new Vector2(x - pc.X, y - pc.Y);
#if DEBUG
                                Log.Info("[touchpad] editor select block " + b);
#endif
                            }
                            return true; // editor consumes all
                        }

                    case Android.Views.MotionEventActions.Move:
                        {
                            if (_dragBlock >= 0)
                            {
                                float w = ScreenW(), h = ScreenH();
                                float mx = Logical(e.GetX(e.ActionIndex));
                                float my = Logical(e.GetY(e.ActionIndex));
                                Vector2 target = new Vector2(mx - _dragOffset.X, my - _dragOffset.Y);
                                Vector2 n = new Vector2(target.X / w, target.Y / h);
                                _dragMoved = true;
                                lock (_lock) { _centers[_dragBlock] = ClampCenter(_dragBlock, n, w, h); }
                                Invalidate();
                            }
                            return true;
                        }

                    case Android.Views.MotionEventActions.Up:
                    case Android.Views.MotionEventActions.PointerUp:
                        _dragBlock = -1;
                        return true;

                    case Android.Views.MotionEventActions.Cancel:
                        _dragBlock = -1;
                        return true;
                }
            }
            catch (Exception ex) { Log.Exception("touchpad.editor", ex); }
            return true;
        }

        private static void AdjustScale(int dir)
        {
            if (_selected < 0 || _selected >= BlockCount) return;
            float w = ScreenW(), h = ScreenH();
            _scales[_selected] = ClampScale(_scales[_selected] + dir * 0.1f);
            // a scaled-up block must still fit: re-clamp its centre
            _centers[_selected] = ClampCenter(_selected, _centers[_selected], w, h);
            Invalidate();
            Log.Info("[touchpad] editor scale block " + _selected + " = "
                + _scales[_selected].ToString("0.0"));
        }

        /// <summary>Nearest block with a generous (finger-friendly) grab radius
        /// that also grows with the block's own size.</summary>
        private static int NearestBlock(float x, float y)
        {
            float w = ScreenW(), h = ScreenH();
            int best = -1;
            float bestD = float.MaxValue;
            for (int b = 0; b < BlockCount; b++)
            {
                Vector2 pc = BlockCenterPx(b, w, h);
                Vector2 he = BlockHalfNormalised(b, w, h);
                float halfPx = Math.Max(he.X * w, he.Y * h);
                float grab = halfPx + 72f; // forgiving slop on a phone
                float dx = x - pc.X, dy = y - pc.Y;
                float d = (float)Math.Sqrt(dx * dx + dy * dy);
                if (d <= grab && d < bestD) { bestD = d; best = b; }
            }
            return best;
        }

        // =====================================================================
        //  DRAW
        // =====================================================================
        public static void Draw(SpriteBatch sprite, Text text, Texture2D comicTex)
        {
            if (!On || sprite == null) return;
            if (!Game1.portTouchPadContext) return;
            EnsureBuilt();
            EnsureTextures(sprite.GraphicsDevice);
            DrawPad(sprite, text, comicTex, -1);
        }

        // ------------------------------------------------------------- comics
        /// <summary>Draw the two explicit comic buttons (SPEED UP / SKIP) when
        /// TOUCH CONTROLS is ON and a comic is playing; they replace the game's
        /// own A/B prompts.  The pad itself is hidden during a comic.</summary>
        public static void DrawComic(SpriteBatch sprite, Text text, Texture2D comicTex)
        {
            if (!On || sprite == null) return;
            if (!Game1.portComicPlaying) return;
            EnsureBuilt();
            EnsureTextures(sprite.GraphicsDevice);

            int px = (int)(ScreenH() * 0.048f);
            Label(sprite.GraphicsDevice, "SPEED UP", px);
            Label(sprite.GraphicsDevice, "SKIP", px);

            sprite.Begin(SpriteSortMode.Deferred, BlendState.NonPremultiplied);
            try
            {
                DrawComicButton(sprite, _comicSpeedUp, _comicDown == 1);
                DrawComicButton(sprite, _comicSkip, _comicDown == 2);
            }
            finally { sprite.End(); }

            sprite.Begin(SpriteSortMode.Deferred, BlendState.NonPremultiplied);
            try
            {
                DrawLabelCentered(sprite, comicTex, "SPEED UP", px, _comicSpeedUp);
                DrawLabelCentered(sprite, comicTex, "SKIP", px, _comicSkip);
            }
            finally { sprite.End(); }
        }

        private static void DrawComicButton(SpriteBatch sprite, Rectangle r, bool pressed)
        {
            sprite.Draw(_pixel, r, new Color(0f, 0f, 0f, pressed ? 0.80f : 0.62f));
            Rectangle inner = new Rectangle(r.X + 3, r.Y + 3, r.Width - 6, r.Height - 6);
            sprite.Draw(_pixel, inner, pressed ? new Color(1f, 1f, 1f, 0.35f) : new Color(1f, 1f, 1f, 0.12f));
        }

        /// <summary>Comic button hit-test (logical px). Returns 1 = SPEED UP,
        /// 2 = SKIP, 0 = none.</summary>
        public static int HitComicButton(float x, float y)
        {
            EnsureBuilt();
            Rectangle a = _comicSpeedUp; a.Inflate(10, 10);
            if (a.Contains((int)x, (int)y)) return 1;
            Rectangle b = _comicSkip; b.Inflate(10, 10);
            if (b.Contains((int)x, (int)y)) return 2;
            return 0;
        }

        /// <summary>Comic pointer down on a button (0 if none).  1 = SPEED UP
        /// (hold), 2 = SKIP (tap).</summary>
        public static int ComicButtonDown(float x, float y)
        {
            _comicDown = HitComicButton(x, y);
            return _comicDown;
        }

        /// <summary>Which comic button the active pointer went down on (0 none).</summary>
        public static int ComicDown { get { return _comicDown; } }

        public static void ClearComicDown() { _comicDown = 0; }

        public static void DrawEditor(SpriteBatch sprite, Text text, Texture2D comicTex)
        {
            if (!EditorActive || sprite == null) return;
            EnsureBuilt();
            EnsureTextures(sprite.GraphicsDevice);

            // Dim backdrop so the editor is a clean canvas.
            sprite.Begin(SpriteSortMode.Deferred, BlendState.NonPremultiplied);
            try { sprite.Draw(_pixel, new Rectangle(0, 0, (int)ScreenW(), (int)ScreenH()), new Color(0f, 0f, 0f, 0.82f)); }
            finally { sprite.End(); }

            DrawPad(sprite, text, comicTex, _selected);
            DrawEditorChrome(sprite, comicTex);
        }

        private static void DrawPad(SpriteBatch sprite, Text text, Texture2D comicTex, int highlightBlock)
        {
            Color line = new Color(1f, 1f, 1f, 0.62f);
            Color linePressed = new Color(1f, 1f, 1f, 0.95f);
            Color highlight = new Color(1f, 0.85f, 0.20f, 1f);

            sprite.Begin(SpriteSortMode.Deferred, BlendState.NonPremultiplied);
            try
            {
                for (int i = 0; i < _ctrls.Length; i++)
                    if (_ctrls[i].kind == K_STICK) DrawStick(sprite, i, line, highlightBlock, highlight);

                for (int i = 0; i < _ctrls.Length; i++)
                {
                    Ctrl c = _ctrls[i];
                    if (c.kind == K_STICK) continue;
                    bool pressed = i < _down.Length && _down[i];
                    bool hl = highlightBlock >= 0 && c.block == highlightBlock;
                    Color col = hl ? highlight : (pressed ? linePressed : line);
                    if (pressed || hl) DrawDisc(sprite, c.center, c.radius * 0.9f, new Color(1f, 1f, 1f, hl ? 0.22f : 0.14f));
                    DrawRing(sprite, c.center, c.radius, col, c.kind == K_STICK);
                    if (c.kind == K_DPAD)
                        DrawGlyph(sprite, c.center, c.radius * 0.62f, col, DpadRotation(c.dir));
                }
            }
            finally { sprite.End(); }

            // Highlights: bounding box around the dragged block.
            if (highlightBlock >= 0)
            {
                float w = ScreenW(), h = ScreenH();
                Vector2 c = BlockCenterPx(highlightBlock, w, h);
                Vector2 he = BlockHalfNormalised(highlightBlock, w, h);
                var box = RectCentered(c.X, c.Y, he.X * w * 2f + 16f, he.Y * h * 2f + 16f);
                DrawBox(sprite, box, highlight);
            }

            // Labels (pre-created before Begin).
            for (int i = 0; i < _ctrls.Length; i++)
            {
                Ctrl c = _ctrls[i];
                if (string.IsNullOrEmpty(c.label)) continue;
                Label(sprite.GraphicsDevice, c.label, LabelPx(c));
            }
            sprite.Begin(SpriteSortMode.Deferred, BlendState.NonPremultiplied);
            try
            {
                for (int i = 0; i < _ctrls.Length; i++)
                {
                    Ctrl c = _ctrls[i];
                    if (string.IsNullOrEmpty(c.label)) continue;
                    Texture2D t = Label(sprite.GraphicsDevice, c.label, LabelPx(c));
                    bool hl = highlightBlock >= 0 && c.block == highlightBlock;
                    bool pressed = i < _down.Length && _down[i];
                    sprite.Draw(t, c.center, null,
                        (hl || pressed) ? Color.White : new Color(1f, 1f, 1f, 0.9f),
                        0f, new Vector2(t.Width / 2f, t.Height / 2f), 1f, SpriteEffects.None, 0f);
                }
            }
            finally { sprite.End(); }
        }

        private static int LabelPx(Ctrl c)
        {
            if (c.label.Length >= 4) return (int)(c.radius * 0.52f);
            if (c.label.Length > 1) return (int)(c.radius * 0.72f);
            return (int)(c.radius * 1.02f);
        }

        private static void DrawEditorChrome(SpriteBatch sprite, Texture2D comicTex)
        {
            float w = ScreenW(), h = ScreenH();

            // Button labels pre-created.
            int bpx = (int)(h * 0.05f);
            int hpx = (int)(h * 0.030f);
            string hintStr = "DRAG TO MOVE  .  TAP TO SELECT  .  - / + TO SIZE  .  SAVE";
            string sizeStr = _selected >= 0 ? ("SIZE " + _scales[_selected].ToString("0.0") + "x") : "SIZE -";
            Label(sprite.GraphicsDevice, "SAVE", bpx);
            Label(sprite.GraphicsDevice, "RESET", bpx);
            Label(sprite.GraphicsDevice, "BACK", bpx);
            Label(sprite.GraphicsDevice, "-", bpx);
            Label(sprite.GraphicsDevice, "+", bpx);
            Label(sprite.GraphicsDevice, hintStr, hpx);
            Label(sprite.GraphicsDevice, sizeStr, hpx);

            sprite.Begin(SpriteSortMode.Deferred, BlendState.NonPremultiplied);
            try
            {
                DrawEditorButton(sprite, _btnMinus, _selected >= 0);
                DrawEditorButton(sprite, _btnPlus, _selected >= 0);
                DrawEditorButton(sprite, _btnSave, true);
                DrawEditorButton(sprite, _btnReset, false);
                DrawEditorButton(sprite, _btnBack, false);
            }
            finally { sprite.End(); }

            sprite.Begin(SpriteSortMode.Deferred, BlendState.NonPremultiplied);
            try
            {
                DrawLabelCentered(sprite, comicTex, "-", bpx, _btnMinus);
                DrawLabelCentered(sprite, comicTex, "+", bpx, _btnPlus);
                DrawLabelCentered(sprite, comicTex, "SAVE", bpx, _btnSave);
                DrawLabelCentered(sprite, comicTex, "RESET", bpx, _btnReset);
                DrawLabelCentered(sprite, comicTex, "BACK", bpx, _btnBack);
                Texture2D hint = Label(sprite.GraphicsDevice, hintStr, hpx);
                sprite.Draw(hint, new Vector2(w * 0.5f, h * 0.165f), null, new Color(1f, 1f, 1f, 0.85f),
                    0f, new Vector2(hint.Width / 2f, hint.Height / 2f), 1f, SpriteEffects.None, 0f);
                Texture2D sizeT = Label(sprite.GraphicsDevice, sizeStr, hpx);
                sprite.Draw(sizeT, new Vector2(w * 0.5f, h * 0.205f), null, new Color(1f, 0.9f, 0.4f, 0.95f),
                    0f, new Vector2(sizeT.Width / 2f, sizeT.Height / 2f), 1f, SpriteEffects.None, 0f);
            }
            finally { sprite.End(); }
        }

        private static void DrawEditorButton(SpriteBatch sprite, Rectangle r, bool primary)
        {
            sprite.Draw(_pixel, r, new Color(0f, 0f, 0f, 0.55f));
            Rectangle inner = new Rectangle(r.X + 2, r.Y + 2, r.Width - 4, r.Height - 4);
            sprite.Draw(_pixel, inner, primary ? new Color(0.20f, 0.55f, 0.25f, 0.85f) : new Color(0.30f, 0.32f, 0.38f, 0.85f));
        }

        private static void DrawLabelCentered(SpriteBatch sprite, Texture2D comicTex, string s, int px, Rectangle r)
        {
            Texture2D t = Label(sprite.GraphicsDevice, s, px);
            sprite.Draw(t, new Vector2(r.Center.X, r.Center.Y), null, Color.White,
                0f, new Vector2(t.Width / 2f, t.Height / 2f), 1f, SpriteEffects.None, 0f);
        }

        private static void DrawBox(SpriteBatch sprite, Rectangle r, Color color)
        {
            int t = 3;
            sprite.Begin(SpriteSortMode.Deferred, BlendState.NonPremultiplied);
            try
            {
                sprite.Draw(_pixel, new Rectangle(r.Left, r.Top, r.Width, t), color);
                sprite.Draw(_pixel, new Rectangle(r.Left, r.Bottom - t, r.Width, t), color);
                sprite.Draw(_pixel, new Rectangle(r.Left, r.Top, t, r.Height), color);
                sprite.Draw(_pixel, new Rectangle(r.Right - t, r.Top, t, r.Height), color);
            }
            finally { sprite.End(); }
        }

        private static float DpadRotation(int dir)
        {
            switch (dir)
            {
                case 1: return MathHelper.Pi;
                case 2: return -MathHelper.PiOver2;
                case 3: return MathHelper.PiOver2;
                default: return 0f;
            }
        }

        private static void DrawStick(SpriteBatch sprite, int index, Color line, int highlightBlock, Color highlight)
        {
            Ctrl c = _ctrls[index];
            bool hl = highlightBlock >= 0 && c.block == highlightBlock;
            bool active = (index < _down.Length && _down[index]) || hl;
            float R = c.radius;
            Vector2 dir = index == _stickLIndex ? _lDir : _rDir;
            float mag = index == _stickLIndex ? _lMag : _rMag;

            Color ring = hl ? highlight : (active ? new Color(1f, 1f, 1f, 0.95f) : line);
            DrawRing(sprite, c.center, R, ring, big: true);
            DrawRing(sprite, c.center, R * 0.66f, new Color(1f, 1f, 1f, 0.35f), big: true);

            Vector2 knob = c.center + dir * (mag * R * 0.40f);
            DrawKnob(sprite, knob, R * 0.44f);
        }

        private static void DrawDisc(SpriteBatch sprite, Vector2 center, float radius, Color color)
        {
            sprite.Draw(_disc, center, null, color, 0f,
                new Vector2(_disc.Width / 2f, _disc.Height / 2f),
                (radius * 2f) / _disc.Width, SpriteEffects.None, 0f);
        }

        private static void DrawKnob(SpriteBatch sprite, Vector2 center, float radius)
        {
            sprite.Draw(_knob, center, null, Color.White, 0f,
                new Vector2(_knob.Width / 2f, _knob.Height / 2f),
                (radius * 2f) / _knob.Width, SpriteEffects.None, 0f);
        }

        private static void DrawRing(SpriteBatch sprite, Vector2 center, float radius, Color color, bool big)
        {
            Texture2D t = big ? _ringBig : _ringSmall;
            sprite.Draw(t, center, null, color, 0f,
                new Vector2(t.Width / 2f, t.Height / 2f),
                (radius * 2f) / t.Width, SpriteEffects.None, 0f);
        }

        private static void DrawGlyph(SpriteBatch sprite, Vector2 center, float size, Color color, float rot)
        {
            sprite.Draw(_tri, center, null, color, rot,
                new Vector2(_tri.Width / 2f, _tri.Height / 2f),
                size / _tri.Width, SpriteEffects.None, 0f);
        }

        // -------------------------------------------------------------- textures
        private static void EnsureTextures(GraphicsDevice gd)
        {
            if (gd == null || (_gd == gd && _ringSmall != null)) return;
            _gd = gd;
            _ringSmall = MakeRing(gd, 128, 3f);
            _ringBig = MakeRing(gd, 256, 3f);
            _tri = MakeShape(gd, 128, Tri);
            _disc = MakeShape(gd, 128, (x, y) => x * x + y * y <= 1f);
            _knob = MakeKnob(gd, 128);
            _pixel = new Texture2D(gd, 1, 1);
            _pixel.SetData(new Color[] { Color.White });
            _labels.Clear();
            _labelGd = null;
        }

        private static Texture2D MakeRing(GraphicsDevice gd, int size, float bandPx)
        {
            float outer = size / 2f;
            float inner = (outer - bandPx) / outer;
            return MakeShape(gd, size, (x, y) =>
            {
                float r = (float)Math.Sqrt(x * x + y * y);
                return r <= 1f && r >= inner;
            });
        }

        private static Texture2D MakeKnob(GraphicsDevice gd, int size)
        {
            Color[] data = new Color[size * size];
            Color grey = new Color((byte)180, (byte)180, (byte)184, (byte)255);
            Color dot = new Color((byte)120, (byte)120, (byte)126, (byte)255);
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float nx = (x + 0.5f) / size * 2f - 1f;
                    float ny = (y + 0.5f) / size * 2f - 1f;
                    float r = (float)Math.Sqrt(nx * nx + ny * ny);
                    if (r > 1f) { data[y * size + x] = Color.Transparent; continue; }
                    bool isDot = (x % 9 < 2) && (y % 9 < 2);
                    data[y * size + x] = isDot ? dot : grey;
                }
            }
            Texture2D t = new Texture2D(gd, size, size);
            t.SetData(data);
            return t;
        }

        private static bool Tri(float x, float y)
        {
            return InsideTri(x, y, 0f, -0.95f, -0.90f, 0.70f, 0.90f, 0.70f);
        }

        private static bool InsideTri(float px, float py,
            float ax, float ay, float bx, float by, float cx2, float cy2)
        {
            float e0 = Edge(ax, ay, bx, by, px, py);
            float e1 = Edge(bx, by, cx2, cy2, px, py);
            float e2 = Edge(cx2, cy2, ax, ay, px, py);
            bool neg = (e0 < 0f) || (e1 < 0f) || (e2 < 0f);
            bool pos = (e0 > 0f) || (e1 > 0f) || (e2 > 0f);
            return !(neg && pos);
        }

        private static float Edge(float ax, float ay, float bx, float by, float px, float py)
        {
            return (bx - ax) * (py - ay) - (by - ay) * (px - ax);
        }

        private static Texture2D MakeShape(GraphicsDevice gd, int size, Func<float, float, bool> inside)
        {
            Color[] data = new Color[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    int hits = 0;
                    for (int sy = 0; sy < 2; sy++)
                        for (int sx = 0; sx < 2; sx++)
                        {
                            float nx = ((x + 0.25f + 0.5f * sx) / size) * 2f - 1f;
                            float ny = ((y + 0.25f + 0.5f * sy) / size) * 2f - 1f;
                            if (inside(nx, ny)) hits++;
                        }
                    data[y * size + x] = new Color(1f, 1f, 1f, hits / 4f);
                }
            }
            Texture2D t = new Texture2D(gd, size, size);
            t.SetData(data);
            return t;
        }

        // ------------------------------------------------------- label textures
        private static Texture2D Label(GraphicsDevice gd, string s, int px)
        {
            if (px < 8) px = 8;
            if (_labelGd != gd) { _labels.Clear(); _labelGd = gd; }
            string key = s + "@" + px;
            Texture2D cached;
            if (_labels.TryGetValue(key, out cached) && cached != null) return cached;
            Texture2D t;
            try { t = MakeTextTexture(gd, s, px); }
            catch (Exception ex)
            {
                Log.Exception("touchpad.label", ex);
                t = new Texture2D(gd, 1, 1);
                t.SetData(new Color[] { Color.Transparent });
            }
            _labels[key] = t;
            return t;
        }

        private static Texture2D MakeTextTexture(GraphicsDevice gd, string s, int px)
        {
            int w = Math.Max(16, px * (s.Length + 2));
            int h = Math.Max(16, px * 2);
            Android.Graphics.Bitmap bmp = Android.Graphics.Bitmap.CreateBitmap(
                w, h, Android.Graphics.Bitmap.Config.Argb8888);
            bmp.EraseColor(Android.Graphics.Color.Transparent);
            using (Android.Graphics.Canvas canvas = new Android.Graphics.Canvas(bmp))
            using (Android.Graphics.Paint paint = new Android.Graphics.Paint())
            {
                paint.AntiAlias = true;
                paint.Color = Android.Graphics.Color.White;
                paint.TextSize = px;
                paint.TextAlign = Android.Graphics.Paint.Align.Center;
                Android.Graphics.Typeface tf = Android.Graphics.Typeface.Create(
                    Android.Graphics.Typeface.SansSerif, Android.Graphics.TypefaceStyle.Bold);
                paint.SetTypeface(tf);
                float cy = h / 2f - (paint.Descent() + paint.Ascent()) / 2f;
                canvas.DrawText(s, w / 2f, cy, paint);
            }

            int[] pix = new int[w * h];
            bmp.GetPixels(pix, 0, w, 0, 0, w, h);
            bmp.Recycle();

            Color[] data = new Color[w * h];
            for (int i = 0; i < pix.Length; i++)
            {
                int a = (pix[i] >> 24) & 0xFF;
                data[i] = new Color(1f, 1f, 1f, a / 255f);
            }
            Texture2D t = new Texture2D(gd, w, h);
            t.SetData(data);
            return t;
        }

        private static float Logical(float v)
        {
            if (WidescreenConfig.Enabled && WidescreenConfig.Scale > 0f)
                return v / WidescreenConfig.Scale;
            return v;
        }
    }
}
