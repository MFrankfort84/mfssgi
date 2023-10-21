using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Serialization;

namespace MF.SSGI {
    [ExecuteInEditMode]
    public class SSGIObject : MonoBehaviour {
        
        public bool RequiresSSGIMaskRendering => !castShadows || omniDirectional || emitIntensity != 1f || lightReceiveIntensity != 1f || shadowReceiveIntensity != 1f;
        public bool RequiresVertexExpandRendering => vertexExpand != Vector3.zero;

        //Serialized
        [Header("Scanning")]
        [Tooltip("When 'Start' is executed, we will wait 1 frame before we collect the renderers and apply values")]
        [SerializeField] private bool delayStartup = true;
        [Tooltip("If set to 'true', all renderers (including child objects) will be collected")]
        [SerializeField] private bool recursive = true;
        [Tooltip("If your renderers have subMeshes, please enable to make sure everything works accordingly")]
        [SerializeField] private bool hasSubMeshes = false;

        [Header("Overrides")]
        [Tooltip("Best used for self-illuminative objects\n" +
            "If false, the forward-vector is used to cast light (because it meant to reflect light) in a spotlight-like way. When true, the is cast in all directions")]
        [SerializeField] private bool omniDirectional = false;

        [Tooltip(
            "Note: If its a small object that needs to cast a lot of light, use 'VertexExpand' to grow the object (invisible in final render).\n" +
            "Open 'Tools -> SSGI -> Debug window' and use the 'LightCapture' to view how much the object has grown in size")]
        [FormerlySerializedAs("emmitIntensity")]
        [SerializeField] private float emitIntensity = 1f;
        [SerializeField] private float lightReceiveIntensity = 1f;
        [SerializeField] private float shadowReceiveIntensity = 1f;


        [Header("Vertex expansion (check 'Lightcapture' pass in Debug-window)")]
        [Tooltip(
            "Use this to reduce flickering when a small object is meant to cast a lot of light. I.e. a lightstrip.\n" +
            "Open 'Tools -> SSGI -> Debug window' and use the 'LightCapture' to view how much the object has grown in size")]
        [SerializeField] private Vector3 vertexExpand = Vector3.zero;
        [SerializeField] private Vector3 vertexOffset = Vector3.zero;
        [Tooltip("How to expand? 0 = uses normals, 1 = uses normalized vertex positions")]
        [Range(0f, 1f)] [SerializeField] private float normalVsVertexPos = 0.5f;

        [Space]
        [Header("Raymarching: (requires 'SSGIRaymarchSettings -> ThicknessMasklayer'")]
        [Tooltip("Adds thickness to the Thickness-pass, increasing the object depth/thickness")]
        [SerializeField] private float raymarchAdditionalThickness = 0f;
        [Tooltip("If an object is causing unwanted shadows, you can disable it all together")]
        [SerializeField] private bool castShadows = true;


        [Space]
        [Header("Alpha clipping/testing")]
        [Tooltip("Can be usefull to increase when dealing with animated vertices.\n" +
            "When set to -1 it will fallback to 'Advanced settings -> DefaultClipDepthBias'.\n" +
            "Above 0 it defines the margin in depth-buffer between the newly rendered SSGI-Object-geometry and the already rendered depth-buffer to be considered a 'match'.\n" +
            "If the bias is exceeded, the SSGIobject will be clipped away")]
        [SerializeField] private float clipDepthBias = -1f;

        [Header("Alpha clipping/testing")]
        [SerializeField] private bool applyAlphaClipping = false;
        [Tooltip("Empty == skipped and saves performance. You can add i.e. '_MainTex' here and the alpha will be used as a cutout")]
        [SerializeField] private List<string> alphaTestTexturePropertyNames = new List<string>(){
            "_MainTex",
            "_BaseMap"
        };
        [SerializeField] private float alphaTestCutoff = 0.5f;

        [Header("Update")]
        [Tooltip("IMPORTANT: Could cost performance as it updates every frame")]
        [SerializeField] private bool updateEveryFrame = false;


        public List<Renderer> AffectedRenderers { get => affectedRenderers; }
        public List<int> SubMeshCounts { get => subMeshCounts; }

        //Members
        private MaterialPropertyBlock block;
        private List<Renderer> affectedRenderers = new List<Renderer>();
        private List<int> subMeshCounts = new List<int>();

        private Dictionary<Renderer, Texture> alphaClipTextures = new Dictionary<Renderer, Texture>();
        private List<Material> nonAllocMaterials = new List<Material>();


        [ContextMenu("Refresh")]
        public void Refresh() {
            Initialize();
        }

        private IEnumerator Start() {
            if (delayStartup) {
                yield return null;
                HookSceneChange();
                Initialize();
            }
        }

        private void OnValidate() {
            Initialize();
        }

        private void OnEnable() {
            if (SSGIPass.RequestedObjects != null && !SSGIPass.RequestedObjects.Contains(this)) {
                SSGIPass.RequestedObjects.Add(this);
            }
            HookSceneChange();
            Initialize();
        }

        private void HookSceneChange() {
            SceneManager.sceneLoaded -= HandleSceneLoaded;
            SceneManager.sceneLoaded += HandleSceneLoaded;
        }

        private void HandleSceneLoaded(Scene arg0, LoadSceneMode arg1) {
            if (SSGIPass.RequestedObjects != null && !SSGIPass.RequestedObjects.Contains(this)) {
                SSGIPass.RequestedObjects.Add(this);
            }
            Initialize();
        }


        private void Initialize() {
            SearchRenderers();
            ScanAlphaClipTexture();
            ApplyBlock();
        }

        private void OnDisable() {
            SceneManager.sceneLoaded -= HandleSceneLoaded;

            if (SSGIPass.RequestedObjects != null) {
                SSGIPass.RequestedObjects.Remove(this);
            }

            foreach (Renderer renderer in affectedRenderers) {
                if (renderer) {
                    renderer.SetPropertyBlock(null);
                }
            }   
        }

        private void LateUpdate() {
            if (updateEveryFrame) { 
                ApplyBlock();
            }
        }

        private void SearchRenderers() {
            affectedRenderers.Clear();
            subMeshCounts.Clear();

            if (recursive) {
                foreach(Renderer renderer in this.gameObject.GetComponentsInChildren<Renderer>(true)) {
                    if(CheckScale(renderer) && renderer.GetComponentInParent<SSGIObject>() == this) {
                        AddRenderer(renderer);
                    }
                }
            } else {
                Renderer renderer = GetComponent<Renderer>();
                if (renderer && CheckScale(renderer)) {
                    AddRenderer(renderer);
                }
            }
        }

        private void AddRenderer(Renderer renderer) {
            affectedRenderers.Add(renderer);

            if (hasSubMeshes) {
                MeshFilter filter = renderer.GetComponent<MeshFilter>();
                if (filter && filter.sharedMesh) {
                    subMeshCounts.Add(filter.sharedMesh.subMeshCount);
                } else {
                    nonAllocMaterials.Clear();
                    renderer.GetMaterials(nonAllocMaterials);
                    subMeshCounts.Add(nonAllocMaterials.Count);
                }
            } else {
                subMeshCounts.Add(1);
            }
        }

        private bool CheckScale(Renderer renderer) {
            Vector3 scale = renderer.transform.lossyScale;
            return scale.x >= 0f && scale.y >= 0f && scale.z >= 0f;
        }

        private void ApplyBlock() {
            if (block == null) {
                block = new MaterialPropertyBlock();
            }

            foreach(Renderer renderer in affectedRenderers) {
                block.Clear();
                renderer.GetPropertyBlock(block);
                block.SetFloat("_SSGIOmniDirectional", omniDirectional ? -1 : 1);
                block.SetFloat("_SSGICastShadows", castShadows ? 1 : 0);
                block.SetFloat("_SSGIEmitIntensity", emitIntensity);
                block.SetFloat("_SSGILightReceiveIntensity", lightReceiveIntensity);
                block.SetFloat("_SSGIShadowReceiveIntensity", shadowReceiveIntensity);
                block.SetFloat("_SSGIAdditionalThickness", raymarchAdditionalThickness);
                block.SetVector("_SSGIVertexExpand", vertexExpand);
                block.SetVector("_SSGIVertexOffset", vertexOffset);
                block.SetFloat("_ExpandNormalVsVertexPos", normalVsVertexPos);
                block.SetFloat("_ClipDepthBias", clipDepthBias);

                //Search alpha test
                block.SetFloat("_AlphaTestCutoff", alphaTestCutoff);
                if (applyAlphaClipping && alphaTestTexturePropertyNames.Count > 0) {
                    Texture alphaTestTexture = null;
                    if (alphaClipTextures.ContainsKey(renderer)) {
                        alphaTestTexture = alphaClipTextures[renderer];
                    }

                    if (alphaTestTexture) {
                        block.SetTexture("_AlphaTestTexture", alphaTestTexture);
                    }
                }

                //Done
                renderer.SetPropertyBlock(block);
            }
        }

        private void ScanAlphaClipTexture() {
            if (!applyAlphaClipping) { return; }

            //Search alpha test
            if (alphaTestTexturePropertyNames.Count == 0) {
                return;
            }

            alphaClipTextures.Clear();
            foreach (Renderer renderer in affectedRenderers) { 
                foreach (string propertyName in alphaTestTexturePropertyNames) {
                    if (string.IsNullOrEmpty(propertyName)) { continue; }

                    Material mat = renderer.sharedMaterial;
                    if (mat && mat.HasProperty(propertyName)) {
                        try {
                            Texture tex = mat.GetTexture(propertyName);
                            if (tex) {
                                alphaClipTextures.Add(renderer, tex);
                                break;
                            }
                        } catch {
                            Debug.Log($"{mat.name} does have the property: {propertyName}, but its not a texture...");
                        }
                    }
                }
            }
        }
    }

}