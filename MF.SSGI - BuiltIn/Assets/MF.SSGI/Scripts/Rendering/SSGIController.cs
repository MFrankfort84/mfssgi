using UnityEngine;
using UnityEngine.Rendering;
using System;
using MF.SSGI.Settings;

namespace MF.SSGI {
    //Built-in Render Pipeline controller. Replaces the URP SSGIFeature/SSGIPass.Setup flow.
    //Attach to any Camera that should render SSGI. Hooks a CommandBuffer at the chosen
    //CameraEvent and runs SSGIPass.Render(...) each frame.
    [ExecuteAlways]
    [RequireComponent(typeof(Camera))]
    public class SSGIController : MonoBehaviour {

        [Serializable]
        public class SSGISettings {
            [Tooltip("BIRP equivalent of URP's RenderPassEvent.BeforeRenderingTransparents.\n" +
                "BeforeImageEffectsOpaque is the standard slot for SSAO/SSR-like effects.")]
            public CameraEvent CameraEvent = CameraEvent.BeforeImageEffectsOpaque;

            [Tooltip("Forward-only on first port. Deferred GBuffer path is not yet implemented in BIRP.")]
            public bool UseDeferredRendering = false;

            [Space]
            [Header("Core settings")]
            public float SSGIRangeMin = 10;
            public float SSGIRangeMax = 25;
            public float ShadowMinDistace = 2.5f;
            public float ShadowMaxDistace = 10f;
            [Range(0.01f, 0.5f)] public float ScanDepth2DRangeFactor = 0.15f;
            [Range(0.01f, 1f)] public float Orthographic2DRangeFactor = 0.15f;

            [Space]
            [Header("Basic settings")]
            public SSGIQualitySettings Quality;
            public SSGILightingSettings Lighting;

            [Space]
            [Header("Advanced settings")]
            public SSGIAdvancedSettings Advanced;
            public SSGIRaymarchSettings Raymarch;
            public SSGIFallbackSettings Fallback;

            [Space]
            [Header("RUNTIME DEBUGGING")]
            [Range(0f, 1f)] public float DebugScreenCoverage = 1f;
            public bool DebugMotionVectors = false;
            public bool DebugReprojection = false;
            public bool DebugAlbedo = false;
        }


        public static bool ShowInSceneView = true;
        public static bool ShowToggleIconInSceneView = true;
        public static bool SSGIActive = true;


        [SerializeField] private bool showInSceneView = true;
        [SerializeField] private bool showToggleIconInSceneView = true;
        public SSGISettings settings;

        public SSGISettings Settings => settings;

        // ===== Runtime tunables (was SSGIRuntimeSettings ScriptableObject) =====
        [Header("Final composition (applies to entire screen, not just SSGI)")]
        [SerializeField, Range(0f, 1f)] private float preMultiply = 0.25f;
        [SerializeField, Range(0.5f, 2f)] private float finalContrast = 1f;
        [SerializeField, Range(0f, 10f)] private float finalIntensity = 2f;

        [Header("Light behaviour")]
        [SerializeField] private float lightFalloffDistance = 1f;
        [SerializeField] private float lightIntensity = 2f;

        [Header("Encoded light directions")]
        [SerializeField, Range(0f, 1f)] private float lightDirInfluence = 0.95f;
        [SerializeField, Range(0f, 1f)] private float normalmapBoost = 0.75f;

        [Space]
        [SerializeField, ColorUsage(false, true)] private Color giTint = Color.white;
        [SerializeField, Range(0f, 3f)] private float giContrast = 0.65f;
        [SerializeField, Range(0f, 5f)] private float giSaturation = 1f;
        [SerializeField, Range(0f, 1f)] private float giVibrance = 0.5f;

        [Header("Direct Reflection probes Fallback")]
        [SerializeField, Range(0f, 3f)] private float fallbackDirectIntensity = 0.25f;
        [SerializeField, Range(0f, 2f)] private float fallbackDirectSaturation = 1f;
        [SerializeField, Range(0.5f, 10f)] private float fallbackDirectPower = 2f;

        [Space]
        [SerializeField, Range(0f, 10f)] private float skyboxInfluence = 1f;

        [Header("Shadow composition")]
        [SerializeField, Range(0f, 10f)] private float shadowIntensity = 2.5f;
        [SerializeField, ColorUsage(false, true)] private Color shadowTint = new Color(0.06f, 0.08f, 0.12f);
        [SerializeField, Range(0.25f, 1.5f)] private float shadowExponential = 0.5f;
        [SerializeField, Range(0f, 2f)] private float shadowContrast = 0.05f;

        [Header("Contact Shadows")]
        [SerializeField, Range(0f, 5f)] private float contactShadowsRange = 0.25f;
        [SerializeField, Range(0f, 1f)] private float contactShadowsSoftKnee = 0.25f;

        [Header("Casted Shadows")]
        [SerializeField, Range(0f, 50f)] private float castedShadowsIntensity = 5f;
        [SerializeField, Range(0f, 1f)] private float castedShadowsOmniDirectional = 0.5f;
        [SerializeField, Range(0f, 100f)] private float castedShadowsRange = 15f;
        [SerializeField, Range(0f, 1f)] private float castedShadowsSoftKnee = 0.85f;

        // Public read-only accessors used by SSGIPass
        public float PreMultiply => preMultiply;
        public float FinalContrast => finalContrast;
        public float FinalIntensity => finalIntensity;
        public float LightFalloffDistance => lightFalloffDistance;
        public float LightIntensity => lightIntensity;
        public float LightDirInfluence => lightDirInfluence;
        public float NormalmapBoost => normalmapBoost;
        public Color GITint => giTint;
        public float GIContrast => giContrast;
        public float GISaturation => giSaturation;
        public float GIVibrance => giVibrance;
        public float FallbackDirectIntensity => fallbackDirectIntensity;
        public float FallbackDirectSaturation => fallbackDirectSaturation;
        public float FallbackDirectPower => fallbackDirectPower;
        public float SkyboxInfluence => skyboxInfluence;
        public float ShadowIntensity => shadowIntensity;
        public Color ShadowTint => shadowTint;
        public float ShadowExponential => shadowExponential;
        public float ShadowContrast => shadowContrast;
        public float ContactShadowsRange => contactShadowsRange;
        public float ContactShadowsSoftKnee => contactShadowsSoftKnee;
        public float CastedShadowsIntensity => castedShadowsIntensity;
        public float CastedShadowsOmniDirectional => castedShadowsOmniDirectional;
        public float CastedShadowsRange => castedShadowsRange;
        public float CastedShadowsSoftKnee => castedShadowsSoftKnee;

        private Camera cam;
        private SSGIPass pass;
        private CommandBuffer hookedBuffer;
        private CameraEvent hookedEvent;

        private void OnEnable() {
            cam = GetComponent<Camera>();
            ApplyStaticSettings();

            if (settings == null) {
                Debug.LogWarning("[SSGIController] No settings assigned - SSGI inactive.", this);
                return;
            }

            //Need motion vectors for the multiframe reprojection path
            cam.depthTextureMode |= DepthTextureMode.Depth | DepthTextureMode.DepthNormals | DepthTextureMode.MotionVectors;

            pass = new SSGIPass(settings);
            HookCommandBuffer();
        }

        private void OnDisable() {
            UnhookCommandBuffer();
            pass?.Dispose();
            pass = null;
        }

        private void HookCommandBuffer() {
            if (cam == null || pass == null) { return; }
            hookedBuffer = new CommandBuffer { name = "MF.SSGI" };
            hookedEvent = settings.CameraEvent;
            cam.AddCommandBuffer(hookedEvent, hookedBuffer);
        }

        private void UnhookCommandBuffer() {
            if (cam != null && hookedBuffer != null) {
                cam.RemoveCommandBuffer(hookedEvent, hookedBuffer);
            }
            hookedBuffer?.Dispose();
            hookedBuffer = null;
        }

        private void OnPreRender() {
            //Always clear the buffer first - if we early-out below, the buffer must be empty
            //(otherwise stale commands from the previous frame would execute against current state).
            if (hookedBuffer != null) hookedBuffer.Clear();

            if (!SSGIActive || pass == null || hookedBuffer == null) { return; }
            if (settings == null) { return; }

            //Re-hook if event changed at runtime
            if (hookedEvent != settings.CameraEvent) {
                UnhookCommandBuffer();
                HookCommandBuffer();
                if (hookedBuffer == null) return;
            }

            pass.Render(cam, hookedBuffer, this);
        }

        private void OnRenderImage(RenderTexture src, RenderTexture dst) {
            if (pass != null && pass.HasFinalComposite) {
                pass.FinalComposite(src, dst);
            } else {
                Graphics.Blit(src, dst);
            }
        }

#if UNITY_EDITOR
        private void OnValidate() {
            ApplyStaticSettings();
            UnityEditor.SceneView.RepaintAll();
        }
#endif

        private void ApplyStaticSettings() {
            ShowInSceneView = showInSceneView;
            ShowToggleIconInSceneView = showToggleIconInSceneView;
        }
    }
}
