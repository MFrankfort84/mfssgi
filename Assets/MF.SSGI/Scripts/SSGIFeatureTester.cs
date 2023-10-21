using System.Collections;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;


namespace MF.SSGI.Demo {
    public class SSGIFeatureTester : MonoBehaviour {

        private enum State {
            Disabled,
            EnabledHalf,
            EnabledFull
        }

        public UniversalRendererData urpRenderer;
        public int ScreenshotSuperSize = 2;

        private float lerp = 0f;
        private State state;
        private SSGIFeature feature;
        private bool isBusy = false;

        private void Awake() {
            foreach (ScriptableRendererFeature feature in urpRenderer.rendererFeatures) {
                if (feature is SSGIFeature) {
                    this.feature = feature as SSGIFeature;
                    state = feature.isActive ? State.EnabledFull : State.Disabled;
                    lerp = feature.isActive ? 1f : 0f;
                    return;
                }
            }
        }

        private void OnDestroy() {
            if (feature != null) {
                feature.SetActive(true);
                feature.Settings.DebugScreenCoverage = 1f;
            }
        }

        private void Update() {
            if (Input.GetKeyUp(KeyCode.Return)) {
                ToggleFeature();
            }

            if (Input.GetKeyUp(KeyCode.Tab)) {
                ScreenCapture.CaptureScreenshot($"{Application.dataPath}/MF.SSGI - {SceneManager.GetActiveScene().name} - {System.DateTime.Now.ToString("yyyy-MM-dd-HH-mm-ss")} - {(feature.isActive ? "ON" : "OFF")}.png", ScreenshotSuperSize);
            }
        }

        private void ToggleFeature() {
            if (isBusy) { return; }
            isBusy = true;

            StartCoroutine(HandleToggleFeature());
        }

        private IEnumerator HandleToggleFeature() {
            float targetLerp = 0f;
            State targetState = State.Disabled;

            //Activate half
            if (state == State.Disabled) {
                feature.SetActive(true);
                feature.Settings.DebugScreenCoverage = 0f;
                targetLerp = 0.5f;
                targetState = State.EnabledHalf;
            }

            //Activate full
            if (state == State.EnabledHalf) {
                targetLerp = 1f;
                targetState = State.EnabledFull;
            }

            while (lerp != targetLerp) {
                lerp = Mathf.MoveTowards(lerp, targetLerp, Time.deltaTime / 1.5f);
                feature.Settings.DebugScreenCoverage = lerp;
                yield return null;
            }

            //Done
            state = targetState;
            if (state == State.Disabled) {
                feature.SetActive(false);
            }
            isBusy = false;
        }
    }
}