namespace UnityEngine.Rendering.Universal {
    public class DrawSkyboxPass : ScriptableRenderPass {
        public DrawSkyboxPass(RenderPassEvent evt) {

            renderPassEvent = evt;
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData) {
            CameraData cameraData = renderingData.cameraData;
            Camera camera = cameraData.camera;

            var activeDebugHandler = GetActiveDebugHandler(renderingData);

            if (activeDebugHandler != null) {
                if (activeDebugHandler.IsScreenClearNeeded) {
                    return;
                }
            }

            context.DrawSkybox(camera);
        }
    }
}