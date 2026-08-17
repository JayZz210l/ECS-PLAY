using Unity.Mathematics;
using UnityEngine;

namespace CitizenSim
{
    // 流场网格配置单例。挂在地面物体上:origin 自动跟随地面中心(地面中心 - 网格半边长),
    // gridSize/cellSize 可在 Inspector 手动设置。FlowFieldBuildSystem 运行时读取。
    public class FlowFieldConfig : MonoBehaviour
    {
        public static FlowFieldConfig Instance { get; private set; }

        [Tooltip("网格中心跟随的地面,默认用自身所在物体")]
        public Transform ground;
        [Tooltip("网格总格子数(x, z)")]
        public Vector2Int gridSize = new Vector2Int(40, 40);
        [Tooltip("每格边长(米)")]
        public float cellSize = 2f;

        public int2 GridSize => new int2(gridSize.x, gridSize.y);

        // 网格原点:格子(0,0)角点世界坐标 = 地面中心 - 网格半边长。
        public float3 Origin
        {
            get
            {
                float3 g = ground != null ? ground.position : float3.zero;
                return g - new float3(gridSize.x * cellSize * 0.5f, 0f, gridSize.y * cellSize * 0.5f);
            }
        }

        void OnEnable() => Instance = this;
        void OnDisable() { if (Instance == this) Instance = null; }

        // 测试/隔离用显式注册(同 PoiRegistry 模式)。
        public void Register() { Instance = this; }
        public static void Clear() { Instance = null; }
    }
}
