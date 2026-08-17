# M5: 多源流场寻路 + 动态障碍物 实现计划

> **For agentic workers:** 用 superpowers:executing-plans 逐任务实现。Steps 用 `- [ ]` 跟踪。TDD:先写失败测试 -> 红 -> 实现 -> 绿 -> commit。

**Goal:** 在 M4 闭环之上,加入多源流场寻路 + 动态障碍物(增量更新)。市民不再直线朝目标走,而是沿流场方向走,能绕开静态障碍物;动态障碍物移动时用 Steering 排斥力临时避让,静止后触发局部增量重算,流场稳定后不再重算。

**Architecture:** 不变。GO 仍是 source of truth,ECS 做高频模拟。新增流场作为 ECS 全局静态资源(不回写 GO,不挂实体),障碍物作为新的 Registry 层。

**Tech Stack:** Unity 6000.5, Entities 1.0, Burst, Unity Behavior 1.0.16。沿用 M1-M4 的 ISystem + IJobEntity + 主线程同步模式。流场生成在主线程(BFS 规模小,<1ms)。

**本计划范围:** M5 = 多源流场(3 张) + 接入 Steering + 静态障碍物 + 动态障碍物增量更新。**不做**威胁系统改造(Flee 保留 Evade 不走流场)、不做 D* Lite 完整实现(用局部重算近似)、不做加权地形(BFS 无权图)。

**参考:** 规格§5(管线)、§9 风险;M3 计划(SpatialGridSystem 模式);面试准备文档(流场设计依据)。

---

## 关键设计决策

1. **流场网格 2m,比 SpatialGrid 1m 粗**。寻路是宏观方向指引,不需要避障那么精细。场景 ~80m×80m -> 40×40=1600 格,BFS <1ms。SpatialGrid(避人)和 FlowField(寻路)是两张独立网格,职责分离。

2. **多源 BFS(无权图)**。把所有同类 POI 同时塞进 BFS 初始队列,算出"每格到最近 POI 的方向"。障碍物格子不可通过。不用 Dijkstra:demo 无加权地形,BFS 足够;障碍物=不可通过比=高代价更简单且符合 demo 场景。

3. **3 张流场**:食物/家/娱乐各一张。市民按 `goal.Type` 查对应流场。Flee 不走流场(保留 Evade,威胁系统已接走高频动态)。

4. **障碍物两层防御**:
   - **流场层**:障碍物格子不可通过,BFS 自动绕开(静态障碍物 + 动态障碍物静止后生效)
   - **Steering 层**:移动中的障碍物作为动态排斥源(临时避让,不触发流场重算)
   - 两层结合:移动中被推开(不穿模),静止后走流场绕开(寻路正确)

5. **动态障碍物状态机**:
   - **移动中**(`isMoving=true`):Steering 排斥力每帧生效(即时避障,不穿模),流场每 **0.5s** 局部重算一次(路径跟上,不被推进死区)
   - **静止累计 1s**(`stationaryTime > 1.0`):`isMoving=false`,触发**一次全量重算**(终态准确,清除移动中局部误差)
   - **静止后**:流场 stable,不再重算(直到下次移动)
   - 0.5s 理由:人眼无感;5m/s×0.5s=2.5m≈1 格,路径变化小;局部重算 0.3ms×2次/s=0.6ms/s,可忽略

6. **增量更新 = 局部重算**(非 D* Lite)。障碍物影响区域(半径 R 格)内重算 BFS:区域内置 INF,从区域边界 cost 已知格子重新扩散。R=10 格(21×21=441 格),局部 BFS <0.3ms。完整 D* Lite 复杂度过高,留作后续优化。

7. **流场方向计算**:BFS 从 POI(目标)反向扩散。访问邻居 n 时,`n.direction = normalize(c.worldPos - n.worldPos)`(指向 c,c 离目标更近)。POI 格子 direction=zero(已到目标)。

8. **障碍物四层标识**(各管不同的事,协作驱动状态机):
   - **GO 层**(`ObstacleAuthoring`):`isMoving`/`lastPosition`/`stationaryTime`/`size` -- 身份证 + 运行时状态
   - **流场层**(`FlowField.blocked` 数组):格子级 `0/1` 标记,BFS 生成路径时跳过 `blocked=1` 的格子
   - **Registry 层**(`ObstacleRegistry`):全局障碍物清单,每帧扫检测移动状态 + 定时触发重算
   - **Steering 层**(`movingObstacles` 临时数组):只收 `isMoving=true` 的障碍物位置/半径,传给 SteeringJob 做排斥力
   - **协作**:移动中走层4(排斥力)+层3(0.5s 定时重算);静止后走层2(blocked 全量重算)+层3(停算)。`isMoving` 是核心切换标识。

---

## 文件结构(M5 新增/改动)

```
Assets/Scripts/CitizenSim/
  Math/FlowFieldMath.cs              # 新:网格坐标转换 + 多源 BFS + 局部重算
  Math/SteeringMath.cs               # 改:加 FlowFieldArrive + ObstacleRepulsion
  Registry/ObstacleRegistry.cs       # 新:障碍物注册 + 脏标记 + 静止判定 + 影响区域计算
  Registry/ObstacleAuthoring.cs      # 新:障碍物 GO 配置(isMoving 检测)
  Systems/FlowFieldBuildSystem.cs    # 新:生成/重算流场(主线程)
  Systems/SteeringSystem.cs          # 改:Arrive -> FlowFieldArrive + 障碍物排斥
  Bootstrap/CitizenBootstrap.cs      # 改:初始化流场 + 注册障碍物
  UI/Hud.cs                          # 改(可选):显示流场状态
Assets/Scripts/CitizenSim.Tests/
  Math/FlowFieldMathTests.cs         # 新:多源 BFS 正确性 + 局部重算
  Math/SteeringMathTests.cs          # 改:FlowFieldArrive + ObstacleRepulsion
  Systems/FlowFieldBuildTests.cs     # 新:障碍物变更触发重算
Assets/Scenes/CitizenSim/CitizenSimScene.unity  # 改:加障碍物 GO
```

---

## Task 1: 流场数据结构 + 单目标 BFS

**Files:**
- Create: `Math/FlowFieldMath.cs`
- Create: `Math/FlowFieldMathTests.cs`

- [ ] **Step 1: FlowFieldMath 数据结构**

```csharp
using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;

namespace CitizenSim
{
    // 流场:固定网格,每格存方向(float3)和代价(float)。
    // 网格原点(0,0)对应世界 (originX, originZ)。格子坐标 = floor((pos - origin) / cellSize)。
    public struct FlowField
    {
        public int2 gridSize;          // 格子数 (x, z)
        public float cellSize;
        public float3 origin;          // 世界原点(格子 (0,0) 的中心)
        public NativeArray<float3> directions;  // 每格方向(normalize),gridSize.x * gridSize.z
        public NativeArray<float> costs;        // 每格代价,INF=未访问/不可达
        public NativeArray<byte> blocked;       // 0=可走,1=障碍物

        public int CellCount => gridSize.x * gridSize.y;
        public int2 WorldToCell(float3 pos) => (int2)math.floor((pos - origin).xz / cellSize);
        public float3 CellCenter(int2 cell) => origin + new float3((cell.x + 0.5f) * cellSize, 0, (cell.y + 0.5f) * cellSize);
        public bool InBounds(int2 cell) => cell.x >= 0 && cell.x < gridSize.x && cell.y >= 0 && cell.y < gridSize.y;
        public int CellIndex(int2 cell) => cell.x + cell.y * gridSize.x;
    }
```

- [ ] **Step 2: 单目标 BFS(纯函数,可单测)**

```csharp
    public static class FlowFieldMath
    {
        const float k_Inf = 1e9f;

        // 单目标 BFS:从单个目标格子反向扩散,填 directions/costs。
        // blocked 格子跳过(不可通过)。用 NativeQueue 做前沿(无权 BFS)。
        public static void BuildSingleTarget(ref FlowField field, int2 targetCell)
        {
            // 初始化:costs=INF, directions=zero
            for (int i = 0; i < field.CellCount; i++) { field.costs[i] = k_Inf; field.directions[i] = float3.zero; }

            if (!field.InBounds(targetCell) || field.blocked[field.CellIndex(targetCell)] == 1) return;

            var queue = new NativeQueue<int2>(Allocator.Temp);
            field.costs[field.CellIndex(targetCell)] = 0f;
            queue.Enqueue(targetCell);

            int2[] offsets = { new(1,0), new(-1,0), new(0,1), new(0,-1) };  // 4 邻域
            while (queue.TryDequeue(out var c))
            {
                float costC = field.costs[field.CellIndex(c)];
                foreach (var off in offsets)
                {
                    int2 n = c + off;
                    if (!field.InBounds(n)) continue;
                    int ni = field.CellIndex(n);
                    if (field.blocked[ni] == 1) continue;
                    float newCost = costC + 1f;
                    if (newCost < field.costs[ni])
                    {
                        field.costs[ni] = newCost;
                        // 方向指向 c(c 离目标更近)
                        field.directions[ni] = math.normalizesafe(field.CellCenter(c) - field.CellCenter(n));
                        queue.Enqueue(n);
                    }
                }
            }
            queue.Dispose();
        }
    }
}
```

- [ ] **Step 3: 写失败测试**

```csharp
// FlowFieldMathTests.cs
[Test] public void SingleTarget_DirectionsPointTowardTarget()
{
    // 5x5 网格,目标在 (4,4),检查 (0,0) 方向指向 (+x,+z)
    var field = MakeTestField(5, 5, 1f, float3.zero);
    FlowFieldMath.BuildSingleTarget(ref field, new int2(4, 4));
    var dir00 = field.directions[field.CellIndex(new int2(0, 0))];
    Assert.IsTrue(dir00.x > 0.5f && dir00.z > 0.5f, $"(0,0) 方向应指向(+x,+z),实际 {dir00}");
    field.Dispose();
}

[Test] public void SingleTarget_BlockedCell_ReachableViaDetour()
{
    // 目标 (4,0),(2,0) 是障碍,检查 (0,0) 能绕道到达(方向不指向障碍)
    var field = MakeTestField(5, 1, 1f, float3.zero);
    field.blocked[field.CellIndex(new int2(2, 0))] = 1;
    FlowFieldMath.BuildSingleTarget(ref field, new int2(4, 0));
    // (0,0) 的 cost 应不是 INF(可绕道,但 1x5 网格被 (2,0) 堵死,实际不可达)
    // 改成 5x3 网格让绕道可能
}

[Test] public void SingleTarget_TargetBlocked_AllInf()
{
    // 目标本身是障碍 -> 全场 INF
}

[Test] public void SingleTarget_TargetCell_ZeroDirection()
{
    // 目标格子自身 direction=zero, cost=0
}
```

- [ ] **Step 4: 实现 + 运行绿 + Commit**
```bash
git add Assets/Scripts/CitizenSim/Math/FlowFieldMath.cs Assets/Scripts/CitizenSim.Tests/Math/FlowFieldMathTests.cs
git commit -m "feat(m5): flow field data structure + single-target BFS"
```

---

## Task 2: 多源流场(3 张)

**Files:**
- Modify: `Math/FlowFieldMath.cs`(加 `BuildMultiSource`)
- Create: `Systems/FlowFieldBuildSystem.cs`
- Modify: `Math/FlowFieldMathTests.cs`

- [ ] **Step 1: 多源 BFS**

```csharp
// 多源 BFS:所有 sources 同时入队(多目标 POI),算出每格到最近源的 direction。
// 这正是"找最近 POI"的流场解法:成本与单目标一致,天然选最近。
public static void BuildMultiSource(ref FlowField field, NativeList<int2> sources)
{
    for (int i = 0; i < field.CellCount; i++) { field.costs[i] = k_Inf; field.directions[i] = float3.zero; }

    var queue = new NativeQueue<int2>(Allocator.Temp);
    for (int i = 0; i < sources.Length; i++)
    {
        int2 s = sources[i];
        if (!field.InBounds(s) || field.blocked[field.CellIndex(s)] == 1) continue;
        field.costs[field.CellIndex(s)] = 0f;
        queue.Enqueue(s);
    }
    // BFS 扩散同 BuildSingleTarget
    // ...
    queue.Dispose();
}
```

- [ ] **Step 2: FlowFieldBuildSystem 设计**

```csharp
// 3 张流场(食物/家/娱乐),全局静态。POI 注册时生成,障碍物变更时重算。
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateBefore(typeof(SteeringSystem))]
[UpdateAfter(typeof(SnapshotSystem))]
public partial class FlowFieldBuildSystem : ISystem
{
    public static FlowField FoodField;
    public static FlowField HomeField;
    public static FlowField FunField;
    public static bool Dirty;  // 障碍物变更标记,触发重算

    FlowFieldConfig config;   // gridSize, cellSize, origin

    public void OnCreate(ref SystemState state) { /* 初始化 config */ }
    public void OnDestroy(ref SystemState state) { /* Dispose 3 张 */ }

    public void OnUpdate(ref SystemState state)
    {
        // 首次生成 + Dirty 时重算
        // 读 PoiRegistry.GetFoodPositions/Home/Fun -> 转格子坐标 -> BuildMultiSource
        // Dirty=false 时跳过(零成本)
    }
}
```

流场配置(场景常量):
```csharp
static readonly int2 k_GridSize = new int2(40, 40);   // 40x40 格
const float k_CellSize = 2f;                           // 2m
static readonly float3 k_Origin = new float3(-40f, 0, -40f);  // 覆盖 80m x 80m 场景
```

- [ ] **Step 3: 写失败测试**

```csharp
[Test] public void MultiSource_TwoSources_PicksNearest()
{
    // 10x1 网格,源在 (0,0) 和 (9,0),检查 (5,0) 方向不确定(等距)
    // 检查 (2,0) 指向 (0,0),(7,0) 指向 (9,0)
    var field = MakeTestField(10, 1, 1f, float3.zero);
    var sources = new NativeList<int2>(Allocator.Temp);
    sources.Add(new int2(0, 0)); sources.Add(new int2(9, 0));
    FlowFieldMath.BuildMultiSource(ref field, sources);
    var dir2 = field.directions[field.CellIndex(new int2(2, 0))];
    Assert.IsTrue(dir2.x < -0.5f, "(2,0) 应指向左(0,0)");
    var dir7 = field.directions[field.CellIndex(new int2(7, 0))];
    Assert.IsTrue(dir7.x > 0.5f, "(7,0) 应指向右(9,0)");
}

[Test] public void MultiSource_BlockedBetween_DetoursAround()
{
    // 两源中间有障碍,验证方向绕开
}
```

- [ ] **Step 4: 实现系统 + 读 POI 生成 3 张流场**

```csharp
void RebuildAll()
{
    var poi = PoiRegistry.Instance;
    RebuildField(ref FoodField, poi != null ? poi.GetFoodPositions() : null);
    RebuildField(ref HomeField, poi != null ? poi.GetHomePositions() : null);
    RebuildField(ref FunField,  poi != null ? poi.GetFunPositions()  : null);
    Dirty = false;
}

void RebuildField(ref FlowField field, Vector3[] poiPositions)
{
    var sources = new NativeList<int2>(Allocator.Temp);
    if (poiPositions != null)
        for (int i = 0; i < poiPositions.Length; i++)
            sources.Add(field.WorldToCell(poiPositions[i]));
    FlowFieldMath.BuildMultiSource(ref field, sources);
    sources.Dispose();
}
```

- [ ] **Step 5: 运行绿 + Commit**
```bash
git add <Task2 文件 + .meta>
git commit -m "feat(m5): multi-source flow field + build system (3 fields)"
```

---

## Task 3: 接入 Steering(市民沿流场走)

**Files:**
- Modify: `Math/SteeringMath.cs`(加 `FlowFieldArrive`)
- Modify: `Systems/SteeringSystem.cs`(用流场替换 Arrive)
- Modify: `Math/SteeringMathTests.cs`

- [ ] **Step 1: SteeringMath.FlowFieldArrive**

```csharp
// 沿流场方向走,接近目标(任意 POI 格子,cost < 阈值)时减速。
// 查当前格子的 direction,乘 speed。cost < slowCost 时线性减速(到 POI 附近)。
public static float3 FlowFieldArrive(
    float3 pos, int2 cell, in FlowField field, float speed, float slowCost)
{
    if (!field.InBounds(cell)) return float3.zero;
    int ci = field.CellIndex(cell);
    float cost = field.costs[ci];
    if (cost >= 1e9f) return float3.zero;  // 不可达,停
    float3 dir = field.directions[ci];
    float v = cost < slowCost ? speed * (cost / slowCost) : speed;  // 接近减速
    return dir * v;
}
```

- [ ] **Step 2: 写失败测试**

```csharp
[Test] public void FlowFieldArrive_FollowsDirection()
{
    // 造一张流场,(0,0) 方向 (1,0,0),speed=2 -> 返回 (2,0,0)
}
[Test] public void FlowFieldArrive_NearTarget_SlowDown()
{
    // cost=1, slowCost=4 -> v = 2 * (1/4) = 0.5
}
[Test] public void FlowFieldArrive_Unreachable_Zero()
{
    // cost=INF -> 返回 zero
}
```

- [ ] **Step 3: SteeringSystem 改用流场**

```csharp
[BurstCompile]
public partial struct SteeringJob : IJobEntity
{
    public float Speed;
    public float SlowCost;
    // ... 原有 grid/positions/avoid 参数
    [ReadOnly] public FlowField FoodField;
    [ReadOnly] public FlowField HomeField;
    [ReadOnly] public FlowField FunField;

    void Execute(ref SimVelocity vel, in SimPosition pos, in SimGoal goal, in GridCell cell, in CitizenIndex idx)
    {
        // Flee 仍走 Evade(不走流场)
        float3 arrive = goal.Type == GoalType.Flee
            ? SteeringMath.Evade(pos.Value, goal.Target, Speed)
            : FlowArriveForGoal(pos.Value, cell.Value, goal.Type, Speed, SlowCost);

        // 邻居避障(不变)
        float3 rep = ...;  // 原 9 邻域逻辑保留
        float3 v = arrive + rep * AvoidStrength;
        // 限速(不变)
        vel.Value = v;
    }

    float3 FlowArriveForGoal(float3 pos, int2 cell, GoalType type, float speed, float slowCost)
    {
        switch (type)
        {
            case GoalType.SeekFood: return SteeringMath.FlowFieldArrive(pos, cell, FoodField, speed, slowCost);
            case GoalType.SeekHome: return SteeringMath.FlowFieldArrive(pos, cell, HomeField, speed, slowCost);
            case GoalType.SeekFun:  return SteeringMath.FlowFieldArrive(pos, cell, FunField,  speed, slowCost);
            default: return SteeringMath.Arrive(pos, /* Wander 目标 */ float3.zero, speed, 0.5f);  // Wander 仍用 Arrive(无固定 POI)
        }
    }
}
```

**注意 Wander**:Wander 没有固定 POI,不走流场,仍用原 Arrive 朝航点走。流场只管 SeekFood/SeekHome/SeekFun。

- [ ] **Step 4: Burst 兼容性**
  - `FlowField` 含 `NativeArray`,在 Burst Job 内只读访问 OK。
  - SteeringSystem.OnUpdate 把 3 张流场传进 job。
  - 注意:`FlowFieldBuildSystem` 必须在 SteeringSystem 前 Complete(主线程 BFS 已同步,无依赖问题)。

- [ ] **Step 5: Play 验证**
  - 市民不再直线冲 POI,而是沿流场方向走。
  - 多个 POI 时,市民朝最近那个走(多源生效)。
  - 编辑器 Gizmo 画流场方向(可选,调试用)。

- [ ] **Step 6: Commit**
```bash
git add <Task3 文件 + .meta>
git commit -m "feat(m5): steering follows flow field directions"
```

---

## Task 4: 静态障碍物 + 全量重算

**Files:**
- Create: `Registry/ObstacleAuthoring.cs`
- Create: `Registry/ObstacleRegistry.cs`
- Modify: `Systems/FlowFieldBuildSystem.cs`(生成时标记 blocked)
- Create: `Systems/FlowFieldBuildTests.cs`

- [ ] **Step 1: ObstacleAuthoring**

```csharp
// 障碍物 GO 配置。标记占用网格的哪些格子(按障碍物 bounds 算)。
// isMoving 由 ObstacleRegistry 每帧检测(位置变化)。
public class ObstacleAuthoring : MonoBehaviour
{
    public Vector2 size = new Vector2(2f, 2f);  // 占地尺寸(x,z)
    [HideInInspector] public bool isMoving;
    [HideInInspector] public Vector3 lastPosition;
    [HideInInspector] public float stationaryTime;
}
```

- [ ] **Step 2: ObstacleRegistry**

```csharp
// 障碍物单例。管理所有 ObstacleAuthoring,每帧检测移动状态,标记流场脏。
public class ObstacleRegistry : MonoBehaviour
{
    public static ObstacleRegistry Instance { get; private set; }
    public ObstacleAuthoring[] obstacles;

    void OnEnable() => Instance = this;
    void OnDisable() { if (Instance == this) Instance = null; }

    // 每帧检测:位置变 -> isMoving=true, stationaryTime=0;不变 -> 累计,>1s -> isMoving=false + 触发重算
    void Update()
    {
        bool anyNewlyStatic = false;
        foreach (var ob in obstacles)
        {
            if (ob == null) continue;
            Vector3 p = ob.transform.position;
            if (Vector3.SqrMagnitude(p - ob.lastPosition) > 1e-4f)
            {
                ob.isMoving = true;
                ob.stationaryTime = 0f;
                ob.lastPosition = p;
            }
            else if (ob.isMoving)
            {
                ob.stationaryTime += Time.deltaTime;
                if (ob.stationaryTime > 1f)
                {
                    ob.isMoving = false;
                    anyNewlyStatic = true;  // 刚静止,触发重算
                }
            }
        }
        if (anyNewlyStatic) FlowFieldBuildSystem.Dirty = true;
    }

    // 把障碍物占用的格子标记到流场的 blocked 数组
    public void WriteBlocked(ref FlowField field)
    {
        for (int i = 0; i < field.blocked.Length; i++) field.blocked[i] = 0;
        if (obstacles == null) return;
        foreach (var ob in obstacles)
        {
            if (ob == null) continue;
            // 按 size 算覆盖的格子范围,标记 blocked=1
            MarkBlockedRect(ref field, ob.transform.position, ob.size);
        }
    }

    static void MarkBlockedRect(ref FlowField field, float3 pos, float2 size)
    {
        int2 minCell = field.WorldToCell(pos - new float3(size.x * 0.5f, 0, size.y * 0.5f));
        int2 maxCell = field.WorldToCell(pos + new float3(size.x * 0.5f, 0, size.y * 0.5f));
        for (int x = minCell.x; x <= maxCell.x; x++)
            for (int z = minCell.y; z <= maxCell.y; z++)
            {
                int2 c = new int2(x, z);
                if (field.InBounds(c)) field.blocked[field.CellIndex(c)] = 1;
            }
    }
}
```

- [ ] **Step 3: FlowFieldBuildSystem 集成障碍物**

```csharp
void RebuildAll()
{
    var obs = ObstacleRegistry.Instance;
    // 三张流场都写同一份障碍物 blocked
    if (obs != null) { obs.WriteBlocked(ref FoodField); obs.WriteBlocked(ref HomeField); obs.WriteBlocked(ref FunField); }
    // 再多源 BFS(blocked 已填)
    RebuildField(ref FoodField, poi.GetFoodPositions());
    RebuildField(ref HomeField, poi.GetHomePositions());
    RebuildField(ref FunField,  poi.GetFunPositions());
    Dirty = false;
}
```

- [ ] **Step 4: 写失败测试**

```csharp
[Test] public void Obstacle_Blocked_Bypassed()
{
    // 场景:目标 (4,0),(2,0) 是障碍。5x3 网格。
    // 检查 (0,0) 的 direction 指向绕道(z 方向),不是直撞障碍
    var field = MakeTestField(5, 3, 1f, float3.zero);
    field.blocked[field.CellIndex(new int2(2, 0))] = 1;
    var sources = new NativeList<int2>(Allocator.Temp); sources.Add(new int2(4, 0));
    FlowFieldMath.BuildMultiSource(ref field, sources);
    var dir = field.directions[field.CellIndex(new int2(0, 0))];
    // (0,0) 应指向绕道方向(z!=0),而不是直撞 (2,0)
    Assert.IsTrue(math.abs(dir.z) > 0.3f, "(0,0) 应绕道(z 方向)");
}

[Test] public void Obstacle_BlockedTarget_AllInf()
{
    // 目标被障碍占 -> 该流场全 INF
}
```

- [ ] **Step 5: Play 验证**
  - 场景放几个静态障碍物(立方体)。
  - 市民绕开障碍物走到 POI(不再穿墙)。
  - 移动障碍物(编辑器拖动) -> 静止 1s 后流场更新,市民走新路径。

- [ ] **Step 6: Commit**
```bash
git add <Task4 文件 + .meta>
git commit -m "feat(m5): static obstacles + full flow field rebuild"
```

---

## Task 5: 动态障碍物 + 增量重算

**Files:**
- Modify: `Math/FlowFieldMath.cs`(加 `RebuildRegion` 局部重算)
- Modify: `Math/SteeringMath.cs`(加 `ObstacleRepulsion`)
- Modify: `Systems/SteeringSystem.cs`(移动障碍物排斥力)
- Modify: `Registry/ObstacleRegistry.cs`(批量合并重算 + 影响区域)

- [ ] **Step 1: 移动中障碍物的 Steering 排斥力**

```csharp
// SteeringMath.ObstacleRepulsion:移动障碍物作为排斥源(类似邻居排斥,但半径更大)。
// 障碍物静止后不用这个(流场已绕开),只移动时临时避让。
public static float3 ObstacleRepulsion(float3 pos, NativeArray<float3> movingObstacles, NativeArray<float> radii, float strength)
{
    float3 sum = float3.zero;
    for (int i = 0; i < movingObstacles.Length; i++)
    {
        float3 away = pos - movingObstacles[i];
        float d2 = math.lengthsq(away);
        float r = radii[i];
        if (d2 > 1e-6f && d2 < r * r)
        {
            float d = math.sqrt(d2);
            sum += (away / d) * (1f - d / r) * strength;  // 越近越强
        }
    }
    return sum;
}
```

- [ ] **Step 2: SteeringSystem 加障碍物排斥(只对移动中障碍物)**

```csharp
// SteeringJob 增参:[ReadOnly] movingObstaclePos, [ReadOnly] movingObstacleRad
// 这些来自 ObstacleRegistry 每帧收集 isMoving=true 的障碍物
float3 obstacleRep = SteeringMath.ObstacleRepulsion(pos.Value, movingObstacles, movingRadii, ObstacleStrength);
float3 v = arrive + rep * AvoidStrength + obstacleRep * ObstacleStrength;
```

- [ ] **Step 3: 局部增量重算 FlowFieldMath.RebuildRegion**

```csharp
// 局部重算:影响区域(中心 cellA、cellB,半径 R 格)内重算 BFS。
// 1. 区域内置 cost=INF, direction=zero
// 2. 收集区域边界(区域外紧邻、cost<INF 且非障碍)的格子,作为多源
// 3. 从边界源 BFS 扩散进区域,更新 cost/direction
// 比 D* Lite 简单:不处理"区域外 direction 指向区域内"的级联更新(R 取大些近似)。
public static void RebuildRegion(ref FlowField field, NativeList<int2> changedCells, int radius)
{
    // 收集影响区域
    var region = new NativeHashSet<int2>(Allocator.Temp);
    foreach (var c in changedCells)
        for (int dx = -radius; dx <= radius; dx++)
            for (int dz = -radius; dz <= radius; dz++)
            {
                int2 n = c + new int2(dx, dz);
                if (field.InBounds(n)) region.Add(n);
            }

    // 区域内置 INF
    foreach (var c in region) { int i = field.CellIndex(c); field.costs[i] = k_Inf; field.directions[i] = float3.zero; }
    // 重新写 blocked(障碍物格子)
    // ... (由调用方 WriteBlocked 后再 RebuildRegion)

    // 收集边界源:区域外的格子,cost<INF,非障碍
    var queue = new NativeQueue<int2>(Allocator.Temp);
    foreach (var c in region)
    {
        int2[] offsets = { new(1,0), new(-1,0), new(0,1), new(0,-1) };
        foreach (var off in offsets)
        {
            int2 n = c + off;
            if (!region.Contains(n) && field.InBounds(n))
            {
                int ni = field.CellIndex(n);
                if (field.blocked[ni] == 0 && field.costs[ni] < k_Inf)
                    queue.Enqueue(n);  // 边界源,保留原 cost
            }
        }
    }
    // 从边界源 BFS 扩散进区域(同 BuildMultiSource 逻辑)
    // ...
    queue.Dispose(); region.Dispose();
}
```

- [ ] **Step 4: ObstacleRegistry 状态机 + 0.5s 定时重算 + 静止全量重算**

```csharp
float rebuildTimer;
const float k_MovingRebuildInterval = 0.5f;
const float k_StationaryThreshold = 1f;
const int k_RegionRadius = 10;

void Update()
{
    bool anyMoving = false;
    bool anyNewlyStatic = false;
    var changedCells = new NativeList<int2>(Allocator.Persistent);

    foreach (var ob in obstacles)
    {
        if (ob == null) continue;
        Vector3 p = ob.transform.position;
        if (Vector3.SqrMagnitude(p - ob.lastPosition) > 1e-4f)
        {
            // 移动了:记录新旧格子(供局部重算),重置静止计时
            changedCells.Add(FlowFieldBuildSystem.FoodField.WorldToCell(ob.lastPosition));
            changedCells.Add(FlowFieldBuildSystem.FoodField.WorldToCell(p));
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
    if (anyMoving && changedCells.Length > 0)
    {
        rebuildTimer += Time.deltaTime;
        if (rebuildTimer >= k_MovingRebuildInterval)
        {
            rebuildTimer = 0f;
            FlowFieldBuildSystem.RebuildRegions(changedCells, k_RegionRadius);
        }
    }

    // 静止后:一次全量重算(终态准确,清除移动中局部误差)
    if (anyNewlyStatic)
    {
        FlowFieldBuildSystem.RebuildAll();
        rebuildTimer = 0f;
    }
    changedCells.Dispose();
}
```

**关键**:移动中用局部重算(便宜,0.3ms),静止后用全量重算(准确,1ms)。两者配合:移动中路径跟得上,静止后流场终态精确,之后停算。`isMoving` 是层4(Steering 排斥力)的切换标识--静止后障碍物不再进 `movingObstacles` 数组,排斥力消失,市民改走流场新路径。

- [ ] **Step 5: 写失败测试**

```csharp
[Test] public void RebuildRegion_AfterObstacleMove_UpdatesLocalField()
{
    // 全图建好流场(障碍在 A),把障碍从 A 移到 B,局部重算
    // 检查 A 附近格子 direction 恢复(不再绕 A),B 附近格子 direction 绕 B
}

[Test] public void MovingObstacle_RepulsionPushesAway()
{
    // SteeringMath.ObstacleRepulsion:市民在障碍半径内,方向背离障碍
}
```

- [ ] **Step 6: Play 验证**
  - 运行时拖动一个障碍物穿过人群:
    - 移动中:市民被障碍排斥力推开(不穿模)。
    - 移动中持续 >0.5s:流场每 0.5s 局部更新,路径跟上(Profiler 确认有周期性重算)。
    - 静止 1s 后:一次全量重算,市民走新路径绕开。
    - 静止后:流场 stable,Profiler 确认不再重算。
  - 多个障碍物同时静止:一次合并重算,不逐个触发。

- [ ] **Step 7: Commit**
```bash
git add <Task5 文件 + .meta>
git commit -m "feat(m5): dynamic obstacles + incremental region rebuild"
```

---

## Task 6: 场景障碍物 + 验收

**Files:**
- Modify: `Assets/Scenes/CitizenSim/CitizenSimScene.unity`

- [ ] **Step 1: 场景加障碍物**
  - 放 3-5 个立方体障碍物,挂 `ObstacleAuthoring`,设 size。
  - 场景挂 `ObstacleRegistry`,拖入障碍物数组。
  - 可选:加一个可拖动的障碍物(运行时用 WASD 移动,演示动态避障)。

- [ ] **Step 2: 流场 Gizmo(调试用,可选)**

```csharp
#if UNITY_EDITOR
// FlowFieldBuildSystem.OnDrawGizmos:画流场方向箭头(每格一个小箭头)
// 只在 Scene 视图画,Game 视图不画。便于调试流场正确性。
#endif
```

- [ ] **Step 3: Play 验收**
  - 市民沿流场走,绕开静态障碍物(不穿墙)。
  - 拖动障碍物:移动中市民被推开(不穿模),静止 1s 后走新路径。
  - 多 POI 时市民朝最近 POI(多源生效)。
  - Flee 仍走 Evade(威胁系统不受影响)。
  - HUD 正常,Console 无报错。
  - EditMode 全测试 PASS(M1-M5)。
  - Profiler 抽测:500 规模流场重算 < 1ms,局部重算 < 0.3ms。

- [ ] **Step 4: 更新 README + 面试文档**
  - README 加 M5 里程碑说明 + 流场寻路段落。
  - 面试文档补"流场寻路"实战经验。

- [ ] **Step 5: Commit**
```bash
git add Assets/Scenes/CitizenSim/CitizenSimScene.unity README.md
git commit -m "feat(m5): scene obstacles + acceptance + docs"
```

---

## M5 验收清单

- [ ] 市民沿流场方向走(不再直线冲 POI)
- [ ] 多 POI 时市民朝最近 POI(多源流场生效)
- [ ] 静态障碍物被绕开(不穿墙)
- [ ] 动态障碍物移动中:市民被推开(Steering 排斥力,不穿模)
- [ ] 动态障碍物移动中:流场每 0.5s 局部重算(Profiler 确认,路径跟上)
- [ ] 动态障碍物静止 1s 后:一次全量重算,市民走新路径
- [ ] 静止后流场 stable,Profiler 确认不再重算
- [ ] Flee 仍走 Evade(威胁系统不受影响)
- [ ] Wander 仍用 Arrive(无固定 POI,不走流场)
- [ ] EditMode 测试全 PASS(M1-M5)
- [ ] Console 无报错
- [ ] 500 规模流场重算 < 1ms,局部重算 < 0.3ms(抽测)

---

## 风险 / 决策点

1. **流场格子尺寸(2m)**:障碍物小于 2m 可能漏标。解法:`MarkBlockedRect` 按 size 算覆盖格子,最小占 1 格。若障碍物很小,设 size ≥ cellSize。
2. **增量更新边界精度**:局部重算不处理"区域外 direction 指向区域内"的级联更新。解法:R 取大(10 格),覆盖可能受影响的范围。极端情况(障碍物堵死主干道)可能不准,这时 fallback 全量重算(Dirty 标记强制全量)。
3. **BFS 在 Burst Job 内**:当前 `FlowFieldMath.BuildMultiSource` 用 `NativeQueue`,可在 Burst Job 内跑。但 demo 规模(1600 格)主线程 BFS <1ms,先主线程跑通,若成瓶颈再移到 Job。
4. **多障碍物同时移动**:每个都触发排斥力收集,`ObstacleRegistry` 每帧重建 `movingObstacles` 数组(用静态缓存免 GC)。5000 市民 × N 障碍物的排斥力计算在 SteeringJob 内并行,成本可控(N 障碍物通常 < 10)。
5. **流场内存**:3 张 × 1600 格 × (float3 direction=12B + float cost=4B + byte blocked=1B) ≈ 82KB,可忽略。
6. **Wander 不走流场**:Wander 目标是随机航点,不是 POI。仍用原 Arrive。若未来要 Wander 也避障,可给 Wander 也建一张"随机航点流场"(过度工程,不做)。

## Self-Review

**Spec 覆盖**:M5 不在原 spec §7 里程碑内(M1-M4 已覆盖 spec 全部交付物)。M5 是面试驱动的新增里程碑,补全 Gameplay 寻路技能点。spec §5 管线新增 FlowFieldBuildSystem(在 Snapshot 后、Steering 前),§9 风险新增"流场重算性能"(已量化 <1ms)。

**架构一致性**:流场是 ECS 全局静态资源(不回写 GO,不挂实体),与 SpatialGrid(中间量)、Threatened(enableable bit)同属"ECS 优化层"哲学。障碍物层(ObstacleRegistry)与 PoiRegistry/ThreatZoneRegistry 同属"Registry 单例"模式。Steering 层"目标驱动 + 避人 + 避障"三力叠加,沿用 M3 的合力 + 限速结构。

**已知 API 风险**:
- `NativeQueue<int2>` 在 Burst Job 内:`TryDequeue`/`Enqueue` 标准用法,确认 Burst 兼容。
- `NativeHashSet<int2>` 在 `RebuildRegion`:Burst 兼容,但 `foreach` 迭代需确认。fallback:用 `NativeList<int2>` + 去重。
- `FlowField` 结构含 `NativeArray` 传进 IJobEntity:作为 `[ReadOnly]` 参数,Burst 兼容。注意 `FlowField` 是 `struct`,传值会拷贝指针(共享底层数组),符合预期。

**未实现(留作后续)**:
- D* Lite 完整增量更新(当前用局部重算近似)
- 加权地形(沼泽/道路,当前 BFS 无权)
- 流场可视化工具(Scene 视图 Gizmo,可选)
- Wander 走流场(当前保留 Arrive)
