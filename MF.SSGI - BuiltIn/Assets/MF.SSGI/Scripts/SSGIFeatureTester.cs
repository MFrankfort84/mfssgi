using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MF.SSGI.Demo {
    //URP-only demo helper, stubbed for the BIRP port.
    //Original toggled the URP SSGIFeature on/off with a screen-coverage wipe; rewriting it
    //against SSGIController would require reading/writing SSGIController.SSGIActive and
    //SSGIController.settings.DebugScreenCoverage. Left as TODO if needed for the demo scene.
    public class SSGIFeatureTester : MonoBehaviour {
        public int ScreenshotSuperSize = 2;

        private void Update() {
            if (Input.GetKeyUp(KeyCode.Return)) {
                SSGIController.SSGIActive = !SSGIController.SSGIActive;
            }
            if (Input.GetKeyUp(KeyCode.Tab)) {
                ScreenCapture.CaptureScreenshot(
                    $"{Application.dataPath}/MF.SSGI - {SceneManager.GetActiveScene().name} - " +
                    $"{System.DateTime.Now:yyyy-MM-dd-HH-mm-ss} - " +
                    $"{(SSGIController.SSGIActive ? "ON" : "OFF")}.png",
                    ScreenshotSuperSize);
            }
        }
    }
}
