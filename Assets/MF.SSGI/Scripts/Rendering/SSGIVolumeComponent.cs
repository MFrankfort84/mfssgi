using System;
using UnityEngine;
using UnityEngine.Rendering;


namespace MF.SSGI {

    [Serializable, VolumeComponentMenu("MF.SSGI")]
    public class SSGIVolumeComponent : VolumeComponent {

        [Space]
        [Header("Final composition (applies to entire screen, not just SSGI)")]
        [Tooltip("'Reshades' the final image, darkening the result. Compensate this lowering FinalContrast and increasing FinalIntensity")]
        public ClampedFloatParameter PreMultiply = new ClampedFloatParameter(0.25f, 0f, 1f);
        [Tooltip("When occlusion is high, it creates a lot more contrast and it's a lot less bright. Occluded shading looks nice, but you want to counter the additional darkness/contrast")]
        public ClampedFloatParameter FinalContrast = new ClampedFloatParameter(1f, 0.5f, 2f);
        [Tooltip("When occlusion is high, it creates a lot more contrast and it's a lot less bright. Occluded shading looks nice, but you want to counter the additional darkness/contrast")]
        public ClampedFloatParameter FinalIntensity = new ClampedFloatParameter(1.25f, 0f, 10f);

        [Space]
        [Header("Light behaviour")]
        [Tooltip("Foreach 'light distance' traveled, the light intensity is cut in half")]
        public FloatParameter LightFalloffDistance = new FloatParameter(1f);
        [Tooltip("Mutliplier to the final GI pass")]
        public FloatParameter LightIntensity = new FloatParameter(2f);
        
        [Header("Encoded light directions")]
        [Tooltip("Set to 0 will disable Light-dir encoding. Above 1 it will blend between 'flat' shading and 'lambert' shading using the encoded light-directions")]
        public ClampedFloatParameter LightDirInfluence = new ClampedFloatParameter(0.95f, 0f, 1f);
        [Tooltip("Zero = normal lambert-shading, 1 = a combination of 'Soft' and 'Hard' lambert shading (see Lighting-settings)")]
        public ClampedFloatParameter NormalmapBoost = new ClampedFloatParameter(0.75f, 0f, 1f);

        [Space]
        [Tooltip("The final tint of the GI. Usually a little blue-ish tint works best")]
        [InspectorName("GI Tint")] public ColorParameter GITint = new ColorParameter(Color.white, false, false, true);
        [Tooltip("Contrast of the GI result")]
        [InspectorName("GI Contrast")] public ClampedFloatParameter GIContrast = new ClampedFloatParameter(0.65f, 0f, 3f);
        [Tooltip("Saturation of the final GI result")]
        [InspectorName("GI Saturation")] public ClampedFloatParameter GISaturation = new ClampedFloatParameter(1f, 0f, 5f);
        [Tooltip("Higher values decrease the GI-intensity when color is closer to 'white', boosting saturated colors in brightness")]
        [InspectorName("GI Vibrance")] public ClampedFloatParameter GIVibrance = new ClampedFloatParameter(0.5f, 0f, 1f);


        [Space]
        [Header("Direct Reflection probes Fallback")]
        [Tooltip("How much light from the ReflectionProbe fallback do we want to see?")]
        public ClampedFloatParameter FallbackDirectIntensity = new ClampedFloatParameter(0.25f, 0f, 3f);
        [Tooltip("The saturation of the fallback-light from the ReflectionProbes")]
        public ClampedFloatParameter FallbackDirectSaturation = new ClampedFloatParameter(1f, 0f, 2f);
        [Tooltip("The contrast of the fallback-light from the ReflectionProbes")]
        public ClampedFloatParameter FallbackDirectPower = new ClampedFloatParameter(2f, 0.5f, 10f);


        [Space]
        [Tooltip("If a sampled lightsource-pixel is the actual skybox, how much should it contribute?")]
        public ClampedFloatParameter SkyboxInfluence = new ClampedFloatParameter(1f, 0f, 10);


        [Space]
        [Space]
        [Header("Shadow composition settings")]
        [Tooltip("0 = Disables Raymarching. Up to '1' blocks the addative light, beyond '1' it will boost shadows as a multiplier")]
        public ClampedFloatParameter ShadowIntensity = new ClampedFloatParameter(2.5f, 0f, 10f);
        [Tooltip("Tints the shadows")]
        public ColorParameter ShadowTint = new ColorParameter(new Color(0.06f, 0.08f, 0.12f), false, false, true);
        [Tooltip("Pow over the final boosted result")]
        public ClampedFloatParameter ShadowExponential = new ClampedFloatParameter(0.3f, 0.25f, 1.5f);
        [Tooltip("Subtracted from the final shadow result, sharpening the shadows")]
        public ClampedFloatParameter ShadowContrast = new ClampedFloatParameter(0.05f, 0f, 2f);

        [Space]
        [Header("Contact Shadows (use SSGI-debugger to view)")]
        [Tooltip("The distance over which a shadow-ray looses its power")]
        public ClampedFloatParameter ContactShadowsRange = new ClampedFloatParameter(0.25f, 0f, 5f);
        [Tooltip("The minimum amount of screen color comming through")]
        public ClampedFloatParameter ContactShadowsSoftKnee = new ClampedFloatParameter(0.25f, 0f, 1f);

        [Space]
        [Header("Casted Shadows (use SSGI-debugger to view)")]
        [Tooltip("The intensity the casted shadows are added to the mixture")]
        public ClampedFloatParameter CastedShadowsIntensity = new ClampedFloatParameter(10f, 0f, 50f);
        [Tooltip("Set to '0' to only allow 'bright' pixels to cast shadows. Set to '1' to have a more tradition omni-dir AO")]
        public ClampedFloatParameter CastedShadowsOmniDirectional = new ClampedFloatParameter(0.5f, 0f, 1f);
        [Tooltip("The distance over which a shadow-ray looses its power")]
        public ClampedFloatParameter CastedShadowsRange = new ClampedFloatParameter(15f, 0f, 100f);
        [Tooltip("The minimum amount of screen color comming through")]
        public ClampedFloatParameter CastedShadowsSoftKnee = new ClampedFloatParameter(0.85f, 0f, 1f);

    }
}