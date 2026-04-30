using System.Collections;
using UnityEngine;

namespace MF.SSGI.Demo {
    public class MFSSGI_Demo_SetupApplication : MonoBehaviour {

        [SerializeField] private int fps = 60;
        [SerializeField] private float renderScale = 0.8f;
        [SerializeField] private int maxResX = 1920;

        private IEnumerator Start() {
            yield return null;

            if((float)Screen.width * renderScale > maxResX) {
                Screen.SetResolution(maxResX, (int)((float)maxResX * ((float)Screen.height / (float)Screen.width)), true);
            } else {
                Screen.SetResolution((int)((float)Screen.width * renderScale), (int)((float)Screen.height * renderScale), true);
            }

            Application.targetFrameRate = fps;
            QualitySettings.vSyncCount = 0;
        }
    }
}