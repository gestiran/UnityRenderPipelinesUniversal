using UnityEngine.Experimental.GlobalIllumination;
using Unity.Collections;

namespace UnityEngine.Rendering.Universal.Internal
{
    // Render all tiled-based deferred lights.
    internal class DeferredPass : ScriptableRenderPass
    {
        DeferredLights m_DeferredLights;

        public DeferredPass(RenderPassEvent evt, DeferredLights deferredLights)
        {
            base.renderPassEvent = evt;
            m_DeferredLights = deferredLights;
        }

        // ScriptableRenderPass
        public override void Configure(CommandBuffer cmd, RenderTextureDescriptor cameraTextureDescripor)
        {
            RenderTargetIdentifier lightingAttachmentId = m_DeferredLights.GbufferAttachmentIdentifiers[m_DeferredLights.GBufferLightingIndex];
            RenderTargetIdentifier depthAttachmentId = m_DeferredLights.DepthAttachmentIdentifier;
            if (m_DeferredLights.UseRenderPass)
                ConfigureInputAttachments(m_DeferredLights.DeferredInputAttachments, m_DeferredLights.DeferredInputIsTransient);

            // TODO: change to m_DeferredLights.GetGBufferFormat(m_DeferredLights.GBufferLightingIndex) when it's not GraphicsFormat.None
            // TODO: Cannot currently bind depth texture as read-only!
            ConfigureTarget(lightingAttachmentId, depthAttachmentId, cameraTextureDescripor.graphicsFormat);
        }

        // ScriptableRenderPass
        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            m_DeferredLights.ExecuteDeferredPass(context, ref renderingData);
        }

        // ScriptableRenderPass
        public override void OnCameraCleanup(CommandBuffer cmd)
        {
            m_DeferredLights.OnCameraCleanup(cmd);
        }
    }
}
