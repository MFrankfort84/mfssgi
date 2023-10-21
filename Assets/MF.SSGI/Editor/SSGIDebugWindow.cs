using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace MF.SSGI {
    public class SSGIDebugWindow : EditorWindow {


        //----------------------------- STATIC
        [MenuItem("Tools/SSGI/DebugWindow")]
        static void Init() {
            SSGIDebugWindow window = CreateInstance<SSGIDebugWindow>();
            window.titleContent = new GUIContent("SSGI - By Michiel Frankfort");
            window.Show();
        }


        [MenuItem("Tools/SSGI/Add SSGI to 'Always included shaders'")]
        static void SetupShaders() {
            AddAlwaysIncludedShader("MF_SSGI/ThicknessMaskBack");
            AddAlwaysIncludedShader("MF_SSGI/ThicknessMaskFront");
            AddAlwaysIncludedShader("MF_SSGI/SSGIObjects");
            AddAlwaysIncludedShader("MF_SSGI/CaptureLight");
            AddAlwaysIncludedShader("MF_SSGI/CaptureNormals");
            AddAlwaysIncludedShader("MF_SSGI/DepthToWorldPos");
            AddAlwaysIncludedShader("MF_SSGI/SSGI");
            AddAlwaysIncludedShader("MF_SSGI/Denoise");
            AddAlwaysIncludedShader("MF_SSGI/FinalBlit");
            AddAlwaysIncludedShader("MF_SSGI/ExpandVertices");
        }

        /// <summary>
        /// https://forum.unity.com/threads/modify-always-included-shaders-with-pre-processor.509479/
        /// </summary>
        public static void AddAlwaysIncludedShader(string shaderName) {
            var shader = Shader.Find(shaderName);
            if (shader == null)
                return;

            var graphicsSettingsObj = AssetDatabase.LoadAssetAtPath<GraphicsSettings>("ProjectSettings/GraphicsSettings.asset");
            var serializedObject = new SerializedObject(graphicsSettingsObj);
            var arrayProp = serializedObject.FindProperty("m_AlwaysIncludedShaders");
            bool hasShader = false;
            for (int i = 0; i < arrayProp.arraySize; ++i) {
                var arrayElem = arrayProp.GetArrayElementAtIndex(i);
                if (shader == arrayElem.objectReferenceValue) {
                    hasShader = true;
                    break;
                }
            }

            if (!hasShader) {
                int arrayIndex = arrayProp.arraySize;
                arrayProp.InsertArrayElementAtIndex(arrayIndex);
                var arrayElem = arrayProp.GetArrayElementAtIndex(arrayIndex);
                arrayElem.objectReferenceValue = shader;
                serializedObject.ApplyModifiedProperties();
                AssetDatabase.SaveAssets();
            }
        }



        //----------------------------- INSTANCE
        private RenderTexture rt;
        private Camera selectedCam;
        private int selectedCamIdx;
        private SSGIPass.SSGIPassType selectedDebugType = SSGIPass.SSGIPassType.SSGIColor;
        private SSGIPass.SSGIPassType[] allDebugTypes;
        private string[] allDebugTypeNames;
        private string[] simpleModeDebugTypeNames = new string[] {
            "ThicknessMask",
            "SSGIObjects",
            "LightCapture",
            "SSGIColor",
            "SSGILightDir",
            "SSGIShadow",
            "FinalDenoisedColor",
            "FinalDenoisedShadow",
            "FinalDenoisedLightDir",
        };
        private bool advancedMode = false;

        private void OnDisable() {
            DisposeRT();
        }

        private void CreateGUI() {
            SceneView.RepaintAll();
            SetSelectedCamera();
        }

        private void Update() {
            Repaint();
        }

        private void OnGUI() {
            DrawSelectionDropdowns();
            DrawDebugRT();
        }

        private void DisposeRT() {
            if (rt) {
                SSGIPass.RemoveDebugRT(rt);
                DestroyImmediate(rt);
                rt = null;
            }
        }

        private void DrawSelectionDropdowns() {
            GUILayout.BeginHorizontal();

            Camera sceneCam = SceneView.lastActiveSceneView?.camera;
            int plus = sceneCam ? 2 : 1;

            //Camera
            string[] camNames = new string[Camera.allCameras.Length + plus];
            camNames[0] = "None";
            for (int i = 0; i < Camera.allCameras.Length; i++) {
                camNames[i + 1] = Camera.allCameras[i].name;
            }
            if (sceneCam) {
                camNames[camNames.Length - 1] = sceneCam.name;
            }

            int oldIdx = selectedCamIdx;
            selectedCamIdx = EditorGUILayout.Popup(selectedCamIdx, camNames);
            if (oldIdx != selectedCamIdx) {
                SetSelectedCamera();
            }

            if(allDebugTypes == null) {
                allDebugTypes = (SSGIPass.SSGIPassType[])Enum.GetValues(typeof(SSGIPass.SSGIPassType));
                allDebugTypeNames = new string[allDebugTypes.Length];
                for(int i = 0; i < allDebugTypes.Length; i++) {
                    allDebugTypeNames[i] = allDebugTypes[i].ToString();
                }
            }

            SSGIPass.SSGIPassType oldType = selectedDebugType;
            if (advancedMode) {
                selectedDebugType = (SSGIPass.SSGIPassType)EditorGUILayout.Popup((int)selectedDebugType, allDebugTypeNames);
            } else {
                string selectedName = selectedDebugType.ToString();
                int idx = Array.IndexOf(simpleModeDebugTypeNames, selectedName);
                if(idx == -1) {
                    selectedName = "SSGIColor";
                    idx = Array.IndexOf(simpleModeDebugTypeNames, selectedName);
                }

                idx = EditorGUILayout.Popup(idx, simpleModeDebugTypeNames);
                selectedDebugType = (SSGIPass.SSGIPassType)Enum.Parse(typeof(SSGIPass.SSGIPassType), simpleModeDebugTypeNames[idx]);
            }
            advancedMode = EditorGUILayout.Toggle("Advanced mode", advancedMode, GUILayout.Width(175));
            GUILayout.EndHorizontal();


            //Repaint if changed
            if (oldType != selectedDebugType) {
                Repaint();
            }
        }

        private void SetSelectedCamera() {
            if (selectedCamIdx == 0) {
                selectedCam = null;
                DisposeRT();
            } else if (selectedCamIdx > Camera.allCameras.Length) {
                selectedCam = SceneView.lastActiveSceneView?.camera;
            } else {
                selectedCam = Camera.allCameras[selectedCamIdx - 1];
            }
        }

        private void DrawDebugRT() {
            if(!selectedCam) { return; }

            if(selectedCam.name != "SceneCamera" && !selectedCam.GetComponent<SSGICamera>()) {
                GUILayout.Label("Make sure the selected camera has a 'SSGICamera' component attached");
                return;
            }

            GUILayout.BeginVertical();
            Rect rect = GUILayoutUtility.GetAspectRect((float)selectedCam.pixelWidth / (float)selectedCam.pixelHeight);
            GUILayout.Box("Texture", GUILayout.Width(rect.width), GUILayout.Height(rect.height));

            if (Event.current.type == EventType.Repaint) {
                if (!rt || rt.width != rect.width || rt.height != rect.height) {
                    DisposeRT();
                    rt = new RenderTexture((int)rect.width, (int)rect.height, 16, RenderTextureFormat.DefaultHDR);
                    rt.name = $"MF_SSGI_DebugCapture_[{this.GetInstanceID()}]";
                    rt.Create();
                }
                SSGIPass.SetDebugRT(rt, selectedCam, selectedDebugType);

                if (rt && rect.width != 0 && rect.height != 0) {
                    GUI.DrawTexture(rect, rt);
                }
            }
            GUILayout.EndVertical();
        }
    }
}