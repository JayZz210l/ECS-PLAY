using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace CitizenSim
{
    // 障碍物单例。管理所有 ObstacleAuthoring,驱动动态障碍物状态机:
    //   移动中(isMoving=true):Steering 排斥力每帧生效 + 流场每 0.5s 局部重算
    //   静止累计 1s:触发一次全量重算(终态准确),之后停算
    // 障碍物四层标识:GO 层(Authoring 状态) / 流场层(blocked) / Registry 层(本类) / Steering 层(MovingObstaclePos)。
    public class ObstacleRegistry : MonoBehaviour
    {
        public static ObstacleRegistry Instance { get; private set; }

        public ObstacleAuthoring[] obstacles;

        // Steering 层:移动中障碍物位置/半径,SteeringSystem 每帧读。预分配静态数组免 GC。
        public static NativeArray<float3> MovingObstaclePos;
        public static NativeArray<float> MovingObstacleRad;
        public static int MovingObstacleCount;
        const int k_MaxMoving = 64;

        // 状态机参数
        const float k_MovingRebuildInterval = 0.5f;   // 移动中流场重算间隔
        const float k_StationaryThreshold = 1f;       // 静止多久算"停"
        const int k_RegionRadius = 10;                // 局部重算影响半径(格)

        // 复用的 changedCells 缓存(免每帧 new NativeList)
        static NativeList<int2> s_ChangedCells;

        float rebuildTimer;

        void OnEnable()
        {
            Instance = this;
            if (!MovingObstaclePos.IsCreated)
                MovingObstaclePos = new NativeArray<float3>(k_MaxMoving, Allocator.Persistent);
            if (!MovingObstacleRad.IsCreated)
                MovingObstacleRad = new NativeArray<float>(k_MaxMoving, Allocator.Persistent);
            if (!s_ChangedCells.IsCreated)
                s_ChangedCells = new NativeList<int2>(64, Allocator.Persistent);
        }

        void OnDisable()
        {
            if (Instance == this) Instance = null;
        }

        // 测试/Bootstrap 显式注册用(同 PoiRegistry 模式)。
        public void Register()
        {
            Instance = this;
            if (!MovingObstaclePos.IsCreated)
                MovingObstaclePos = new NativeArray<float3>(k_MaxMoving, Allocator.Persistent);
            if (!MovingObstacleRad.IsCreated)
                MovingObstacleRad = new NativeArray<float>(k_MaxMoving, Allocator.Persistent);
            if (!s_ChangedCells.IsCreated)
                s_ChangedCells = new NativeList<int2>(64, Allocator.Persistent);
        }

        public static void Clear() { Instance = null; }

        // 测试用:释放静态数组。
        public static void DisposeStatics()
        {
            if (MovingObstaclePos.IsCreated) MovingObstaclePos.Dispose();
            if (MovingObstacleRad.IsCreated) MovingObstacleRad.Dispose();
            if (s_ChangedCells.IsCreated) s_ChangedCells.Dispose();
            MovingObstacleCount = 0;
        }

        void Update()
        {
            if (obstacles == null) { MovingObstacleCount = 0; return; }

            // 初始化 lastPosition(首帧)
            for (int i = 0; i < obstacles.Length; i++)
            {
                var ob = obstacles[i];
                if (ob != null && ob.lastPosition == default)
                    ob.lastPosition = ob.transform.position;
            }

            s_ChangedCells.Clear();
            bool anyMoving = false;
            bool anyNewlyStatic = false;

            for (int i = 0; i < obstacles.Length; i++)
            {
                var ob = obstacles[i];
                if (ob == null) continue;
                Vector3 p = ob.transform.position;
                if (Vector3.SqrMagnitude(p - ob.lastPosition) > 1e-4f)
                {
                    // 移动了:记录新旧格子(供局部重算),重置静止计时
                    s_ChangedCells.Add(FlowFieldBuildSystem.FoodField.WorldToCell(ob.lastPosition));
                    s_ChangedCells.Add(FlowFieldBuildSystem.FoodField.WorldToCell(p));
                    ob.isMoving = true;
                    ob.stationaryTime = 0f;
                    ob.lastPosition = p;
                    anyMoving = true;
                }
                else if (ob.isMoving)
                {
                    ob.stationaryTime += Time.deltaTime;
                    if (ob.stationaryTime > k_StationaryThreshold)
                    {
                        ob.isMoving = false;        // 刚静止
                        anyNewlyStatic = true;
                    }
                    else
                    {
                        anyMoving = true;           // 还在静止判定窗口,继续定时重算
                    }
                }
            }

            // 移动中:每 0.5s 局部重算(路径跟上,不被推进死区)
            if (anyMoving && s_ChangedCells.Length > 0)
            {
                rebuildTimer += Time.deltaTime;
                if (rebuildTimer >= k_MovingRebuildInterval)
                {
                    rebuildTimer = 0f;
                    FlowFieldBuildSystem.RebuildRegions(s_ChangedCells, k_RegionRadius);
                }
            }

            // 静止后:一次全量重算(终态准确,清除移动中局部误差)
            if (anyNewlyStatic)
            {
                FlowFieldBuildSystem.RebuildAll();
                rebuildTimer = 0f;
            }

            CollectMovingObstacles();
        }

        // 收集所有障碍物(静态+动态)位置/半径,供 SteeringJob 做排斥力。
        // 流场 blocked 管宏观绕路,排斥力管微观避让(防市民擦入 mesh 边缘)。
        void CollectMovingObstacles()
        {
            MovingObstacleCount = 0;
            if (obstacles == null) return;
            for (int i = 0; i < obstacles.Length && MovingObstacleCount < k_MaxMoving; i++)
            {
                var ob = obstacles[i];
                if (ob == null) continue;  // 收所有(静态+动态)
                MovingObstaclePos[MovingObstacleCount] = ob.transform.position;
                // 排斥半径 = 障碍物半径 + 缓冲,让市民在 mesh 边缘外被推开
                MovingObstacleRad[MovingObstacleCount] = Mathf.Max(ob.size.x, ob.size.y) * 0.5f + 0.5f;
                MovingObstacleCount++;
            }
        }

        // 把所有障碍物占用的格子标记到流场 blocked。先清零再标记。FlowFieldBuildSystem 调用。
        public void WriteBlocked(ref FlowField field)
        {
            for (int i = 0; i < field.blocked.Length; i++) field.blocked[i] = 0;
            if (obstacles == null) return;
            for (int i = 0; i < obstacles.Length; i++)
            {
                var ob = obstacles[i];
                if (ob == null) continue;
                MarkBlockedRect(ref field, ob.transform.position, ob.size, ob.transform.eulerAngles.y);
            }
        }

        // 纯函数:按 pos + size + 旋转角(绕 Y)算覆盖的格子范围,标记 blocked=1。可单测。
        // max 用排他边界(max - eps),避免障碍物右边贴格子边界时多标一格。
        // angleDeg=0 走轴对齐逻辑;否则逐格判断格子中心是否在旋转矩形内(视觉与逻辑一致)。
        public static void MarkBlockedRect(ref FlowField field, float3 pos, float2 size, float angleDeg = 0f)
        {
            if (angleDeg == 0f)
            {
                float3 min = pos - new float3(size.x * 0.5f, 0f, size.y * 0.5f);
                float3 max = pos + new float3(size.x * 0.5f, 0f, size.y * 0.5f);
                int2 minCell = field.WorldToCell(min);
                int2 maxCell = field.WorldToCell(max - new float3(1e-4f, 0f, 1e-4f));
                for (int x = minCell.x; x <= maxCell.x; x++)
                {
                    for (int z = minCell.y; z <= maxCell.y; z++)
                    {
                        int2 c = new int2(x, z);
                        if (field.InBounds(c)) field.blocked[field.CellIndex(c)] = 1;
                    }
                }
                return;
            }

            // 有旋转:旋转矩形的 AABB 圈候选格子,逐格判断格子与矩形是否相交
            // (障碍物擦到格子一角也算,不保守不漏格)。
            float rad = angleDeg * Mathf.Deg2Rad;
            float cosA = math.cos(rad), sinA = math.sin(rad);
            float hx = size.x * 0.5f, hz = size.y * 0.5f;
            float maxExtent = math.length(new float2(hx, hz));  // 旋转后到中心的最远距离
            int2 cMin = field.WorldToCell(pos - new float3(maxExtent, 0f, maxExtent));
            int2 cMax = field.WorldToCell(pos + new float3(maxExtent, 0f, maxExtent));
            float half = field.cellSize * 0.5f;
            for (int x = cMin.x; x <= cMax.x; x++)
            {
                for (int z = cMin.y; z <= cMax.y; z++)
                {
                    int2 c = new int2(x, z);
                    if (!field.InBounds(c)) continue;
                    float3 center = field.CellCenter(c);
                    if (CellIntersectsRotatedRect(center, half, pos, hx, hz, cosA, sinA))
                        field.blocked[field.CellIndex(c)] = 1;
                }
            }
        }

        // 格子(中心 + 半边长)与旋转矩形(中心 pos,半尺寸 hx/hz,旋转 cosA/sinA)是否相交。
        // 用分离轴定理(SAT):矩形两个局部轴 + 格子两个世界轴,4 轴投影都重叠才相交。
        // 比"格子角点在矩形内"精确——边界擦到才算,不膨胀短边方向。
        static bool CellIntersectsRotatedRect(float3 cellCenter, float half, float3 pos, float hx, float hz, float cosA, float sinA)
        {
            float dx = cellCenter.x - pos.x, dz = cellCenter.z - pos.z;

            // 轴1:矩形局部 X 轴(cosA, sinA)。格子中心投影 = cosA*dx + sinA*dz。
            // 格子在 X 轴的半投影 = half*(|cosA|+|sinA|)。
            float rectX = cosA * dx + sinA * dz;
            float cellProjX = half * (math.abs(cosA) + math.abs(sinA));
            if (math.abs(rectX) > hx + cellProjX) return false;

            // 轴2:矩形局部 Z 轴(-sinA, cosA)。
            float rectZ = -sinA * dx + cosA * dz;
            float cellProjZ = half * (math.abs(sinA) + math.abs(cosA));
            if (math.abs(rectZ) > hz + cellProjZ) return false;

            // 轴3:世界 X 轴。矩形半投影 = hx*|cosA| + hz*|sinA|。
            float rectProjX = hx * math.abs(cosA) + hz * math.abs(sinA);
            if (math.abs(dx) > rectProjX + half) return false;

            // 轴4:世界 Z 轴。矩形半投影 = hx*|sinA| + hz*|cosA|。
            float rectProjZ = hx * math.abs(sinA) + hz * math.abs(cosA);
            if (math.abs(dz) > rectProjZ + half) return false;

            return true;  // 四轴都重叠 -> 相交
        }

        // pos 是否在任一障碍物内(用于 spawn 避开障碍物)。
        public bool IsInObstacle(Vector3 pos)
        {
            if (obstacles == null) return false;
            for (int i = 0; i < obstacles.Length; i++)
            {
                var ob = obstacles[i];
                if (ob == null) continue;
                float hx = ob.size.x * 0.5f;
                float hz = ob.size.y * 0.5f;
                Vector3 op = ob.transform.position;
                // 逆旋转 pos 到障碍物局部坐标,判断是否在未旋转矩形内(支持旋转障碍物)。
                float angle = ob.transform.eulerAngles.y * Mathf.Deg2Rad;
                float dx = pos.x - op.x, dz = pos.z - op.z;
                if (angle != 0f)
                {
                    float cosA = Mathf.Cos(angle), sinA = Mathf.Sin(angle);
                    float lx =  cosA * dx + sinA * dz;
                    float lz = -sinA * dx + cosA * dz;
                    if (Mathf.Abs(lx) <= hx && Mathf.Abs(lz) <= hz) return true;
                }
                else if (Mathf.Abs(dx) <= hx && Mathf.Abs(dz) <= hz)
                {
                    return true;
                }
            }
            return false;
        }
    }
}
