# Phase 2 Guide — Port SSGIPass.cs core flow to BIRP

The original 1100-line URP `SSGIPass.cs` is preserved at `/_SSGIPass.urp.txt`. This guide is the porting recipe.

---

## High-level changes

The URP `SSGIPass` was a `ScriptableRenderPass` whose `Execute(context, ref renderingData)` got called by URP. The new BIRP `SSGIPass` is a plain class whose `Render(Camera cam, CommandBuffer cmd, SSGIRuntimeSettings runtime)` gets called by `SSGIController.OnPreRender` each frame.

**Key architectural shifts:**
1. `ScriptableRenderContext context` parameter is gone. All `context.ExecuteCommandBuffer(cmd); cmd.Clear();` calls disappear — the command buffer accumulates commands and is executed by Unity at the configured `CameraEvent`.
2. Methods that used to take `ScriptableRenderContext context, RenderingData renderingData` now take `Camera cam, CommandBuffer cmd`.
3. `cmd.GetTemporaryRT(...)` and `cmd.ReleaseTemporaryRT(...)` work the same way in BIRP.
4. Camera color/depth target accessors change (see mapping below).

---

## URP → BIRP type & API mapping

| URP | BIRP equivalent |
|-----|-----------------|
| `RenderingData.cameraData.camera` | passed-in `Camera cam` |
| `RenderingData.cameraData.cameraTargetDescriptor` | manually built `RenderTextureDescriptor`: `new RenderTextureDescriptor(cam.pixelWidth, cam.pixelHeight, format, depthBits)` |
| `RenderingData.cameraData.GetProjectionMatrix()` | `cam.projectionMatrix` (or `GL.GetGPUProjectionMatrix(cam.projectionMatrix, true)` for the GPU-flipped variant) |
| `RenderingData.cullResults` | **not directly available**; see geometry rendering section below |
| `renderer.cameraColorTargetHandle` | `BuiltinRenderTextureType.CameraTarget` |
| `renderer.cameraDepthTargetHandle` | `BuiltinRenderTextureType.Depth` (or `_CameraDepthTexture` after the depth prepass) |
| `_CameraDepthTexture` (URP global) | `_CameraDepthTexture` (same name in BIRP — needs `cam.depthTextureMode \|= DepthTextureMode.Depth`, already done in `SSGIController.OnEnable`) |
| `_CameraNormalsTexture` (URP) | **No direct equivalent in BIRP forward.** BIRP has `_CameraDepthNormalsTexture` (`DepthNormals` mode, packed differently). See `CaptureNormals.shader` adaptation in Phase 4 |
| `_MotionVectorTexture` | `_MotionVectorTexture` (same name; needs `cam.depthTextureMode \|= DepthTextureMode.MotionVectors`, already done) |
| `_GBuffer0/1/2/3` (URP deferred) | `_CameraGBufferTexture0/1/2/3` (BIRP deferred) — only matters if you tackle deferred (Phase 7) |
| `context.ExecuteCommandBuffer(cmd); cmd.Clear();` | **delete** — not needed; the buffer is executed by Unity automatically at the camera event |
| `CommandBufferPool.Get("name") / Release` | the buffer is owned by `SSGIController.hookedBuffer`. Use that single buffer; don't pool. |
| `context.DrawRenderers(cullResults, drawingSettings, filterSettings)` | see "Geometry rendering" section below |
| `RenderTextureDescriptor descriptor = GetDescriptor(renderingData, ...)` | rewrite `GetDescriptor` to take a `Camera` instead — multiply by render-scale, set `colorFormat`, `useMipMap`, `depthBufferBits = 0` etc. |
| `LimitFarClipPlane(renderingData, cmd, far)` | take `Camera cam` instead; use `cam.fieldOfView`, `cam.aspect`, `cam.nearClipPlane`. |
| `cmd.SetProjectionMatrix(...)` | works the same in BIRP. To restore: `cmd.SetProjectionMatrix(GL.GetGPUProjectionMatrix(cam.projectionMatrix, true))`. |

---

## VolumeComponent → ScriptableObject mapping

`SSGIVolumeComponent.X.value` → `SSGIRuntimeSettings.X` (all field names preserved). All ~30 references in the old `Denoise()`, `GatherSSGI()`, `BlitToScreen()` methods need this mechanical change. Grep for `ssgiComp\.` in `_SSGIPass.urp.txt` for the full list — there are ~30.

---

## CommandBuffer ownership

The single `CommandBuffer` is owned by `SSGIController.hookedBuffer`, cleared each `OnPreRender`, and added to the camera at `settings.CameraEvent`. So:

- All `cmd.X` calls in the ported `SSGIPass` should use the buffer **passed in to `Render(...)`**.
- No need to `Get`/`Release` from the pool, no need to `ExecuteCommandBuffer`.
- The order in which you queue commands IS the order they execute.

If you need to render at multiple `CameraEvent` slots (e.g., capture-normals at one event, final composite at another), you'd need multiple buffers attached at different events. For Forward-only at `BeforeImageEffectsOpaque`, a single buffer should be sufficient — but the **final composite** is best done in `OnRenderImage` (Phase 5), which gives you `src` and `dst` cleanly and runs after Unity's tonemap/post-FX setup is done.

---

## Geometry rendering (thickness mask + SSGIObjects) — Phase 3 detail

The URP version uses `context.DrawRenderers(cullResults, drawingSettings, filterSettings)` to draw scene geometry with an override material. BIRP doesn't expose culling results to scripts the same way. Two viable approaches:

### Approach A: Manual `cmd.DrawRenderer` enumeration (recommended for first port)
The existing `WriteSSGIObjects` already does this for per-object overrides:
```csharp
foreach (SSGIObject obj in RequestedObjects) {
    foreach (Renderer r in obj.AffectedRenderers) {
        if (!r || !r.enabled || !r.gameObject.activeInHierarchy) continue;
        if (!GeometryUtility.TestPlanesAABB(frustumPlanes, r.bounds)) continue;
        for (int j = 0; j < submeshCount; j++) {
            cmd.DrawRenderer(r, mat, j, passIndex);
        }
    }
}
```
For thickness mask, do the same but enumerate `Object.FindObjectsOfType<MeshRenderer>()` (or a cached list refreshed periodically) filtered by `settings.Raymarch.ThicknessMaskLayers`.

**Caveat:** `FindObjectsOfType` is slow if called every frame. Cache and refresh via:
- `OnSceneLoaded` callback (already hooked in `SSGIPass.HandleSceneUnloaded`)
- A periodic refresh (every N seconds)
- A registration pattern: have a `[DefaultExecutionOrder(-1000)] MonoBehaviour` on each thickness-eligible object that registers itself in `OnEnable`/`OnDisable`. Cleanest but requires user setup.

### Approach B: Replacement shader on a hidden secondary camera
`Camera.SetReplacementShader(shader, replacementTag)` does culling + drawing for you. Renders to a secondary RT. Standard BIRP pattern, ~30 lines of setup. Trickier to integrate into the SSGI's CommandBuffer flow because it's a synchronous render call rather than a queued command.

**Recommendation: A** — keeps the rendering inside the existing CommandBuffer flow, easier to reason about, and the SSGIObjects path is already doing exactly this.

---

## Static field migration already done

These statics were on `SSGIFeature`, now on `SSGIController`:
- `public static bool ShowInSceneView`
- `public static bool ShowToggleIconInSceneView`
- `public static bool SSGIActive`

`SSGISceneViewOverlay.cs` and `SSGIFeatureTester.cs` already updated.

---

## Things that disappear in BIRP (no port needed)

- `RefreshInput()` and `ConfigureInput(ScriptableRenderPassInput.X)` — URP's way of declaring dependencies. BIRP just needs `cam.depthTextureMode` flags set, already done in `SSGIController.OnEnable`.
- `Configure(CommandBuffer cmd, RenderTextureDescriptor cameraTextureDescriptor)` — URP-only.
- `Setup(ScriptableRenderer renderer)` — URP-only; the renderer reference was used for `cameraColorTargetHandle`. Replaced by `BuiltinRenderTextureType.CameraTarget`.
- `thicknessShaderTagIDList` (URP shader tag IDs) — gone with the URP `DrawRenderers` call; manual `DrawRenderer` doesn't need shader tags.

---

## Recommended porting order for Phase 2

1. Port the **constructor + Dispose + scene-unload hook + RT cache** — purely C#, no rendering.
2. Port `GetDescriptor`/`LimitFarClipPlane` helpers to take `Camera` instead of `RenderingData`.
3. Port `CaptureWorldPositionsAndDepth` — single blit, simplest pass. Verify the `_WorldPositions` global gets set.
4. Port `CaptureNormalsAndDepth` — single blit, but the shader needs Phase 4 work. **You can stub this until Phase 4** (just allocate the RT and clear to zero).
5. Port `CaptureScreenAndLight` — multiple blits, no geometry rendering.
6. Port `GatherSSGI` — the big one. Calls into the SSGI.shader main pass.
7. Port `Denoise` — pyramid loop + TAA pass. The C# changes are mechanical but there are ~25 lines.
8. Port reflection-probe collection (`CollectReflectionProbes` / `FilterActiveReflectionProbes`) — pure C#, no URP types except possibly the camera frustum.
9. Defer `WriteThicknessMask` and `WriteSSGIObjects` to Phase 3 (they're the geometry-rendering bits).
10. Defer `BlitToScreen` to Phase 5 (final composite via `OnRenderImage`).

After step 9, set `HasFinalComposite = true` in the stub and Phase 5 wires the final blit.

---

## Smoke test plan after Phase 5

1. Forward camera, simple scene with a few opaque meshes.
2. Add `SSGIController` to camera. Assign all settings + runtime SO.
3. Hit Play. Expected: scene renders with SSGI applied. Frame Debugger should show the SSGI passes between geometry and post-FX.
4. Compare to the screenshots/benchmark from the URP version (62 fps target).
5. Toggle `SSGIController.SSGIActive` (Enter key with the demo tester) — SSGI should toggle on/off.

---

## Known risks / gotchas

- **`_CameraNormalsTexture` doesn't exist in BIRP forward.** `CaptureNormals.shader` currently expects it. Phase 4 must replace with `_CameraDepthNormalsTexture` (BIRP's stereo-encoded normal+depth packed in RGBA) and add the unpack code, OR render normals to a RT ourselves via a depth-prepass-like setup. The latter is heavier but more compatible.
- **Motion vectors in BIRP** are only generated when something in the frame requests them via `cam.depthTextureMode`. Already set in `SSGIController.OnEnable`. If shaders complain about black motion vectors, check the camera flag actually stuck.
- **`OnRenderImage` requires the MonoBehaviour to be on the camera object itself.** Already the case (`[RequireComponent(typeof(Camera))]`).
- **`OnRenderImage` runs even if the scene is empty / camera doesn't render anything.** Need a guard for "SSGIPass hasn't been initialized yet" — currently handled via `pass.HasFinalComposite` flag.
- **Editor scene-view camera** also has `OnRenderImage` triggered if `[ExecuteAlways]`. Watch for SSGI rendering in scene-view when not desired. The original URP code had explicit checks for `SceneCamera`. Phase 2 should preserve those.
