
using MF.SSGI;
using UnityEditor;
using UnityEngine;

namespace MF.SSGI {
    [InitializeOnLoad]
    public class PointLabelHandlersEditor : Editor {

        private const string PATH_ICON_HOVER = "MFSSGI-SceneViewIcon-Hover";
        private const string PATH_ICON_OFF = "MFSSGI-SceneViewIcon-Off";
        private const string PATH_ICON_ON = "MFSSGI-SceneViewIcon-On";


        private static Texture2D icon_hover;
        private static Texture2D icon_off;
        private static Texture2D icon_on;

        static PointLabelHandlersEditor() {
            SceneView.duringSceneGui -= OnDuringSceneGui;
            SceneView.duringSceneGui += OnDuringSceneGui;
        }

        private static void OnDuringSceneGui(SceneView sceneView) {
            if (sceneView == null) { return; }
            if (!SSGIFeature.ShowToggleIconInSceneView) {
                SSGIFeature.SSGIActive = true;
                return;
            }
            if (!SSGIFeature.ShowInSceneView) {
                return;
            }

            if (!icon_hover) {
                icon_hover = Resources.Load<Texture2D>(PATH_ICON_HOVER);
            }
            if (!icon_off) {
                icon_off = Resources.Load<Texture2D>(PATH_ICON_OFF);
            }
            if (!icon_on) {
                icon_on = Resources.Load<Texture2D>(PATH_ICON_ON);
            }

            if (!icon_on) { return; }
            Handles.BeginGUI();

            //Get correct icon
            Texture2D icon = SSGIFeature.SSGIActive ? icon_on : icon_off;
            Rect location = new Rect(sceneView.position.width - icon_on.width - 5, sceneView.position.height - icon_on.height - 35, icon_on.width, icon_on.height);

            GUI.color = Color.white;
            GUI.DrawTexture(location, icon);

            GUI.color = Color.clear;
            if (GUI.Button(location, "")) {
                SSGIFeature.SSGIActive = !SSGIFeature.SSGIActive;
            }
            Handles.EndGUI();
        }
    }
}