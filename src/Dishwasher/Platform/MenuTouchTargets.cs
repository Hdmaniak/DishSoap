// ============================================================================
// PORT (touch) -- direct tap-to-activate hit targets for the game's menus.
//
// Requirement: putting a finger on a menu entry should ACTIVATE that entry,
// rather than "swipe to move a cursor, tap to press A at the cursor". This
// helper is the geometry half of that: MainMenu.drawOption records the
// on-screen rectangle of every row it draws, and TouchControls.Tick hit-tests
// the last tap against those rectangles on the game thread.
//
// --- coordinate spaces (the important part) ---------------------------------
//   * Activity1.OnCreate measures the SurfaceView at the LOGICAL backbuffer
//     size (WidescreenConfig.LogicalWidth x 720) and scales it by
//     WidescreenConfig.Scale (~1.5x) to fill the physical panel. Android hands
//     MotionEvents to Activity.DispatchTouchEvent in PHYSICAL window pixels, so
//     TouchControls.OnTouch divides by Scale to obtain logical pixels.
//   * MainMenu lays its rows out in the game's ORIGINAL menu canvas -- 1024x600
//     when Globals.wideScreen, else 800x600 -- and Game1.Draw then blits that
//     canvas to the full Globals.screenSize backbuffer (Game1.Draw case 1:
//     SetRenderTarget(rTarg) -> SpriteBatch draws at canvas coords ->
//     sprite.Draw(rTarg, dest=screenSize, src=(0,0,1024,600))).
//     So a row recorded at canvas (x,y) is displayed at logical backbuffer
//     (x * screenSize.X / canvasW, y * screenSize.Y / 600).
//   * HitTest() applies that canvas->logical scale before comparing against the
//     logical tap point, so the two spaces agree.
//
// Threading: Add/Begin/HitTest/TakePendingRow all run on the GAME thread
// (MainMenu.Draw records, TouchControls.Tick tests, MainMenu.Update consumes).
// The only cross-thread hand-off is the tap point held by TouchControls.
//
// Scope: menus only. TouchControls gates everything on MenuContext(), so this
// is never reached during active gameplay.
//
// Revert: delete this file, the Dishwasher.MenuTouchTargets.Add(...) call in
// MainMenu.drawOption, the Begin() call in MainMenu.DrawOptionButtons, and the
// TakePendingRow() hook in MainMenu.Update.
// ============================================================================
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace Dishwasher
{
    public static class MenuTouchTargets
    {
        private struct Row
        {
            public int Index;
            public Rectangle Rect;
        }

        private static readonly List<Row> _rows = new List<Row>();
        private static int _pendingRow = -1;

        // PORT (touch): the exact canvas->screen blit rectangles used by the
        // last menu Draw, captured from Game1.Draw case 1. HitTest maps the tap
        // point back through these rectangles, so the hit rects always match
        // what is actually on screen (stretch or letterbox) with no fudge
        // factor. `_mapped` false => fall back to the derived constants.
        private static bool _mapped;
        private static Rectangle _src;
        private static Rectangle _dst;

        /// <summary>Record the menu-canvas -> screen blit used by Game1.Draw.
        /// `source` is the canvas rect (e.g. 0,0,1024,600) and `dest` the screen
        /// rect it is drawn into (e.g. 0,0,1600,720).</summary>
        public static void SetCanvasMapping(Rectangle source, Rectangle dest)
        {
            _src = source;
            _dst = dest;
            _mapped = true;
        }

        /// <summary>Number of rows recorded by the most recent menu Draw.</summary>
        public static int Count { get { return _rows.Count; } }

        /// <summary>Start a fresh frame of row recording. Called from
        /// MainMenu.DrawOptionButtons before any rows are drawn, and before its
        /// message-box / device-failed early returns, so those screens record
        /// no rows (a tap there falls back to A).</summary>
        public static void Begin()
        {
            _rows.Clear();
        }

        /// <summary>Record one drawn menu row. `x/y/width/height` are in the
        /// game's menu-canvas pixels (the space drawOption lays out in).</summary>
        public static void Add(int index, int x, int y, int width, int height)
        {
            Row r;
            r.Index = index;
            r.Rect = new Rectangle(x, y, width, height);
            _rows.Add(r);
        }

        /// <summary>Hit-test a logical-backbuffer tap point. Returns true and the
        /// row index on a hit. Checked last-recorded-first so the most recent
        /// layout wins if a frame ever records overlapping rows.</summary>
        public static bool HitTest(float logicalX, float logicalY, out int index)
        {
            index = -1;
            if (_rows.Count == 0)
            {
                return false;
            }

            // Canvas -> logical backbuffer, derived from the ACTUAL menu blit
            // rectangles captured by SetCanvasMapping (Game1.Draw case 1). This
            // is the exact inverse of the transform the renderer uses, so the
            // tap point and the row rects are guaranteed to be in the same
            // space: the 1024x600 (or 800x600) canvas is STRETCHED to
            // screenSize (dest 0,0,1600,720), not letterboxed. The fallback
            // below is only used for the first frame before Draw has run.
            float srcX, srcY, srcW, srcH, dstX, dstY, dstW, dstH;
            if (_mapped)
            {
                srcX = _src.X; srcY = _src.Y; srcW = _src.Width; srcH = _src.Height;
                dstX = _dst.X; dstY = _dst.Y; dstW = _dst.Width; dstH = _dst.Height;
            }
            else
            {
                srcX = 0f; srcY = 0f;
                srcW = projectDish.Globals.wideScreen ? 1024f : 800f;
                srcH = 600f;
                dstX = 0f; dstY = 0f;
                dstW = projectDish.Globals.screenSize.X;
                dstH = projectDish.Globals.screenSize.Y;
            }
            if (dstW <= 0f || dstH <= 0f)
            {
                return false;
            }

            // Inverse map: logical tap -> menu-canvas point.
            float cx = srcX + (logicalX - dstX) * srcW / dstW;
            float cy = srcY + (logicalY - dstY) * srcH / dstH;

            for (int i = _rows.Count - 1; i >= 0; i--)
            {
                Rectangle r = _rows[i].Rect;
                if (cx >= r.Left && cx <= r.Right
                    && cy >= r.Top && cy <= r.Bottom)
                {
                    index = _rows[i].Index;
                    return true;
                }
            }
            return false;
        }

        /// <summary>Stash a hit row for MainMenu.Update to apply this frame.</summary>
        public static void SetPendingRow(int index)
        {
            _pendingRow = index;
        }

        /// <summary>Consume the pending row (game thread, MainMenu.Update).
        /// Returns -1 when nothing was hit.</summary>
        public static int TakePendingRow()
        {
            int r = _pendingRow;
            _pendingRow = -1;
            return r;
        }
    }
}
