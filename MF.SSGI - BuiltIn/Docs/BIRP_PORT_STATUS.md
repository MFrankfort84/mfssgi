# MF.SSGI: URP → Built-in Render Pipeline port — STATUS

**Phases 1–5 complete.** Smoke test next; treat this doc as the cold-start handoff.

## Phases

| # | Description | Status |
|---|-------------|--------|
| 1 | Skeleton: SSGIController MonoBehaviour + Runtime SO; delete URP feature/volume | **Done** |
| 2 | Port SSGIPass.cs core flow to BIRP (no thickness/objects yet) | **Done** |
| 3 | Port thickness mask + SSGIObjects geometry rendering | **Done** |
| 4 | Adapt CaptureNormals.shader + SSGI.hlsl albedo for BIRP forward | **Done** |
| 5 | Hook final composite via OnRenderImage; smoke test | **Done (test pending)** |
| 7 | Optional: BIRP deferred GBuffer path (`_CameraGBufferTexture0/1/2/3`) | Not started — only if needed |

Decision: **Forward only.** GBuffer references in `SSGI.hlsl` use BIRP names (`_CameraGBufferTexture0/1`) but are gated by `#ifdef _USE_DEFERRED` which never gets enabled in forward.

---

## File state

### Lives now
- `Scripts/Rendering/SSGIController.cs` — per-camera MonoBehaviour, `[ExecuteAlways]`. `OnPreRender` rebuilds the SSGI command buffer; `OnRenderImage` runs the final composite. Holds the `SSGISettings` nested class (the per-camera config) and references an `SSGIRuntimeSettings` ScriptableObject.
- `Scripts/Rendering/SSGIRuntimeSettings.cs` — replaces the URP `SSGIVolumeComponent`. `[CreateAssetMenu(menuName = "MF.SSGI/Runtime Settings")]`.
- `Scripts/Rendering/SSGIPass.cs` — full BIRP rewrite. Single entry: `Render(Camera, CommandBuffer, SSGIRuntimeSettings)`. Single composite: `FinalComposite(RenderTexture src, RenderTexture dst)`.
- `Shaders/CaptureNormals.shader` — rewritten for BIRP forward (`_CameraDepthNormalsTexture` + `DecodeDepthNormal`).
- `Shaders/SSGI.hlsl` — GBuffer references renamed to BIRP names; dead `SampleCameraNormalsTexture` helper removed; `_CameraNormalsTexture` declaration removed.

### Deleted
- `Scripts/Rendering/SSGIFeature.cs` (URP `ScriptableRendererFeature`)
- `Scripts/Rendering/SSGIVolumeComponent.cs` (URP `VolumeComponent`)
- `Scripts/SSGICamera.cs` (marker MonoBehaviour — role merged into SSGIController)

### Stubbed (URP-coupled, will not toggle SSGI in BIRP)
- `Scripts/SSGIFeatureTester.cs` — reduced to Enter-toggles + Tab-screenshot. The original screen-coverage wipe animation is gone.

### Updated references
- `Editor/SSGISceneViewOverlay.cs` — `SSGIFeature.X` → `SSGIController.X`.
- `Editor/SSGIDebugWindow.cs` — `SSGICamera` → `SSGIController`. Also includes `MF_SSGI/TAA` in the Always-Included shaders list.

### Reference (outside Assets, ignored by Unity)
- `_SSGIPass.urp.txt` at repo root — original 1100-line URP `SSGIPass.cs` for cross-reference.

---

## Architecture summary

**Camera lifecycle:**
1. `SSGIController.OnEnable` — sets `cam.depthTextureMode |= Depth | DepthNormals | MotionVectors`, allocates a single CommandBuffer named `MF.SSGI`, attaches it to the camera at `settings.CameraEvent` (default `BeforeImageEffectsOpaque`).
2. `SSGIController.OnPreRender` — clears the buffer, calls `pass.Render(cam, buffer, runtime)` which queues the entire SSGI pipeline.
3. Unity executes the buffer at `BeforeImageEffectsOpaque`. All SSGI work (capture, gather, denoise, TAA) runs as queued commands.
4. `SSGIController.OnRenderImage(src, dst)` — calls `pass.FinalComposite(src, dst)` which `Graphics.Blit`s through the FinalBlit material, then issues queued releases of all temp RTs.
5. `SSGIController.OnDisable` — removes the buffer, disposes pass.

**Render-target lifecycle:**
- Persistent buffered RTs (history for SSGI/TAA/WorldPositions): managed by `RTWrapper` ping-pong via `GenBufferedRT` (same as URP).
- Temp RTs (per-frame): allocated via `cmd.GetTemporaryRT`. **Released after FinalComposite, not at end of Render()** — they need to survive into `OnRenderImage`.

**Geometry rendering (thickness mask):**
- Renderers in the `settings.Raymarch.ThicknessMaskLayers` mask are enumerated via `Object.FindObjectsOfType<Renderer>()`, cached for 1 second.
- Each frustum-culled renderer is drawn via `cmd.DrawRenderer(r, overrideMat, submeshIndex, 0)` for each submesh.
- Replaces URP's `context.DrawRenderers(cullResults, drawingSettings, filterSettings)`.

**Geometry rendering (SSGIObjects):**
- Uses pre-registered `SSGIPass.RequestedObjects` list (populated by `SSGIObject.OnEnable`). Each entry already caches `AffectedRenderers` + `SubMeshCounts`.
- Same `cmd.DrawRenderer` per-submesh pattern.

---

## Smoke test plan

1. Open Unity (project should compile clean — verified no URP types remain).
2. **Switch to Built-in Render Pipeline** (Project Settings → Graphics → Scriptable Render Pipeline = None).
3. Run **Tools → SSGI → Add SSGI to 'Always included shaders'** to ensure the runtime can find them all (including the new `MF_SSGI/TAA`).
4. Add `SSGIController` to a forward-rendering camera.
5. Assign all five settings ScriptableObjects (`Quality` / `Lighting` / `Advanced` / `Raymarch` / `Fallback`) — same assets that worked under URP.
6. Create a new **SSGI Runtime Settings** asset (right-click → Create → MF.SSGI → Runtime Settings) and assign to the `runtime` slot.
7. Hit Play. Expected: scene renders with SSGI applied. **Frame Debugger** should show:
   - `MF.SSGI` command buffer firing at `BeforeImageEffectsOpaque`
   - Capture → ThicknessMask → SSGIObjects → ScreenCapture → SSGI gather → Denoise pyramid → TAA → (then OnRenderImage → FinalBlit at the end)

## Known risks for the smoke test

- **`_CameraDepthNormalsTexture` content quality** — BIRP encodes view-space normals with stereographic projection; the decode quality is lower than URP's OCT encoding. Surfaces facing nearly perpendicular to the camera may show slight normal-precision artifacts. Should be close enough for SSGI.
- **`unity_CameraToWorld` Z-flip** — Unity's view-space convention varies between platforms. If world-normals look inverted (lighting comes from wrong direction), in `CaptureNormals.shader` change `mul((float3x3)unity_CameraToWorld, viewNormal)` to `mul((float3x3)unity_CameraToWorld, viewNormal * float3(1, 1, -1))`.
- **Reflection probes** — BIRP's `Material.SetGlobalTexture` and `cmd.SetGlobalTexture` for cubemaps work the same as URP. Should be transparent.
- **Frustum culling for thickness/SSGIObjects** — using `GeometryUtility.CalculateFrustumPlanes(cam, ...)` which works for non-camera coords too. `Renderer.bounds` may not be tight on skinned meshes; expect occasional false-positives.
- **Editor scene view** — SSGIController has `[ExecuteAlways]`. If you don't want SSGI in the scene view, leave SSGIController OFF on any scene-view camera (the user wouldn't add it there normally).

If the screen renders pure black or pure scene (no SSGI visible), check Frame Debugger for which pass is failing. Most likely: `CaptureNormals` outputting garbage → world-normals are wrong → SSGI gather rejects everything.

## What landed during the optimization series (all preserved through the port)

See `OPTIMIZATION_HISTORY.md`. Net result: 48 → 62 fps with crisper shadows. All shader-side work transfers identically to BIRP.
