using System.Collections.Generic;
using Unity.Mathematics;

namespace UnityEngine.Rendering.Universal
{
    internal struct LightStats
    {
        public int totalLights;
        public int totalNormalMapUsage;
        public int totalVolumetricUsage;
        public uint blendStylesUsed;
        public uint blendStylesWithLights;
    }

    internal interface ILight2DCullResult
    {
        List<Light2D> visibleLights { get; }
        LightStats GetLightStatsByLayer(int layer);
        bool IsSceneLit();
    }

    internal class Light2DCullResult : ILight2DCullResult
    {
        private List<Light2D> m_VisibleLights = new List<Light2D>();
        public List<Light2D> visibleLights => m_VisibleLights;

        public bool IsSceneLit()
        {
            return Light2DManager.lights.Count > 0;
        }

        public LightStats GetLightStatsByLayer(int layer)
        {
            var returnStats = new LightStats();
            foreach (var light in visibleLights)
            {
                if (!light.IsLitLayer(layer))
                    continue;

                returnStats.totalLights++;
                if (light.normalMapQuality != Light2D.NormalMapQuality.Disabled)
                    returnStats.totalNormalMapUsage++;
                if (light.volumeIntensity > 0)
                    returnStats.totalVolumetricUsage++;

                returnStats.blendStylesUsed |= (uint)(1 << light.blendStyleIndex);
                if (light.lightType != Light2D.LightType.Global)
                    returnStats.blendStylesWithLights |= (uint)(1 << light.blendStyleIndex);
            }

            return returnStats;
        }

        public void SetupCulling(ref ScriptableCullingParameters cullingParameters, Camera camera)
        {
            m_VisibleLights.Clear();
            foreach (var light in Light2DManager.lights)
            {
                if ((camera.cullingMask & (1 << light.gameObject.layer)) == 0)
                    continue;

#if UNITY_EDITOR
                if (!UnityEditor.SceneManagement.StageUtility.IsGameObjectRenderedByCamera(light.gameObject, camera))
                    continue;
#endif

                if (light.lightType == Light2D.LightType.Global)
                {
                    m_VisibleLights.Add(light);
                    continue;
                }

                var position = light.boundingSphere.position;
                var culled = false;
                for (var i = 0; i < cullingParameters.cullingPlaneCount; ++i)
                {
                    var plane = cullingParameters.GetCullingPlane(i);
                    // most of the time is spent getting world position
                    var distance = math.dot(position, plane.normal) + plane.distance;
                    if (distance < -light.boundingSphere.radius)
                    {
                        culled = true;
                        break;
                    }
                }
                if (culled)
                    continue;

                m_VisibleLights.Add(light);
            }

            // must be sorted here because light order could change
            m_VisibleLights.Sort((l1, l2) => l1.lightOrder - l2.lightOrder);
        }
    }
}
