namespace UnityEngine.Rendering.Universal.Internal {
    /// <summary>
    /// Copy the given color target to the current camera target
    ///
    /// You can use this pass to copy the result of rendering to
    /// the camera target. The pass takes the screen viewport into
    /// consideration.
    /// </summary>
    public class FinalBlitPass : ScriptableRenderPass {
        RenderTargetIdentifier m_Source;
        Material m_BlitMaterial;

        public FinalBlitPass(RenderPassEvent evt, Material blitMaterial) {
            base.useNativeRenderPass = false;

            m_BlitMaterial = blitMaterial;
            renderPassEvent = evt;
        }

        /// <summary>
        /// Configure the pass
        /// </summary>
        /// <param name="baseDescriptor"></param>
        /// <param name="colorHandle"></param>
        public void Setup(RenderTextureDescriptor baseDescriptor, RenderTargetHandle colorHandle) {
            m_Source = colorHandle.id;
        }

        /// <inheritdoc/>
        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData) {
            if (m_BlitMaterial == null) {
                return;
            }

            ref CameraData cameraData = ref renderingData.cameraData;
            RenderTargetIdentifier cameraTarget = (cameraData.targetTexture != null) ? new RenderTargetIdentifier(cameraData.targetTexture) : BuiltinRenderTextureType.CameraTarget;
        
        #if UNITY_EDITOR
            bool isSceneViewCamera = cameraData.isSceneViewCamera;
        #endif
            
            CommandBuffer cmd = CommandBufferPool.Get();

            if (m_Source == cameraData.renderer.GetCameraColorFrontBuffer(cmd)) {
                m_Source = renderingData.cameraData.renderer.cameraColorTarget;
            }
            
            GetActiveDebugHandler(renderingData)?.UpdateShaderGlobalPropertiesForFinalValidationPass(cmd, ref cameraData, true);

            CoreUtils.SetKeyword(cmd, ShaderKeywordStrings.LinearToSRGBConversion, cameraData.requireSrgbConversion);

            cmd.SetGlobalTexture(ShaderPropertyId.sourceTex, m_Source);

            if (
            #if UNITY_EDITOR
                    isSceneViewCamera || 
            #endif
                    cameraData.isDefaultViewport) {
                // This set render target is necessary so we change the LOAD state to DontCare.
                cmd.SetRenderTarget(BuiltinRenderTextureType.CameraTarget, RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store, // color
                        RenderBufferLoadAction.DontCare, RenderBufferStoreAction.DontCare); // depth

                cmd.Blit(m_Source, cameraTarget, m_BlitMaterial);
                cameraData.renderer.ConfigureCameraTarget(cameraTarget, cameraTarget);
            } else {
                CoreUtils.SetRenderTarget(cmd, cameraTarget, RenderBufferLoadAction.Load, RenderBufferStoreAction.Store, ClearFlag.None, Color.black);

                Camera camera = cameraData.camera;
                cmd.SetViewProjectionMatrices(Matrix4x4.identity, Matrix4x4.identity);
                cmd.SetViewport(cameraData.pixelRect);
                cmd.DrawMesh(RenderingUtils.fullscreenMesh, Matrix4x4.identity, m_BlitMaterial);
                cmd.SetViewProjectionMatrices(camera.worldToCameraMatrix, camera.projectionMatrix);
                cameraData.renderer.ConfigureCameraTarget(cameraTarget, cameraTarget);
            }

            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }
    }
}