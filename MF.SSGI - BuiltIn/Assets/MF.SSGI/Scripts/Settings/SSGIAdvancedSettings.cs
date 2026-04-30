using UnityEngine;

namespace MF.SSGI.Settings {

    [CreateAssetMenu(fileName = "SSGI Quality settings", menuName = "MF.SSGI/Quality Settings")]
    public class SSGIAdvancedSettings : ScriptableObject {
        [Header("Environment scan")]
        [Tooltip(
            "When a light-source-pixel is evaluated for contribution, its depth should be smaller then target-pixel-depth * 'ScanDepthThresholdFactor'.\n" +
            "This prevents flickering when a pixel very far away (i.e. bleeding skybox distance) contributes to a pixel near camera")]
        public float ScanDepthThresholdFactor = 10;
        [Tooltip("Lower values randomly purges lots of samples near the center, increasing performance. A value of 1.0 skips the operation as 100% will of the samples will be used\n" +
            "Because of the added noise and circular pattern there are a lot more samples near the 'center' compared to the edges.\n" +
            "This value reduces the samples in the center as such detail is ofter blurred out by the denoising, saving performance.")]
        [Range(0f, 1f)] public float SSGISamplesReduction = 0.25f;


        [Space]
        [Header("Screen-space search (2D)")]
        [Tooltip(
            "A fixed percentage of the screen will be searched.\n" +
            "Note: Noise is also added to the range in 2D-space")]
        [Range(0f, 1f)] public float Search2DRange = 0.5f;
        [Tooltip("Noise added to the UV's")]
        [Range(0f, 1f)] public float Search2DNoise = 0.5f;
        [Tooltip("Pixels close to the edge will contribute less to the overall intensity to prevent bright sources from popping in/out")]
        [Range(0f, 0.5f)] public float SearchEdgeVignette = 0.1f;

        [Space]
        [Header("Near-camera cutoff")]
        [Tooltip("This cutsoff objects close/intersecting with the camera from contributing GI.\n" +
            "It prevents bright flashes in the background caused by objects very lose to the camera.\n" +
            "The contribution fades in between Near and Far. Make sure Far is always roughly 2-3x the Near-value")]
        public float CamCutoffNear = 0.25f;
        [Tooltip("This cutsoff objects close/intersecting with the camera from contributing GI.\n" +
            "It prevents bright flashes in the background caused by objects very lose to the camera.\n" +
            "The contribution fades in between Near and Far. Make sure Far is always roughly 2-3x the Near-value")]
        public float CamCutoffFar = 0.75f;

        [Space]
        [Header("Multi-frame Reprojection")]
        public bool MultiFrameApplyMultiSample = true;
        [Tooltip("If the reprojected world-position is within this margin, there is no need to re-render the GI")]
        [Range(0f, 0.2f)] public float MultiFrameDistanceThreshold = 0.1f;
        [Tooltip("This value boosts shadows where GI-gaps occure due to reprojection.\n" +
            "This is needed because backfill-shadows are rendered at a lower sample-count, therefore less intensive.\n" +
            "This reduces 'Ghosting' of shadows is tweaked correctly")]
        [Range(1f, 4f)] public float MultiFrameShadowCompensate = 2f;


        [Space]
        [Header("SSGIObject render pass")]
        [Tooltip("Disable when you want to kip SSGIObjects to be rendererd - saves a full render-pass")]
        public bool UseSSGIObjectOverrides = true;
        [Tooltip(
            "Determines from which depth-distance the Vertex-expansion will occure. Use the Tools/SSGI/DebugWindow -> Light Capture' to visualize the result" +
            "You dont want extremely large/streched objects near the camera, that's why this option exists.")]
        [Range(0f, 1f)] public float ExpandObjectsRangeFactorMin = 0.01f;
        [Tooltip(
            "Determines from which depth-distance the Vertex-expansion will occure. Use the Tools/SSGI/DebugWindow -> Light Capture' to visualize the result" +
            "You dont want extremely large/streched objects near the camera, that's why this option exists.")]
        [Range(0f, 1f)] public float ExpandObjectsRangeFactorMax = 0.025f;


        [Space]
        [Header("Denoise")]
        [Range(0f, 1f)] public float DenoiseNormalDotMin = 0.5f;
        [Range(0f, 1f)] public float DenoiseNormalDotMax = 1f;
        [Range(0f, 0.5f)] public float DenoiseDepthDiffTheshold = 0.15f;
        [Range(0f, 1f)] public float DenoiseShadowNormalContribution = 0.15f;
        [Range(0f, 1f)] public float DenoiseColorNormalContribution = 0.1f;

        [Tooltip("When the center normal is sampled, we can sample its neighbours as well, resulting in a 'blended normals' result.\n" +
            "This reduces jitter on objects with heavy normal-maps")]
        [Range(0f, 5f)] public float MultiSampleNormalsDistance = 2f;

        [Space]
        [Header("Object thickness & SSGIObject depth sorting")]
        [Range(0f, 0.2f)] public float DefaultClipDepthBias = 0.01f;


        [Space]
        [Header("Anti Aliasing: Edge detect")]
        public bool DebugAAEdgeDetect = false;
        [Tooltip(
            "DEPTH (difference): Please use 'DebugAAEdgeDetect' to debug: Keep this value as HIGH as possible. Foreach red-pixel in debug-mode, multiple normals and color pixels are sampled.\n" +
            "The fewer red-pixels you see, the higher the performance!")]
        [Range(0f, 1f)] public float AAEdgeDetectDepthThreshold = 0.15f;
        [Tooltip(
            "ANGLE (dot product): Please use 'DebugAAEdgeDetect' to debug: Keep this value as LOW as possible. Foreach red-pixel in debug-mode, multiple normals and color pixels are sampled.\n" +
            "The fewer red-pixels you see, the higher the performance!")]
        [Range(0f, 1f)] public float AAEdgeDetectDotThreshold = 0.5f;

        [Header("Anti Aliasing: Reconstruction")]
        [Tooltip("If the edge is detected, how well does the LQ and HQ normal-maps need to match to fill in the pixel")]
        [Range(0f, 1f)] public float AANormalMapMatchThreshold = 0.85f;
        [Tooltip("If the edge is detected, how far will we search for a matching pixel to fill the edge?")]
        [Range(0f, 10f)] public float FinalCompositAARange = 2;
    }
}