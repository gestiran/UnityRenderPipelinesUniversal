using System;
using UnityEngine.Experimental.Rendering;

namespace UnityEngine.Rendering.Universal.Internal {
    public class CopyDepthPass : ScriptableRenderPass {
        private RenderTargetHandle source {
            get;
            set;
        }
        private RenderTargetHandle destination {
            get;
            set;
        }
        internal bool AllocateRT {
            get;
            set;
        }
        internal int MssaSamples {
            get;
            set;
        }
        Material m_CopyDepthMaterial;

        public CopyDepthPass(RenderPassEvent evt, Material copyDepthMaterial) {
            AllocateRT = true;
            m_CopyDepthMaterial = copyDepthMaterial;
            renderPassEvent = evt;
        }

        /// <summary>
        /// Configure the pass with the source and destination to execute on.
        /// </summary>
        /// <param name="source">Source Render Target</param>
        /// <param name="destination">Destination Render Targt</param>
        public void Setup(RenderTargetHandle source, RenderTargetHandle destination) {
            this.source = source;
            this.destination = destination;
            this.AllocateRT = !destination.HasInternalRenderTargetId();
            this.MssaSamples = -1;
        }

        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData) {
            var descriptor = renderingData.cameraData.cameraTargetDescriptor;
            descriptor.colorFormat = RenderTextureFormat.Depth;
            descriptor.depthBufferBits = UniversalRenderer.k_DepthStencilBufferBits;
            descriptor.msaaSamples = 1;
            if (this.AllocateRT)
                cmd.GetTemporaryRT(destination.id, descriptor, FilterMode.Point);

            // On Metal iOS, prevent camera attachments to be bound and cleared during this pass.
            ConfigureTarget(new RenderTargetIdentifier(destination.Identifier(), 0, CubemapFace.Unknown, -1), descriptor.depthStencilFormat, descriptor.width, descriptor.height,
                    descriptor.msaaSamples, true);

            ConfigureClear(ClearFlag.None, Color.black);
        }

        /// <inheritdoc/>
        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData) {
            if (m_CopyDepthMaterial == null) {
                return;
            }

            CommandBuffer cmd = CommandBufferPool.Get();
            
            int cameraSamples = 0;

            if (MssaSamples == -1) {
                RenderTextureDescriptor descriptor = renderingData.cameraData.cameraTargetDescriptor;
                cameraSamples = descriptor.msaaSamples;
            } else
                cameraSamples = MssaSamples;

            // When auto resolve is supported or multisampled texture is not supported, set camera samples to 1
            if (SystemInfo.supportsMultisampledTextures == 0)
                cameraSamples = 1;

            CameraData cameraData = renderingData.cameraData;

            switch (cameraSamples) {
                case 8:
                    cmd.DisableShaderKeyword(ShaderKeywordStrings.DepthMsaa2);
                    cmd.DisableShaderKeyword(ShaderKeywordStrings.DepthMsaa4);
                    cmd.EnableShaderKeyword(ShaderKeywordStrings.DepthMsaa8);

                    break;

                case 4:
                    cmd.DisableShaderKeyword(ShaderKeywordStrings.DepthMsaa2);
                    cmd.EnableShaderKeyword(ShaderKeywordStrings.DepthMsaa4);
                    cmd.DisableShaderKeyword(ShaderKeywordStrings.DepthMsaa8);

                    break;

                case 2:
                    cmd.EnableShaderKeyword(ShaderKeywordStrings.DepthMsaa2);
                    cmd.DisableShaderKeyword(ShaderKeywordStrings.DepthMsaa4);
                    cmd.DisableShaderKeyword(ShaderKeywordStrings.DepthMsaa8);

                    break;

                // MSAA disabled, auto resolve supported or ms textures not supported
                default:
                    cmd.DisableShaderKeyword(ShaderKeywordStrings.DepthMsaa2);
                    cmd.DisableShaderKeyword(ShaderKeywordStrings.DepthMsaa4);
                    cmd.DisableShaderKeyword(ShaderKeywordStrings.DepthMsaa8);

                    break;
            }

            cmd.SetGlobalTexture("_CameraDepthAttachment", source.Identifier());
                
            bool isGameViewFinalTarget = (cameraData.cameraType == CameraType.Game && destination == RenderTargetHandle.CameraTarget);
            bool yflip = (cameraData.IsCameraProjectionMatrixFlipped()) && !isGameViewFinalTarget;
            float flipSign = yflip ? -1.0f : 1.0f;
            Vector4 scaleBiasRt = (flipSign < 0.0f) ? new Vector4(flipSign, 1.0f, -1.0f, 1.0f) : new Vector4(flipSign, 0.0f, 1.0f, 1.0f);
            cmd.SetGlobalVector(ShaderPropertyId.scaleBiasRt, scaleBiasRt);
            if (isGameViewFinalTarget)
                cmd.SetViewport(cameraData.pixelRect);

            cmd.DrawMesh(RenderingUtils.fullscreenMesh, Matrix4x4.identity, m_CopyDepthMaterial);

            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }

        /// <inheritdoc/>
        public override void OnCameraCleanup(CommandBuffer cmd) {
            if (cmd == null)
                throw new ArgumentNullException("cmd");

            if (this.AllocateRT)
                cmd.ReleaseTemporaryRT(destination.id);

            destination = RenderTargetHandle.CameraTarget;
        }
    }
}