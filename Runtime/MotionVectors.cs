namespace UnityEngine.Rendering.Universal {
    internal sealed class MotionVectorsPersistentData {
        readonly Matrix4x4[] m_ViewProjection = new Matrix4x4[2];
        readonly Matrix4x4[] m_PreviousViewProjection = new Matrix4x4[2];
        readonly int[] m_LastFrameIndex = new int[2];
        readonly float[] m_PrevAspectRatio = new float[2];

        internal MotionVectorsPersistentData() {
            for (int i = 0; i < m_ViewProjection.Length; i++) {
                m_ViewProjection[i] = Matrix4x4.identity;
                m_PreviousViewProjection[i] = Matrix4x4.identity;
                m_LastFrameIndex[i] = -1;
                m_PrevAspectRatio[i] = -1;
            }
        }

        internal int lastFrameIndex {
            get => m_LastFrameIndex[0];
        }

        internal Matrix4x4 viewProjection {
            get => m_ViewProjection[0];
        }

        internal Matrix4x4 previousViewProjection {
            get => m_PreviousViewProjection[0];
        }

        internal Matrix4x4[] viewProjectionStereo {
            get => m_ViewProjection;
        }

        internal Matrix4x4[] previousViewProjectionStereo {
            get => m_PreviousViewProjection;
        }

        public void Update(ref CameraData cameraData) {
            bool aspectChanged = m_PrevAspectRatio[0] != cameraData.aspectRatio;

            if (m_LastFrameIndex[0] != Time.frameCount || aspectChanged) {
                Matrix4x4 gpuVP = GL.GetGPUProjectionMatrix(cameraData.GetProjectionMatrix(0), true) * cameraData.GetViewMatrix(0);
                m_PreviousViewProjection[0] = aspectChanged ? gpuVP : m_ViewProjection[0];
                m_ViewProjection[0] = gpuVP;

                m_LastFrameIndex[0] = Time.frameCount;
                m_PrevAspectRatio[0] = cameraData.aspectRatio;
            }
        }
    }
}