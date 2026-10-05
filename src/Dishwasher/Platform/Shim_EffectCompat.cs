// Compatibility extension methods: XNA 3.0 exposed Effect.Begin/End and
// EffectPass.Begin/End; XNA 4.0 / MonoGame removed them in favour of
// EffectPass.Apply(). These extensions keep the decompiled call sites
// compiling without editing every effect block. Shaders themselves are
// re-authored separately.
//
// RENDER DIAGNOSIS FIX (2026-10-02): the re-authored post-process effects are
// pixel-only in the original XNA 3.0 (the Xbox SpriteBatch supplied the sprite
// vertex shader). In MonoGame the effect must supply the VS, and SpriteBatch
// feeds it *pixel-space* vertices (see MonoGame's SpriteEffect: it transforms
// them with Matrix.CreateOrthographicOffCenter(0, viewport.W, viewport.H, 0,
// 0, -1)). The re-authored pass-through VS omitted that transform, so every
// custom-effect SpriteBatch draw landed outside clip space and rendered
// nothing -> the whole post-process chain (lense/grad/bloom/fade/ink/...) was
// black. `Effect.Begin()` is the one hook every XNA-3.0-style effect block
// calls, so we set the effect's `MatrixTransform` parameter here.
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Microsoft.Xna.Framework.Graphics
{
    public static class Xna3EffectCompat
    {
        public static void Begin(this Effect effect)
        {
            if (effect == null)
            {
                return;
            }
            try
            {
                EffectParameter p = effect.Parameters?["MatrixTransform"];
                if (p == null)
                {
                    return; // this effect has no sprite transform (e.g. a non-SpriteBatch effect)
                }
                GraphicsDevice gd = effect.GraphicsDevice;
                if (gd == null)
                {
                    return;
                }
                Viewport vp = gd.Viewport;
                Matrix m = Matrix.CreateOrthographicOffCenter(0f, vp.Width, vp.Height, 0f, 0f, -1f);
                p.SetValue(m);
            }
            catch
            {
                // Never let diagnostics/state setup break rendering.
            }
        }

        public static void End(this Effect effect) { }
        public static void Begin(this EffectPass pass) => pass.Apply();
        public static void End(this EffectPass pass) { }
    }
}
