using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Experimental.Rendering;
using System;
using System.Collections.Generic;
using UnityEngine.SceneManagement;
using System.Linq;

namespace MF.SSGI {
    public class SSGIPass : ScriptableRenderPass {



        //---------------------- STATIC
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
                if (RT1) {
                    GameObject.DestroyImmediate(RT1);
                }
                if (RT2) {
                    GameObject.DestroyImmediate(RT2);
                }

                RT1 = null;
                RT2 = null;
                Cam = null;
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
                debugRenderTextures.Add(rt, new DebugRTData() {
                    Camera = cam,
                    Type = type
                });
            }
        }

        public static void RemoveDebugRT(RenderTexture rt) {
            if (rt == null || debugRenderTextures == null) { return; }
            if (debugRenderTextures.ContainsKey(rt)) {
                debugRenderTextures.Remove(rt);
            }
        }



        //---------------------- INSTANCE
        //Fixed settings
        public const string UNITY_RTNAME_DEPTH = "_CameraDepthTexture";
        public const string UNITY_RTNAME_NORMAL = "_CameraNormalsTexture";

        //Members
        private Material thicknessMaskMaterialBack;
        private Material thicknessMaskMaterialFront;
        private Material ssgiExpandVerticesMaterial;
        private Material ssgiObjectMaterial;
        private Material captureLightMaterial;
        private Material worldPosMaterial;
        private Material captureNormalsMaterial;
        private Material scanEnvironmentMaterial;
        private Material denoiseImageMaterial;
        private Material blitFinalImageMaterial;

        private ScriptableRenderer renderer;
        private SSGIFeature.SSGISettings settings;

        private List<int> rtsToRelease = new List<int>();
        private FilteringSettings thicknessFilterSettings;
        private List<ShaderTagId> thicknessShaderTagIDList = new List<ShaderTagId>();
        
        private Material debugBlitMaterial;
        private RenderTextureFormat halfHDR = RenderTextureFormat.ARGBHalf;
        private RenderTextureFormat fullHDR = RenderTextureFormat.ARGBFloat;
        private bool sceneLoadedHooked = false;

        private ReflectionProbe[] allProbes;
        private List<ReflectionProbe> filteredProbes = new List<ReflectionProbe>();
        private List<ReflectionProbe> activeProbes = new List<ReflectionProbe>();
        private float probesLastCollectTimestamp;
        private Plane[] frustumPlanes = new Plane[6];
        private float lastEditorUpdateTimestamp = -1f;
        private Vector2Int[] rndNumbers;

        private int prevMultiFrameCellSize;
        private Matrix4x4 backupProjectionMatrix;
        private RenderTextureDescriptor currentCamTexDescriptor;

        
        public SSGIPass(SSGIFeature.SSGISettings settings) {
            this.settings = settings;
            if (!SystemInfo.SupportsRenderTextureFormat(halfHDR)) {
                halfHDR = RenderTextureFormat.ARGBFloat;
            }

            if (!sceneLoadedHooked) {
                sceneLoadedHooked = true;
                SceneManager.sceneUnloaded -= HandleSceneUnloaded;
                SceneManager.sceneUnloaded += HandleSceneUnloaded;
            }
        }

        public void Setup(ScriptableRenderer renderer) {
            this.renderer = renderer;
        }

        public override void Configure(CommandBuffer cmd, RenderTextureDescriptor cameraTextureDescriptor) {
            base.Configure(cmd, cameraTextureDescriptor);
            this.currentCamTexDescriptor = cameraTextureDescriptor;
            RefreshInput();

            thicknessShaderTagIDList.Clear();
            thicknessShaderTagIDList.Add(new ShaderTagId("UniversalForward"));
            thicknessShaderTagIDList.Add(new ShaderTagId("LightweightForward"));
            thicknessShaderTagIDList.Add(new ShaderTagId("SRPDefaultUnlit"));
            thicknessShaderTagIDList.Add(new ShaderTagId("Opaque"));
            renderPassEvent = settings.RenderPassEvent;
        }

        private void RefreshInput() {
            if (settings.Quality.MultiFrameCellSize > 0) {
                ConfigureInput(ScriptableRenderPassInput.Normal | ScriptableRenderPassInput.Depth | ScriptableRenderPassInput.Motion);
            } else {
                ConfigureInput(ScriptableRenderPassInput.Normal | ScriptableRenderPassInput.Depth);
            }
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData) {
            if(prevMultiFrameCellSize != settings.Quality.MultiFrameCellSize) {
                RefreshInput();
                prevMultiFrameCellSize = settings.Quality.MultiFrameCellSize;
            }

            RenderSSGI(context, renderingData);
        }

        private void RenderSSGI(ScriptableRenderContext context, RenderingData renderingData) {
            if (!SSGIFeature.SSGIActive) { return; }

            bool isSceneCam = false;
#if UNITY_EDITOR
            //If not an in-game camera
            if (Array.IndexOf(Camera.allCameras, renderingData.cameraData.camera) == -1) {
                isSceneCam = renderingData.cameraData.camera.name != "SceneCamera";
                //Skip all camera's that are non-game camera's
                if (!SSGIFeature.ShowInSceneView || isSceneCam) {
                    return;
                }
            }
#endif
            //Fetch post-process volume components
            SSGIVolumeComponent ssgiComp = null;
            if (VolumeManager.instance != null) {
                ssgiComp = VolumeManager.instance.stack.GetComponent<SSGIVolumeComponent>();
            }
            if (!ssgiComp || !ssgiComp.active) { return; }

            //Skip if camera doesn't have the SSGICamera attached
            if (renderingData.cameraData.camera.name != "SceneCamera"){
                SSGICamera camComp = renderingData.cameraData.camera.GetComponent<SSGICamera>();
                if (!camComp || !camComp.enabled) {
                    return;
                }
            }

            //Limit screen resolution
            float limitedResScale = 1f;
            int maxP = Mathf.Min(currentCamTexDescriptor.width, currentCamTexDescriptor.height);
            if (maxP > settings.Quality.MaxGIResolution) {
                limitedResScale = (float)settings.Quality.MaxGIResolution / (float)maxP;
            }

            //Raymarch required?
            bool doShadows = settings.Quality.UseRaymarchedShadows && settings.Raymarch && ssgiComp.ShadowIntensity.value > 0;
            bool doEncodeLightDir = settings.Quality.UseEncodedLightDirections && ssgiComp.LightDirInfluence.value > 0.0;

            //Capture depth first
            CaptureWorldPositionsAndDepth(context, renderingData, limitedResScale);
            CaptureNormalsAndDepth(context, renderingData, limitedResScale);

            //Pre-render object thickness
            backupProjectionMatrix = renderingData.cameraData.camera.projectionMatrix;
            WriteSSGIObjects(context, renderingData, limitedResScale);
            if (doShadows) {
                WriteThicknessMask(context, renderingData, limitedResScale);
            }

            //Render MAIN
            CollectReflectionProbes(renderingData.cameraData.camera);
            FilterActiveReflectionProbes(renderingData.cameraData.camera, ssgiComp);
            CaptureScreenAndLight(context, renderingData, limitedResScale);
            
            //Execute SSGI
            GatherSSGI(isSceneCam, context, renderingData, limitedResScale, doShadows, ssgiComp, doEncodeLightDir);
            CaptureDebugRTSSGI(context, renderingData, doEncodeLightDir);
            Denoise(context, renderingData, limitedResScale, doEncodeLightDir);
            BlitToScreen(context, renderingData, limitedResScale, ssgiComp);
            CaptureDebugRTOthers(context, renderingData, doEncodeLightDir);

            //Release rendertextures
            CommandBuffer cmd = CommandBufferPool.Get("MFSSGI_ReleaseRTs");
            foreach (int nameID in rtsToRelease) {
                cmd.ReleaseTemporaryRT(nameID);
            }
            rtsToRelease.Clear();
            context.ExecuteCommandBuffer(cmd);
            cmd.Clear();
        }

        private void CaptureWorldPositionsAndDepth(ScriptableRenderContext context, RenderingData renderingData, float limitedResScale) {
            RenderTextureDescriptor descriptor = GetDescriptor(renderingData, halfHDR, 1f);

            //Setup material
            if (!worldPosMaterial) {
                worldPosMaterial = new Material(Shader.Find("MF_SSGI/DepthToWorldPos"));
            }

            //Setup command buffer
            CommandBuffer cmd = CommandBufferPool.Get("MFSSGI_WorldPositions");
            RenderTexture target = GenBufferedRT(SSGIPassType.WorldPositions, renderingData.cameraData.camera, descriptor, cmd, FilterMode.Point);
            cmd.Blit(UNITY_RTNAME_DEPTH, target, worldPosMaterial);

            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }

        private void CaptureNormalsAndDepth(ScriptableRenderContext context, RenderingData renderingData, float limitedResScale) {
            if (!captureNormalsMaterial) {
                captureNormalsMaterial = new Material(Shader.Find("MF_SSGI/CaptureNormals"));
            }

            Shader.SetGlobalInt("_use_deferred", settings.UseDeferredRendering ? 1 : 0);
            Camera cam = renderingData.cameraData.camera;
            Shader.SetGlobalVector("_cam_world_forward", cam.transform.forward);
            if (cam.orthographic && cam.nearClipPlane < 0f) {
                Shader.SetGlobalVector("_cam_world_position", cam.transform.position + (cam.transform.forward * cam.nearClipPlane));
            } else {
                Shader.SetGlobalVector("_cam_world_position", cam.transform.position);
            }

            //--- Highres version
            RenderTextureDescriptor descriptor = GetDescriptor(renderingData, halfHDR, 1f);
            int nameID_HQ = Shader.PropertyToID("_MF_SSGI_Normals_HQ");
            CommandBuffer cmd = CommandBufferPool.Get("MFSSGI_DepthNormals");
            cmd.GetTemporaryRT(nameID_HQ, descriptor, FilterMode.Point);
            captureNormalsMaterial.SetTexture("_WorldPositions", FetchBufferedRTwrapper(SSGIPassType.WorldPositions, cam).RT1);
            cmd.Blit(null, nameID_HQ, captureNormalsMaterial);
            rtsToRelease.Add(nameID_HQ);

            //--- Lowres version
            int nameID_LQ = Shader.PropertyToID("_MF_SSGI_Normals_LQ");
            descriptor = GetDescriptor(renderingData, halfHDR, settings.Quality.SSGIRenderScale * limitedResScale);
            descriptor.useMipMap = true;
            descriptor.autoGenerateMips = true;
            descriptor.mipCount = settings.Raymarch.RaymarchNormalDepthMipLevel;
            cmd.GetTemporaryRT(nameID_LQ, descriptor, FilterMode.Point);
            cmd.Blit(nameID_HQ, nameID_LQ);
            rtsToRelease.Add(nameID_LQ);

            //Execute
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }

        private void WriteThicknessMask(ScriptableRenderContext context, RenderingData renderingData, float limitedResScale) {
            if (!thicknessMaskMaterialBack) {
                thicknessMaskMaterialBack = new Material(Shader.Find("MF_SSGI/ThicknessMaskBack"));
            }
            if (!thicknessMaskMaterialFront) {
                thicknessMaskMaterialFront = new Material(Shader.Find("MF_SSGI/ThicknessMaskFront"));
            }

            CommandBuffer cmd = CommandBufferPool.Get("MF.SSGI_ThicknessMask");

            Camera cam = renderingData.cameraData.camera;
            SortingCriteria sortingCriteria = renderingData.cameraData.defaultOpaqueSortFlags;
            DrawingSettings drawingSettings = CreateDrawingSettings(thicknessShaderTagIDList, ref renderingData, sortingCriteria);
            RenderTextureDescriptor descriptor = GetDescriptor(renderingData, RenderTextureFormat.RFloat, settings.Quality.SSGIRenderScale * limitedResScale, true);

            drawingSettings.overrideMaterialPassIndex = 0;
            drawingSettings.enableDynamicBatching = true;
            drawingSettings.enableInstancing = true;

            thicknessFilterSettings = FilteringSettings.defaultValue;
            thicknessFilterSettings.layerMask = settings.Raymarch.ThicknessMaskLayers;

            //Set pivot-vs-normal: How to 'grow' the mesh when rendering the mask
            Shader.SetGlobalFloat("_object_thickness_pivot_vs_normal", settings.Raymarch.ObjectPivotToNormal);
            Shader.SetGlobalFloat("_object_min_thickness", settings.Raymarch.ObjectMinimalThickness);

            void RenderGeo() {
                //Expand and render
                if (settings.Raymarch.ObjectExpand > 0.0f) {
                    LimitFarClipPlane(renderingData, cmd, settings.ShadowMaxDistace);
                    cmd.SetGlobalFloat("_thickness_mask_expand", settings.Raymarch.ObjectExpand);
                    context.ExecuteCommandBuffer(cmd);
                    context.DrawRenderers(renderingData.cullResults, ref drawingSettings, ref thicknessFilterSettings);
                }

                //Normal size and re-render
                LimitFarClipPlane(renderingData, cmd, settings.ShadowMaxDistace);
                cmd.SetGlobalFloat("_thickness_mask_expand", 0.0f);
                context.ExecuteCommandBuffer(cmd);
                context.DrawRenderers(renderingData.cullResults, ref drawingSettings, ref thicknessFilterSettings);
            }

            //Pre-render custom depth-pass as Unity's depth in Orthographic messes up everything
            //Pass 1: Setup rendertarget
            descriptor.colorFormat = RenderTextureFormat.RHalf;
            int nameIDPrePass = Shader.PropertyToID("_MF_SSGI_ThicknessMask_Prepass");
            SetTempRTActive(context, cmd, descriptor, nameIDPrePass);

            //Pass 1: Start drawing
            drawingSettings.overrideMaterial = thicknessMaskMaterialBack;
            drawingSettings.overrideMaterialPassIndex = 0;
            RenderGeo();
            rtsToRelease.Add(nameIDPrePass);

            //Pass 2: Setup rendertarget
            descriptor.colorFormat = RenderTextureFormat.RGHalf;
            int nameIDPostPass = Shader.PropertyToID("_MF_SSGI_ThicknessMask");
            SetTempRTActive(context, cmd, descriptor, nameIDPostPass, Color.green, FilterMode.Point);

            //Pass 2: Ortho & slipping Depth-issue work-around
            drawingSettings.overrideMaterialPassIndex = 0;
            drawingSettings.overrideMaterial = thicknessMaskMaterialFront;
            RenderGeo();
            rtsToRelease.Add(nameIDPostPass);

            //Done
            CommandBufferPool.Release(cmd);
        }

        private void WriteSSGIObjects(ScriptableRenderContext context, RenderingData renderingData, float limitedResScale) {
            //Test if any SSGIObjects are in the scene
            bool requiresSSGIObjectsRendering =
                settings.Advanced.UseSSGIObjectOverrides &&
                RequestedObjects.Count((item) => item && item.RequiresSSGIMaskRendering) > 0;

            Shader.SetGlobalFloat("_default_clip_depth_bias", settings.Advanced.DefaultClipDepthBias);
            Shader.SetGlobalInt("_use_ssgi_objects", requiresSSGIObjectsRendering ? 1 : 0);

            //Gen material
            if (!ssgiObjectMaterial) {
                ssgiObjectMaterial = new Material(Shader.Find("MF_SSGI/SSGIObjects"));
            }

            //Setup rendertarget
            CommandBuffer cmd = CommandBufferPool.Get("MF.SSGI_SSGIObjects");
            RenderTextureDescriptor descriptor = GetDescriptor(renderingData, halfHDR, 1f, true);
            int nameID = Shader.PropertyToID("_MF_SSGI_SSGIObjects");
            SetTempRTActive(context, cmd, descriptor, nameID, Color.white, FilterMode.Point);
            LimitFarClipPlane(renderingData, cmd, settings.SSGIRangeMax);
            rtsToRelease.Add(nameID);

            //Render objects
            GeometryUtility.CalculateFrustumPlanes(renderingData.cameraData.camera, frustumPlanes);
            foreach (SSGIObject obj in RequestedObjects) {
                if (obj.RequiresSSGIMaskRendering) {
                    for (int i = 0; i < obj.AffectedRenderers.Count; i++) {
                        Renderer renderer = obj.AffectedRenderers[i];
                        if (!renderer || !renderer.enabled || !renderer.gameObject.activeInHierarchy || !GeometryUtility.TestPlanesAABB(frustumPlanes, renderer.bounds)) { continue; }
                        for (int j = 0; j < obj.SubMeshCounts[i]; j++) {
                            cmd.DrawRenderer(renderer, ssgiObjectMaterial, j, 0);
                        }
                    }
                }
            }

            //Done
            cmd.SetProjectionMatrix(renderingData.cameraData.GetProjectionMatrix());
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }

        private void LimitFarClipPlane(RenderingData renderingData, CommandBuffer cmd, float far) {
            Camera cam = renderingData.cameraData.camera;
            if (cam.orthographic) {
                //TODO: Limit ortho cam
            } else {
                cmd.SetProjectionMatrix(Matrix4x4.Perspective(cam.fieldOfView, cam.aspect, cam.nearClipPlane, Mathf.Min(cam.farClipPlane, far)));
            }
        }

        private void CaptureScreenAndLight(ScriptableRenderContext context, RenderingData renderingData, float limitedResScale) {
            if (!captureLightMaterial) {
                captureLightMaterial = new Material(Shader.Find("MF_SSGI/CaptureLight"));
            }
            if (!ssgiExpandVerticesMaterial) {
                ssgiExpandVerticesMaterial = new Material(Shader.Find("MF_SSGI/ExpandVertices"));
            }
            CommandBuffer cmd = CommandBufferPool.Get("MFSSGI_ScreenLightCapture");

            //Full-res screen capture
            RenderTextureDescriptor descriptor = GetDescriptor(renderingData, halfHDR, 1f);
            int nameID = Shader.PropertyToID("_MF_SSGI_ScreenCapture");
            if (!settings.UseDeferredRendering && settings.Lighting.AlbedoDetailBoost > 0f) {
                descriptor.useMipMap = true;
                descriptor.autoGenerateMips = true;
                descriptor.mipCount = (int)Mathf.Ceil(settings.Lighting.AlbedoDetailBoostMipLevle);
            }

            cmd.GetTemporaryRT(nameID, descriptor, FilterMode.Trilinear); //2021 compatible
            cmd.Blit(renderer.cameraColorTarget, nameID);
            rtsToRelease.Add(nameID);

            //Execute context, as we need the ScreenCapture before we write the expand vertices
            context.ExecuteCommandBuffer(cmd);
            cmd.Clear();

            //Set worldposition
            RTWrapper worldPosWrapper = FetchBufferedRTwrapper(SSGIPassType.WorldPositions, renderingData.cameraData.camera);
            captureLightMaterial.SetTexture("_WorldPositions", worldPosWrapper.RT1);
            
            //Reflection Probe Fallback shadows
            cmd.SetGlobalInt("_ssgi_refprobe_raymarch_samples", settings.Quality.ReflectionProbeFallbackIndirectShadows ? settings.Quality.ReflectionProbeFallbackRaymarchSamples : 0);

            //SSGI-res light-info capture
            descriptor = GetDescriptor(renderingData, halfHDR, settings.Quality.SSGIRenderScale * limitedResScale);
            nameID = Shader.PropertyToID("_MF_SSGI_LightCapture");
            cmd.GetTemporaryRT(nameID, descriptor, FilterMode.Point);
            cmd.Blit(renderer.cameraColorTarget, nameID, captureLightMaterial); //2021 compatible
            rtsToRelease.Add(nameID);

            //Albedo boost
            Shader.SetGlobalFloat("_albedo_boost", settings.Lighting.AlbedoDetailBoost);
            Shader.SetGlobalFloat("_albedo_boost_miplevel", settings.Lighting.AlbedoDetailBoostMipLevle);

            //Set properties
            ssgiExpandVerticesMaterial.SetFloat("_OneOverScreenResX", 1f / (float)descriptor.width);
            ssgiExpandVerticesMaterial.SetFloat("_OneOverScreenResY", 1f / (float)descriptor.height);
            ssgiExpandVerticesMaterial.SetFloat("_ExpandRangeMin", settings.SSGIRangeMax * settings.Advanced.ExpandObjectsRangeFactorMin);
            ssgiExpandVerticesMaterial.SetFloat("_ExpandRangeMax", settings.SSGIRangeMax * settings.Advanced.ExpandObjectsRangeFactorMax);

            //Draw SSGIObjects that need expanded vertices
            cmd.SetRenderTarget(nameID);
            LimitFarClipPlane(renderingData, cmd, settings.SSGIRangeMax);
            GeometryUtility.CalculateFrustumPlanes(renderingData.cameraData.camera, frustumPlanes);

            foreach (SSGIObject obj in RequestedObjects) {
                if (obj.RequiresVertexExpandRendering) {
                    for(int i = 0; i < obj.AffectedRenderers.Count; i++) {
                        Renderer renderer = obj.AffectedRenderers[i];
                        if (!renderer || !renderer.enabled || !renderer.gameObject.activeInHierarchy || !GeometryUtility.TestPlanesAABB(frustumPlanes, renderer.bounds)) { continue; }
                        for (int j = 0; j < obj.SubMeshCounts[i]; j++) {
                            cmd.DrawRenderer(renderer, ssgiExpandVerticesMaterial, j, 0);
                        }
                    }
                }
            }

            //Execute context, as we need the Lightcapture before we write the expand vertices
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }

        private void GatherSSGI(bool isSceneCam, ScriptableRenderContext context, RenderingData renderingData, float limitedResScale, bool doShadows, SSGIVolumeComponent ssgiComp, bool doEncodeLightDir) {
            //Setup material
            if (!scanEnvironmentMaterial) {
                scanEnvironmentMaterial = new Material(Shader.Find("MF_SSGI/SSGI"));
            }

            //Throw warning
            if(settings.Quality.MultiFrameCellSize == 2) {
                Debug.LogWarning("MultiFrameCellSize is set to 2, this causes rounding errors. Please set to 0 to disable or a value of 3 or higher (at QualitySettings)");
            }

            //Setup shuffled RND
            Camera cam = renderingData.cameraData.camera;
            Shader.SetGlobalInt("_debug_reprojection", settings.DebugReprojection ? 1 : 0);
            Shader.SetGlobalFloat("_multiframe_cell_size", (isSceneCam || !Application.isPlaying) ? 0 : settings.Quality.MultiFrameCellSize);
            Shader.SetGlobalFloat("_multiframe_energy_falloff", settings.Quality.MultiFrameEnergyFalloff);

            Shader.SetGlobalFloat("_multiframe_dist", settings.Advanced.MultiFrameDistanceThreshold);
            Shader.SetGlobalFloat("_multiframe_shadow_compensate", settings.Advanced.MultiFrameShadowCompensate);
            Shader.SetGlobalInt("_multiframe_apply_multisample", settings.Advanced.MultiFrameApplyMultiSample ? 1 : 0);

            if (settings.Quality.MultiFrameCellSize > 0) {
                int cellSqr = settings.Quality.MultiFrameCellSize * settings.Quality.MultiFrameCellSize;
                if (rndNumbers == null || rndNumbers.Length != cellSqr) {
                    rndNumbers = new Vector2Int[cellSqr];
                    int i = 0;
                    for (int x = 0; x < settings.Quality.MultiFrameCellSize; x++) {
                        for (int y = 0; y < settings.Quality.MultiFrameCellSize; y++) {
                            rndNumbers[i++] = new Vector2Int(x,y);
                        }
                    }
                    Shuffle(rndNumbers);
                }
                //Set RND shuffled pixel numbers
                if (!frameCountTable.ContainsKey(cam)) {
                    frameCountTable.Add(cam, 0);
                }
                frameCountTable[cam]++;
                Shader.SetGlobalVector("_rnd_pixel", (Vector2)rndNumbers[frameCountTable[cam] % cellSqr]);
            }
            //Debug.Log(Shader.GetGlobalInt("_rnd_pixel_x") + ", "+ Shader.GetGlobalInt("_rnd_pixel_y"));

            //Pass pattern values
            RenderTextureDescriptor descriptor = GetDescriptor(renderingData, doEncodeLightDir ? fullHDR : halfHDR, settings.Quality.SSGIRenderScale * limitedResScale);

            //Set globals
            Shader.SetGlobalInt("_ssgi_samples_hq", settings.Quality.SSGISamplesHQ);
            Shader.SetGlobalInt("_ssgi_samples_backfill", settings.Quality.SSGISamplesBackfill);
            Shader.SetGlobalFloat("_ssgi_samples_reduction", settings.Advanced.SSGISamplesReduction);
            Shader.SetGlobalVector("_ssgi_res", new Vector4(descriptor.width, descriptor.height, 0f, 0f));
            Shader.SetGlobalFloat("_edge_vignette", settings.Advanced.SearchEdgeVignette);
            Shader.SetGlobalInt("_do_encode_lightdir", doEncodeLightDir ? 1 : 0);
            Shader.SetGlobalFloat("_multi_sample_normal_distance", settings.Advanced.MultiSampleNormalsDistance);

            Shader.SetGlobalFloat("_scan_base_range", settings.Advanced.Search2DRange);
            Shader.SetGlobalFloat("_scan_ratio_y", (float)descriptor.height / (float)descriptor.width);
            Shader.SetGlobalFloat("_scan_noise", settings.Advanced.Search2DNoise);

            //Screen space search
            Shader.SetGlobalFloat("_scan_depth_threshold_factor", settings.Advanced.ScanDepthThresholdFactor);
            Shader.SetGlobalFloat("_ssgi_range_max", settings.SSGIRangeMax);
            Shader.SetGlobalFloat("_ssgi_range_min", settings.SSGIRangeMin);

            //Energy
            Shader.SetGlobalFloat("_max_light_attenuation", settings.Lighting.MaxLightAttenuation);
            Shader.SetGlobalFloat("_max_input_energy", settings.Lighting.MaxInputEnergy);
            Shader.SetGlobalFloat("_frustum_pixel_size", 2f * Mathf.Tan(cam.fieldOfView * Mathf.Deg2Rad / 2f) * settings.Lighting.DistanceEnergyBoost);

            //SSGIComp
            Shader.SetGlobalFloat("_ssgi_intensity", ssgiComp.LightIntensity.value * 100f); //*100 to compensate for V1.0 to V1.1 update
            Shader.SetGlobalFloat("_light_falloff_distance", ssgiComp.LightFalloffDistance.value);
            Shader.SetGlobalFloat("_skybox_influence", ssgiComp.SkyboxInfluence.value);
            
            //Light cast/receive
            Shader.SetGlobalFloat("_light_cast_dot_min", settings.Lighting.LightCastDotMin);
            Shader.SetGlobalFloat("_light_cast_dot_max", settings.Lighting.LightCastDotMax);
            Shader.SetGlobalFloat("_light_receive_dot_min", settings.Lighting.LightReceiveDotMin);
            //compensate for missing Light-directions
            Shader.SetGlobalFloat("_light_receive_dot_max", settings.Quality.UseEncodedLightDirections ? settings.Lighting.LightReceiveDotMax : Mathf.Min(1f, settings.Lighting.LightReceiveDotMax * 2f));

            Shader.SetGlobalFloat("_depth_cutoff_near", settings.Advanced.CamCutoffNear);
            Shader.SetGlobalFloat("_depth_cutoff_far", settings.Advanced.CamCutoffFar);

            //Distance-based scan-2d-size
            float distance = settings.SSGIRangeMax * settings.ScanDepth2DRangeFactor * settings.Quality.ScanDepthMultiplier;
            Shader.SetGlobalFloat("_scansize_distance_multiplier", Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad));
            Shader.SetGlobalFloat("_scansize_distance_threshold", distance);
            Shader.SetGlobalFloat("_scansize_distance_base_size", distance * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad));
            Shader.SetGlobalFloat("_scansize_ortho", settings.Orthographic2DRangeFactor * settings.Quality.ScanDepthMultiplier);

            if (doShadows) {
                //Addative
                Shader.SetGlobalFloat("_shadow_intensity", ssgiComp.ShadowIntensity.value);

                //SSGIComponent
                Shader.SetGlobalFloat("_contact_shadow_range", ssgiComp.ContactShadowsRange.value);
                Shader.SetGlobalFloat("_casted_shadow_range", ssgiComp.CastedShadowsRange.value);
                Shader.SetGlobalFloat("_casted_shadow_intensity", ssgiComp.CastedShadowsIntensity.value);
                Shader.SetGlobalFloat("_casted_shadow_omni_dir", ssgiComp.CastedShadowsOmniDirectional.value);
                Shader.SetGlobalFloat("_result_shadows_contrast", ssgiComp.ShadowContrast.value);
                Shader.SetGlobalFloat("_contact_shadow_soft_knee", ssgiComp.ContactShadowsSoftKnee.value);
                Shader.SetGlobalFloat("_casted_shadow_soft_knee", ssgiComp.CastedShadowsSoftKnee.value);

                //Raymarching
                Shader.SetGlobalFloat("_raymarch_min_distance", settings.ShadowMinDistace);
                Shader.SetGlobalFloat("_raymarch_max_distance", settings.ShadowMaxDistace);

                Shader.SetGlobalFloat("_raymarch_samples_hq", settings.Quality.RaymarchSamplesHQ);
                Shader.SetGlobalFloat("_raymarch_samples_backfill", settings.Quality.RaymarchSamplesBackfill);
                Shader.SetGlobalFloat("_raymarch_cubic_distance_falloff", settings.Quality.RaymarchCubicDistanceFalloff);

                Shader.SetGlobalFloat("_raymarch_depth_bias", settings.Raymarch.RaymarchDepthBias);
                Shader.SetGlobalInt("_raymarch_normal_depth_miplevel", settings.Raymarch.RaymarchNormalDepthMipLevel);
                Shader.SetGlobalFloat("_raymarch_surface_depth_bias_min", settings.Raymarch.RaymarchSurfaceDepthBiasMin);
                Shader.SetGlobalFloat("_raymarch_surface_depth_bias_max", settings.Raymarch.RaymarchSurfaceDepthBiasMax);
                Shader.SetGlobalFloat("_raymarch_min_hit_count", settings.Raymarch.RaymarchMinimumHitCount);
                Shader.SetGlobalFloat("_raymarch_contact_min_dist", settings.Raymarch.RaymarchContactMinDistance);
                Shader.SetGlobalFloat("_raymarch_casted_min_dist", settings.Raymarch.RaymarchCastedMinDistance);
                Shader.SetGlobalFloat("_raymarch_shorten", settings.Quality.RaymarchMaxRangeFactor);
            } else {
                Shader.SetGlobalFloat("_shadow_intensity", 0f);
            }

            //Setup command buffer
            CommandBuffer cmd = CommandBufferPool.Get("MFSSGI_SSGI");

            //Reset Far clipping distance
            cmd.SetProjectionMatrix(backupProjectionMatrix);

            //Reflection Probe Fallback shadows
            cmd.SetGlobalInt("_ssgi_refprobe_raymarch_samples", settings.Quality.ReflectionProbeFallbackDirectShadows ? settings.Quality.ReflectionProbeFallbackRaymarchSamples : 0);

            RenderTexture target = GenBufferedRT(SSGIPassType.SSGIColor, cam, descriptor, cmd, FilterMode.Point);
            RTWrapper worldPosWrapper = FetchBufferedRTwrapper(SSGIPassType.WorldPositions, cam);
            RTWrapper ssgiWrapper = FetchBufferedRTwrapper(SSGIPassType.SSGIColor, cam);
            scanEnvironmentMaterial.SetTexture("_WorldPositions", worldPosWrapper.RT1);
            scanEnvironmentMaterial.SetTexture("_PrevWorldPositions", worldPosWrapper.RT2);
            scanEnvironmentMaterial.SetTexture("_PrevSSGI", ssgiWrapper.RT2);
            cmd.Blit(null, target, scanEnvironmentMaterial);

            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }

        private void Denoise(ScriptableRenderContext context, RenderingData renderingData, float limitedResScale, bool doEncodeLightDir) {
            if (!denoiseImageMaterial) {
                denoiseImageMaterial = new Material(Shader.Find("MF_SSGI/Denoise"));
            }

            //Setup variables
            Shader.SetGlobalFloat("_denoise_min_dot_match", settings.Advanced.DenoiseNormalDotMin);
            Shader.SetGlobalFloat("_denoise_max_dot_match", settings.Advanced.DenoiseNormalDotMax);
            Shader.SetGlobalFloat("_denoise_max_depth_diff", settings.Advanced.DenoiseDepthDiffTheshold);
            Shader.SetGlobalFloat("_denoise_color_normal_contribution", settings.Advanced.DenoiseColorNormalContribution);
            Shader.SetGlobalFloat("_denoise_shadow_normal_contribution", settings.Advanced.DenoiseShadowNormalContribution);
            Shader.SetGlobalInt("_denoise_min_shadows_hit_count", settings.Raymarch.DenoiseShadowsMinHitCount);

            //Setup pre-denoise
            CommandBuffer cmd = CommandBufferPool.Get("MFSSGI_Denoise");

            //--------- Pre-desnoise
            RenderTextureDescriptor descriptor = GetDescriptor(renderingData, doEncodeLightDir ? fullHDR : halfHDR, settings.Quality.SSGIRenderScale * limitedResScale);
            cmd.SetGlobalVector("_oneover_denoise_res", new Vector4(1f / (float)descriptor.width, 1f / (float)descriptor.height, 0f, 0f));
            Shader.SetGlobalFloat("_denoise_energy_compensation", settings.Quality.IntensityCompensation);
            Shader.SetGlobalFloat("_denoise_shadow_compensation", settings.Quality.ShadowCompensation);

            FilterMode filterMode = doEncodeLightDir ? FilterMode.Point : FilterMode.Trilinear;
            int nameID1 = Shader.PropertyToID("_MF_SSGI_Pre_Denoised_1");
            int nameID2 = Shader.PropertyToID("_MF_SSGI_Pre_Denoised_2");
            int nameIDFinal = Shader.PropertyToID("_MF_SSGI_Denoised_Final");
            cmd.GetTemporaryRT(nameID1, descriptor, filterMode);
            cmd.GetTemporaryRT(nameID2, descriptor, filterMode);
            cmd.GetTemporaryRT(nameIDFinal, descriptor, filterMode);

            for (int i = 0; i < settings.Quality.DenoisePasses; i++) {
                //Decrement added result foreach new pass
                float pixelSize = settings.Quality.DenoisePasses > 1 ?
                    Mathf.Lerp(settings.Quality.PreDenoisePixelSize, 1f, (float)i / (float)(settings.Quality.DenoisePasses - 1)) :
                    settings.Quality.PreDenoisePixelSize;

                cmd.SetGlobalFloat("_denoise_pixel_size", pixelSize);

                //Blit multiple passes
                if (i == 0) {
                    cmd.Blit(FetchBufferedRTwrapper(SSGIPassType.SSGIColor, renderingData.cameraData.camera).RT1, nameID1, denoiseImageMaterial); //Copy SSGI to D1
                } else if (i % 2 != 0) {
                    cmd.Blit(nameID1, i == settings.Quality.DenoisePasses - 1 ? nameIDFinal : nameID2, denoiseImageMaterial); //Copy D1 to D2/Final
                } else {
                    cmd.Blit(nameID2, i == settings.Quality.DenoisePasses - 1 ? nameIDFinal : nameID1, denoiseImageMaterial); //Copy D2 to D1/Final
                }
            }

            //Done
            rtsToRelease.Add(nameID1);
            rtsToRelease.Add(nameID2);
            rtsToRelease.Add(nameIDFinal);

            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }

        private void BlitToScreen(ScriptableRenderContext context, RenderingData renderingData, float limitedResScale, SSGIVolumeComponent ssgiComp) {
            if (!blitFinalImageMaterial) {
                blitFinalImageMaterial = new Material(Shader.Find("MF_SSGI/FinalBlit"));
            }

            Shader.SetGlobalFloat("_debug_screen_coverage", settings.DebugScreenCoverage);
            Shader.SetGlobalInt("_debug_motion_vectors", settings.DebugMotionVectors ? 1 : 0);
            Shader.SetGlobalInt("_debug_albedo", settings.DebugAlbedo ? 1 : 0);


            Shader.SetGlobalFloat("_max_output_energy", settings.Lighting.MaxOutputEnergy);

            //SSGIComponent
            Shader.SetGlobalFloat("_composit_lightdir_influence", settings.Quality.UseEncodedLightDirections ? ssgiComp.LightDirInfluence.value : 0f);
            Shader.SetGlobalFloat("_composit_lightdir_normal_boost", ssgiComp.NormalmapBoost.value);
            Shader.SetGlobalFloat("_composit_final_contrast", ssgiComp.FinalContrast.value);
            Shader.SetGlobalFloat("_composit_final_intensity", ssgiComp.FinalIntensity.value);
            Shader.SetGlobalFloat("_composit_occlusion_intensity", ssgiComp.PreMultiply.value);
            Shader.SetGlobalColor("_composit_color", ssgiComp.GITint.value);
            Shader.SetGlobalFloat("_composit_gi_contrast", ssgiComp.GIContrast.value);
            Shader.SetGlobalFloat("_composit_gi_saturate", ssgiComp.GISaturation.value);
            Shader.SetGlobalFloat("_composit_gi_vibrance", ssgiComp.GIVibrance.value);
            Shader.SetGlobalVector("_shadow_boost_tint", ssgiComp.ShadowTint.value);
            Shader.SetGlobalFloat("_shadow_boost_exp", ssgiComp.ShadowExponential.value);

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
                (1f / (float)currentCamTexDescriptor.width) * settings.Quality.SSGIRenderScale * limitedResScale * settings.Advanced.FinalCompositAARange,
                (1f / (float)currentCamTexDescriptor.height) * settings.Quality.SSGIRenderScale * limitedResScale * settings.Advanced.FinalCompositAARange, 
            0f, 0f));

            CommandBuffer cmd = CommandBufferPool.Get("MFSSGI_FinalBlit");
            cmd.SetRenderTarget(renderer.cameraColorTarget, renderer.cameraDepthTarget); //Unity 2021 compatible
            cmd.Blit(null, renderer.cameraColorTarget, blitFinalImageMaterial);

            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }


        private void SetTempRTActive(ScriptableRenderContext context, CommandBuffer cmd, RenderTextureDescriptor descriptor, int nameID, Color color = default(Color), FilterMode filterMode = FilterMode.Point) {
            cmd.GetTemporaryRT(nameID, descriptor, filterMode);
            cmd.SetRenderTarget(nameID);
            cmd.ClearRenderTarget(true, true, color);
            context.ExecuteCommandBuffer(cmd);
            cmd.Clear();
        }

        private RenderTextureDescriptor GetDescriptor(RenderingData renderingData, RenderTextureFormat format, float customScale, bool requiresDepth = false, int msaa = 1) {
            RenderTextureDescriptor descriptor = currentCamTexDescriptor;
            descriptor.width = (int)(currentCamTexDescriptor.width * customScale);
            descriptor.height = (int)(currentCamTexDescriptor.height * customScale);
            descriptor.colorFormat = format;
            descriptor.depthBufferBits = requiresDepth ? 16 : 0;
            
            descriptor.useMipMap = false;
            descriptor.autoGenerateMips = false;
            descriptor.sRGB = false;
            descriptor.msaaSamples = msaa;
            return descriptor;
        }

        private void CollectReflectionProbes(Camera cam) {
            if (!reflectionProbeWrappers.ContainsKey(cam)) {
                reflectionProbeWrappers.Add(cam, new List<ReflectionProbeWrapper>());
            }

            //Collect in scene
            float time = GetTime();
            if (allProbes == null || time - probesLastCollectTimestamp > settings.Fallback.CollectReflectionProbesInterval) {
                allProbes = GameObject.FindObjectsOfType<ReflectionProbe>();

                //Update all wrappers for all camera's
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

        private void FilterActiveReflectionProbes(Camera camera, SSGIVolumeComponent ssgiComp) {
            //By default its disabled, only enable when active probes are found for this camera
            Shader.SetGlobalFloat("_ssgi_fallback_direct_intensity", 0f);
            Shader.SetGlobalFloat("_ssgi_fallback_indirect_intensity", 0f);
            
            //Early return when disabled
            if (allProbes == null) { return; }
            if (settings.Quality.MaxProbesPerPixel == 0) {
                return;
            }

            //Setup cheap non-alloc data
            GeometryUtility.CalculateFrustumPlanes(camera, frustumPlanes);
            filteredProbes.Clear();
            foreach (ReflectionProbe probe in allProbes) {
                if(!probe || !probe.texture || !probe.enabled || !probe.gameObject.activeInHierarchy || !GeometryUtility.TestPlanesAABB(frustumPlanes, probe.bounds)) { continue; }
                filteredProbes.Add(probe);
            }

            //Sort probes - Highest score wins
            Vector3 center = camera.transform.position;
            Vector3 forward = camera.transform.forward;
            filteredProbes.Sort((a, b) => GetProbeSortingValue(b, center, forward).CompareTo(GetProbeSortingValue(a, center, forward)));

            //Get correct delta-time
            float deltaTime = Time.deltaTime;// GetDeltaTime();
             
            //Populate a new list based on Fade-status
            if (reflectionProbeWrappers.ContainsKey(camera)) {
                activeProbes.Clear();
                List<ReflectionProbeWrapper> wrappers = reflectionProbeWrappers[camera];
                foreach (ReflectionProbeWrapper wrapper in wrappers) {
                    int idx = filteredProbes.IndexOf(wrapper.Probe);
                    float target = (idx != -1 && idx < settings.Quality.MaxProbesPerPixel) ? 1f : 0f;
                    wrapper.Fade = Mathf.MoveTowards(wrapper.Fade, target, deltaTime / settings.Fallback.EnterExitFadeDuration);
                    if (wrapper.Fade > 0f && wrapper.Probe) {
                        activeProbes.Add(wrapper.Probe);
                    }
                }

                if (activeProbes.Count > 0) {
                    activeProbes.Sort((a, b) => filteredProbes.IndexOf(a).CompareTo(filteredProbes.IndexOf(b)));

                    //Set shader values
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

                        //Intensity
                        float intensity = activeProbes[i].intensity * wrapper.Fade;
                        if (wrapper.Override) {
                            intensity *= wrapper.Override.IntenityMultiplier;
                        }
                        Shader.SetGlobalVector("_ssgi_refprobe_params_" + i, new Vector4(intensity, activeProbes[i].textureHDRDecodeValues.y, activeProbes[i].textureHDRDecodeValues.w, 0f));
                    }

                    //Pass composition-settings
                    Shader.SetGlobalFloat("_ssgi_fallback_direct_intensity", ssgiComp.FallbackDirectIntensity.value);
                    Shader.SetGlobalFloat("_ssgi_fallback_direct_saturation", ssgiComp.FallbackDirectSaturation.value);
                    Shader.SetGlobalFloat("_ssgi_fallback_direct_power", ssgiComp.FallbackDirectPower.value);

                    if (settings.Quality.ApplyIndirectReflectionProbes) {
                        Shader.SetGlobalFloat("_ssgi_fallback_indirect_intensity", ssgiComp.FallbackDirectIntensity.value * settings.Fallback.FallbackIndirectIntensityMultiplier);
                        Shader.SetGlobalFloat("_ssgi_fallback_indirect_saturation", ssgiComp.FallbackDirectSaturation.value * settings.Fallback.FallbackIndirectSaturationMultiplier);
                        Shader.SetGlobalFloat("_ssgi_fallback_indirect_power", ssgiComp.FallbackDirectPower.value * settings.Fallback.FallbackIndirectPowerMultiplier);
                    }
                }
            }
        }

        private Vector4 GetRefProbeRaymarchParams(Camera camera, Vector3 center) {
            Vector3 screenPoint = camera.WorldToViewportPoint(center);

            Vector2 distToCenter = new Vector2(screenPoint.x - 0.5f, screenPoint.y - 0.5f);
            Vector2 absDistToCenter = new Vector2(Mathf.Abs(distToCenter.x), Mathf.Abs(distToCenter.y));
            if (absDistToCenter.x > 0.5f) {
                absDistToCenter.y /= screenPoint.x * 2f;
                absDistToCenter.x = 0.5f;
            }
            if (absDistToCenter.y > 0.5f) {
                absDistToCenter.x /= screenPoint.y * 2f;
                absDistToCenter.y = 0.5f;
            }
            //screenPoint = new Vector3((absDistToCenter.x + 0.5f) * Mathf.Sign(distToCenter.x), (absDistToCenter.y + 0.5f) * Mathf.Sign(distToCenter.y), screenPoint.z);
            screenPoint.x = Mathf.Clamp01(screenPoint.x);
            screenPoint.y = Mathf.Clamp01(screenPoint.y);

            if(screenPoint.z < 0f) {
                screenPoint.x = -screenPoint.x;
                screenPoint.y = 1f - screenPoint.y;
            } else {
                screenPoint.y = -screenPoint.y;
            }

            return screenPoint;
        }

        private float GetProbeSortingValue(ReflectionProbe probe, Vector3 center, Vector3 forward) {
            float distance = (probe.center - center).magnitude;
            
            //Favour bounds enveloping the camera, salted by importance and distance
            if (probe.bounds.Contains(center)) {
                return 1000000f + (probe.importance * 1000) -distance;
            }

            //Favour bounds inside expanded proximity 
            float proximitySqr = settings.Fallback.ExpandCenterProximityFactor * settings.SSGIRangeMax;
            proximitySqr *= proximitySqr;
            if ((probe.bounds.ClosestPoint(center) - center).sqrMagnitude < proximitySqr) {
                return 10000f + (probe.importance * 10) - distance;
            }

            //Probes outside of bounds, but still biased by distance
            return -distance;
        }


        private float GetTime() {
#if UNITY_EDITOR
            if (Application.isPlaying) {
                return Time.realtimeSinceStartup;
            } else {
                return (float)UnityEditor.EditorApplication.timeSinceStartup;
            }
#else
            return Time.realtimeSinceStartup;
#endif
        }

        private float GetDeltaTime() {
#if UNITY_EDITOR
            float time = GetTime();
            float deltaTime = lastEditorUpdateTimestamp == -1f ? 0f : time - lastEditorUpdateTimestamp;
            lastEditorUpdateTimestamp = time;
            return deltaTime;
#else
            return Time.deltaTime;
#endif
        }

        private void HandleSceneUnloaded(Scene scene) {
            reflectionProbeWrappers.Clear();
            RequestedObjects.Clear();
            allProbes = null;
            lastEditorUpdateTimestamp = -1f;

            //Dispose all buffered RT's
            foreach(RTWrapper wrapper in rtWrappers) {
                wrapper.Dispose();
            }
            rtWrappers.Clear();
        }


        private RTWrapper FetchBufferedRTwrapper(SSGIPassType type, Camera camera) {
            RTWrapper wrapper = rtWrappers.Find(item => item.Cam == camera && item.Type == type);
            if (wrapper == null) {
                wrapper = new RTWrapper() {
                    Cam = camera,
                    Type = type
                };
                rtWrappers.Add(wrapper);
            }

            return wrapper;
        }

        private RenderTexture GenBufferedRT(SSGIPassType type, Camera camera, RenderTextureDescriptor descriptor, CommandBuffer cmd, FilterMode filterMode) {
            //Fetch wrapper per camera
            RTWrapper wrapper = FetchBufferedRTwrapper(type, camera);

            //Tuple swap RT's
            (wrapper.RT1, wrapper.RT2) = (wrapper.RT2, wrapper.RT1);

            //Setup RT;
            if (!wrapper.RT1 || wrapper.RT1.width != descriptor.width || wrapper.RT1.height != descriptor.height) {
                if (wrapper.RT1) {
                    GameObject.DestroyImmediate(wrapper.RT1);
                }

                wrapper.RT1 = new RenderTexture(descriptor);
                string prefix = wrapper.RT2 ? "B" : "A";
                wrapper.RT1.name = $"{type.ToString()} [{prefix}]";
                wrapper.RT1.Create();
            }

            //Set nameID
            wrapper.RT1.filterMode = filterMode;
            return wrapper.RT1;
        }




        //-------------------- DEBUG --------------------
        private void CaptureDebugRTSSGI(ScriptableRenderContext context, RenderingData renderingData, bool doEncodeLightDir) {
#if UNITY_EDITOR
            if (debugRenderTextures != null && debugRenderTextures.Count > 0) {
                int colorMode = doEncodeLightDir ? 2 : 0;
                CommandBuffer cmd = CommandBufferPool.Get("MFSSGI_DebugCapture");
                foreach (KeyValuePair<RenderTexture, DebugRTData> pair in debugRenderTextures) {
                    if (pair.Value.WasHandled) { continue; }
                    if (pair.Value.Camera == renderingData.cameraData.camera) {

                        RenderTexture rt = FetchBufferedRTwrapper(SSGIPassType.SSGIColor, pair.Value.Camera).RT1;
                        if (rt) {
                            if (pair.Value.Type == SSGIPassType.SSGIColor) {
                                cmd.Blit(rt, pair.Key, SetupDebugMaterial(cmd, colorMode, 10f)); pair.Value.WasHandled = true;
                            } else if (pair.Value.Type == SSGIPassType.SSGIShadow) {
                                cmd.Blit(rt, pair.Key, SetupDebugMaterial(cmd, 1, 2f)); pair.Value.WasHandled = true;
                            } else if (pair.Value.Type == SSGIPassType.SSGILightDir) {
                                cmd.Blit(rt, pair.Key, SetupDebugMaterial(cmd, 3, 2f)); pair.Value.WasHandled = true;
                            }
                        }
                    }
                }

                context.ExecuteCommandBuffer(cmd);
                CommandBufferPool.Release(cmd);
            }
#endif
        }

        private void CaptureDebugRTOthers(ScriptableRenderContext context, RenderingData renderingData, bool doEncodeLightDir) {
#if UNITY_EDITOR
            if (debugRenderTextures != null && debugRenderTextures.Count > 0) {
                CommandBuffer cmd = CommandBufferPool.Get("MFSSGI_DebugCapture");
                int colorMode = doEncodeLightDir ? 2 : 0;

                foreach (KeyValuePair<RenderTexture, DebugRTData> pair in debugRenderTextures) {
                    if (pair.Value.Camera == renderingData.cameraData.camera) {

                        float fp = settings.SSGIRangeMax;//renderingData.cameraData.camera.farClipPlane;
                        if (pair.Value.WasHandled) { continue; }
                        RenderTexture worldPos = FetchBufferedRTwrapper(SSGIPassType.WorldPositions, pair.Value.Camera).RT1;
                        switch (pair.Value.Type) {
                            case SSGIPassType.ThicknessMaskPrePass:      cmd.Blit("_MF_SSGI_ThicknessMask_Prepass",     pair.Key, SetupDebugMaterial(cmd, 0, 10f));                 pair.Value.WasHandled = true; break;
                            case SSGIPassType.ThicknessMask:             cmd.Blit("_MF_SSGI_ThicknessMask",             pair.Key, SetupDebugMaterial(cmd, 0, 10f));                 pair.Value.WasHandled = true; break;
                            case SSGIPassType.SSGIObjects:               cmd.Blit("_MF_SSGI_SSGIObjects",               pair.Key, SetupDebugMaterial(cmd, 0, 10f, 1f, 1f, 0f));     pair.Value.WasHandled = true; break;
                            case SSGIPassType.ScreenCapture:             cmd.Blit("_MF_SSGI_ScreenCapture",             pair.Key, SetupDebugMaterial(cmd, 0, 10f));                 pair.Value.WasHandled = true; break;
                            case SSGIPassType.LightCapture:              cmd.Blit("_MF_SSGI_LightCapture",              pair.Key, SetupDebugMaterial(cmd, 0, 10f));                 pair.Value.WasHandled = true; break;
                            case SSGIPassType.WorldPositions:            cmd.Blit(worldPos,                             pair.Key, SetupDebugMaterial(cmd, 0, 1f));                  pair.Value.WasHandled = true; break;
                            case SSGIPassType.WorldPosDepth:             cmd.Blit(worldPos,                             pair.Key, SetupDebugMaterial(cmd, 1, fp));                  pair.Value.WasHandled = true; break;
                            case SSGIPassType.Normals:                   cmd.Blit("_MF_SSGI_Normals_HQ",                pair.Key, SetupDebugMaterial(cmd, 0, 1f));                  pair.Value.WasHandled = true; break;
                            case SSGIPassType.NormalsDepth:              cmd.Blit("_MF_SSGI_Normals_HQ",                pair.Key, SetupDebugMaterial(cmd, 1, fp));                  pair.Value.WasHandled = true; break;
                            case SSGIPassType.PreDenoised1Color:         cmd.Blit("_MF_SSGI_Pre_Denoised_1",            pair.Key, SetupDebugMaterial(cmd, colorMode, 1f));          pair.Value.WasHandled = true; break;
                            case SSGIPassType.PreDenoised1Shadow:        cmd.Blit("_MF_SSGI_Pre_Denoised_1",            pair.Key, SetupDebugMaterial(cmd, 1, 1f));                  pair.Value.WasHandled = true; break;
                            case SSGIPassType.PreDenoised1LightDir:      cmd.Blit("_MF_SSGI_Pre_Denoised_1",            pair.Key, SetupDebugMaterial(cmd, 3, 1f));                  pair.Value.WasHandled = true; break;
                            case SSGIPassType.PreDenoised2Color:         cmd.Blit("_MF_SSGI_Pre_Denoised_2",            pair.Key, SetupDebugMaterial(cmd, colorMode, 1f));          pair.Value.WasHandled = true; break;
                            case SSGIPassType.PreDenoised2Shadow:        cmd.Blit("_MF_SSGI_Pre_Denoised_2",            pair.Key, SetupDebugMaterial(cmd, 1, 1f));                  pair.Value.WasHandled = true; break;
                            case SSGIPassType.PreDenoised2LightDir:      cmd.Blit("_MF_SSGI_Pre_Denoised_2",            pair.Key, SetupDebugMaterial(cmd, 3, 1f));                  pair.Value.WasHandled = true; break;
                            case SSGIPassType.FinalDenoisedColor:        cmd.Blit("_MF_SSGI_Denoised_Final",            pair.Key, SetupDebugMaterial(cmd, colorMode, 1f));          pair.Value.WasHandled = true; break;
                            case SSGIPassType.FinalDenoisedShadow:       cmd.Blit("_MF_SSGI_Denoised_Final",            pair.Key, SetupDebugMaterial(cmd, 1, 1f));                  pair.Value.WasHandled = true; break;
                            case SSGIPassType.FinalDenoisedLightDir:     cmd.Blit("_MF_SSGI_Denoised_Final",            pair.Key, SetupDebugMaterial(cmd, 3, 1f));                  pair.Value.WasHandled = true; break;
                        }
                    }
                }

                context.ExecuteCommandBuffer(cmd);
                CommandBufferPool.Release(cmd);
            }
#endif
        }

        private Material SetupDebugMaterial(CommandBuffer cmd, int mode, float range, float maskR = 1f, float maskG = 1f, float maskB = 1f) {
            if (!debugBlitMaterial) {
                debugBlitMaterial = new Material(Shader.Find("MF_SSGI/DebugBlit"));
            }

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
    }
}