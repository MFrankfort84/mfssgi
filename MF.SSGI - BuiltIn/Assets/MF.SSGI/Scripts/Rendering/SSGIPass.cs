using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using System.Collections.Generic;
using System.Linq;
using MF.SSGI.Settings;

namespace MF.SSGI {
    //Built-in Render Pipeline pass class. Replaces the URP ScriptableRenderPass-based version.
    //Owns the per-camera RT lifecycle, materials, and the fixed pipeline of:
    //  CaptureWorldPos -> CaptureNormals -> ThicknessMask -> SSGIObjects ->
    //  CaptureScreenAndLight -> GatherSSGI -> Denoise -> TAA -> FinalComposite
    //
    //Phase 2: core flow ported. Phase 3 will fill in the geometry-rendering passes
    //(WriteThicknessMask, WriteSSGIObjects, expand-vertices). Phase 4 adapts shaders
    //(_CameraNormalsTexture / albedo). Phase 5 wires the final composite into
    //SSGIController.OnRenderImage.
    public class SSGIPass {

        public enum SSGIPassType {
            ThicknessMaskPrePass,
            ThicknessMask,
            SSGIObjects,
            ScreenCapture,
            LightCapture,
            WorldPositions,
            WorldPosDepth,
            Normals,
            NormalsDepth,
            SSGIColor,
            SSGIShadow,
            SSGILightDir,
            PreDenoised1Color,
            PreDenoised1Shadow,
            PreDenoised1LightDir,
            PreDenoised2Color,
            PreDenoised2Shadow,
            PreDenoised2LightDir,
            FinalDenoisedColor,
            FinalDenoisedShadow,
            FinalDenoisedLightDir,
            TAAHistory,
        }

        public class DebugRTData {
            public Camera Camera;
            public SSGIPassType Type;
            public bool WasHandled = false;
        }

        public class RTWrapper {
            public Camera Cam;
            public SSGIPassType Type;
            public RenderTexture RT1;
            public RenderTexture RT2;

            public void Dispose() {
                if (RT1) GameObject.DestroyImmediate(RT1);
                if (RT2) GameObject.DestroyImmediate(RT2);
                RT1 = null; RT2 = null; Cam = null;
            }
        }

        public class ReflectionProbeWrapper {
            public ReflectionProbe Probe;
            public SSGIReflectionProbeOverride Override;
            public float Fade;

            public ReflectionProbeWrapper(ReflectionProbe probe) {
                Probe = probe;
                Override = probe.GetComponent<SSGIReflectionProbeOverride>();
            }
        }

        //---------------------- STATIC
        public const string UNITY_RTNAME_DEPTH = "_CameraDepthTexture";

        public static List<SSGIObject> RequestedObjects = new List<SSGIObject>();
        private static Dictionary<RenderTexture, DebugRTData> debugRenderTextures = new Dictionary<RenderTexture, DebugRTData>();
        private static Dictionary<Camera, List<ReflectionProbeWrapper>> reflectionProbeWrappers = new Dictionary<Camera, List<ReflectionProbeWrapper>>();
        private static List<RTWrapper> rtWrappers = new List<RTWrapper>();
        private static Dictionary<Camera, int> frameCountTable = new Dictionary<Camera, int>();

        public static void SetDebugRT(RenderTexture rt, Camera cam, SSGIPassType type) {
            if (debugRenderTextures.ContainsKey(rt)) {
                debugRenderTextures[rt].Camera = cam;
                debugRenderTextures[rt].Type = type;
                debugRenderTextures[rt].WasHandled = false;
            } else {
                debugRenderTextures.Add(rt, new DebugRTData { Camera = cam, Type = type });
            }
        }

        public static void RemoveDebugRT(RenderTexture rt) {
            if (rt == null) return;
            debugRenderTextures.Remove(rt);
        }

        //---------------------- INSTANCE
        private SSGIController.SSGISettings settings;

        private Material thicknessMaskMaterialBack;
        private Material thicknessMaskMaterialFront;
        private Material ssgiExpandVerticesMaterial;
        private Material ssgiObjectMaterial;
        private Material captureLightMaterial;
        private Material worldPosMaterial;
        private Material captureNormalsMaterial;
        private Material scanEnvironmentMaterial;
        private Material denoiseImageMaterial;
        private Material taaShadowMaterial;
        private Material blitFinalImageMaterial;
        private Material debugBlitMaterial;

        private List<int> rtsToRelease = new List<int>();
        private RenderTextureFormat halfHDR = RenderTextureFormat.ARGBHalf;
        private RenderTextureFormat fullHDR = RenderTextureFormat.ARGBFloat;

        private ReflectionProbe[] allProbes;
        private List<ReflectionProbe> filteredProbes = new List<ReflectionProbe>();
        private List<ReflectionProbe> activeProbes = new List<ReflectionProbe>();
        private float probesLastCollectTimestamp;
        private Plane[] frustumPlanes = new Plane[6];
        private float lastEditorUpdateTimestamp = -1f;
        private Vector2Int[] rndNumbers;

        private Matrix4x4 backupProjectionMatrix;
        private RenderTextureDescriptor currentCamTexDescriptor;

        //Thickness-mask renderer cache. Refreshed periodically since FindObjectsOfType is
        //expensive every frame. Layer mask change forces an immediate refresh.
        private Renderer[] cachedThicknessRenderers;
        private int cachedThicknessLayerMaskValue;
        private bool cachedThicknessInitialized = false;
        private float thicknessRenderersRefreshTime = -1f;
        private const float THICKNESS_REFRESH_INTERVAL = 1f;

        //Only true when Render() ran AND completed THIS frame. Guards FinalComposite against
        //sampling stale globals from a previous frame whose temp RTs have been released.
        private bool renderedThisFrame;
        public bool HasFinalComposite => renderedThisFrame && blitFinalImageMaterial != null;

        //Tracks which "X is missing" warnings we've already emitted, so the log doesn't spam.
        private HashSet<string> warnedMissingRefs = new HashSet<string>();

        private void LogMissingOnce(string key, string message) {
            if (warnedMissingRefs.Add(key)) Debug.LogWarning($"[MF.SSGI] {message} — SSGI is disabled until this is fixed.");
        }

        private bool ValidateSettingsRefs() {
            bool ok = true;
            CheckRef(settings.Quality,  "Quality",  ref ok);
            CheckRef(settings.Lighting, "Lighting", ref ok);
            CheckRef(settings.Advanced, "Advanced", ref ok);
            CheckRef(settings.Raymarch, "Raymarch", ref ok);
            CheckRef(settings.Fallback, "Fallback", ref ok);
            return ok;
        }

        private void CheckRef(ScriptableObject so, string fieldName, ref bool ok) {
            if (so == null) {
                LogMissingOnce(fieldName, $"SSGIController.settings.{fieldName} is unassigned");
                ok = false;
            } else {
                warnedMissingRefs.Remove(fieldName);
            }
        }

        //Persistent release buffer reused across frames. Avoids per-frame CommandBuffer churn.
        private CommandBuffer releaseBuffer;

        public SSGIPass(SSGIController.SSGISettings settings) {
            this.settings = settings;
            if (!SystemInfo.SupportsRenderTextureFormat(halfHDR)) {
                halfHDR = RenderTextureFormat.ARGBFloat;
            }

            SceneManager.sceneUnloaded -= HandleSceneUnloaded;
            SceneManager.sceneUnloaded += HandleSceneUnloaded;
        }

        public void Dispose() {
            SceneManager.sceneUnloaded -= HandleSceneUnloaded;
            ReleaseAllBufferedRTs();
            releaseBuffer?.Dispose();
            releaseBuffer = null;
        }

        //=== Main entry, called by SSGIController.OnPreRender each frame ===
        public void Render(Camera cam, CommandBuffer cmd, SSGIController controller) {
            //Default: Render didn't complete. Set to true at the very end if it did.
            renderedThisFrame = false;

            if (!SSGIController.SSGIActive) return;
            if (settings == null) { LogMissingOnce("settings", "SSGIController.settings is null"); return; }
            else warnedMissingRefs.Remove("settings");
            if (controller == null) { LogMissingOnce("controller", "SSGIPass.Render received a null controller"); return; }
            else warnedMissingRefs.Remove("controller");
            if (!ValidateSettingsRefs()) return;

            //Clear rtsToRelease at the START — if last frame's FinalComposite was skipped (e.g., camera
            //disabled mid-frame), Unity already auto-released those nameIDs and we don't want stale entries.
            rtsToRelease.Clear();

            //Build descriptor from the camera (replaces URP's currentCamTexDescriptor)
            currentCamTexDescriptor = new RenderTextureDescriptor(cam.pixelWidth, cam.pixelHeight, halfHDR, 0) {
                useMipMap = false,
                autoGenerateMips = false,
                sRGB = false,
                msaaSamples = 1,
            };

            //Resolution clamp
            float limitedResScale = 1f;
            int maxP = Mathf.Min(currentCamTexDescriptor.width, currentCamTexDescriptor.height);
            if (maxP > settings.Quality.MaxGIResolution) {
                limitedResScale = (float)settings.Quality.MaxGIResolution / (float)maxP;
            }

            bool doShadows = settings.Quality.UseRaymarchedShadows && settings.Raymarch && controller.ShadowIntensity > 0f;
            bool doEncodeLightDir = settings.Quality.UseEncodedLightDirections && controller.LightDirInfluence > 0f;

            //--- Phase 2 pipeline ---
            CaptureWorldPositionsAndDepth(cam, cmd);
            CaptureNormalsAndDepth(cam, cmd, limitedResScale);

            backupProjectionMatrix = cam.projectionMatrix;
            WriteSSGIObjects(cam, cmd);                                //Phase 3 stub
            if (doShadows) WriteThicknessMask(cam, cmd, limitedResScale); //Phase 3 stub

            CollectReflectionProbes(cam);
            FilterActiveReflectionProbes(cam, controller);
            CaptureScreenAndLight(cam, cmd, limitedResScale);

            GatherSSGI(cam, cmd, limitedResScale, doShadows, controller, doEncodeLightDir);
            CaptureDebugRTSSGI(cam, cmd, doEncodeLightDir);
            Denoise(cam, cmd, limitedResScale, doEncodeLightDir);
            //BlitToScreen happens in OnRenderImage (Phase 5). For now, prepare its uniforms here:
            PreparePostBlitUniforms(controller);

            CaptureDebugRTOthers(cam, cmd, doEncodeLightDir);

            //Note: temporary RTs are NOT released here. They are needed by FinalComposite which runs
            //later in OnRenderImage. Release happens at the end of FinalComposite instead.
            renderedThisFrame = true;
        }

        //=== Final composite — runs from SSGIController.OnRenderImage after the SSGI buffer fired ===
        public void FinalComposite(RenderTexture src, RenderTexture dst) {
            if (blitFinalImageMaterial != null) {
                //All globals (_MF_SSGI_ScreenCapture, _MF_SSGI_Denoised_Final, etc.) were uploaded in
                //Render() and bound by the queued buffer at BeforeImageEffectsOpaque. The shader reads
                //those, not _MainTex, so src is mostly ignored — but pass it anyway for completeness.
                Graphics.Blit(src, dst, blitFinalImageMaterial);
            } else {
                Graphics.Blit(src, dst);
            }

            //Now release the temp RTs that the queued buffer + final composite consumed
            if (rtsToRelease.Count > 0) {
                if (releaseBuffer == null) releaseBuffer = new CommandBuffer { name = "MFSSGI_ReleaseRTs" };
                else releaseBuffer.Clear();
                for (int i = 0; i < rtsToRelease.Count; i++) releaseBuffer.ReleaseTemporaryRT(rtsToRelease[i]);
                Graphics.ExecuteCommandBuffer(releaseBuffer);
                rtsToRelease.Clear();
            }

            //Mark for next frame: globals are now stale (temp RTs released). FinalComposite must not
            //run until next Render() repopulates them.
            renderedThisFrame = false;
        }

        //=== Capture passes ===
        //Set camera-space globals BEFORE the WorldPos blit. The DepthToWorldPos shader uses
        //_cam_world_position and _cam_world_forward to compute the linear depth that ends up
        //in _WorldPositions.a. If those are stale, the SSGIObjects software depth test (which
        //compares fragment world-depth against the cached _MF_SSGI_Normals_HQ.a) gets a
        //frame-of-motion mismatch and starts writing the mask at occluded pixels.
        private void SetCameraSpaceGlobals(Camera cam, CommandBuffer cmd) {
            cmd.SetGlobalVector("_cam_world_forward", cam.transform.forward);
            if (cam.orthographic && cam.nearClipPlane < 0f) {
                cmd.SetGlobalVector("_cam_world_position", cam.transform.position + (cam.transform.forward * cam.nearClipPlane));
            } else {
                cmd.SetGlobalVector("_cam_world_position", cam.transform.position);
            }
        }

        private void CaptureWorldPositionsAndDepth(Camera cam, CommandBuffer cmd) {
            RenderTextureDescriptor descriptor = GetDescriptor(halfHDR, 1f);

            if (!worldPosMaterial) worldPosMaterial = new Material(Shader.Find("MF_SSGI/DepthToWorldPos"));

            //Globals must be set BEFORE this blit — the shader's frag uses them to write into RT.a
            SetCameraSpaceGlobals(cam, cmd);

            RenderTexture target = GenBufferedRT(SSGIPassType.WorldPositions, cam, descriptor, FilterMode.Point);
            cmd.Blit(UNITY_RTNAME_DEPTH, target, worldPosMaterial);
        }

        private void CaptureNormalsAndDepth(Camera cam, CommandBuffer cmd, float limitedResScale) {
            if (!captureNormalsMaterial) captureNormalsMaterial = new Material(Shader.Find("MF_SSGI/CaptureNormals"));

            if (settings.UseDeferredRendering) Shader.EnableKeyword("_USE_DEFERRED");
            else Shader.DisableKeyword("_USE_DEFERRED");

            //Re-assert in case anything inside the geometry passes clobbered them.
            SetCameraSpaceGlobals(cam, cmd);

            //Highres
            RenderTextureDescriptor descriptor = GetDescriptor(halfHDR, 1f);
            int nameID_HQ = Shader.PropertyToID("_MF_SSGI_Normals_HQ");
            cmd.GetTemporaryRT(nameID_HQ, descriptor, FilterMode.Point);
            captureNormalsMaterial.SetTexture("_WorldPositions", FetchBufferedRTwrapper(SSGIPassType.WorldPositions, cam).RT1);
            cmd.Blit(null, nameID_HQ, captureNormalsMaterial);
            rtsToRelease.Add(nameID_HQ);

            //Lowres mipped
            int nameID_LQ = Shader.PropertyToID("_MF_SSGI_Normals_LQ");
            descriptor = GetDescriptor(halfHDR, settings.Quality.SSGIRenderScale * limitedResScale);
            descriptor.useMipMap = true;
            descriptor.autoGenerateMips = true;
            descriptor.mipCount = settings.Raymarch.RaymarchNormalDepthMipLevel;
            cmd.GetTemporaryRT(nameID_LQ, descriptor, FilterMode.Point);
            cmd.Blit(nameID_HQ, nameID_LQ);
            rtsToRelease.Add(nameID_LQ);
        }

        //=== Geometry-rendering passes (Phase 3) ===
        private void WriteThicknessMask(Camera cam, CommandBuffer cmd, float limitedResScale) {
            if (!thicknessMaskMaterialBack) thicknessMaskMaterialBack = new Material(Shader.Find("MF_SSGI/ThicknessMaskBack"));
            if (!thicknessMaskMaterialFront) thicknessMaskMaterialFront = new Material(Shader.Find("MF_SSGI/ThicknessMaskFront"));

            int layerMask = settings.Raymarch.ThicknessMaskLayers;
            RefreshThicknessRenderersIfNeeded(layerMask);
            GeometryUtility.CalculateFrustumPlanes(cam, frustumPlanes);

            cmd.SetGlobalFloat("_object_thickness_pivot_vs_normal", settings.Raymarch.ObjectPivotToNormal);
            cmd.SetGlobalFloat("_object_min_thickness", settings.Raymarch.ObjectMinimalThickness);

            RenderTextureDescriptor descriptor = GetDescriptor(RenderTextureFormat.RHalf,
                settings.Quality.SSGIRenderScale * limitedResScale, true);

            //Pass 1: back faces -> Prepass RT (RHalf)
            int nameIDPrePass = Shader.PropertyToID("_MF_SSGI_ThicknessMask_Prepass");
            cmd.GetTemporaryRT(nameIDPrePass, descriptor, FilterMode.Point);
            cmd.SetRenderTarget(nameIDPrePass);
            cmd.ClearRenderTarget(true, true, Color.clear);
            DrawThicknessGeo(cam, cmd, thicknessMaskMaterialBack);
            rtsToRelease.Add(nameIDPrePass);

            //Pass 2: front faces -> final mask RT (RGHalf, cleared green = "no occluder" sentinel)
            descriptor.colorFormat = RenderTextureFormat.RGHalf;
            int nameIDPostPass = Shader.PropertyToID("_MF_SSGI_ThicknessMask");
            cmd.GetTemporaryRT(nameIDPostPass, descriptor, FilterMode.Point);
            cmd.SetRenderTarget(nameIDPostPass);
            cmd.ClearRenderTarget(true, true, Color.green);
            DrawThicknessGeo(cam, cmd, thicknessMaskMaterialFront);
            rtsToRelease.Add(nameIDPostPass);
        }

        //Helper: draws the cached thickness-mask renderers with the given override material.
        //Called once with expand value (if > 0), then again with expand=0 to layer the result.
        private void DrawThicknessGeo(Camera cam, CommandBuffer cmd, Material overrideMat) {
            //Expanded pass (only if Object Expand > 0)
            if (settings.Raymarch.ObjectExpand > 0f) {
                LimitFarClipPlane(cam, cmd, settings.ShadowMaxDistace);
                cmd.SetGlobalFloat("_thickness_mask_expand", settings.Raymarch.ObjectExpand);
                DrawAllThicknessRenderers(overrideMat, cmd);
            }
            //Normal-size pass
            LimitFarClipPlane(cam, cmd, settings.ShadowMaxDistace);
            cmd.SetGlobalFloat("_thickness_mask_expand", 0f);
            DrawAllThicknessRenderers(overrideMat, cmd);
        }

        private void DrawAllThicknessRenderers(Material overrideMat, CommandBuffer cmd) {
            if (cachedThicknessRenderers == null) return;
            for (int i = 0; i < cachedThicknessRenderers.Length; i++) {
                Renderer r = cachedThicknessRenderers[i];
                if (!r || !r.enabled || !r.gameObject.activeInHierarchy) continue;
                if (!GeometryUtility.TestPlanesAABB(frustumPlanes, r.bounds)) continue;
                int subMeshes = GetSubMeshCount(r);
                for (int j = 0; j < subMeshes; j++) {
                    cmd.DrawRenderer(r, overrideMat, j, 0);
                }
            }
        }

        private void RefreshThicknessRenderersIfNeeded(int layerMask) {
            float now = GetTime();
            bool layerChanged = cachedThicknessInitialized && cachedThicknessLayerMaskValue != layerMask;
            bool stale = !cachedThicknessInitialized || (now - thicknessRenderersRefreshTime) > THICKNESS_REFRESH_INTERVAL;
            if (!layerChanged && !stale) return;

            cachedThicknessLayerMaskValue = layerMask;
            cachedThicknessInitialized = true;
            thicknessRenderersRefreshTime = now;

            Renderer[] all = Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None);
            List<Renderer> list = new List<Renderer>(all.Length);
            for (int i = 0; i < all.Length; i++) {
                Renderer r = all[i];
                if (!r) continue;
                if ((layerMask & (1 << r.gameObject.layer)) == 0) continue;
                if (!(r is MeshRenderer || r is SkinnedMeshRenderer)) continue;
                list.Add(r);
            }
            cachedThicknessRenderers = list.ToArray();
        }

        private static int GetSubMeshCount(Renderer r) {
            if (r is MeshRenderer) {
                MeshFilter mf = r.GetComponent<MeshFilter>();
                return (mf && mf.sharedMesh) ? mf.sharedMesh.subMeshCount : 0;
            }
            if (r is SkinnedMeshRenderer smr) {
                return smr.sharedMesh ? smr.sharedMesh.subMeshCount : 0;
            }
            return 0;
        }

        private void WriteSSGIObjects(Camera cam, CommandBuffer cmd) {
            bool requiresSSGIObjectsRendering =
                settings.Advanced.UseSSGIObjectOverrides &&
                RequestedObjects.Count(item => item && item.RequiresSSGIMaskRendering) > 0;

            cmd.SetGlobalFloat("_default_clip_depth_bias", settings.Advanced.DefaultClipDepthBias);
            if (requiresSSGIObjectsRendering) Shader.EnableKeyword("_USE_SSGI_OBJECTS");
            else Shader.DisableKeyword("_USE_SSGI_OBJECTS");

            if (!ssgiObjectMaterial) ssgiObjectMaterial = new Material(Shader.Find("MF_SSGI/SSGIObjects"));

            //Always allocate the RT (downstream passes sample it). Cleared white = "no override".
            RenderTextureDescriptor descriptor = GetDescriptor(halfHDR, 1f, true);
            int nameID = Shader.PropertyToID("_MF_SSGI_SSGIObjects");
            cmd.GetTemporaryRT(nameID, descriptor, FilterMode.Point);
            cmd.SetRenderTarget(nameID);
            cmd.ClearRenderTarget(true, true, Color.white);
            rtsToRelease.Add(nameID);

            //If nothing to draw, we're done — the cleared RT is the correct empty state.
            if (!requiresSSGIObjectsRendering) return;

            LimitFarClipPlane(cam, cmd, settings.SSGIRangeMax);
            GeometryUtility.CalculateFrustumPlanes(cam, frustumPlanes);

            foreach (SSGIObject obj in RequestedObjects) {
                if (!obj || !obj.RequiresSSGIMaskRendering) continue;
                for (int i = 0; i < obj.AffectedRenderers.Count; i++) {
                    Renderer r = obj.AffectedRenderers[i];
                    if (!r || !r.enabled || !r.gameObject.activeInHierarchy) continue;
                    if (!GeometryUtility.TestPlanesAABB(frustumPlanes, r.bounds)) continue;
                    for (int j = 0; j < obj.SubMeshCounts[i]; j++) {
                        cmd.DrawRenderer(r, ssgiObjectMaterial, j, 0);
                    }
                }
            }
        }

        //=== Screen + light capture ===
        private void CaptureScreenAndLight(Camera cam, CommandBuffer cmd, float limitedResScale) {
            if (!captureLightMaterial) captureLightMaterial = new Material(Shader.Find("MF_SSGI/CaptureLight"));
            if (!ssgiExpandVerticesMaterial) ssgiExpandVerticesMaterial = new Material(Shader.Find("MF_SSGI/ExpandVertices"));

            //Full-res screen capture from the camera color target
            RenderTextureDescriptor descriptor = GetDescriptor(halfHDR, 1f);
            int nameID = Shader.PropertyToID("_MF_SSGI_ScreenCapture");
            if (!settings.UseDeferredRendering && settings.Lighting.AlbedoDetailBoost > 0f) {
                descriptor.useMipMap = true;
                descriptor.autoGenerateMips = true;
                descriptor.mipCount = (int)Mathf.Ceil(settings.Lighting.AlbedoDetailBoostMipLevle);
            }
            cmd.GetTemporaryRT(nameID, descriptor, FilterMode.Trilinear);
            cmd.Blit(BuiltinRenderTextureType.CameraTarget, nameID);
            rtsToRelease.Add(nameID);

            //Worldpos texture for capture material
            RTWrapper worldPosWrapper = FetchBufferedRTwrapper(SSGIPassType.WorldPositions, cam);
            captureLightMaterial.SetTexture("_WorldPositions", worldPosWrapper.RT1);

            //Reflection-probe fallback shadows toggle
            cmd.SetGlobalInt("_ssgi_refprobe_raymarch_samples",
                settings.Quality.ReflectionProbeFallbackIndirectShadows ? settings.Quality.ReflectionProbeFallbackRaymarchSamples : 0);

            //SSGI-res light-info capture
            descriptor = GetDescriptor(halfHDR, settings.Quality.SSGIRenderScale * limitedResScale);
            int nameIDLight = Shader.PropertyToID("_MF_SSGI_LightCapture");
            cmd.GetTemporaryRT(nameIDLight, descriptor, FilterMode.Point);
            cmd.Blit(BuiltinRenderTextureType.CameraTarget, nameIDLight, captureLightMaterial);
            rtsToRelease.Add(nameIDLight);

            //Albedo boost
            cmd.SetGlobalFloat("_albedo_boost", settings.Lighting.AlbedoDetailBoost);
            cmd.SetGlobalFloat("_albedo_boost_miplevel", settings.Lighting.AlbedoDetailBoostMipLevle);

            //Expand-vertices material setup
            ssgiExpandVerticesMaterial.SetFloat("_OneOverScreenResX", 1f / (float)descriptor.width);
            ssgiExpandVerticesMaterial.SetFloat("_OneOverScreenResY", 1f / (float)descriptor.height);
            ssgiExpandVerticesMaterial.SetFloat("_ExpandRangeMin", settings.SSGIRangeMax * settings.Advanced.ExpandObjectsRangeFactorMin);
            ssgiExpandVerticesMaterial.SetFloat("_ExpandRangeMax", settings.SSGIRangeMax * settings.Advanced.ExpandObjectsRangeFactorMax);

            //Draw SSGIObjects with expanded vertices into the light-capture RT
            cmd.SetRenderTarget(nameIDLight);
            LimitFarClipPlane(cam, cmd, settings.SSGIRangeMax);
            GeometryUtility.CalculateFrustumPlanes(cam, frustumPlanes);

            foreach (SSGIObject obj in RequestedObjects) {
                if (!obj || !obj.RequiresVertexExpandRendering) continue;
                for (int i = 0; i < obj.AffectedRenderers.Count; i++) {
                    Renderer r = obj.AffectedRenderers[i];
                    if (!r || !r.enabled || !r.gameObject.activeInHierarchy) continue;
                    if (!GeometryUtility.TestPlanesAABB(frustumPlanes, r.bounds)) continue;
                    for (int j = 0; j < obj.SubMeshCounts[i]; j++) {
                        cmd.DrawRenderer(r, ssgiExpandVerticesMaterial, j, 0);
                    }
                }
            }
        }

        //=== Main SSGI gather ===
        private void GatherSSGI(Camera cam, CommandBuffer cmd, float limitedResScale, bool doShadows,
                                SSGIController controller, bool doEncodeLightDir) {
            if (!scanEnvironmentMaterial) scanEnvironmentMaterial = new Material(Shader.Find("MF_SSGI/SSGI"));

            if (settings.Quality.MultiFrameCellSize == 2) {
                Debug.LogWarning("MultiFrameCellSize is set to 2 — causes rounding errors. Set to 0 (off) or >= 3.");
            }

            cmd.SetGlobalInt("_debug_reprojection", settings.DebugReprojection ? 1 : 0);
            cmd.SetGlobalFloat("_multiframe_cell_size", Application.isPlaying ? settings.Quality.MultiFrameCellSize : 0);
            cmd.SetGlobalFloat("_multiframe_energy_falloff", settings.Quality.MultiFrameEnergyFalloff);
            cmd.SetGlobalFloat("_multiframe_dist", settings.Advanced.MultiFrameDistanceThreshold);
            cmd.SetGlobalFloat("_multiframe_shadow_compensate", settings.Advanced.MultiFrameShadowCompensate);
            cmd.SetGlobalInt("_multiframe_apply_multisample", settings.Advanced.MultiFrameApplyMultiSample ? 1 : 0);

            if (settings.Quality.MultiFrameCellSize > 0) {
                int cellSqr = settings.Quality.MultiFrameCellSize * settings.Quality.MultiFrameCellSize;
                if (rndNumbers == null || rndNumbers.Length != cellSqr) {
                    rndNumbers = new Vector2Int[cellSqr];
                    int i = 0;
                    for (int x = 0; x < settings.Quality.MultiFrameCellSize; x++) {
                        for (int y = 0; y < settings.Quality.MultiFrameCellSize; y++) {
                            rndNumbers[i++] = new Vector2Int(x, y);
                        }
                    }
                    Shuffle(rndNumbers);
                }
                if (!frameCountTable.ContainsKey(cam)) frameCountTable.Add(cam, 0);
                frameCountTable[cam]++;
                cmd.SetGlobalVector("_rnd_pixel", (Vector2)rndNumbers[frameCountTable[cam] % cellSqr]);
            }

            //Pass-pattern descriptor
            RenderTextureDescriptor descriptor = GetDescriptor(doEncodeLightDir ? fullHDR : halfHDR,
                settings.Quality.SSGIRenderScale * limitedResScale);

            //Globals
            cmd.SetGlobalInt("_ssgi_samples_hq", settings.Quality.SSGISamplesHQ);
            cmd.SetGlobalInt("_ssgi_samples_backfill", settings.Quality.SSGISamplesBackfill);
            cmd.SetGlobalFloat("_ssgi_samples_reduction", settings.Advanced.SSGISamplesReduction);
            cmd.SetGlobalVector("_ssgi_res", new Vector4(descriptor.width, descriptor.height, 0f, 0f));
            cmd.SetGlobalFloat("_edge_vignette", settings.Advanced.SearchEdgeVignette);
            if (doEncodeLightDir) Shader.EnableKeyword("_ENCODE_LIGHTDIR"); else Shader.DisableKeyword("_ENCODE_LIGHTDIR");
            cmd.SetGlobalFloat("_multi_sample_normal_distance", settings.Advanced.MultiSampleNormalsDistance);

            cmd.SetGlobalFloat("_scan_base_range", settings.Advanced.Search2DRange);
            cmd.SetGlobalFloat("_scan_ratio_y", (float)descriptor.height / (float)descriptor.width);
            cmd.SetGlobalFloat("_scan_noise", settings.Advanced.Search2DNoise);

            cmd.SetGlobalFloat("_scan_depth_threshold_factor", settings.Advanced.ScanDepthThresholdFactor);
            cmd.SetGlobalFloat("_ssgi_range_max", settings.SSGIRangeMax);
            cmd.SetGlobalFloat("_ssgi_range_min", settings.SSGIRangeMin);

            cmd.SetGlobalFloat("_max_light_attenuation", settings.Lighting.MaxLightAttenuation);
            cmd.SetGlobalFloat("_max_input_energy", settings.Lighting.MaxInputEnergy);
            cmd.SetGlobalFloat("_frustum_pixel_size", 2f * Mathf.Tan(cam.fieldOfView * Mathf.Deg2Rad / 2f) * settings.Lighting.DistanceEnergyBoost);

            //Runtime settings (was VolumeComponent)
            cmd.SetGlobalFloat("_ssgi_intensity", controller.LightIntensity * 100f);
            cmd.SetGlobalFloat("_light_falloff_distance", controller.LightFalloffDistance);
            cmd.SetGlobalFloat("_skybox_influence", controller.SkyboxInfluence);

            cmd.SetGlobalFloat("_light_cast_dot_min", settings.Lighting.LightCastDotMin);
            cmd.SetGlobalFloat("_light_cast_dot_max", settings.Lighting.LightCastDotMax);
            cmd.SetGlobalFloat("_light_receive_dot_min", settings.Lighting.LightReceiveDotMin);
            cmd.SetGlobalFloat("_light_receive_dot_max",
                settings.Quality.UseEncodedLightDirections ? settings.Lighting.LightReceiveDotMax : Mathf.Min(1f, settings.Lighting.LightReceiveDotMax * 2f));

            cmd.SetGlobalFloat("_depth_cutoff_near", settings.Advanced.CamCutoffNear);
            cmd.SetGlobalFloat("_depth_cutoff_far", settings.Advanced.CamCutoffFar);

            float distance = settings.SSGIRangeMax * settings.ScanDepth2DRangeFactor * settings.Quality.ScanDepthMultiplier;
            cmd.SetGlobalFloat("_scansize_distance_multiplier", Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad));
            cmd.SetGlobalFloat("_scansize_distance_threshold", distance);
            cmd.SetGlobalFloat("_scansize_distance_base_size", distance * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad));
            cmd.SetGlobalFloat("_scansize_ortho", settings.Orthographic2DRangeFactor * settings.Quality.ScanDepthMultiplier);

            if (doShadows) {
                cmd.SetGlobalFloat("_shadow_intensity", controller.ShadowIntensity);
                cmd.SetGlobalFloat("_contact_shadow_range", controller.ContactShadowsRange);
                cmd.SetGlobalFloat("_casted_shadow_range", controller.CastedShadowsRange);
                cmd.SetGlobalFloat("_casted_shadow_intensity", controller.CastedShadowsIntensity);
                cmd.SetGlobalFloat("_casted_shadow_omni_dir", controller.CastedShadowsOmniDirectional);
                cmd.SetGlobalFloat("_result_shadows_contrast", controller.ShadowContrast);
                cmd.SetGlobalFloat("_contact_shadow_soft_knee", controller.ContactShadowsSoftKnee);
                cmd.SetGlobalFloat("_casted_shadow_soft_knee", controller.CastedShadowsSoftKnee);

                cmd.SetGlobalFloat("_raymarch_min_distance", settings.ShadowMinDistace);
                cmd.SetGlobalFloat("_raymarch_max_distance", settings.ShadowMaxDistace);

                cmd.SetGlobalFloat("_raymarch_samples_hq", settings.Quality.RaymarchSamplesHQ);
                cmd.SetGlobalFloat("_raymarch_samples_backfill", settings.Quality.RaymarchSamplesBackfill);
                cmd.SetGlobalFloat("_raymarch_cubic_distance_falloff", settings.Quality.RaymarchCubicDistanceFalloff);

                cmd.SetGlobalFloat("_raymarch_depth_bias", settings.Raymarch.RaymarchDepthBias);
                cmd.SetGlobalInt("_raymarch_normal_depth_miplevel", settings.Raymarch.RaymarchNormalDepthMipLevel);
                cmd.SetGlobalFloat("_raymarch_surface_depth_bias_min", settings.Raymarch.RaymarchSurfaceDepthBiasMin);
                cmd.SetGlobalFloat("_raymarch_surface_depth_bias_max", settings.Raymarch.RaymarchSurfaceDepthBiasMax);
                cmd.SetGlobalFloat("_raymarch_min_hit_count", settings.Raymarch.RaymarchMinimumHitCount);
                cmd.SetGlobalFloat("_raymarch_contact_min_dist", settings.Raymarch.RaymarchContactMinDistance);
                cmd.SetGlobalFloat("_raymarch_casted_min_dist", settings.Raymarch.RaymarchCastedMinDistance);
                cmd.SetGlobalFloat("_raymarch_shorten", settings.Quality.RaymarchMaxRangeFactor);
            } else {
                cmd.SetGlobalFloat("_shadow_intensity", 0f);
            }

            //Restore main projection (in case thickness/SSGIObjects nuked it earlier)
            cmd.SetProjectionMatrix(backupProjectionMatrix);

            cmd.SetGlobalInt("_ssgi_refprobe_raymarch_samples",
                settings.Quality.ReflectionProbeFallbackDirectShadows ? settings.Quality.ReflectionProbeFallbackRaymarchSamples : 0);

            //Set per-frame textures on the SSGI material (these don't ping-pong via globals)
            RenderTexture target = GenBufferedRT(SSGIPassType.SSGIColor, cam, descriptor, FilterMode.Point);
            RTWrapper worldPosWrapper = FetchBufferedRTwrapper(SSGIPassType.WorldPositions, cam);
            RTWrapper ssgiWrapper = FetchBufferedRTwrapper(SSGIPassType.SSGIColor, cam);
            scanEnvironmentMaterial.SetTexture("_WorldPositions", worldPosWrapper.RT1);
            scanEnvironmentMaterial.SetTexture("_PrevWorldPositions", worldPosWrapper.RT2);
            scanEnvironmentMaterial.SetTexture("_PrevSSGI", ssgiWrapper.RT2);
            cmd.Blit(null, target, scanEnvironmentMaterial);
        }

        //=== Denoise + TAA ===
        private void Denoise(Camera cam, CommandBuffer cmd, float limitedResScale, bool doEncodeLightDir) {
            if (!denoiseImageMaterial) denoiseImageMaterial = new Material(Shader.Find("MF_SSGI/Denoise"));

            cmd.SetGlobalFloat("_denoise_min_dot_match", settings.Advanced.DenoiseNormalDotMin);
            cmd.SetGlobalFloat("_denoise_max_dot_match", settings.Advanced.DenoiseNormalDotMax);
            cmd.SetGlobalFloat("_denoise_max_depth_diff", settings.Advanced.DenoiseDepthDiffTheshold);
            cmd.SetGlobalFloat("_denoise_color_normal_contribution", settings.Advanced.DenoiseColorNormalContribution);
            cmd.SetGlobalFloat("_denoise_shadow_normal_contribution", settings.Advanced.DenoiseShadowNormalContribution);
            cmd.SetGlobalInt("_denoise_min_shadows_hit_count", settings.Raymarch.DenoiseShadowsMinHitCount);

            RenderTextureDescriptor descriptor = GetDescriptor(doEncodeLightDir ? fullHDR : halfHDR,
                settings.Quality.SSGIRenderScale * limitedResScale);
            cmd.SetGlobalVector("_oneover_denoise_res",
                new Vector4(1f / (float)descriptor.width, 1f / (float)descriptor.height, 0f, 0f));
            cmd.SetGlobalFloat("_denoise_energy_compensation", settings.Quality.IntensityCompensation);
            cmd.SetGlobalFloat("_denoise_shadow_compensation", settings.Quality.ShadowCompensation);

            FilterMode filterMode = doEncodeLightDir ? FilterMode.Point : FilterMode.Trilinear;
            int nameID1 = Shader.PropertyToID("_MF_SSGI_Pre_Denoised_1");
            int nameID2 = Shader.PropertyToID("_MF_SSGI_Pre_Denoised_2");
            int nameIDFinal = Shader.PropertyToID("_MF_SSGI_Denoised_Final");
            cmd.GetTemporaryRT(nameID1, descriptor, filterMode);
            cmd.GetTemporaryRT(nameID2, descriptor, filterMode);
            cmd.GetTemporaryRT(nameIDFinal, descriptor, filterMode);

            for (int i = 0; i < settings.Quality.DenoisePasses; i++) {
                float pixelSize = settings.Quality.DenoisePasses > 1
                    ? Mathf.Lerp(settings.Quality.PreDenoisePixelSize, 1f, (float)i / (float)(settings.Quality.DenoisePasses - 1))
                    : settings.Quality.PreDenoisePixelSize;
                cmd.SetGlobalFloat("_denoise_pixel_size", pixelSize);

                if (i == 0) {
                    cmd.Blit(FetchBufferedRTwrapper(SSGIPassType.SSGIColor, cam).RT1, nameID1, denoiseImageMaterial);
                } else if (i % 2 != 0) {
                    cmd.Blit(nameID1, i == settings.Quality.DenoisePasses - 1 ? nameIDFinal : nameID2, denoiseImageMaterial);
                } else {
                    cmd.Blit(nameID2, i == settings.Quality.DenoisePasses - 1 ? nameIDFinal : nameID1, denoiseImageMaterial);
                }
            }

            //--------- TAA on shadow channel
            if (!taaShadowMaterial) taaShadowMaterial = new Material(Shader.Find("MF_SSGI/TAA"));
            cmd.SetGlobalFloat("_taa_shadow_blend", 0.85f);
            cmd.SetGlobalFloat("_taa_world_pos_threshold", 0.1f);
            RenderTexture taaCurrent = GenBufferedRT(SSGIPassType.TAAHistory, cam, descriptor, filterMode);
            RTWrapper taaWrapper = FetchBufferedRTwrapper(SSGIPassType.TAAHistory, cam);
            cmd.SetGlobalTexture("_TAAHistory", taaWrapper.RT2 != null ? (Texture)taaWrapper.RT2 : Texture2D.blackTexture);

            //TAA's disocclusion check samples _WorldPositions / _PrevWorldPositions. They're set as
            //material properties on scanEnvironmentMaterial / captureLightMaterial / captureNormalsMaterial,
            //but NOT on taaShadowMaterial. Without an explicit bind here, those samplers fall back to
            //whatever is globally bound by another camera or previous frame — producing intermittent
            //disocclusion misfires that show up as flickering cube silhouettes when DenoisePasses is low.
            RTWrapper worldPosWrapperTAA = FetchBufferedRTwrapper(SSGIPassType.WorldPositions, cam);
            cmd.SetGlobalTexture("_WorldPositions", worldPosWrapperTAA.RT1);
            cmd.SetGlobalTexture("_PrevWorldPositions",
                worldPosWrapperTAA.RT2 != null ? (Texture)worldPosWrapperTAA.RT2 : Texture2D.blackTexture);

            cmd.Blit(nameIDFinal, taaCurrent, taaShadowMaterial);
            cmd.SetGlobalTexture("_MF_SSGI_Denoised_Final", taaCurrent);

            rtsToRelease.Add(nameID1);
            rtsToRelease.Add(nameID2);
            rtsToRelease.Add(nameIDFinal);
        }

        //=== Final-blit uniforms (pre-uploaded; blit happens in OnRenderImage Phase 5) ===
        private void PreparePostBlitUniforms(SSGIController controller) {
            if (!blitFinalImageMaterial) blitFinalImageMaterial = new Material(Shader.Find("MF_SSGI/FinalBlit"));

            Shader.SetGlobalFloat("_debug_screen_coverage", settings.DebugScreenCoverage);
            Shader.SetGlobalInt("_debug_motion_vectors", settings.DebugMotionVectors ? 1 : 0);
            Shader.SetGlobalInt("_debug_albedo", settings.DebugAlbedo ? 1 : 0);

            Shader.SetGlobalFloat("_max_output_energy", settings.Lighting.MaxOutputEnergy);

            Shader.SetGlobalFloat("_composit_lightdir_influence",
                settings.Quality.UseEncodedLightDirections ? controller.LightDirInfluence : 0f);
            Shader.SetGlobalFloat("_composit_lightdir_normal_boost", controller.NormalmapBoost);
            Shader.SetGlobalFloat("_composit_final_contrast", controller.FinalContrast);
            Shader.SetGlobalFloat("_composit_final_intensity", controller.FinalIntensity);
            Shader.SetGlobalFloat("_composit_occlusion_intensity", controller.PreMultiply);
            Shader.SetGlobalColor("_composit_color", controller.GITint);
            Shader.SetGlobalFloat("_composit_gi_contrast", controller.GIContrast);
            Shader.SetGlobalFloat("_composit_gi_saturate", controller.GISaturation);
            Shader.SetGlobalFloat("_composit_gi_vibrance", controller.GIVibrance);
            Shader.SetGlobalVector("_shadow_boost_tint", controller.ShadowTint);
            Shader.SetGlobalFloat("_shadow_boost_exp", controller.ShadowExponential);

            Shader.SetGlobalFloat("_shadow_lambert_influence", settings.Lighting.ShadowsLambertInfluence);
            Shader.SetGlobalVector("_light_direction_info_a", new Vector4(settings.Lighting.LightDirectionDotMinSoft, settings.Lighting.LightDirectionDotMaxSoft, settings.Lighting.LightDirectionIntensitySoft));
            Shader.SetGlobalVector("_light_direction_info_b", new Vector4(settings.Lighting.LightDirectionDotMinHard, settings.Lighting.LightDirectionDotMaxHard, settings.Lighting.LightDirectionIntensityHard));

            Shader.SetGlobalColor("_deferred_specular_tint", settings.Lighting.SpecularTint);
            Shader.SetGlobalFloat("_albedo_min_whiteness", settings.Lighting.MinimumAlbedoWhiteness);

            Shader.SetGlobalFloat("_forward_albedo_contrast", settings.Lighting.AlbedoContrast);
            Shader.SetGlobalFloat("_forward_albedo_subtract_fog", RenderSettings.fog ? settings.Lighting.AlbedoSubtractFogColor : 0f);
            Shader.SetGlobalFloat("_forward_albedo_subtract_sky", settings.Lighting.AlbedoSubtractSkyColor);

            Shader.SetGlobalInt("_aa_quality_level", (int)settings.Quality.AAQuality);
            Shader.SetGlobalInt("_aa_debug_edge_detect", settings.Advanced.DebugAAEdgeDetect ? 1 : 0);
            Shader.SetGlobalFloat("_aa_edge_detect_depth_theshold", settings.Advanced.AAEdgeDetectDepthThreshold);
            Shader.SetGlobalFloat("_aa_edge_detect_dot_theshold", settings.Advanced.AAEdgeDetectDotThreshold);
            Shader.SetGlobalFloat("_aa_normal_match_threshold", settings.Advanced.AANormalMapMatchThreshold);
            Shader.SetGlobalVector("_aa_sample_distance", new Vector4(
                (1f / (float)currentCamTexDescriptor.width) * settings.Quality.SSGIRenderScale * settings.Advanced.FinalCompositAARange,
                (1f / (float)currentCamTexDescriptor.height) * settings.Quality.SSGIRenderScale * settings.Advanced.FinalCompositAARange,
                0f, 0f));
        }

        //=== Helpers ===
        private RenderTextureDescriptor GetDescriptor(RenderTextureFormat format, float customScale,
                                                      bool requiresDepth = false, int msaa = 1) {
            RenderTextureDescriptor d = currentCamTexDescriptor;
            d.width = Mathf.Max(1, (int)(currentCamTexDescriptor.width * customScale));
            d.height = Mathf.Max(1, (int)(currentCamTexDescriptor.height * customScale));
            d.colorFormat = format;
            d.depthBufferBits = requiresDepth ? 16 : 0;
            d.useMipMap = false;
            d.autoGenerateMips = false;
            d.sRGB = false;
            d.msaaSamples = msaa;
            return d;
        }

        private void LimitFarClipPlane(Camera cam, CommandBuffer cmd, float far) {
            float clampedFar = Mathf.Min(cam.farClipPlane, far);
            if (cam.orthographic) {
                float halfH = cam.orthographicSize;
                float halfW = halfH * cam.aspect;
                cmd.SetProjectionMatrix(Matrix4x4.Ortho(-halfW, halfW, -halfH, halfH, cam.nearClipPlane, clampedFar));
            } else {
                cmd.SetProjectionMatrix(Matrix4x4.Perspective(cam.fieldOfView, cam.aspect, cam.nearClipPlane, clampedFar));
            }
        }

        private RTWrapper FetchBufferedRTwrapper(SSGIPassType type, Camera camera) {
            RTWrapper wrapper = rtWrappers.Find(item => item.Cam == camera && item.Type == type);
            if (wrapper == null) {
                wrapper = new RTWrapper { Cam = camera, Type = type };
                rtWrappers.Add(wrapper);
            }
            return wrapper;
        }

        private RenderTexture GenBufferedRT(SSGIPassType type, Camera camera, RenderTextureDescriptor descriptor, FilterMode filterMode) {
            RTWrapper wrapper = FetchBufferedRTwrapper(type, camera);
            (wrapper.RT1, wrapper.RT2) = (wrapper.RT2, wrapper.RT1);

            if (!wrapper.RT1 || wrapper.RT1.width != descriptor.width || wrapper.RT1.height != descriptor.height) {
                if (wrapper.RT1) GameObject.DestroyImmediate(wrapper.RT1);
                wrapper.RT1 = new RenderTexture(descriptor);
                string prefix = wrapper.RT2 ? "B" : "A";
                wrapper.RT1.name = $"{type} [{prefix}]";
                wrapper.RT1.Create();
            }
            wrapper.RT1.filterMode = filterMode;
            return wrapper.RT1;
        }

        //=== Reflection probes ===
        private void CollectReflectionProbes(Camera cam) {
            if (!reflectionProbeWrappers.ContainsKey(cam)) {
                reflectionProbeWrappers.Add(cam, new List<ReflectionProbeWrapper>());
            }

            float time = GetTime();
            if (allProbes == null || time - probesLastCollectTimestamp > settings.Fallback.CollectReflectionProbesInterval) {
                allProbes = Object.FindObjectsByType<ReflectionProbe>(FindObjectsSortMode.None);
                foreach (KeyValuePair<Camera, List<ReflectionProbeWrapper>> pair in reflectionProbeWrappers) {
                    foreach (ReflectionProbe probe in allProbes) {
                        if (pair.Value.FindIndex(item => item.Probe == probe) == -1) {
                            pair.Value.Add(new ReflectionProbeWrapper(probe));
                        }
                    }
                }
            }
            probesLastCollectTimestamp = time;
        }

        private void FilterActiveReflectionProbes(Camera camera, SSGIController controller) {
            Shader.SetGlobalFloat("_ssgi_fallback_direct_intensity", 0f);
            Shader.SetGlobalFloat("_ssgi_fallback_indirect_intensity", 0f);

            if (allProbes == null) return;
            if (settings.Quality.MaxProbesPerPixel == 0) return;

            GeometryUtility.CalculateFrustumPlanes(camera, frustumPlanes);
            filteredProbes.Clear();
            foreach (ReflectionProbe probe in allProbes) {
                if (!probe || !probe.texture || !probe.enabled || !probe.gameObject.activeInHierarchy ||
                    !GeometryUtility.TestPlanesAABB(frustumPlanes, probe.bounds)) continue;
                filteredProbes.Add(probe);
            }

            Vector3 center = camera.transform.position;
            Vector3 forward = camera.transform.forward;
            filteredProbes.Sort((a, b) => GetProbeSortingValue(b, center, forward).CompareTo(GetProbeSortingValue(a, center, forward)));

            float deltaTime = Time.deltaTime;

            if (reflectionProbeWrappers.ContainsKey(camera)) {
                activeProbes.Clear();
                List<ReflectionProbeWrapper> wrappers = reflectionProbeWrappers[camera];
                foreach (ReflectionProbeWrapper wrapper in wrappers) {
                    int idx = filteredProbes.IndexOf(wrapper.Probe);
                    float target = (idx != -1 && idx < settings.Quality.MaxProbesPerPixel) ? 1f : 0f;
                    wrapper.Fade = Mathf.MoveTowards(wrapper.Fade, target, deltaTime / settings.Fallback.EnterExitFadeDuration);
                    if (wrapper.Fade > 0f && wrapper.Probe) activeProbes.Add(wrapper.Probe);
                }

                if (activeProbes.Count > 0) {
                    activeProbes.Sort((a, b) => filteredProbes.IndexOf(a).CompareTo(filteredProbes.IndexOf(b)));

                    Shader.SetGlobalInt("_ssgi_refprobe_count", activeProbes.Count);
                    Shader.SetGlobalFloat("_ssgi_refprobe_mip", settings.Fallback.ProbeSampleMipLevel);
                    Shader.SetGlobalFloat("_ssgi_refprobe_falloff", settings.Fallback.ProbeVolumeFalloffDistance);
                    Shader.SetGlobalFloat("_ssgi_refprobe_realtime_intensity", settings.Fallback.ProbeRealtimeIntensity);
                    Shader.SetGlobalFloat("_ssgi_refprobe_realtime_saturation", settings.Fallback.ProbeRealtimeSaturation);
                    Shader.SetGlobalFloat("_ssgi_refprobe_realtime_power", settings.Fallback.ProbeRealtimeExp);

                    for (int i = 0; i < activeProbes.Count; i++) {
                        ReflectionProbeWrapper wrapper = wrappers.Find(item => item.Probe == activeProbes[i]);
                        Shader.SetGlobalTexture("_ssgi_refprobe_texture_" + i, activeProbes[i].texture);
                        Shader.SetGlobalVector("_ssgi_refprobe_center_" + i, activeProbes[i].bounds.center);
                        Shader.SetGlobalVector("_ssgi_refprobe_source_" + i, activeProbes[i].transform.position);
                        Shader.SetGlobalVector("_ssgi_refprobe_extents_" + i, activeProbes[i].bounds.extents);
                        Shader.SetGlobalVector("_ssgi_refprobe_rayinfo_" + i, GetRefProbeRaymarchParams(camera, activeProbes[i].transform.position));

                        float intensity = activeProbes[i].intensity * wrapper.Fade;
                        if (wrapper.Override) intensity *= wrapper.Override.IntenityMultiplier;
                        Shader.SetGlobalVector("_ssgi_refprobe_params_" + i,
                            new Vector4(intensity, activeProbes[i].textureHDRDecodeValues.y, activeProbes[i].textureHDRDecodeValues.w, 0f));
                    }

                    Shader.SetGlobalFloat("_ssgi_fallback_direct_intensity", controller.FallbackDirectIntensity);
                    Shader.SetGlobalFloat("_ssgi_fallback_direct_saturation", controller.FallbackDirectSaturation);
                    Shader.SetGlobalFloat("_ssgi_fallback_direct_power", controller.FallbackDirectPower);

                    if (settings.Quality.ApplyIndirectReflectionProbes) {
                        Shader.SetGlobalFloat("_ssgi_fallback_indirect_intensity", controller.FallbackDirectIntensity * settings.Fallback.FallbackIndirectIntensityMultiplier);
                        Shader.SetGlobalFloat("_ssgi_fallback_indirect_saturation", controller.FallbackDirectSaturation * settings.Fallback.FallbackIndirectSaturationMultiplier);
                        Shader.SetGlobalFloat("_ssgi_fallback_indirect_power", controller.FallbackDirectPower * settings.Fallback.FallbackIndirectPowerMultiplier);
                    }
                }
            }
        }

        private Vector4 GetRefProbeRaymarchParams(Camera camera, Vector3 center) {
            Vector3 screenPoint = camera.WorldToViewportPoint(center);
            Vector2 distToCenter = new Vector2(screenPoint.x - 0.5f, screenPoint.y - 0.5f);
            Vector2 absDistToCenter = new Vector2(Mathf.Abs(distToCenter.x), Mathf.Abs(distToCenter.y));
            if (absDistToCenter.x > 0.5f) { absDistToCenter.y /= screenPoint.x * 2f; absDistToCenter.x = 0.5f; }
            if (absDistToCenter.y > 0.5f) { absDistToCenter.x /= screenPoint.y * 2f; absDistToCenter.y = 0.5f; }
            screenPoint.x = Mathf.Clamp01(screenPoint.x);
            screenPoint.y = Mathf.Clamp01(screenPoint.y);
            if (screenPoint.z < 0f) { screenPoint.x = -screenPoint.x; screenPoint.y = 1f - screenPoint.y; }
            else { screenPoint.y = -screenPoint.y; }
            return screenPoint;
        }

        private float GetProbeSortingValue(ReflectionProbe probe, Vector3 center, Vector3 forward) {
            float distance = (probe.center - center).magnitude;
            if (probe.bounds.Contains(center)) {
                return 1000000f + (probe.importance * 1000) - distance;
            }
            float proximitySqr = settings.Fallback.ExpandCenterProximityFactor * settings.SSGIRangeMax;
            proximitySqr *= proximitySqr;
            if ((probe.bounds.ClosestPoint(center) - center).sqrMagnitude < proximitySqr) {
                return 10000f + (probe.importance * 10) - distance;
            }
            return -distance;
        }

        //=== Time helpers ===
        private float GetTime() {
#if UNITY_EDITOR
            if (Application.isPlaying) return Time.realtimeSinceStartup;
            return (float)UnityEditor.EditorApplication.timeSinceStartup;
#else
            return Time.realtimeSinceStartup;
#endif
        }

        //=== Debug captures (Editor-only) ===
        private void CaptureDebugRTSSGI(Camera cam, CommandBuffer cmd, bool doEncodeLightDir) {
#if UNITY_EDITOR
            if (debugRenderTextures == null || debugRenderTextures.Count == 0) return;
            int colorMode = doEncodeLightDir ? 2 : 0;
            foreach (KeyValuePair<RenderTexture, DebugRTData> pair in debugRenderTextures) {
                if (pair.Value.WasHandled) continue;
                if (pair.Value.Camera != cam) continue;

                RenderTexture rt = FetchBufferedRTwrapper(SSGIPassType.SSGIColor, pair.Value.Camera).RT1;
                if (!rt) continue;
                if (pair.Value.Type == SSGIPassType.SSGIColor) {
                    cmd.Blit(rt, pair.Key, SetupDebugMaterial(cmd, colorMode, 10f)); pair.Value.WasHandled = true;
                } else if (pair.Value.Type == SSGIPassType.SSGIShadow) {
                    cmd.Blit(rt, pair.Key, SetupDebugMaterial(cmd, 1, 2f)); pair.Value.WasHandled = true;
                } else if (pair.Value.Type == SSGIPassType.SSGILightDir) {
                    cmd.Blit(rt, pair.Key, SetupDebugMaterial(cmd, 3, 2f)); pair.Value.WasHandled = true;
                }
            }
#endif
        }

        private void CaptureDebugRTOthers(Camera cam, CommandBuffer cmd, bool doEncodeLightDir) {
#if UNITY_EDITOR
            if (debugRenderTextures == null || debugRenderTextures.Count == 0) return;
            int colorMode = doEncodeLightDir ? 2 : 0;
            float fp = settings.SSGIRangeMax;

            foreach (KeyValuePair<RenderTexture, DebugRTData> pair in debugRenderTextures) {
                if (pair.Value.Camera != cam) continue;
                if (pair.Value.WasHandled) continue;
                RenderTexture worldPos = FetchBufferedRTwrapper(SSGIPassType.WorldPositions, pair.Value.Camera).RT1;
                switch (pair.Value.Type) {
                    case SSGIPassType.ThicknessMaskPrePass:  cmd.Blit("_MF_SSGI_ThicknessMask_Prepass", pair.Key, SetupDebugMaterial(cmd, 0, 10f)); pair.Value.WasHandled = true; break;
                    case SSGIPassType.ThicknessMask:         cmd.Blit("_MF_SSGI_ThicknessMask",         pair.Key, SetupDebugMaterial(cmd, 0, 10f)); pair.Value.WasHandled = true; break;
                    case SSGIPassType.SSGIObjects:           cmd.Blit("_MF_SSGI_SSGIObjects",           pair.Key, SetupDebugMaterial(cmd, 0, 10f, 1f, 1f, 0f)); pair.Value.WasHandled = true; break;
                    case SSGIPassType.ScreenCapture:         cmd.Blit("_MF_SSGI_ScreenCapture",         pair.Key, SetupDebugMaterial(cmd, 0, 10f)); pair.Value.WasHandled = true; break;
                    case SSGIPassType.LightCapture:          cmd.Blit("_MF_SSGI_LightCapture",          pair.Key, SetupDebugMaterial(cmd, 0, 10f)); pair.Value.WasHandled = true; break;
                    case SSGIPassType.WorldPositions:        cmd.Blit(worldPos,                         pair.Key, SetupDebugMaterial(cmd, 0, 1f));  pair.Value.WasHandled = true; break;
                    case SSGIPassType.WorldPosDepth:         cmd.Blit(worldPos,                         pair.Key, SetupDebugMaterial(cmd, 1, fp));  pair.Value.WasHandled = true; break;
                    case SSGIPassType.Normals:               cmd.Blit("_MF_SSGI_Normals_HQ",            pair.Key, SetupDebugMaterial(cmd, 0, 1f));  pair.Value.WasHandled = true; break;
                    case SSGIPassType.NormalsDepth:          cmd.Blit("_MF_SSGI_Normals_HQ",            pair.Key, SetupDebugMaterial(cmd, 1, fp));  pair.Value.WasHandled = true; break;
                    case SSGIPassType.PreDenoised1Color:     cmd.Blit("_MF_SSGI_Pre_Denoised_1",        pair.Key, SetupDebugMaterial(cmd, colorMode, 1f)); pair.Value.WasHandled = true; break;
                    case SSGIPassType.PreDenoised1Shadow:    cmd.Blit("_MF_SSGI_Pre_Denoised_1",        pair.Key, SetupDebugMaterial(cmd, 1, 1f));  pair.Value.WasHandled = true; break;
                    case SSGIPassType.PreDenoised1LightDir:  cmd.Blit("_MF_SSGI_Pre_Denoised_1",        pair.Key, SetupDebugMaterial(cmd, 3, 1f));  pair.Value.WasHandled = true; break;
                    case SSGIPassType.PreDenoised2Color:     cmd.Blit("_MF_SSGI_Pre_Denoised_2",        pair.Key, SetupDebugMaterial(cmd, colorMode, 1f)); pair.Value.WasHandled = true; break;
                    case SSGIPassType.PreDenoised2Shadow:    cmd.Blit("_MF_SSGI_Pre_Denoised_2",        pair.Key, SetupDebugMaterial(cmd, 1, 1f));  pair.Value.WasHandled = true; break;
                    case SSGIPassType.PreDenoised2LightDir:  cmd.Blit("_MF_SSGI_Pre_Denoised_2",        pair.Key, SetupDebugMaterial(cmd, 3, 1f));  pair.Value.WasHandled = true; break;
                    case SSGIPassType.FinalDenoisedColor:    cmd.Blit("_MF_SSGI_Denoised_Final",        pair.Key, SetupDebugMaterial(cmd, colorMode, 1f)); pair.Value.WasHandled = true; break;
                    case SSGIPassType.FinalDenoisedShadow:   cmd.Blit("_MF_SSGI_Denoised_Final",        pair.Key, SetupDebugMaterial(cmd, 1, 1f));  pair.Value.WasHandled = true; break;
                    case SSGIPassType.FinalDenoisedLightDir: cmd.Blit("_MF_SSGI_Denoised_Final",        pair.Key, SetupDebugMaterial(cmd, 3, 1f));  pair.Value.WasHandled = true; break;
                }
            }
#endif
        }

        private Material SetupDebugMaterial(CommandBuffer cmd, int mode, float range, float maskR = 1f, float maskG = 1f, float maskB = 1f) {
            if (!debugBlitMaterial) debugBlitMaterial = new Material(Shader.Find("MF_SSGI/DebugBlit"));
            cmd.SetGlobalVector("_debug_color_mask", new Vector4(maskR, maskG, maskB));
            cmd.SetGlobalInt("_debug_mode", mode);
            cmd.SetGlobalFloat("_debug_range", range);
            return debugBlitMaterial;
        }

        public void Shuffle<T>(T[] array) {
            int n = array.Length;
            while (n > 1) {
                n--;
                int k = UnityEngine.Random.Range(0, n + 1);
                T value = array[k];
                array[k] = array[n];
                array[n] = value;
            }
        }

        //=== Cleanup ===
        private void HandleSceneUnloaded(Scene scene) {
            reflectionProbeWrappers.Clear();
            RequestedObjects.Clear();
            allProbes = null;
            lastEditorUpdateTimestamp = -1f;
            cachedThicknessRenderers = null;
            cachedThicknessInitialized = false;
            ReleaseAllBufferedRTs();
        }

        private void ReleaseAllBufferedRTs() {
            foreach (RTWrapper w in rtWrappers) w.Dispose();
            rtWrappers.Clear();
        }
    }
}
