using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace MF.SSGI.Settings {

    [CreateAssetMenu(fileName = "SSGI Fallback settings", menuName = "MF.SSGI/Fallback Settings")]
    public class SSGIFallbackSettings : ScriptableObject {
        [Header("Feature pass settings")]
        public float CollectReflectionProbesInterval = 0.5f;

        [Space]
        [Header("Indirect Reflection probes Fallback (experimental)")]
        [Tooltip("Uses a multiplied value of 'FallbackDirectIntensity' to light the Light-capture pass")]
        [Range(0f, 2f)] public float FallbackIndirectIntensityMultiplier = 0.15f;
        [Tooltip("Uses a multiplied value of 'FallbackDirectSaturation' to light the Light-capture pass")]
        [Range(0f, 2f)] public float FallbackIndirectSaturationMultiplier = 1f;
        [Tooltip("Uses a multiplied value of 'FallbackDirectPower' to light the Light-capture pass")]
        [Range(0f, 2f)] public float FallbackIndirectPowerMultiplier = 1f;

        [Space]
        [Header("Shader settings")]
        [Range(0f, 1f)] public float ProbeVolumeFalloffDistance = 0.5f;
        [Range(0f, 7f)] public float ProbeSampleMipLevel = 5;
        [Range(0f, 10f)] public float ProbeRealtimeIntensity = 5;
        [Range(0f, 2f)] public float ProbeRealtimeSaturation = 1.75f;
        [Range(0f, 1f)] public float ProbeRealtimeExp = 0.35f;

        [Space]
        [Header("Sorting")]
        [Range(0f, 1f)] public float ExpandCenterProximityFactor = 0.2f;
        public float EnterExitFadeDuration = 2f;
    }
}