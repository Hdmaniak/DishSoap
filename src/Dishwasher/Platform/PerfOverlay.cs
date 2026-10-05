// ============================================================================
// PORT (fps overlay) --  real measured FPS drawn on screen when
// AndroidSettings.ShowFps is on.
//
// Measurement: every presented frame (Game1.Draw tail) is timestamped with the
// monotonic Stopwatch clock.  The displayed value is the number of frames
// presented in the trailing ~0.75 s window divided by that window's wall-clock
// span, so it reflects frames actually presented per second (post-limiter,
// post-vsync) rather than a per-frame instantaneous 1/dt.
//
// Drawing: reuses the game's own comic text path (`Text.drawCText(string,...)`),
// the same font the HUD score/exp readouts use, so it matches the game's look.
// It is drawn at the bottom-left, a corner the HUD does not use (health is
// top-left, score/combo top-right, buttons bottom-right).  It is drawn from the
// common tail of Game1.Draw, so it appears on the main menu and during gameplay
// (loader / fatal-error screens return before the tail and are not covered).
//
// Revert: delete this file + the `PORT (fps overlay)` hook in Game1.Draw +
// FrameLimiter.cs + the Android settings row.  Nothing else changes.
// ============================================================================
using System;
using System.Diagnostics;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using projectDish;

namespace Dishwasher
{
    public static class PerfOverlay
    {
        /// <summary>Sliding measurement window, seconds.</summary>
        private const double WindowSeconds = 0.75;
        private const int MaxSamples = 256;

        private static readonly long[] _stamps = new long[MaxSamples];
        private static int _head;
        private static int _count;
        private static double _fps;

        // Set on every drawn frame so the last draw can log the value / a test
        // harness can read it.
        public static double Fps { get { return _fps; } }

        /// <summary>Called once per presented frame, from Game1.Draw's tail.
        /// Records the timestamp, recomputes the window, and draws the counter
        /// when enabled.</summary>
        public static void Frame(SpriteBatch sprite, Text text, Texture2D comicTex)
        {
            long now = Stopwatch.GetTimestamp();

            _stamps[_head] = now;
            _head = (_head + 1) % MaxSamples;
            if (_count < MaxSamples) _count++;

            if (_count >= 2)
            {
                long cutoff = now - (long)(WindowSeconds * Stopwatch.Frequency);
                int inWindow = 0;
                int oldest = -1;
                // Walk backwards from the newest sample; stop once outside the window.
                for (int i = 0; i < _count; i++)
                {
                    int idx = (_head - 1 - i + MaxSamples * 2) % MaxSamples;
                    if (_stamps[idx] < cutoff) break;
                    inWindow++;
                    oldest = idx;
                }
                if (inWindow >= 2)
                {
                    double span = (now - _stamps[oldest]) / (double)Stopwatch.Frequency;
                    if (span > 0.0) _fps = (inWindow - 1) / span;
                }
            }

            if (!AndroidSettings.ShowFps) return;
            if (sprite == null || text == null || comicTex == null) return;
            if (_fps <= 0.0) return;

            Draw(sprite, text, comicTex);
        }

        private static void Draw(SpriteBatch sprite, Text text, Texture2D comicTex)
        {
            int x = (int)Globals.border.X + 12;
            int y = (int)(Globals.screenSize.Y - Globals.border.Y) - 44;
            string label = "fps " + (int)Math.Round(_fps);

            text.setSize(0.8f);
            // Cheap drop shadow so it stays legible over both dark and light art.
            text.setColor(new Color(0f, 0f, 0f, 0.8f));
            text.drawCText(x + 2, y + 2, label, sprite, comicTex, centered: false);
            text.setColor(Color.White);
            text.drawCText(x, y, label, sprite, comicTex, centered: false);
        }
    }
}
