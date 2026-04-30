using UnityEngine;

namespace MF.SSGI.Settings {

    [CreateAssetMenu(fileName = "SSGI Raymarch settings", menuName = "MF.SSGI/Raymarch Settings")]
    public class SSGIRaymarchSettings : ScriptableObject {

        [Header("Object thickness")]
        public LayerMask ThicknessMaskLayers;
        [Range(0f, 0.5f)] public float ObjectMinimalThickness = 0.1f;
        [Range(0f, 0.5f)] public float ObjectExpand = 0f;
        [Range(0f, 1f)] public float ObjectPivotToNormal = 0.25f;

        [Header("Noise reduction")]
        [Tooltip("This value 'shortens' the depth from which the ray starts, moving it closer to the camera.\n" +
            "This prevents small objects and normal-maps from creating hard edges that start to flicker")]
        [Range(0.85f, 1f)] public float RaymarchDepthBias = 0.99f;
        [Tooltip("By picking a lower mip-level to sample the center/target pixel's normal and depth, we can filter-out tiny edges and creases from casting shadow")]
        public int RaymarchNormalDepthMipLevel = 2;

        [Tooltip(
            "How far does a ray needs to be 'lifted of' the surface before its allowed to count as a hit.\n" +
            "The min-value represents the distance required when matching the surface-normal")]
        [Range(0f, 1f)] public float RaymarchSurfaceDepthBiasMin = 0.05f;
        [Tooltip(
            "How far does a ray needs to be 'lifted of' the surface before its allowed to count as a hit.\n" +
            "The max-value represents the distance required when perpendicular the surface-normal")]
        [Range(0f, 1f)] public float RaymarchSurfaceDepthBiasMax = 0.25f;
        [Range(0, 5)] public int RaymarchMinimumHitCount = 2;
        [Range(0, 16)] public int DenoiseShadowsMinHitCount = 2;
        public float RaymarchContactMinDistance = 0.01f;
        public float RaymarchCastedMinDistance = 0.5f;
    }
}