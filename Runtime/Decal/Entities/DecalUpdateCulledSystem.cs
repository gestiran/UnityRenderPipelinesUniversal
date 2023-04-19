namespace UnityEngine.Rendering.Universal
{
    /// <summary>
    /// Writes culling results into <see cref="DecalCulledChunk"/>.
    /// </summary>
    internal class DecalUpdateCulledSystem
    {
        private DecalEntityManager m_EntityManager;

        public DecalUpdateCulledSystem(DecalEntityManager entityManager)
        {
            m_EntityManager = entityManager;
        }

        public void Execute() {

            for (int i = 0; i < m_EntityManager.chunkCount; ++i) {
                Execute(m_EntityManager.culledChunks[i], m_EntityManager.culledChunks[i].count);
            }
        }

        private void Execute(DecalCulledChunk culledChunk, int count)
        {
            if (count == 0)
                return;

            culledChunk.currentJobHandle.Complete();

            CullingGroup cullingGroup = culledChunk.cullingGroups;
            culledChunk.visibleDecalCount = cullingGroup.QueryIndices(true, culledChunk.visibleDecalIndexArray, 0);
            culledChunk.visibleDecalIndices.CopyFrom(culledChunk.visibleDecalIndexArray);
        }
    }
}
