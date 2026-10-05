| effect | loaded | params (name:type) | technique(s) / pass(es) | shader blobs | blob bytes |
|---|---|---|---|---|---|
| `RadialBlur` | **dead** | WorldViewProjection:float, UVOffset:float, UVScale:float, TexelSize:float, Center:float, GlobalAlpha:float, PixelDistance:float, DiffuseTexture:texture, EffectMaskTexture:texture, DiffuseTextureSampler:sampler2d, EffectMaskTextureSampler:sampler2d | RadialBlurQuad[p0]; RadialBlurStandard[MainPass] | 4 (PS/VS/PS/VS) | 1284, 516, 1284, 360 |
| `blood` | **dead** | samplerState:sampler, originalMap:sampler, fore:bool | Blood[P0] | 1 (PS) | 1380 |
| `blood2` | **dead** | samplerState:sampler, bright:float, statik:float, lineStatik:float | Blood[P0] | 1 (PS) | 1584 |
| `bloom` | yes | samplerState:sampler, offsets:float | PostBloom[P0] | 1 (PS) | 1016 |
| `blur` | yes | samplerState:sampler, levs:int, width:float, xcenter:float, ycenter:float, blackblood:bool | Blast[P0, P1] | 2 (PS/PS) | 1164, 816 |
| `bubble` | yes | samplerState:sampler, width:float, xcenter:float, ycenter:float, mag:float, reddish:bool | Bubble[P0] | 1 (PS) | 860 |
| `burnblur` | yes | samplerState:sampler, levs:int, width:float, xcenter:float, ycenter:float, mag:float | Blast[P0] | 1 (PS) | 904 |
| `camsplat` | yes | samplerState:sampler, red:float | CamSplat[P0] | 1 (PS) | 940 |
| `color` | yes | samplerState:sampler, x:float, y:float, alpha:float | PostColor[P0] | 1 (PS) | 560 |
| `comicblur` | yes | samplerState:sampler, left1:float, top1:float, right1:float, bottom1:float, left2:float, top2:float, right2:float, bottom2:float, a:float, speed:float | ComicBlur[P0] | 1 (PS) | 596 |
| `fade` | yes | samplerState:sampler, rad:float, fader:int, offsets:float | PostFade[P0] | 1 (PS) | 1400 |
| `forefade` | **dead** | samplerState:sampler, rad:float, r:float, g:float, b:float, colorOnly:bool, offsets:float | PostFade[P0] | 1 (PS) | 1368 |
| `glisten` | **dead** | samplerState:sampler, offx:float, offy:float | Glistener[P0] | 1 (PS) | 484 |
| `grad` | yes | samplerState:sampler, burnmag:float, gradFlip:bool, rgrad:float, ggrad:float, bgrad:float, bright:float, levs:int, width:int, xcenter:float, ycenter:float | PostGrad[P0] | 1 (PS) | 1024 |
| `ink` | yes | samplerState:sampler, bright:float | Ink[P0] | 1 (PS) | 556 |
| `lense` | yes | refractSampler:sampler, backBuffer:sampler, edgeBlur:float, offsets:float | PostLense[P0] | 1 (PS) | 1116 |
| `newblood` | yes | samplerState:sampler, bright:float, statik:float, lineStatik:float | Blood[P0] | 1 (PS) | 1048 |
| `poster` | yes | samplerState:sampler, alpha:float, rMin:float, gMin:float, bMin:float, rMid:float, gMid:float, bMid:float, rMax:float, gMax:float, bMax:float, fore:bool | Poster[P0] | 1 (PS) | 1392 |
| `radblur` | **dead** | samplerState:sampler | PostRad[P0] | 1 (PS) | 976 |
| `redhaze` | yes | samplerState:sampler, horiz:bool, glare:bool | PostHaze[P0] | 1 (PS) | 712 |
| `trail` | yes | samplerState:sampler, tx:float, ty:float, alpha:float, r:float, g:float, b:float | PostTrail[P0] | 1 (PS) | 668 |
| `trainblur` | yes | samplerState:sampler, levs:int, width:float, xcenter:float, ycenter:float, facta:float, horizon:float | Blast[P0] | 1 (PS) | 996 |
| `trainover` | yes | samplerState:sampler, facta:float | Train[P0] | 1 (PS) | 756 |
| `wallblood` | yes | samplerState:sampler, red:float, offsets:float | WallBlood[P0] | 1 (PS) | 944 |
| `water` | yes | samplerState:sampler, horizon:float, delta:float, theta:float, rnd:float, puddle:bool | PostWater[P0] | 1 (PS) | 872 |
