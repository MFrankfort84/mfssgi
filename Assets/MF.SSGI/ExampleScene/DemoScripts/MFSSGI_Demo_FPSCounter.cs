using UnityEngine;
using UnityEngine.UI;

namespace MF.SSGI.Demo {
    public class MFSSGI_Demo_FPSCounter : MonoBehaviour {

        private Text text;

        private float time = 0;
        private float lowestFPS = 999;
        private float highestFPS = 0;

        private string staticInfo;
        private string dynamicInfo1;
        private string dynamicInfo2;
        private float avFPS = 0f;
        private float avFPS1Sec = 0f;
        private float avTime1SecTime = 0f;
        private int avTime1SecFrameCount = 0;

        private void Start() {
            text = GetComponent<Text>();
        }


        private void Update() {
            time += Time.deltaTime;
            if (time >= 1f) {
                time -= 1f;

                staticInfo = $"MIN : {lowestFPS.ToString(".0")}\nMAX: {highestFPS.ToString(".0")}";
                lowestFPS = 999;
                highestFPS = 0;
            }

            float currentFSP = 1f / Time.deltaTime;
            avFPS += currentFSP;

            avFPS1Sec += currentFSP;
            avTime1SecTime += Time.deltaTime;
            avTime1SecFrameCount++;

            lowestFPS = Mathf.Min(lowestFPS, currentFSP);
            highestFPS = Mathf.Max(highestFPS, currentFSP);

            if (Time.frameCount % 10 == 0) {
                dynamicInfo1 = $"CUR: { (avFPS / 10f).ToString(".0")}";
                avFPS = 0f;
            }

            if (avTime1SecTime >= 1f) {
                dynamicInfo2 = $"AVR: {(avFPS1Sec / (float)avTime1SecFrameCount).ToString(".0")}";
                avFPS1Sec = 0f;
                avTime1SecFrameCount = 0;
                avTime1SecTime -= 1f;
            }

            text.text = $"{dynamicInfo1}\n{dynamicInfo2}\n{staticInfo}";
        }
    }
}