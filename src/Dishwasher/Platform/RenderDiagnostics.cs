// ============================================================================
// TEMP RENDERING DIAGNOSTICS  --  Rendering Diagnostics Specialist.
//                             DEBUG TOOL -- INERT BY DEFAULT.
//
// Additive, clearly-marked, revertable. Drives controlled render experiments
// at runtime so rendering bugs can be isolated without a rebuild per
// experiment. The mode table below is the full inventory; mode 0 is the
// shipping behaviour and is the only mode this build should ever run.
//
// HOW TO DISABLE (already the default): `Enabled` is `false`, so Mode reads as
// 0, all Skip*/Bypass* predicates are false, IsLenseFix is true and the volume
// keys fall through to the system. To re-enable, set `Enabled = true` below.
//
// Toggle the mode live with the hardware VOLUME keys (which the game does not
// use) ONLY while `Enabled == true`:
//
//     VOLUME_UP   -> Mode + 1
//     VOLUME_DOWN -> Mode - 1
//
// Mode table (see also android/notes/render-diagnosis.md):
//   0  normalized behaviour (lense sampler-bind FIX enabled)   <- shipping
//   90 lense ORIGINAL bind order (known-broken)                <- "before"
//   1  gameplay final: show rTarg[curRTarg] (pre-lense scene)
//   2  gameplay final: show rTarg[1-curRTarg] (post-lense)
//   3  gameplay: force noRefractEffect -> drawMinimalTarg copy
//   4  gameplay final: show refractTarg
//   5  gameplay final: show lenseTarg
//   6  gameplay final: show mapTarg (upscaled)
//   7  gameplay final: show bloomTarg (upscaled)
//   8  gameplay final: apply grad via sprite.Begin(..., gradEffect)
//   9  log-only: dump grad params + RT/texture bindings once
//   10 menu: skip refract overlay
//   11 menu: skip lense overlay
//   12 menu: skip both overlays
// ============================================================================
using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Dishwasher
{
    public static class RenderDiagnostics
    {
        // ---- master switch -------------------------------------------------
        // false == shipping. When false, `Mode` reads as 0, every Skip*/Bypass*
        // predicate is false, IsLenseFix is true, and Bump() (volume keys) is a
        // no-op: the render path is exactly the shipping path and no diagnostic
        // work runs. Set true to re-enable the mode harness below.
        public static bool Enabled = false;

        private static int _mode = 0;

        // Effective mode. Hidden `_mode` is ignored while !Enabled so a stale
        // experiment value can never leak into a shipping build.
        public static int Mode => Enabled ? _mode : 0;

        public static string LastPath = "";
        private static double _lastLog;

        // Read back a few pixels from a render target and log them. Used by mode 9.
        public static void LogRT(string name, RenderTarget2D rt)
        {
            if (rt == null) { Log.Info("[rdiag] rt " + name + " = null"); return; }
            try
            {
                Color[] px = new Color[1];
                int x = Math.Max(0, rt.Width / 2);
                int y = Math.Max(0, rt.Height / 2);
                rt.GetData(0, new Rectangle(x, y, 1, 1), px, 0, 1);
                Log.Info("[rdiag] rt " + name + " " + rt.Width + "x" + rt.Height
                    + " msaa=" + rt.MultiSampleCount
                    + " center=(" + px[0].R + "," + px[0].G + "," + px[0].B + "," + px[0].A + ")");
            }
            catch (Exception ex) { Log.Exception("rdiag.logrt." + name, ex); }
        }

        // Throttled logging helper for hot paths.
        public static bool LogThrottled(string msg, double now)
        {
            if (now - _lastLog < 1.0) return false;
            _lastLog = now;
            Log.Info(msg);
            return true;
        }

        public static bool IsLenseFix => Mode != 90;

        public static bool SkipRefractOverlay => Mode == 10 || Mode == 12;

        public static bool SkipLenseOverlay => Mode == 11 || Mode == 12;

        // ---- COLOUR-GRADE ISOLATION (Shader Fidelity Specialist) ------------
        // Each gameplay post overlay can be neutralised independently so its
        // contribution to the final framebuffer can be measured.  The mode is
        // chosen with the volume keys; see Describe().
        //
        //   20 skip additive bloom overlay          25 skip wallblood composite
        //   21 skip additive fade theme overlay     26 skip newblood / black-blood
        //   22 skip cam-splat overlay               27 skip bloom+fade+camsplat
        //   23 skip water overlay                   32 neutral ALL overlays + grad bypass
        //   24 grad bypass (opaque post-lense)      33 neutral ALL + no lense
        //                                           34 neutral overlays, grad kept
        private static bool Any(int a, int b, int c3, int c4, int c5) =>
            Mode == a || Mode == b || Mode == c3 || Mode == c4 || Mode == c5;
        public static bool SkipBloom       => Any(20, 27, 32, 33, 34);
        public static bool SkipFadeOverlay => Any(21, 27, 32, 33, 34);
        public static bool SkipCamSplat    => Any(22, 27, 32, 33, 34);
        public static bool SkipWater       => Any(23, 32, 33, 34, -1);
        public static bool SkipWallBlood   => Any(25, 32, 33, 34, -1);
        public static bool SkipNewBlood    => Any(26, 32, 33, 34, -1);
        public static bool BypassGrad      => Any(24, 32, 33, -1, -1);
        public static bool NeutralAll      => Mode == 32 || Mode == 33 || Mode == 34;

        public static void Bump(int delta)
        {
            if (!Enabled) return; // inert by default; see Enabled
            _mode += delta;
            if (_mode < 0) _mode = 0;
            if (_mode > 120) _mode = 120;
            Log.Info("[rdiag] mode=" + _mode + "  " + Describe(_mode));
        }

        public static string Describe(int m)
        {
            switch (m)
            {
                case 0: return "normal (lense fix ON)";
                case 1: return "gameplay: show pre-lense rTarg";
                case 2: return "gameplay: show post-lense rTarg";
                case 3: return "gameplay: force noRefract (minimal copy)";
                case 4: return "gameplay: show refractTarg";
                case 5: return "gameplay: show lenseTarg";
                case 6: return "gameplay: show mapTarg";
                case 7: return "gameplay: show bloomTarg";
                case 8: return "gameplay: grad via sprite.Begin(effect)";
                case 9: return "log-only (grad params + bindings)";
                case 10: return "menu: skip refract overlay";
                case 11: return "menu: skip lense overlay";
                case 12: return "menu: skip both overlays";
                case 18: return "readback after minimal copy (no lense)";
                case 14: return "gameplay: RT->RT copy mapTarg then show";
                case 15: return "gameplay: RT->RT copy refractTarg then show";
                case 16: return "gameplay: RT->RT copy rTarg[cur] then show";
                case 20: return "gameplay: skip bloom overlay";
                case 21: return "gameplay: skip fade theme overlay";
                case 22: return "gameplay: skip cam-splat overlay";
                case 23: return "gameplay: skip water overlay";
                case 24: return "gameplay: grad bypass (opaque post-lense)";
                case 25: return "gameplay: skip wallblood composite";
                case 26: return "gameplay: skip newblood / black-blood";
                case 27: return "gameplay: skip bloom+fade+camsplat";
                case 32: return "gameplay: neutral ALL overlays + grad bypass";
                case 33: return "gameplay: neutral ALL + minimal (no lense)";
                case 34: return "gameplay: neutral overlays, grad kept";
                case 30: return "menu: ink drawn with White (no tint)";
                case 40: return "menu: show refractTarg";
                case 41: return "menu: show lenseTarg";
                case 42: return "menu: show rTarg[cur]";
                case 43: return "menu: show mapTarg";
                case 90: return "lense ORIGINAL bind order (broken)";
                default: return "(unused)";
            }
        }
    }
}
