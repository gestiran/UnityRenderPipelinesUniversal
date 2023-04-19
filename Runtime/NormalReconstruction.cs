namespace UnityEngine.Rendering.Universal.Internal {
    public static class NormalReconstruction {
        private static readonly int s_NormalReconstructionMatrixID = Shader.PropertyToID("_NormalReconstructionMatrix");
        private static Matrix4x4[] s_NormalReconstructionMatrix = new Matrix4x4[2];

        public static void SetupProperties(CommandBuffer cmd, in CameraData cameraData) {
            Matrix4x4 view = cameraData.GetViewMatrix(0);
            Matrix4x4 proj = cameraData.GetProjectionMatrix(0);
            s_NormalReconstructionMatrix[0] = proj * view;
            
            Matrix4x4 cview = view;
            cview.SetColumn(3, new Vector4(0.0f, 0.0f, 0.0f, 1.0f));
            Matrix4x4 cviewProj = proj * cview;
            Matrix4x4 cviewProjInv = cviewProj.inverse;

            s_NormalReconstructionMatrix[0] = cviewProjInv;

            cmd.SetGlobalMatrixArray(s_NormalReconstructionMatrixID, s_NormalReconstructionMatrix);
        }
    }
}