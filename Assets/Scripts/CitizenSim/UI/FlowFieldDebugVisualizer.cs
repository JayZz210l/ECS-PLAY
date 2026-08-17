using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace CitizenSim
{
    // 流场调试可视化:
    //  - Scene 视图(开启 Gizmos):网格线 + 障碍物占用(红) + 流场方向(黄箭头)。
    //  - Game 视图:运行时构建调试 mesh(网格线 + blocked 方块),挂 DebugOverlay 透明材质,定时刷新。
    // 挂在任意 active GameObject 上。Play mode 下 FlowFieldBuildSystem 分配流场后才显示。
    public class FlowFieldDebugVisualizer : MonoBehaviour
    {
        [Header("显示开关")]
        public bool showGrid = true;
        public bool showBlocked = true;
        public bool showDirections = false;  // 1600 箭头较密,默认关

        [Header("Game 视图")]
        public bool showInGame = true;       // 调试 mesh 渲染到 Game/Scene 视图
        [Tooltip("Player 构建必须显式引用，避免 DebugOverlay 被 shader stripping 移除")]
        [SerializeField] Shader runtimeShader;
        public float gridLineWidth = 0.18f;  // 网格线宽(米)
        public float refreshInterval = 0.25f;// 调试 mesh 重建间隔(秒)

        [Header("颜色")]
        public Color gridColor = new Color(0.5f, 1f, 0.5f, 0.3f);
        public Color blockedColor = new Color(1f, 0.2f, 0.2f, 0.4f);
        public Color directionColor = new Color(1f, 0.9f, 0.2f, 0.7f);

        // 网格画在这个 y 高度,避免被地面 mesh 遮挡(z-fighting)。
        const float k_GridY = 0.1f;
        const float k_BlockedY = 0.5f;

        // 默认查食物流场(3 张配置相同,direction 不同但 blocked 相同)。
        public GoalType fieldGoal = GoalType.SeekFood;

        // Game 视图运行时对象
        MeshFilter _filter;
        MeshRenderer _renderer;
        Mesh _mesh;
        Material _material;
        float _refreshTimer;

        void Start()
        {
            EnsureRuntimeObjects();
        }

        void Update()
        {
            if (!showInGame)
            {
                if (_renderer != null) _renderer.enabled = false;
                return;
            }
            EnsureRuntimeObjects();
            _refreshTimer += Time.deltaTime;
            if (_refreshTimer >= refreshInterval)
            {
                _refreshTimer = 0f;
                var f = GetField();
                if (f.directions.IsCreated)
                    RebuildMesh(f);
            }
        }

        void OnDestroy()
        {
            if (_mesh != null) { if (Application.isPlaying) Destroy(_mesh); else DestroyImmediate(_mesh); }
            if (_material != null) { if (Application.isPlaying) Destroy(_material); else DestroyImmediate(_material); }
            if (_filter != null && _filter.gameObject != null)
            {
                var go = _filter.gameObject;
                if (Application.isPlaying) Destroy(go); else DestroyImmediate(go);
            }
        }

        // 懒创建调试 mesh 子物体(挂 MeshFilter/MeshRenderer + DebugOverlay 材质)。
        // 父物体用 FlowFieldConfig.ground(若存在),让网格跟随地面移动;否则挂组件所在物体。
        void EnsureRuntimeObjects()
        {
            if (_filter != null) return;

            // Shader.Find 在 Editor 可用不代表 Player 会包含该 shader。场景中的 runtimeShader
            // 是构建依赖；Find 只保留给旧场景/测试作为兼容回退。
            var shader = runtimeShader != null
                ? runtimeShader
                : Shader.Find("CitizenSim/DebugOverlay");
            if (shader == null)
            {
                Debug.LogError(
                    "FlowFieldDebugVisualizer: 缺少 CitizenSim/DebugOverlay shader，无法显示运行时网格。",
                    this);
                enabled = false;
                return;
            }

            var go = new GameObject("FlowFieldDebugMesh");
            Transform parent = null;
            var cfg = FlowFieldConfig.Instance;
            if (cfg != null && cfg.ground != null) parent = cfg.ground;
            if (parent == null) parent = transform;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            // 抵消父级缩放:顶点用「相对父物体的局部坐标(=世界 - parent.pos)」,
            // 父物体 scale≠1 会放大子物体,故 localScale = 1/parentScale 使 1:1 米。
            var ps = parent.lossyScale;
            go.transform.localScale = new Vector3(
                Mathf.Abs(ps.x) < 1e-5f ? 1f : 1f / ps.x,
                Mathf.Abs(ps.y) < 1e-5f ? 1f : 1f / ps.y,
                Mathf.Abs(ps.z) < 1e-5f ? 1f : 1f / ps.z);
            _filter = go.AddComponent<MeshFilter>();
            _renderer = go.AddComponent<MeshRenderer>();
            _material = new Material(shader) { name = "FlowFieldDebugOverlay (Runtime)" };
            _material.SetColor("_Color", Color.white);
            _renderer.sharedMaterial = _material;
            _renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _renderer.receiveShadows = false;
        }

        // mesh 父物体的世界位置。mesh.localScale 已抵消父级缩放(lossyScale=1),
        // 顶点用「相对父物体的局部坐标 = 世界坐标 - parent.pos」即可 1:1 对齐。
        Vector3 ParentWorldPos()
        {
            var cfg = FlowFieldConfig.Instance;
            Transform parent = (cfg != null && cfg.ground != null) ? cfg.ground : transform;
            return parent.position;
        }

        // 重建调试 mesh:网格线 quad + blocked 方块 quad,顶点色区分绿/红。
        // 顶点是相对父物体(ground)的局部坐标;父物体是 ground 时 mesh 跟随地面移动。
        void RebuildMesh(FlowField f)
        {
            var verts = new List<Vector3>();
            var colors = new List<Color>();
            var uvs = new List<Vector2>();
            var tris = new List<int>();

            float w = f.gridSize.x * f.cellSize;
            float h = f.gridSize.y * f.cellSize;
            // 相对父物体的局部原点(父物体缩放已由 localScale 抵消)。
            float3 o = f.origin - (float3)ParentWorldPos();

            if (showGrid)
            {
                float lw = gridLineWidth * 0.5f;
                // x 方向竖线(沿 z),x = o.x + k*cellSize
                for (int k = 0; k <= f.gridSize.x; k++)
                {
                    float x = o.x + k * f.cellSize;
                    AddQuad(verts, uvs, tris, colors,
                        new Vector3(x - lw, k_GridY, o.z),
                        new Vector3(x + lw, k_GridY, o.z + h),
                        gridColor);
                }
                // z 方向横线(沿 x),z = o.z + k*cellSize
                for (int k = 0; k <= f.gridSize.y; k++)
                {
                    float z = o.z + k * f.cellSize;
                    AddQuad(verts, uvs, tris, colors,
                        new Vector3(o.x, k_GridY, z - lw),
                        new Vector3(o.x + w, k_GridY, z + lw),
                        gridColor);
                }
            }

            if (showBlocked)
            {
                float pad = Mathf.Min(0.1f, f.cellSize * 0.2f);
                float s = f.cellSize - pad * 2f;
                for (int i = 0; i < f.blocked.Length; i++)
                {
                    if (f.blocked[i] != 1) continue;
                    int x = i % f.gridSize.x;
                    int z = i / f.gridSize.x;
                    float3 center = o + new float3((x + 0.5f) * f.cellSize, k_BlockedY, (z + 0.5f) * f.cellSize);
                    AddQuad(verts, uvs, tris, colors,
                        new Vector3(center.x - s * 0.5f, k_BlockedY, center.z - s * 0.5f),
                        new Vector3(center.x + s * 0.5f, k_BlockedY, center.z + s * 0.5f),
                        blockedColor);
                }
            }

            if (_mesh == null) _mesh = new Mesh { name = "FlowFieldDebugMesh" };
            _mesh.Clear();
            _mesh.SetVertices(verts);
            _mesh.SetColors(colors);
            _mesh.SetUVs(0, uvs);
            _mesh.SetTriangles(tris, 0);
            _mesh.RecalculateBounds();
            _filter.sharedMesh = _mesh;
            if (_renderer != null) _renderer.enabled = true;
        }

        // 以两个对角点加一个 quad(2 三角),顶点色 = c。
        static void AddQuad(List<Vector3> verts, List<Vector2> uvs, List<int> tris, List<Color> colors,
            Vector3 min, Vector3 max, Color c)
        {
            int s = verts.Count;
            verts.Add(new Vector3(min.x, min.y, min.z));
            verts.Add(new Vector3(max.x, min.y, min.z));
            verts.Add(new Vector3(max.x, max.y, max.z));
            verts.Add(new Vector3(min.x, max.y, max.z));
            uvs.Add(Vector2.zero); uvs.Add(Vector2.one); uvs.Add(Vector2.one); uvs.Add(Vector2.zero);
            colors.Add(c); colors.Add(c); colors.Add(c); colors.Add(c);
            tris.Add(s); tris.Add(s + 1); tris.Add(s + 2);
            tris.Add(s); tris.Add(s + 2); tris.Add(s + 3);
        }

        void OnDrawGizmos()
        {
            var f = GetField();
            if (!f.directions.IsCreated) return;

            if (showGrid) DrawGrid(f);
            if (showBlocked) DrawBlocked(f);
            if (showDirections) DrawDirections(f);
        }

        FlowField GetField()
        {
            switch (fieldGoal)
            {
                case GoalType.SeekFood: return FlowFieldBuildSystem.FoodField;
                case GoalType.SeekHome: return FlowFieldBuildSystem.HomeField;
                case GoalType.SeekFun:  return FlowFieldBuildSystem.FunField;
                default: return FlowFieldBuildSystem.FoodField;
            }
        }

        void DrawGrid(FlowField f)
        {
            Gizmos.color = gridColor;
            float3 o = f.origin;
            float w = f.gridSize.x * f.cellSize;
            float h = f.gridSize.y * f.cellSize;
            // x 方向竖线
            for (int x = 0; x <= f.gridSize.x; x++)
            {
                float px = o.x + x * f.cellSize;
                Gizmos.DrawLine(new Vector3(px, k_GridY, o.z), new Vector3(px, k_GridY, o.z + h));
            }
            // z 方向横线
            for (int z = 0; z <= f.gridSize.y; z++)
            {
                float pz = o.z + z * f.cellSize;
                Gizmos.DrawLine(new Vector3(o.x, k_GridY, pz), new Vector3(o.x + w, k_GridY, pz));
            }
        }

        void DrawBlocked(FlowField f)
        {
            Gizmos.color = blockedColor;
            for (int i = 0; i < f.CellCount; i++)
            {
                if (f.blocked[i] != 1) continue;
                int x = i % f.gridSize.x;
                int z = i / f.gridSize.x;
                Vector3 center = f.origin + new float3((x + 0.5f) * f.cellSize, 0.5f, (z + 0.5f) * f.cellSize);
                Gizmos.DrawCube(center, new Vector3(f.cellSize, 1f, f.cellSize));
            }
        }

        void DrawDirections(FlowField f)
        {
            Gizmos.color = directionColor;
            for (int i = 0; i < f.CellCount; i++)
            {
                if (f.blocked[i] == 1) continue;
                float3 dir = f.directions[i];
                if (math.lengthsq(dir) < 0.01f) continue;
                int x = i % f.gridSize.x;
                int z = i / f.gridSize.x;
                Vector3 center = f.origin + new float3((x + 0.5f) * f.cellSize, 0.5f, (z + 0.5f) * f.cellSize);
                Gizmos.DrawLine(center, center + (Vector3)dir * f.cellSize * 0.5f);
            }
        }
    }
}
