using System;
using UnityEngine;
using UnityEngine.Serialization;

namespace MF.SSGI.Settings {

    [CreateAssetMenu(fileName = "SSGI Basic Quality settings", menuName = "MF.SSGI/Basic Quality Settings")]
    public class SSGIQualitySettings : ScriptableObject {
        [Serializable]
        public enum AAQualityLevel {
            Off,
            Low,
            Medium,
            High
        }


        [Header("Directional light enconding")]
        [Tooltip(
            "Saves upto 10% performance by switching to half-precision HDR and skipping light-direction encoding\n." +
            "This does look less realistic and light could appear 'flat' on surfaces with normalmaps.\n" +
            "To compensate a bit the 'LightReceiveDotMax' over at the LightingSettings is doubled (with a max of 1.0)")]
        public bool UseEncodedLightDirections = true;

        [Header("Sampling settings")]
        [Tooltip("Determines how many samples per-pixel will be traced")]
        public int SSGISamplesHQ = 10;
        [Tooltip("Multi-frame-reprojection ONLY: When the reprojection fails and leaves a gap, how much samples are we willing to spend?")]
        public int SSGISamplesBackfill= 6;


        [Tooltip("This is the resolution at which the SSGI is computed")]
        [Range(0.1f, 1f)] public float SSGIRenderScale = 0.5f;
        [Tooltip("Limits the max GI resolution: Default is great performance up to HD, beyond (i.e. QHD) memory usually is the bottleneck hence the limitation")]
        public int MaxGIResolution = 1024;
        [Tooltip("This is a multiplier on the SSGIFeature 'Scan Depth 2D Range factor'.\n" +
            "Basically it allows you to 'narrow' the spread of samples, resulting in slightly less realistic light falloff, but more densly packed samples.\n" +
            "This increases image stability and shadow definition")]
        [Range(0f, 1f)] public float ScanDepthMultiplier = 1f;

        [Space]
        [Header("Multi frame reprojection")]
        public int MultiFrameCellSize = 3;
        [Tooltip("When a GI-pixel is 'recycled' we want it to be less intense over time.\n" +
            "Otherwise it keeps building up and moving emissive objects will have 'painted' the entire scene white.\n" +
            "This is why this number can never be 1. A value of i.e. 0.98 will depricate a recycled-pixel by 2% each frame.\n" +
            "Since it fully re-rendered within roughly 9-25 frames, its refreshed in time anyways")]
        [Range(0.8f, 0.999f)] public float MultiFrameEnergyFalloff = 0.98f;

        [Space]
        [Header("Shadows")]
        [Tooltip(
            "Killswitch: Raymarching requires a lot of memory throughput and shader calculations, so for mobile its best not to use it.\n" +
            "Setting the ShadowIntensity to '0' does the same thing, thats why its moved to advanced settings to prevent confusion")]
        public bool UseRaymarchedShadows = true;
        [Tooltip("Determines how many samples per sample-per-pixel will be raymarched")]
        [Range(0, 100)] public int RaymarchSamplesHQ = 40;
        [Tooltip("Multi-frame-reprojection ONLY: When the reprojection fails and leaves a gap, how much samples are we willing to spend?")]
        [Range(0, 100)] public int RaymarchSamplesBackfill = 20;
        [Tooltip("0 = Linear distribution of samples, 1 = favours the target-pixel and generates a higher density for contact-shadows")]
        [Range(0f, 1f)] public float RaymarchCubicDistanceFalloff = 0.5f;
        [Tooltip("Do we need to sample all the way from the target-pixel to the light-source? Or can we simply skip the last part?\n" +
            "In most cases we can, compressing the Samples into a shorter area, improving the quality")]
        [Range(0f, 1f)] public float RaymarchMaxRangeFactor = 0.5f;

        [Space]
        [Header("Denoise prePasses: Light distribution")]
        [Range(1, 10)] public int DenoisePasses = 7;
        [Space]
        [FormerlySerializedAs("PreDenoisePixelSizeMax")]
        [Range(0.5f, 32)] public float PreDenoisePixelSize = 20f;

        [Space]
        [Header("Compensation (to match other profiles)")]
        [Tooltip("Multiplier: As resolution lowers, less SSGI pixels are available. Each block pixel contributes to the shadow intensity, with fewer samples, intensity is turns lower")]
        public float ShadowCompensation = 1f;
        public float IntensityCompensation = 1f;

        [Space]
        [Header("Anti Aliasing")]
        [Tooltip("Determines how many environment-samples are used to fill in the jaggered edges.\n" +
            "NOTE: See 'Advanced settings' for more options")]
        public AAQualityLevel AAQuality = AAQualityLevel.Medium;

        [Space]
        [Header("Reflection probe fallback")]
        public bool ApplyIndirectReflectionProbes = true;
        [Range(0, 6)] public int MaxProbesPerPixel = 6;


        [Header("Reflection Probes: Shadows (EXPERIMENTAL)")]
        public bool ReflectionProbeFallbackDirectShadows = false;
        public bool ReflectionProbeFallbackIndirectShadows = false;
        public int ReflectionProbeFallbackRaymarchSamples = 20;
    }
}