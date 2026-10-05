// ============================================================================
// PORT WIDESCREEN CONFIG (2026-10-02) — Widescreen Rendering Specialist.
//
// The game's world/camera, render targets and HUD are all expressed in
// `Globals.screenSize` pixels, and the engine draws world->screen 1:1 (a world
// unit is a pixel). To show MORE of the level horizontally while keeping the
// vertical framing (and therefore character scale / feel) identical, we render
// the whole game into a *logical* 16:9-plus backbuffer whose height stays 720
// and whose width is widened to the panel aspect, then let the Android
// compositor scale that buffer uniformly to the physical panel.
//
// Activity1 sets the MonoGame SurfaceView's measured size to
// (LogicalWidth x LogicalHeight) and applies a uniform `Scale` so the view
// covers the whole window; MonoGame then creates its GL surface / viewport at
// the logical size (its DisplayMode is the view size) and Game1.Initialize reads
// the values here instead of the old fixed 1280x720 lookup.
//
// This file is additive and revertable: delete it, delete the WidescreenConfig
// block in Game1.Initialize and the wrapper/scale in Activity1.OnCreate.
// ============================================================================
namespace Dishwasher
{
    public static class WidescreenConfig
    {
        /// <summary>True once Activity1 has chosen the logical size.</summary>
        public static bool Enabled = false;

        /// <summary>Logical backbuffer width in game pixels (720 * panel aspect).</summary>
        public static int LogicalWidth = 1600;

        /// <summary>Logical backbuffer height in game pixels (kept at 720).</summary>
        public static int LogicalHeight = 720;

        /// <summary>Uniform scale applied to the SurfaceView to fill the panel.</summary>
        public static float Scale = 1.5f;
    }
}
