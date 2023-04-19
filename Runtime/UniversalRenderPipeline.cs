using System;
using Unity.Collections;
using System.Collections.Generic;
#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.Rendering.Universal;
#endif
using UnityEngine.Scripting.APIUpdating;
using Lightmapping = UnityEngine.Experimental.GlobalIllumination.Lightmapping;
using UnityEngine.Experimental.Rendering;

namespace UnityEngine.Rendering.Universal {
    public sealed partial class UniversalRenderPipeline : RenderPipeline {
        
        public static float maxShadowBias {
            get => 10.0f;
        }

        public static float minRenderScale {
            get => 0.1f;
        }

        public static float maxRenderScale {
            get => 2.0f;
        }

        // Amount of Lights that can be shaded per object (in the for loop in the shader)
        public static int maxPerObjectLights {
            // No support to bitfield mask and int[] in gles2. Can't index fast more than 4 lights.
            // Check Lighting.hlsl for more details.
            get => (SystemInfo.graphicsDeviceType == GraphicsDeviceType.OpenGLES2) ? 4 : 8;
        }

        // These limits have to match same limits in Input.hlsl
        internal const int k_MaxVisibleAdditionalLightsMobileShaderLevelLessThan45 = 16;
        internal const int k_MaxVisibleAdditionalLightsMobile = 32;
        internal const int k_MaxVisibleAdditionalLightsNonMobile = 256;
        public static int maxVisibleAdditionalLights {
            get {
                // Must match: Input.hlsl, MAX_VISIBLE_LIGHTS
                bool isMobile = GraphicsSettings.HasShaderDefine(BuiltinShaderDefine.SHADER_API_MOBILE);

                if (isMobile &&
                    (SystemInfo.graphicsDeviceType == GraphicsDeviceType.OpenGLES2 ||
                     (SystemInfo.graphicsDeviceType == GraphicsDeviceType.OpenGLES3 && Graphics.minOpenGLESVersion <= OpenGLESVersion.OpenGLES30)))
                    return k_MaxVisibleAdditionalLightsMobileShaderLevelLessThan45;

                // GLES can be selected as platform on Windows (not a mobile platform) but uniform buffer size so we must use a low light count.
                return (isMobile ||
                        SystemInfo.graphicsDeviceType == GraphicsDeviceType.OpenGLCore ||
                        SystemInfo.graphicsDeviceType == GraphicsDeviceType.OpenGLES2 ||
                        SystemInfo.graphicsDeviceType == GraphicsDeviceType.OpenGLES3) ? k_MaxVisibleAdditionalLightsMobile : k_MaxVisibleAdditionalLightsNonMobile;
            }
        }

        // Match with values in Input.hlsl
        internal static int lightsPerTile => ((maxVisibleAdditionalLights + 31) / 32) * 32;
        internal static int maxZBins => 1024 * 4;
        internal static int maxTileVec4s => 4096;

        internal const int k_DefaultRenderingLayerMask = 0x00000001;
        private readonly DebugDisplaySettingsUI m_DebugDisplaySettingsUI = new DebugDisplaySettingsUI();

        private UniversalRenderPipelineGlobalSettings m_GlobalSettings;
        public override RenderPipelineGlobalSettings defaultSettings => m_GlobalSettings;
        private readonly UniversalRenderPipelineAsset pipelineAsset;

        /// <inheritdoc/>
        public override string ToString() => pipelineAsset?.ToString();

        public UniversalRenderPipeline(UniversalRenderPipelineAsset asset) {
            pipelineAsset = asset;
        #if UNITY_EDITOR
            m_GlobalSettings = UniversalRenderPipelineGlobalSettings.Ensure();
        #else
            m_GlobalSettings = UniversalRenderPipelineGlobalSettings.instance;
        #endif
            SetSupportedRenderingFeatures();

            // In QualitySettings.antiAliasing disabled state uses value 0, where in URP 1
            int qualitySettingsMsaaSampleCount = QualitySettings.antiAliasing > 0 ? QualitySettings.antiAliasing : 1;
            bool msaaSampleCountNeedsUpdate = qualitySettingsMsaaSampleCount != asset.msaaSampleCount;

            // Let engine know we have MSAA on for cases where we support MSAA backbuffer
            if (msaaSampleCountNeedsUpdate) {
                QualitySettings.antiAliasing = asset.msaaSampleCount;
            }

            Shader.globalRenderPipeline = "UniversalPipeline";

            Lightmapping.SetDelegate(lightsDelegate);

            CameraCaptureBridge.enabled = true;

            RenderingUtils.ClearSystemInfoCache();

            DecalProjector.defaultMaterial = asset.decalMaterial;

            DebugManager.instance.RefreshEditor();
            m_DebugDisplaySettingsUI.RegisterDebug(DebugDisplaySettings.Instance);
        }

        protected override void Dispose(bool disposing) {
            m_DebugDisplaySettingsUI.UnregisterDebug();

            base.Dispose(disposing);

            pipelineAsset.DestroyRenderers();

            Shader.globalRenderPipeline = "";

            SupportedRenderingFeatures.active = new SupportedRenderingFeatures();
            ShaderData.instance.Dispose();
            DeferredShaderData.instance.Dispose();

        #if UNITY_EDITOR
            SceneViewDrawMode.ResetDrawMode();
        #endif
            Lightmapping.ResetDelegate();
            CameraCaptureBridge.enabled = false;
        }

        protected override void Render(ScriptableRenderContext renderContext, Camera[] cameras) {
            Render(renderContext, new List<Camera>(cameras));
        }

        protected override void Render(ScriptableRenderContext renderContext, List<Camera> cameras) {
            BeginContextRendering(renderContext, cameras);

            GraphicsSettings.lightsUseLinearIntensity = (QualitySettings.activeColorSpace == ColorSpace.Linear);
            GraphicsSettings.lightsUseColorTemperature = true;
            GraphicsSettings.useScriptableRenderPipelineBatching = asset.useSRPBatcher;
            GraphicsSettings.defaultRenderingLayerMask = k_DefaultRenderingLayerMask;
            SetupPerFrameShaderConstants();

        #if UNITY_EDITOR
            if (m_GlobalSettings == null || UniversalRenderPipelineGlobalSettings.instance == null) {
                m_GlobalSettings = UniversalRenderPipelineGlobalSettings.Ensure();

                if (m_GlobalSettings == null) return;
            }
        #endif

            SortCameras(cameras);
        #if UNITY_2021_1_OR_NEWER
            for (int i = 0; i < cameras.Count; ++i)
        #else
            for (int i = 0; i < cameras.Length; ++i)
        #endif
            {
                var camera = cameras[i];

                if (IsGameCamera(camera)) {
                    RenderCameraStack(renderContext, camera);
                } else {
                    BeginCameraRendering(renderContext, camera);

                #if VISUAL_EFFECT_GRAPH_0_0_1_OR_NEWER
                    //It should be called before culling to prepare material. When there isn't any VisualEffect component, this method has no effect.
                    VFX.VFXManager.PrepareCamera(camera);
                #endif
                    UpdateVolumeFramework(camera, null);
                    RenderSingleCamera(renderContext, camera);
                    EndCameraRendering(renderContext, camera);
                }
            }
            EndContextRendering(renderContext, cameras);
        }

        public static void RenderSingleCamera(ScriptableRenderContext context, Camera camera) {
            UniversalAdditionalCameraData additionalCameraData = null;
            if (IsGameCamera(camera))
                camera.gameObject.TryGetComponent(out additionalCameraData);

            if (additionalCameraData != null && additionalCameraData.renderType != CameraRenderType.Base) {
                return;
            }

            InitializeCameraData(camera, additionalCameraData, true, out var cameraData);
            RenderSingleCamera(context, cameraData, cameraData.postProcessEnabled);
        }

        static bool TryGetCullingParameters(CameraData cameraData, out ScriptableCullingParameters cullingParams) {
            return cameraData.camera.TryGetCullingParameters(false, out cullingParams);
        }

        static void RenderSingleCamera(ScriptableRenderContext context, CameraData cameraData, bool anyPostProcessingEnabled) {
            Camera camera = cameraData.camera;
            var renderer = cameraData.renderer;

            if (renderer == null) {
                return;
            }

            if (!TryGetCullingParameters(cameraData, out var cullingParameters))
                return;

            ScriptableRenderer.current = renderer;
            bool isSceneViewCamera = cameraData.isSceneViewCamera;

            CommandBuffer cmd = CommandBufferPool.Get();
            
            renderer.Clear(cameraData.renderType);
            renderer.OnPreCullRenderPasses(in cameraData);
            renderer.SetupCullingParameters(ref cullingParameters, ref cameraData);

            context.ExecuteCommandBuffer(cmd); // Send all the commands enqueued so far in the CommandBuffer cmd, to the ScriptableRenderContext context
            cmd.Clear();

        #if UNITY_EDITOR

            // Emit scene view UI
            if (isSceneViewCamera) {
                ScriptableRenderContext.EmitWorldGeometryForSceneView(camera);
            }
        #endif

            var cullResults = context.Cull(ref cullingParameters);
            InitializeRenderingData(asset, ref cameraData, ref cullResults, anyPostProcessingEnabled, out var renderingData);
            
            renderer.Setup(context, ref renderingData);

            // Timing scope inside
            renderer.Execute(context, ref renderingData);
            CleanupLightData(ref renderingData.lightData);

            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
            
            if (renderer.useRenderPassEnabled && !context.SubmitForRenderPassValidation()) {
                renderer.useRenderPassEnabled = false;
                CoreUtils.SetKeyword(cmd, ShaderKeywordStrings.RenderPassEnabled, false);
            }

            context.Submit();
            ScriptableRenderer.current = null;
        }

        static void RenderCameraStack(ScriptableRenderContext context, Camera baseCamera) {
            baseCamera.TryGetComponent<UniversalAdditionalCameraData>(out var baseCameraAdditionalData);

            if (baseCameraAdditionalData != null && baseCameraAdditionalData.renderType == CameraRenderType.Overlay)
                return;

            var renderer = baseCameraAdditionalData?.scriptableRenderer;
            bool supportsCameraStacking = renderer != null && renderer.SupportsCameraStackingType(CameraRenderType.Base);
            List<Camera> cameraStack = (supportsCameraStacking) ? baseCameraAdditionalData?.cameraStack : null;

            bool anyPostProcessingEnabled = baseCameraAdditionalData != null && baseCameraAdditionalData.renderPostProcessing;

            int lastActiveOverlayCameraIndex = -1;

            if (cameraStack != null) {
                var baseCameraRendererType = baseCameraAdditionalData?.scriptableRenderer.GetType();
                bool shouldUpdateCameraStack = false;

                for (int i = 0; i < cameraStack.Count; ++i) {
                    Camera currCamera = cameraStack[i];

                    if (currCamera == null) {
                        shouldUpdateCameraStack = true;

                        continue;
                    }

                    if (currCamera.isActiveAndEnabled) {
                        currCamera.TryGetComponent<UniversalAdditionalCameraData>(out var data);

                        var currCameraRendererType = data?.scriptableRenderer.GetType();

                        if (currCameraRendererType != baseCameraRendererType) {
                            continue;
                        }

                        var overlayRenderer = data.scriptableRenderer;

                        if ((overlayRenderer.SupportedCameraStackingTypes() & 1 << (int)CameraRenderType.Overlay) == 0) {
                            continue;
                        }

                        if (data == null || data.renderType != CameraRenderType.Overlay) {
                            continue;
                        }

                        anyPostProcessingEnabled |= data.renderPostProcessing;
                        lastActiveOverlayCameraIndex = i;
                    }
                }

                if (shouldUpdateCameraStack) {
                    baseCameraAdditionalData.UpdateCameraStack();
                }
            }

            anyPostProcessingEnabled &= SystemInfo.graphicsDeviceType != GraphicsDeviceType.OpenGLES2;

            bool isStackedRendering = lastActiveOverlayCameraIndex != -1;

            BeginCameraRendering(context, baseCamera);

            UpdateVolumeFramework(baseCamera, baseCameraAdditionalData);
            InitializeCameraData(baseCamera, baseCameraAdditionalData, !isStackedRendering, out var baseCameraData);

            RenderSingleCamera(context, baseCameraData, anyPostProcessingEnabled);

            EndCameraRendering(context, baseCamera);

            if (isStackedRendering) {
                for (int i = 0; i < cameraStack.Count; ++i) {
                    var currCamera = cameraStack[i];

                    if (!currCamera.isActiveAndEnabled)
                        continue;

                    currCamera.TryGetComponent<UniversalAdditionalCameraData>(out var currCameraData);
                    
                    if (currCameraData != null) {
                        CameraData overlayCameraData = baseCameraData;
                        bool lastCamera = i == lastActiveOverlayCameraIndex;

                        BeginCameraRendering(context, currCamera);

                        UpdateVolumeFramework(currCamera, currCameraData);
                        InitializeAdditionalCameraData(currCamera, currCameraData, lastCamera, ref overlayCameraData);

                        RenderSingleCamera(context, overlayCameraData, anyPostProcessingEnabled);

                        EndCameraRendering(context, currCamera);
                    }
                }
            }
        }

        static void UpdateVolumeFramework(Camera camera, UniversalAdditionalCameraData additionalCameraData) {
            bool shouldUpdate = camera.cameraType == CameraType.SceneView;
            shouldUpdate |= additionalCameraData != null && additionalCameraData.requiresVolumeFrameworkUpdate;

        #if UNITY_EDITOR
            shouldUpdate |= Application.isPlaying == false;
        #endif

            // When we have volume updates per-frame disabled...
            if (!shouldUpdate && additionalCameraData) {
                // Create a local volume stack and cache the state if it's null
                if (additionalCameraData.volumeStack == null) {
                    camera.UpdateVolumeStack(additionalCameraData);
                }

                VolumeManager.instance.stack = additionalCameraData.volumeStack;

                return;
            }

            // When we want to update the volumes every frame...

            // We destroy the volumeStack in the additional camera data, if present, to make sure
            // it gets recreated and initialized if the update mode gets later changed to ViaScripting...
            if (additionalCameraData && additionalCameraData.volumeStack != null) {
                camera.DestroyVolumeStack(additionalCameraData);
            }

            // Get the mask + trigger and update the stack
            camera.GetVolumeLayerMaskAndTrigger(additionalCameraData, out LayerMask layerMask, out Transform trigger);
            VolumeManager.instance.ResetMainStack();
            VolumeManager.instance.Update(trigger, layerMask);
        }

        static bool CheckPostProcessForDepth(in CameraData cameraData) {
            if (!cameraData.postProcessEnabled)
                return false;

            if (cameraData.antialiasing == AntialiasingMode.SubpixelMorphologicalAntiAliasing)
                return true;

            var stack = VolumeManager.instance.stack;

            if (stack.GetComponent<DepthOfField>().IsActive())
                return true;

            if (stack.GetComponent<MotionBlur>().IsActive())
                return true;

            return false;
        }

        static void SetSupportedRenderingFeatures() {
        #if UNITY_EDITOR
            SupportedRenderingFeatures.active = new SupportedRenderingFeatures()
                                                { reflectionProbeModes = SupportedRenderingFeatures.ReflectionProbeModes.None,
                                                  defaultMixedLightingModes = SupportedRenderingFeatures.LightmapMixedBakeModes.Subtractive,
                                                  mixedLightingModes =
                                                          SupportedRenderingFeatures.LightmapMixedBakeModes.Subtractive |
                                                          SupportedRenderingFeatures.LightmapMixedBakeModes.IndirectOnly |
                                                          SupportedRenderingFeatures.LightmapMixedBakeModes.Shadowmask,
                                                  lightmapBakeTypes = LightmapBakeType.Baked | LightmapBakeType.Mixed | LightmapBakeType.Realtime,
                                                  lightmapsModes = LightmapsMode.CombinedDirectional | LightmapsMode.NonDirectional,
                                                  lightProbeProxyVolumes = false,
                                                  motionVectors = false,
                                                  receiveShadows = false,
                                                  reflectionProbes = false,
                                                  reflectionProbesBlendDistance = true,
                                                  particleSystemInstancing = true };

            SceneViewDrawMode.SetupDrawMode();
        #endif
        }

        static void InitializeCameraData(Camera camera, UniversalAdditionalCameraData additionalCameraData, bool resolveFinalTarget, out CameraData cameraData) {
            cameraData = new CameraData();
            InitializeStackedCameraData(camera, additionalCameraData, ref cameraData);
            InitializeAdditionalCameraData(camera, additionalCameraData, resolveFinalTarget, ref cameraData);

            var renderer = additionalCameraData?.scriptableRenderer;
            bool rendererSupportsMSAA = renderer != null && renderer.supportedRenderingFeatures.msaa;

            int msaaSamples = 1;
            if (camera.allowMSAA && asset.msaaSampleCount > 1 && rendererSupportsMSAA)
                msaaSamples = (camera.targetTexture != null) ? camera.targetTexture.antiAliasing : asset.msaaSampleCount;
            bool needsAlphaChannel = Graphics.preserveFramebufferAlpha;
            cameraData.cameraTargetDescriptor = CreateRenderTextureDescriptor(camera, cameraData.renderScale, cameraData.isHdrEnabled, msaaSamples, needsAlphaChannel,
                    cameraData.requiresOpaqueTexture);
        }

        static void InitializeStackedCameraData(Camera baseCamera, UniversalAdditionalCameraData baseAdditionalCameraData, ref CameraData cameraData) {
            var settings = asset;
            cameraData.targetTexture = baseCamera.targetTexture;
            cameraData.cameraType = baseCamera.cameraType;
            bool isSceneViewCamera = cameraData.isSceneViewCamera;

            if (isSceneViewCamera) {
                cameraData.volumeLayerMask = 1; // "Default"
                cameraData.volumeTrigger = null;
                cameraData.isStopNaNEnabled = false;
                cameraData.isDitheringEnabled = false;
                cameraData.antialiasing = AntialiasingMode.None;
                cameraData.antialiasingQuality = AntialiasingQuality.High;
            } else if (baseAdditionalCameraData != null) {
                cameraData.volumeLayerMask = baseAdditionalCameraData.volumeLayerMask;
                cameraData.volumeTrigger = baseAdditionalCameraData.volumeTrigger == null ? baseCamera.transform : baseAdditionalCameraData.volumeTrigger;
                cameraData.isStopNaNEnabled = baseAdditionalCameraData.stopNaN && SystemInfo.graphicsShaderLevel >= 35;
                cameraData.isDitheringEnabled = baseAdditionalCameraData.dithering;
                cameraData.antialiasing = baseAdditionalCameraData.antialiasing;
                cameraData.antialiasingQuality = baseAdditionalCameraData.antialiasingQuality;
            } else {
                cameraData.volumeLayerMask = 1; // "Default"
                cameraData.volumeTrigger = null;
                cameraData.isStopNaNEnabled = false;
                cameraData.isDitheringEnabled = false;
                cameraData.antialiasing = AntialiasingMode.None;
                cameraData.antialiasingQuality = AntialiasingQuality.High;
            }

            cameraData.isHdrEnabled = baseCamera.allowHDR && settings.supportsHDR;

            Rect cameraRect = baseCamera.rect;
            cameraData.pixelRect = baseCamera.pixelRect;
            cameraData.pixelWidth = baseCamera.pixelWidth;
            cameraData.pixelHeight = baseCamera.pixelHeight;
            cameraData.aspectRatio = (float)cameraData.pixelWidth / (float)cameraData.pixelHeight;
            cameraData.isDefaultViewport =
                    (!(Math.Abs(cameraRect.x) > 0.0f || Math.Abs(cameraRect.y) > 0.0f || Math.Abs(cameraRect.width) < 1.0f || Math.Abs(cameraRect.height) < 1.0f));

            // Discard variations lesser than kRenderScaleThreshold.
            // Scale is only enabled for gameview.
            const float kRenderScaleThreshold = 0.05f;
            cameraData.renderScale = (Mathf.Abs(1.0f - settings.renderScale) < kRenderScaleThreshold) ? 1.0f : settings.renderScale;

            // Convert the upscaling filter selection from the pipeline asset into an image upscaling filter
            cameraData.upscalingFilter =
                    ResolveUpscalingFilterSelection(new Vector2(cameraData.pixelWidth, cameraData.pixelHeight), cameraData.renderScale, settings.upscalingFilter);

            if (cameraData.renderScale > 1.0f) {
                cameraData.imageScalingMode = ImageScalingMode.Downscaling;
            } else if ((cameraData.renderScale < 1.0f) || (cameraData.upscalingFilter == ImageUpscalingFilter.FSR)) {
                // When FSR is enabled, we still consider 100% render scale an upscaling operation.
                // This allows us to run the FSR shader passes all the time since they improve visual quality even at 100% scale.

                cameraData.imageScalingMode = ImageScalingMode.Upscaling;
            } else {
                cameraData.imageScalingMode = ImageScalingMode.None;
            }

            cameraData.fsrOverrideSharpness = settings.fsrOverrideSharpness;
            cameraData.fsrSharpness = settings.fsrSharpness;
            
            var commonOpaqueFlags = SortingCriteria.CommonOpaque;
            var noFrontToBackOpaqueFlags = SortingCriteria.SortingLayer | SortingCriteria.RenderQueue | SortingCriteria.OptimizeStateChanges | SortingCriteria.CanvasOrder;
            bool hasHSRGPU = SystemInfo.hasHiddenSurfaceRemovalOnGPU;
            bool canSkipFrontToBackSorting = (baseCamera.opaqueSortMode == OpaqueSortMode.Default && hasHSRGPU) || baseCamera.opaqueSortMode == OpaqueSortMode.NoDistanceSort;

            cameraData.defaultOpaqueSortFlags = canSkipFrontToBackSorting ? noFrontToBackOpaqueFlags : commonOpaqueFlags;
            cameraData.captureActions = CameraCaptureBridge.GetCaptureActions(baseCamera);
        }

        /// <summary>
        /// Initialize settings that can be different for each camera in the stack.
        /// </summary>
        /// <param name="camera">Camera to initialize settings from.</param>
        /// <param name="additionalCameraData">Additional camera data component to initialize settings from.</param>
        /// <param name="resolveFinalTarget">True if this is the last camera in the stack and rendering should resolve to camera target.</param>
        /// <param name="cameraData">Settings to be initilized.</param>
        static void InitializeAdditionalCameraData(Camera camera, UniversalAdditionalCameraData additionalCameraData, bool resolveFinalTarget, ref CameraData cameraData) {
            var settings = asset;
            cameraData.camera = camera;

            bool anyShadowsEnabled = settings.supportsMainLightShadows || settings.supportsAdditionalLightShadows;
            cameraData.maxShadowDistance = Mathf.Min(settings.shadowDistance, camera.farClipPlane);
            cameraData.maxShadowDistance = (anyShadowsEnabled && cameraData.maxShadowDistance >= camera.nearClipPlane) ? cameraData.maxShadowDistance : 0.0f;

            // Getting the background color from preferences to add to the preview camera
        #if UNITY_EDITOR
            if (cameraData.camera.cameraType == CameraType.Preview) {
                camera.backgroundColor = CoreRenderPipelinePreferences.previewBackgroundColor;
            }
        #endif

            bool isSceneViewCamera = cameraData.isSceneViewCamera;

            if (isSceneViewCamera) {
                cameraData.renderType = CameraRenderType.Base;
                cameraData.clearDepth = true;
                cameraData.postProcessEnabled = CoreUtils.ArePostProcessesEnabled(camera);
                cameraData.requiresDepthTexture = settings.supportsCameraDepthTexture;
                cameraData.requiresOpaqueTexture = settings.supportsCameraOpaqueTexture;
                cameraData.renderer = asset.scriptableRenderer;
            } else if (additionalCameraData != null) {
                cameraData.renderType = additionalCameraData.renderType;
                cameraData.clearDepth = (additionalCameraData.renderType != CameraRenderType.Base) ? additionalCameraData.clearDepth : true;
                cameraData.postProcessEnabled = additionalCameraData.renderPostProcessing;
                cameraData.maxShadowDistance = (additionalCameraData.renderShadows) ? cameraData.maxShadowDistance : 0.0f;
                cameraData.requiresDepthTexture = additionalCameraData.requiresDepthTexture;
                cameraData.requiresOpaqueTexture = additionalCameraData.requiresColorTexture;
                cameraData.renderer = additionalCameraData.scriptableRenderer;
            } else {
                cameraData.renderType = CameraRenderType.Base;
                cameraData.clearDepth = true;
                cameraData.postProcessEnabled = false;
                cameraData.requiresDepthTexture = settings.supportsCameraDepthTexture;
                cameraData.requiresOpaqueTexture = settings.supportsCameraOpaqueTexture;
                cameraData.renderer = asset.scriptableRenderer;
            }

            // Disables post if GLes2
            cameraData.postProcessEnabled &= SystemInfo.graphicsDeviceType != GraphicsDeviceType.OpenGLES2;

            cameraData.requiresDepthTexture |= isSceneViewCamera;
            cameraData.postProcessingRequiresDepthTexture |= CheckPostProcessForDepth(cameraData);
            cameraData.resolveFinalTarget = resolveFinalTarget;

            // Disable depth and color copy. We should add it in the renderer instead to avoid performance pitfalls
            // of camera stacking breaking render pass execution implicitly.
            bool isOverlayCamera = (cameraData.renderType == CameraRenderType.Overlay);

            if (isOverlayCamera) {
                cameraData.requiresOpaqueTexture = false;
                cameraData.postProcessingRequiresDepthTexture = false;
            }

            Matrix4x4 projectionMatrix = camera.projectionMatrix;

            // Overlay cameras inherit viewport from base.
            // If the viewport is different between them we might need to patch the projection to adjust aspect ratio
            // matrix to prevent squishing when rendering objects in overlay cameras.
            if (isOverlayCamera && !camera.orthographic && cameraData.pixelRect != camera.pixelRect) {
                // m00 = (cotangent / aspect), therefore m00 * aspect gives us cotangent.
                float cotangent = camera.projectionMatrix.m00 * camera.aspect;

                // Get new m00 by dividing by base camera aspectRatio.
                float newCotangent = cotangent / cameraData.aspectRatio;
                projectionMatrix.m00 = newCotangent;
            }

            cameraData.SetViewAndProjectionMatrix(camera.worldToCameraMatrix, projectionMatrix);

            cameraData.worldSpaceCameraPos = camera.transform.position;
        }

        static void InitializeRenderingData(UniversalRenderPipelineAsset settings, ref CameraData cameraData, ref CullingResults cullResults, bool anyPostProcessingEnabled,
                                            out RenderingData renderingData) {
            var visibleLights = cullResults.visibleLights;

            int mainLightIndex = GetMainLightIndex(settings, visibleLights);
            bool mainLightCastShadows = false;
            bool additionalLightsCastShadows = false;

            if (cameraData.maxShadowDistance > 0.0f) {
                mainLightCastShadows = (mainLightIndex != -1 && visibleLights[mainLightIndex].light != null && visibleLights[mainLightIndex].light.shadows != LightShadows.None);

                // If additional lights are shaded per-pixel they cannot cast shadows
                if (settings.additionalLightsRenderingMode == LightRenderingMode.PerPixel) {
                    for (int i = 0; i < visibleLights.Length; ++i) {
                        if (i == mainLightIndex)
                            continue;

                        Light light = visibleLights[i].light;

                        // UniversalRP doesn't support additional directional light shadows yet
                        if ((visibleLights[i].lightType == LightType.Spot || visibleLights[i].lightType == LightType.Point) &&
                            light != null &&
                            light.shadows != LightShadows.None) {
                            additionalLightsCastShadows = true;

                            break;
                        }
                    }
                }
            }

            renderingData.cullResults = cullResults;
            renderingData.cameraData = cameraData;
            InitializeLightData(settings, visibleLights, mainLightIndex, out renderingData.lightData);
            InitializeShadowData(settings, visibleLights, mainLightCastShadows, additionalLightsCastShadows && !renderingData.lightData.shadeAdditionalLightsPerVertex,
                    out renderingData.shadowData);

            InitializePostProcessingData(settings, out renderingData.postProcessingData);
            renderingData.supportsDynamicBatching = settings.supportsDynamicBatching;
            renderingData.perObjectData = GetPerObjectLightFlags(renderingData.lightData.additionalLightsCount);
            renderingData.postProcessingEnabled = anyPostProcessingEnabled;

            CheckAndApplyDebugSettings(ref renderingData);
        }

        static void InitializeShadowData(UniversalRenderPipelineAsset settings, NativeArray<VisibleLight> visibleLights, bool mainLightCastShadows,
                                         bool additionalLightsCastShadows, out ShadowData shadowData) {
            m_ShadowBiasData.Clear();
            m_ShadowResolutionData.Clear();

            for (int i = 0; i < visibleLights.Length; ++i) {
                Light light = visibleLights[i].light;
                UniversalAdditionalLightData data = null;

                if (light != null) {
                    light.gameObject.TryGetComponent(out data);
                }

                if (data && !data.usePipelineSettings)
                    m_ShadowBiasData.Add(new Vector4(light.shadowBias, light.shadowNormalBias, 0.0f, 0.0f));
                else
                    m_ShadowBiasData.Add(new Vector4(settings.shadowDepthBias, settings.shadowNormalBias, 0.0f, 0.0f));

                if (data && (data.additionalLightsShadowResolutionTier == UniversalAdditionalLightData.AdditionalLightsShadowResolutionTierCustom)) {
                    m_ShadowResolutionData.Add((int)light.shadowResolution); // native code does not clamp light.shadowResolution between -1 and 3
                } else if (data && (data.additionalLightsShadowResolutionTier != UniversalAdditionalLightData.AdditionalLightsShadowResolutionTierCustom)) {
                    int resolutionTier = Mathf.Clamp(data.additionalLightsShadowResolutionTier, UniversalAdditionalLightData.AdditionalLightsShadowResolutionTierLow,
                            UniversalAdditionalLightData.AdditionalLightsShadowResolutionTierHigh);

                    m_ShadowResolutionData.Add(settings.GetAdditionalLightsShadowResolution(resolutionTier));
                } else {
                    m_ShadowResolutionData.Add(settings.GetAdditionalLightsShadowResolution(UniversalAdditionalLightData.AdditionalLightsShadowDefaultResolutionTier));
                }
            }

            shadowData.bias = m_ShadowBiasData;
            shadowData.resolution = m_ShadowResolutionData;
            shadowData.supportsMainLightShadows = SystemInfo.supportsShadows && settings.supportsMainLightShadows && mainLightCastShadows;

            // We no longer use screen space shadows in URP.
            // This change allows us to have particles & transparent objects receive shadows.
        #pragma warning disable 0618
            shadowData.requiresScreenSpaceShadowResolve = false;
        #pragma warning restore 0618

            shadowData.mainLightShadowCascadesCount = settings.shadowCascadeCount;
            shadowData.mainLightShadowmapWidth = settings.mainLightShadowmapResolution;
            shadowData.mainLightShadowmapHeight = settings.mainLightShadowmapResolution;

            switch (shadowData.mainLightShadowCascadesCount) {
                case 1:
                    shadowData.mainLightShadowCascadesSplit = new Vector3(1.0f, 0.0f, 0.0f);

                    break;

                case 2:
                    shadowData.mainLightShadowCascadesSplit = new Vector3(settings.cascade2Split, 1.0f, 0.0f);

                    break;

                case 3:
                    shadowData.mainLightShadowCascadesSplit = new Vector3(settings.cascade3Split.x, settings.cascade3Split.y, 0.0f);

                    break;

                default:
                    shadowData.mainLightShadowCascadesSplit = settings.cascade4Split;

                    break;
            }

            shadowData.mainLightShadowCascadeBorder = settings.cascadeBorder;

            shadowData.supportsAdditionalLightShadows = SystemInfo.supportsShadows && settings.supportsAdditionalLightShadows && additionalLightsCastShadows;
            shadowData.additionalLightsShadowmapWidth = shadowData.additionalLightsShadowmapHeight = settings.additionalLightsShadowmapResolution;
            shadowData.supportsSoftShadows = settings.supportsSoftShadows && (shadowData.supportsMainLightShadows || shadowData.supportsAdditionalLightShadows);
            shadowData.shadowmapDepthBufferBits = 16;

            // This will be setup in AdditionalLightsShadowCasterPass.
            shadowData.isKeywordAdditionalLightShadowsEnabled = false;
            shadowData.isKeywordSoftShadowsEnabled = false;
        }

        static void InitializePostProcessingData(UniversalRenderPipelineAsset settings, out PostProcessingData postProcessingData) {
            postProcessingData.gradingMode = settings.supportsHDR ? settings.colorGradingMode : ColorGradingMode.LowDynamicRange;

            postProcessingData.lutSize = settings.colorGradingLutSize;
            postProcessingData.useFastSRGBLinearConversion = settings.useFastSRGBLinearConversion;
        }

        static void InitializeLightData(UniversalRenderPipelineAsset settings, NativeArray<VisibleLight> visibleLights, int mainLightIndex, out LightData lightData) {
            int maxPerObjectAdditionalLights = UniversalRenderPipeline.maxPerObjectLights;
            int maxVisibleAdditionalLights = UniversalRenderPipeline.maxVisibleAdditionalLights;

            lightData.mainLightIndex = mainLightIndex;

            if (settings.additionalLightsRenderingMode != LightRenderingMode.Disabled) {
                lightData.additionalLightsCount = Math.Min((mainLightIndex != -1) ? visibleLights.Length - 1 : visibleLights.Length, maxVisibleAdditionalLights);
                lightData.maxPerObjectAdditionalLightsCount = Math.Min(settings.maxAdditionalLightsCount, maxPerObjectAdditionalLights);
            } else {
                lightData.additionalLightsCount = 0;
                lightData.maxPerObjectAdditionalLightsCount = 0;
            }

            lightData.supportsAdditionalLights = settings.additionalLightsRenderingMode != LightRenderingMode.Disabled;
            lightData.shadeAdditionalLightsPerVertex = settings.additionalLightsRenderingMode == LightRenderingMode.PerVertex;
            lightData.visibleLights = visibleLights;
            lightData.supportsMixedLighting = settings.supportsMixedLighting;
            lightData.reflectionProbeBlending = settings.reflectionProbeBlending;
            lightData.reflectionProbeBoxProjection = settings.reflectionProbeBoxProjection;
            lightData.supportsLightLayers = RenderingUtils.SupportsLightLayers(SystemInfo.graphicsDeviceType) && settings.supportsLightLayers;
            lightData.originalIndices = new NativeArray<int>(visibleLights.Length, Allocator.Temp);

            for (var i = 0; i < lightData.originalIndices.Length; i++) {
                lightData.originalIndices[i] = i;
            }
        }

        static void CleanupLightData(ref LightData lightData) {
            lightData.originalIndices.Dispose();
        }

        static PerObjectData GetPerObjectLightFlags(int additionalLightsCount) {
            var configuration = PerObjectData.ReflectionProbes |
                                PerObjectData.Lightmaps |
                                PerObjectData.LightProbe |
                                PerObjectData.LightData |
                                PerObjectData.OcclusionProbe |
                                PerObjectData.ShadowMask;

            if (additionalLightsCount > 0) {
                configuration |= PerObjectData.LightData;

                // In this case we also need per-object indices (unity_LightIndices)
                if (!RenderingUtils.useStructuredBuffer)
                    configuration |= PerObjectData.LightIndices;
            }

            return configuration;
        }

        // Main Light is always a directional light
        static int GetMainLightIndex(UniversalRenderPipelineAsset settings, NativeArray<VisibleLight> visibleLights) {
            int totalVisibleLights = visibleLights.Length;

            if (totalVisibleLights == 0 || settings.mainLightRenderingMode != LightRenderingMode.PerPixel)
                return -1;

            Light sunLight = RenderSettings.sun;
            int brightestDirectionalLightIndex = -1;
            float brightestLightIntensity = 0.0f;

            for (int i = 0; i < totalVisibleLights; ++i) {
                VisibleLight currVisibleLight = visibleLights[i];
                Light currLight = currVisibleLight.light;

                if (currLight == null)
                    break;

                if (currVisibleLight.lightType == LightType.Directional) {
                    if (currLight == sunLight)
                        return i;
                    
                    if (currLight.intensity > brightestLightIntensity) {
                        brightestLightIntensity = currLight.intensity;
                        brightestDirectionalLightIndex = i;
                    }
                }
            }

            return brightestDirectionalLightIndex;
        }

        static void SetupPerFrameShaderConstants() {
            SphericalHarmonicsL2 ambientSH = RenderSettings.ambientProbe;
            Color linearGlossyEnvColor = new Color(ambientSH[0, 0], ambientSH[1, 0], ambientSH[2, 0]) * RenderSettings.reflectionIntensity;
            Color glossyEnvColor = CoreUtils.ConvertLinearToActiveColorSpace(linearGlossyEnvColor);
            
            Shader.SetGlobalVector(ShaderPropertyId.glossyEnvironmentColor, glossyEnvColor);
            Shader.SetGlobalVector(ShaderPropertyId.glossyEnvironmentCubeMapHDR, ReflectionProbe.defaultTextureHDRDecodeValues);
            Shader.SetGlobalTexture(ShaderPropertyId.glossyEnvironmentCubeMap, ReflectionProbe.defaultTexture);
            Shader.SetGlobalVector(ShaderPropertyId.ambientSkyColor, CoreUtils.ConvertSRGBToActiveColorSpace(RenderSettings.ambientSkyColor));
            Shader.SetGlobalVector(ShaderPropertyId.ambientEquatorColor, CoreUtils.ConvertSRGBToActiveColorSpace(RenderSettings.ambientEquatorColor));
            Shader.SetGlobalVector(ShaderPropertyId.ambientGroundColor, CoreUtils.ConvertSRGBToActiveColorSpace(RenderSettings.ambientGroundColor));
            Shader.SetGlobalVector(ShaderPropertyId.subtractiveShadowColor, CoreUtils.ConvertSRGBToActiveColorSpace(RenderSettings.subtractiveShadowColor));
            Shader.SetGlobalColor(ShaderPropertyId.rendererColor, Color.white);
        }

        static void CheckAndApplyDebugSettings(ref RenderingData renderingData) {
            DebugDisplaySettings debugDisplaySettings = DebugDisplaySettings.Instance;
            ref CameraData cameraData = ref renderingData.cameraData;

            if (debugDisplaySettings.AreAnySettingsActive && !cameraData.isPreviewCamera) {
                DebugDisplaySettingsRendering renderingSettings = debugDisplaySettings.RenderingSettings;
                int msaaSamples = cameraData.cameraTargetDescriptor.msaaSamples;

                if (!renderingSettings.enableMsaa)
                    msaaSamples = 1;

                if (!renderingSettings.enableHDR)
                    cameraData.isHdrEnabled = false;

                if (!debugDisplaySettings.IsPostProcessingAllowed)
                    cameraData.postProcessEnabled = false;

                cameraData.cameraTargetDescriptor.graphicsFormat = MakeRenderTextureGraphicsFormat(cameraData.isHdrEnabled, true);
                cameraData.cameraTargetDescriptor.msaaSamples = msaaSamples;
            }
        }

        static ImageUpscalingFilter ResolveUpscalingFilterSelection(Vector2 imageSize, float renderScale, UpscalingFilterSelection selection) {
            ImageUpscalingFilter filter = ImageUpscalingFilter.Linear;
            if ((selection == UpscalingFilterSelection.FSR) && !FSRUtils.IsSupported()) {
                selection = UpscalingFilterSelection.Auto;
            }

            switch (selection) {
                case UpscalingFilterSelection.Auto: {
                    float pixelScale = (1.0f / renderScale);
                    bool isIntegerScale = Mathf.Approximately((pixelScale - Mathf.Floor(pixelScale)), 0.0f);

                    if (isIntegerScale) {
                        float widthScale = (imageSize.x / pixelScale);
                        float heightScale = (imageSize.y / pixelScale);

                        bool isImageCompatible = (Mathf.Approximately((widthScale - Mathf.Floor(widthScale)), 0.0f) &&
                                                  Mathf.Approximately((heightScale - Mathf.Floor(heightScale)), 0.0f));

                        if (isImageCompatible) {
                            filter = ImageUpscalingFilter.Point;
                        }
                    }

                    break;
                }

                case UpscalingFilterSelection.Linear: {
                    break;
                }

                case UpscalingFilterSelection.Point: {
                    filter = ImageUpscalingFilter.Point;

                    break;
                }

                case UpscalingFilterSelection.FSR: {
                    filter = ImageUpscalingFilter.FSR;

                    break;
                }
            }

            return filter;
        }
    }
}