# MF.SSGI shader optimization history

Pre-port optimizations applied while the asset was still on URP. **All shader changes carry through to the BIRP port** since shaders are 95% pipeline-agnostic. Documented here so the rationale isn't lost if anyone re-reads the shader files cold.

Starting point: 48 fps. Ending point: 62 fps (~29% gain) with **better** shadow quality.

---

## Tier S — biggest structural wins

1. **`_do_encode_lightdir` → `_ENCODE_LIGHTDIR` keyword.** Was a uniform branch in 5 hot fragment paths. Now `multi_compile`, eliminates an entire encode/decode codepath at compile time when off. Affects `SSGI.shader`, `Denoise.shader`, `FinalBlit.shader`, `TAA.shader`. C# side: `Shader.SetGlobalInt` replaced with `Shader.EnableKeyword/DisableKeyword`.
2. **Hash-without-sine** in `SSGI.hlsl::MFSSGI_Hash23(float2 p)`. Replaces three `sin(dot(...))*frac(...)` calls per inner-loop iteration with one cheap integer-style hash. ~128 sin calls eliminated per pixel at samples=8.
3. **Parabola window** `4t(1-t)` replacing `sin(t*π)` for the radial sample weighting. ~64 sin calls eliminated per pixel.
4. **`[branch]` hints** on uniform conditionals: `_multi_sample_normal_distance`, `_multiframe_cell_size`, `_edge_vignette`, `_ssgi_samples_reduction`, `_shadow_intensity`, `_ssgi_fallback_direct_intensity`. Free; helps cross-compilers emit predicated code instead of unrolled flattening.
5. **`_use_deferred` → `_USE_DEFERRED`** keyword (same treatment as `_ENCODE_LIGHTDIR`).
6. **`_use_ssgi_objects` → `_USE_SSGI_OBJECTS`** keyword.

## Tier A — local micro-ops

- Vignette `min` cascade vectorized: 4 div + 4 scalar min → 1 vec2 op + 1 div.
- Cached duplicate `_MF_SSGI_Normals_HQ` reads in `FinalBlit.shader::frag` (was 4 reads of the same texel).
- Inner-loop reciprocals precomputed: `invLightDirCastRange`, `invLightDirReceiveRange`, `invNearCamRange`, `invContactShadowRange`, `invSamples`. 3 divisions saved per inner iteration.
- Skybox-influence divisor `_skybox_influence/(samples*samples)` hoisted out of the loop.
- Contact-shadow distance check moved to squared-vs-squared comparison; sqrt deferred until inside the range.
- `color = color/sqrt(x)*k` → `color *= rsqrt(x)*k` (rsqrt is faster than 1/sqrt on most GPUs).

## Tier B — denoiser quality

- **Half-texel fix** at `SSGI.shader:164` and `:423`: `round(uv*res)/res` was snapping to pixel borders, causing visible shadow bleed under bilinear sampling. Fixed to `(floor(uv*res) + 0.5) / res`. **This was the smoking gun for the bleeding the user originally complained about.**
- 5-tap shadow cross: shadow channel uses only center + 4 cross taps from the 9-tap Denoise kernel. Color stays full 9-tap. Crisper shadow edges with no extra cost.
- Fixed shadow double-accumulate bug in `Denoise.shader::SampleEnvironmentSimple` (was `resultShadow += min(2.0, resultShadow + ...)`, correct is `resultShadow = min(...)`).
- Stricter shadow rejection: `shadowDotMin = lerp(min, max, 0.5)` (vs `> min` for color), depth-diff threshold halved, falloff squared.
- **Bilateral similarity term** in `Denoise.shader::SampleEnvironment`: shadow neighbor downweighted by `saturate(1.0 - abs(neighborShadow - centerShadow) * _denoise_shadow_similarity)` (default 6.0). Center shadow returned by `SampleEnvironmentSimple` for free (was already sampling it).

## Tier C — WebGL-specific

- Reflection probe sampler cap on GLES3: probes 4-7 wrapped in `#if MFSSGI_MAX_PROBES > 4`. WebGL2 has 16-sampler limit; SSGI.shader uses many already. Frees 4 cubemap slots.
- (Skipped intentionally) `half` precision audit — too risky for the encoded normal/color paths.

## Joint bilateral upscale (`FinalBlit.shader`)

`SampleSimpleSSGI` (plain bilinear) replaced with `JointBilateralUpscaleSSGI` — proper 4-tap edge-aware upscale of low-res SSGI to full-res, weighted by:
- Standard bilinear distance weight
- Normal similarity (HQ vs each LQ tap, x⁴ sharpened)
- Depth similarity (linear falloff, scales with depth)

Cost: 8 tex samples per pixel (4 LQ normals + 4 LQ SSGI) instead of 1 bilinear sample. Replaces all 3 `SampleSimpleSSGI` call sites in `frag`. Has a fallback to plain bilinear if all 4 taps got rejected. Encoded path correctly decodes each tap before weighting.

## TAA on shadow channel (new `TAA.shader`)

Sits between Denoise and FinalBlit in the pipeline. Uses:
- New buffered RT: `SSGIPassType.TAAHistory` (ping-pong via `GenBufferedRT`)
- Motion vector reprojection: `prevUV = i.uv - tex2D(_MotionVectorTexture, i.uv).xy`
- Disocclusion check: rejects history if `|currentWorldPos - prevWorldPos|² > _taa_world_pos_threshold²` (default 0.1 world units squared)
- Out-of-bounds rejection: rejects history if reprojected UV offscreen
- 5-tap cross neighborhood clamp on shadow alpha with 25% slack
- Blend: `lerp(current.a, clampedHistory.a, 0.85)` — alpha only, color/light-dir pass through

Effect: lets the user cut SSGI sample counts substantially (~30-40% on HQ + Backfill) while shadow quality improves rather than degrades. The single biggest visual+perf win.

C# side: re-binds `_MF_SSGI_Denoised_Final` global to the TAA output so `FinalBlit` transparently reads the stabilized version. No FinalBlit shader change needed.

## Hygiene

- Dead `RGBToHSV` / `HSVToRGB` removed from `SSGI.hlsl` (declared but never called).

## Final per-frame state

- Denoise Passes: dropped from original 7 to 5 by user pre-optimization, can now go to 3
- Pre Denoise Pixel Size: 25 (can drop to ~12 with TAA active)
- AA Quality Level: was 2, can drop to 1 (joint bilateral handles upscale)
- SSGI Samples HQ + Backfill: confirmed user can drop "a lot" with TAA banking the noise reduction

## Tunables exposed in shaders, not in C# settings

- `_denoise_shadow_similarity` (default 6.0 in `Denoise.shader`)
- `_taa_shadow_blend` (set to 0.85 in `SSGIPass.cs::Denoise()` C#)
- `_taa_world_pos_threshold` (set to 0.1 in same place)

If the user later wants these in the inspector, they'd add fields to `SSGIQualitySettings` or `SSGIRuntimeSettings` and set them via `cmd.SetGlobalFloat`.
