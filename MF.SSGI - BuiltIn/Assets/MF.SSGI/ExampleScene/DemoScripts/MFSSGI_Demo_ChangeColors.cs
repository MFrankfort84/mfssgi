using UnityEngine;

namespace MF.SSGI.Demo {
    public class MFSSGI_Demo_ChangeColors : MonoBehaviour {

        [SerializeField] private Gradient gradient;
        [SerializeField] private Renderer stripRenderer;

        [SerializeField] private float colorChangeSpeed = 1f;
        [SerializeField] private float lightDimSpeed = 0.25f;
        [SerializeField] private float lightIntensity = 4f;

        [Space]
        [SerializeField] private Transform directionalLight;
        [SerializeField] private float dirLightDirMin = -0.25f;
        [SerializeField] private float dirLightDirMax = 0.5f;

        private float gradientTime = 0f;
        private float lightDimTime = 0f;


        private void Update() {
            float angleLerp = Mathf.Max(0f, (directionalLight.transform.forward.x - dirLightDirMin) / (dirLightDirMax - dirLightDirMin));
            Color color = gradient.Evaluate(gradientTime % 1f) * angleLerp;

            gradientTime += Time.deltaTime * colorChangeSpeed;
            stripRenderer.material.SetColor("_BaseColor", gradient.Evaluate(gradientTime % 1f));


            lightDimTime += Time.deltaTime * lightDimSpeed;
            stripRenderer.material.SetColor("_EmissionColor", color * lightIntensity);
        }
    }
}