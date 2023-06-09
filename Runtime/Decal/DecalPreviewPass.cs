using System.Collections.Generic;

namespace UnityEngine.Rendering.Universal
{
    internal class DecalPreviewPass : ScriptableRenderPass
    {
        private FilteringSettings m_FilteringSettings;
        private List<ShaderTagId> m_ShaderTagIdList;

        public DecalPreviewPass()
        {
            renderPassEvent = RenderPassEvent.AfterRenderingOpaques;
            ConfigureInput(ScriptableRenderPassInput.Depth); // Require depth
            m_FilteringSettings = new FilteringSettings(RenderQueueRange.opaque, -1);

            m_ShaderTagIdList = new List<ShaderTagId>();
            m_ShaderTagIdList.Add(new ShaderTagId(DecalShaderPassNames.DecalScreenSpaceMesh));
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            SortingCriteria sortingCriteria = renderingData.cameraData.defaultOpaqueSortFlags;
            DrawingSettings drawingSettings = CreateDrawingSettings(m_ShaderTagIdList, ref renderingData, sortingCriteria);

            CommandBuffer cmd = CommandBufferPool.Get();
            context.ExecuteCommandBuffer(cmd);
            cmd.Clear();

            context.DrawRenderers(renderingData.cullResults, ref drawingSettings, ref m_FilteringSettings);

            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }
    }
}
