# CitizenSim 类与方法级 API 参考

> 精细到每个类、每个方法(含签名与职责)的代码总结。覆盖 `Assets/Scripts/CitizenSim` 全部运行时代码 + 测试。
> 与 `docs/市民模拟-program-architecture.md`(宏观架构)互补,本文是代码级速查表。
>
> 最后更新:2026-08-10,分支 `feat/m1-ecs-sync-spine`(代码已到 M5 流场寻路 + 人形模型)。

---

## 目录结构

| 目录 | 职责 |
|------|------|
| `Components/` | ECS 组件(struct) |
| `Systems/` | ECS 系统(ISystem / SystemBase) |
| `Behavior/` | Unity Behavior BT 节点 + GoalDecision 纯逻辑 + BtScheduler |
| `Math/` | 纯函数工具(流场/steering/威胁/POI) |
| `Registry/` | MonoBehaviour 单例(数据源) |
| `Bootstrap/` | 场景启动器 |
| `UI/` | HUD / 调试可视化 / 交互 |
| `CitizenSim.Tests/` | EditMode 单元测试(90+ 个) |

**运行时更新顺序**(SimulationSystemGroup 内):

```
Snapshot → NeedsDecay / SpatialGrid / ThreatDetection / FlowFieldBuild → Steering → Resolve → Coloring
```

---

## 一、Components — `Components/SimComponents.cs`

### `enum GoalType`
市民目标状态,驱动行为与颜色:
- `Wander = 0`(漫游)、`SeekFood = 1`(觅食)、`SeekHome = 2`(回家)、`SeekFun = 3`(娱乐)、`Flee = 4`(逃离)

### ECS 组件 struct(均 `IComponentData`)

| 组件 | 字段 | 职责 |
|------|------|------|
| `SimPosition` | `float3 Value` | 世界位置(每帧 Snapshot 写) |
| `SimVelocity` | `float3 Value` | 速度(Steering 计算) |
| `SimGoal` | `GoalType Type; float3 Target` | 目标类型 + 目标点 |
| `SimRadius` | `float Value` | 市民半径(Bootstrap 设 0.5;当前未参与核心逻辑) |
| `CitizenIndex` | `int Value` | 市民索引(对应 GO 数组下标) |
| `GridCell` | `int2 Value` | 空间哈希格坐标(SpatialGrid 每帧写) |
| `Threatened` | `IEnableableComponent`(空体) | enableable 威胁标记,默认 disabled,零 archetype 变更 |
| `SimNeeds` | `float3 Value` | x=hunger, y=fatigue, z=fun(均 0..1) |
| `SimExit` | `float Timer; float3 Direction` | 离开推力(吃饱/休息够后 1s 背离原 POI) |

---

## 二、Registry(MonoBehaviour 单例,数据源)

### `CitizenAuthoring` — 市民 GO 数据容器
无方法。字段:
- `Index`
- `needs`(x=hunger / y=fatigue / z=fun)
- 阈值:`hungerThreshold=0.7 / fullThreshold=0`、`fatigueThreshold=0.7 / restedThreshold=0`、`boredThreshold=0.3 / funFullThreshold=0.9`
- `currentGoalType / currentGoalTarget`(BT 写,ECS 读)
- `threatened`、`lastGoalType / exitTimer / exitDirection`(exit 推力)
- `capsuleRenderer`(着色)、`animator`(人形动画)、`moveSpeed`(动画速度,Resolve 写)

### `CitizenRegistry` — 市民注册表单例
- 静态 `Instance`
- 字段:`GameObjects[]`、`Entities[]`、`Authoring[]`(缓存免 GetComponent)、`Renderers[]`;`Count`
- `OnEnable()/OnDisable()` — 单例生命周期
- `Register(GameObject[], Entity[])` — 填充数组 + 缓存 Authoring/Renderer

### `PoiRegistry` — POI(食物/家/娱乐)单例
- 静态 `Instance`;字段 `foodPoints[]/homePoints[]/funPoints[]`
- `OnEnable/OnDisable/Register/Clear` — 单例(测试用 Register/Clear)
- `GetFoodPositions()/GetHomePositions()/GetFunPositions()` → `Vector3[]`
- `static ToArray()` — 内部位置转换工具
- `OnDrawGizmos()` — Scene 视图画范围球(`NeedsDecaySystem.PoIRadius`)

### `ThreatZoneRegistry` — 威胁区单例
- 静态 `Instance`;字段 `zones[]`(常驻)、`radius=5f`、`active`;嵌套 `struct TempZone { Vector3 position; float radius; float expireTime; }` + `List<TempZone> tempZones`
- `OnEnable/OnDisable/Register/Clear` — 单例
- `GetZonePositions()` — 仅常驻区位置
- `GetActiveZonePositions()` — 常驻 + 临时区(受 active 开关),BT Flee 用
- `SpawnTempZone(pos, radius, duration)` — 加临时恐惧区
- `Update()` — 清理过期临时区
- `GetActiveZones(List<Vector3> outPos, List<float> outRad)` — 输出活动区位置+半径(复用托管 List 免 GC),ThreatDetection 用
- `TempZoneCount` — 属性
- `OnDrawGizmos()` — 场景画威胁球

### `ObstacleRegistry` — 障碍物单例
- 静态 `Instance`;字段 `obstacles[]`;静态 `MovingObstaclePos/Rad`(NativeArray)、`MovingObstacleCount`;常量 `k_MaxMoving=64`、`k_MovingRebuildInterval=0.5f`、`k_StationaryThreshold=1f`、`k_RegionRadius=10`
- `OnEnable/OnDisable/Register/Clear` — 单例
- `DisposeStatics()` — 测试释放静态数组
- `Update()` — 状态机:检测移动/静止;移动中每 0.5s 局部重算流场,静止后全量重算
- `CollectMovingObstacles()` — 填充移动障碍位置/半径(Steering 排斥)
- `WriteBlocked(ref FlowField)` — 标记所有障碍占用的格子
- `static MarkBlockedRect(ref FlowField, pos, size, angleDeg=0)` — **旋转感知格子标记**:angle=0 走轴对齐;有旋转用 **SAT 精确相交**
- `static CellIntersectsRotatedRect(...)` — 格子 vs 旋转矩形相交(SAT,4 轴投影)
- `IsInObstacle(pos)` — spawn 避开(支持旋转)

### `ObstacleAuthoring` — 障碍物 GO 配置
- 字段 `size`(x,z 占地)、`height`、`isMoving/lastPosition/stationaryTime`
- `OnValidate()` — 编辑模式只在 size 变化时同步 x/z(不覆盖用户拖的 scale.y)
- `SyncSize()` — 运行时同步 x/z

### `FlowFieldConfig` — 流场网格配置单例
- 静态 `Instance`;字段 `ground`(Transform)、`gridSize=(40,40)`、`cellSize=2f`
- `GridSize` 属性 → int2;`Origin` 属性 = 地面中心 - 网格半边长
- `OnEnable/OnDisable/Register/Clear` — 单例

---

## 三、Math(纯函数,可单测)

### `FlowFieldMath`
- 常量 `Inf=1e9f`;8 邻域偏移 `k_Neighbors[]`;`k_DiagCost=√2`
- `static StepCost(int2 offset)` — 正交 1、对角 √2
- `static CanDiagonal(field, cell, n)` / `static IsWalkable(field, c)` — 角落穿越检测
- `static BuildSingleTarget(ref field, targetCell, density=default, congestionStrength=0, maxDensity=8)` — 单目标 BFS(可选拥堵成本)
- `static BuildMultiSource(ref field, sources, density=default, congestionStrength=0, maxDensity=8)` — 多源 BFS(找最近 POI)
- `static RebuildRegion(ref field, changedCells, radius, density=default, ...)` — 局部重算(动态障碍增量)
- `static EnqueueBoundary / RelaxInRegion / TryRelax` — 内部松弛

### `FlowField` struct
- 字段 `gridSize/cellSize/origin`、`directions[]/costs[]/blocked[]`(NativeArray)
- `CellCount`、`WorldToCell(pos)`、`CellCenter(cell)`、`InBounds(cell)`、`CellIndex(cell)`、`Dispose()`

### `SteeringMath`
- `Seek(pos, target, speed)` — 直线朝目标
- `Arrive(pos, target, speed, slowRadius)` — 接近减速
- `Evade(pos, threat, speed)` — 全速远离威胁
- `EscapeDirection(cell, field, speed)` / `TryEscape(...)` — 被困时逃出
- `RepulsionFrom(pos, neighbor, avoidRadius)` — **1/d² 饱和钳制**(防密集抖动)
- `Repulsion(pos, neighbors, avoidRadius)` — 邻居排斥和
- `ObstacleRepulsion(pos, obstacles, radii, count, strength)` — 障碍排斥(1-d/r 线性)
- `FlowFieldArrive(pos, cell, field, speed, slowCost)` — 沿流场走 + 减速 + 不可达逃出

### `PoiMath`
- `NearestIndex(pos, points)` — 最近 POI 下标
- `WithinRadius(pos, points, radius)` — 是否在任一 POI 半径内(平方比较)

### `ThreatMath`
- `IsThreatened(pos, zones, radius)` — 距任一威胁中心 < radius

---

## 四、Systems(ECS 系统)

### `SnapshotSystem : SystemBase` [Before Steering]
- `OnUpdate()` — GO→ECS:写 SimPosition;检测 goal 切换(Seek→非Seek)时设 `exitTimer=1s` + 方向;写 SimExit/SimNeeds/SimGoal

### `SpatialGridSystem : ISystem` [After Snapshot, Before Steering]
- 静态 `Grid`(NativeParallelMultiHashMap)、`Positions[]`、`Count`、`CellSize=1`
- `OnCreate/OnDestroy/OnUpdate` — 建/释放/重建空间哈希网格
- 内嵌 `BuildGridJob : IJobEntity`(Burst)— 算格坐标 + 写 positions + grid

### `FlowFieldBuildSystem : ISystem` [After Snapshot, Before Steering]
- 静态 `FoodField/HomeField/FunField`、`Dirty`、`Initialized`、`Density`
- `GridSize/CellSize/Origin` 属性 — 从 FlowFieldConfig 读
- 常量 `CongestionStrength=3`、`MaxDensity=8`、`k_DensityInterval=0.25f`
- `OnCreate/OnDestroy/OnUpdate` — 延迟分配(首次运行时),每 0.25s 更新密度并重建流场
- `RebuildAll()/RebuildRegions(changedCells, radius)` — 全量/局部重算(传密度)
- `IsWorldInBounds(pos)` — spawn 检查
- `AllocateField/RebuildField/WriteObstacles/ComputeDensity` — 内部

### `SteeringSystem : ISystem` [After SpatialGrid, Before Resolve]
- 内嵌 `SteeringJob : IJobEntity`(Burst)— 合力 = 流场/Arrive/Evade + 排斥×强度 + 障碍排斥 + exit;**速度低通滤波**(SmoothTime=0.12)
- `OnUpdate()` — 配参(Speed=2、AvoidRadius=0.5、AvoidStrength=1.5、SmoothTime=0.12 等)调度 job

### `NeedsDecaySystem : SystemBase` [After Snapshot, Before Steering]
- 常量 `HungerRate=0.008 / EatRate=0.7 / FatigueRate=0.015 / RestRate=0.7 / FunDecayRate=0.025 / PlayRate=0.7 / PoIRadius=4`
- `OnUpdate()` — 读 POI 位置 → NativeArray → 调度 NeedsDecayJob
- 内嵌 `NeedsDecayJob : IJobEntity`(Burst)— 在 POI 内反向恢复,否则衰减,`math.saturate`

### `ResolveSystem : SystemBase` [After Steering]
- 常量 `k_TurnSmoothTime=0.2f`
- `OnUpdate()` — ECS→GO:位置移动(硬约束 blocked/网格外滑动)、写 needs/threatened、**动画驱动**(Speed 参数 clamp 到 0.5=slow run)、**平滑转向**(LookRotation+Slerp)、exit 计时递减
- `static IsCellBlocked(field, pos)` — 格子 blocked 或网格外检查

### `ThreatDetectionSystem : ISystem` [After Snapshot, Before Steering]
- 字段 `flagsEnter/flagsExit`(滞回)
- `OnCreate/OnDestroy/OnUpdate` — 读活动威胁区,调度 ThreatJob,主线程滞回翻转 enableable bit
- `ToNativeFloat3/ToNativeFloat` — 托管→Native
- 内嵌 `ThreatJob : IJobEntity`(Burst)— enter(×1)/exit(×1.3) 标志

### `ColoringSystem : SystemBase` [After Resolve]
- `static ColorFor(GoalType)` — 状态→颜色(吃红/家蓝/玩黄/逛绿/逃白)
- `OnUpdate()` — MPB 设 `_BaseColor`(当前只对单个 capsuleRenderer;人形仅 Beta_Joints 被着色,Beta_Surface 未着色 → 状态颜色偏淡)

---

## 五、Behavior

### `GoalDecision` — 决策纯函数(核心)
- `IsHungry(ca)` — 滞回:SeekFood 时需 <fullThreshold,否则 >hungerThreshold
- `IsFatigued(ca)` / `IsBored(ca)` — 同滞回模式
- `IsThreatened(ca)` — 直接读 threatened
- `SetGoal(ca, type, pois)` — Seek* 选最近 POI；Wander 走 SetWanderGoal；Flee 选最近威胁
- `static SetWanderGoal(ca)` — 随机漫游点,**≥5m 最小距离约束**(防原地抖动)

### `BtScheduler : MonoBehaviour` — 时间分片 BT 调度器
- 静态 `Instance`、`LastTickCount`;字段 `agents/cursor/lastTick`
- `static BtScheduler()` — 反射缓存 Parent 字段
- `OnEnable/OnDisable` — 单例
- `AgentCount` — 属性
- `SetAgents(BehaviorGraphAgent[])` — 注册 + 修复 Parent 链 + Start
- `static FixupParentChain(graph) / FixupNode(node, parent)` — 反射补 Parent(BT 深拷贝丢失)
- `static ComputePerFrame(agentCount)` — 每帧 tick 数 = count/30
- `static ShouldPreempt(threatened, lastTickFrame, currentFrame)` — 受威胁插队判定
- `Update()` — round-robin + 受威胁插队 tick
- `TickAgent(i)` — 单 agent tick

### BT 节点(Behavior/,Unity Behavior 节点)
**Action**(OnStart 设目标返回 Success,OnUpdate 恒 Success):
- `SeekFoodAction` → `GoalDecision.SetGoal(SeekFood, foodPositions)`
- `SeekHomeAction` → SetGoal(SeekHome, homePositions)
- `SeekFunAction` → SetGoal(SeekFun, funPositions)
- `WanderAction` → SetGoal(Wander, 空)
- `FleeAction` → SetGoal(Flee, GetActiveZonePositions)

**Condition**(转发 GoalDecision):
- `IsHungryCondition` / `IsFatiguedCondition` / `IsBoredCondition` / `IsThreatenedCondition` → 各转发对应 IsXxx

**Spike 验证**(BtNodeSpikes.cs):`SpikeLogAction`(日志)、`SpikeAlwaysTrueCondition`(恒真)

---

## 六、Bootstrap

### `CitizenBootstrap : MonoBehaviour`
- 字段 `citizenPrefab`、`count=500`、`spawnRadius=40f`、`spawnFollowsGround=true`
- `Start()` — Spawn
- `Clear()` — 销毁全体市民 entity+GO,清 registry/scheduler
- `Spawn()` — 只编排出生流程；细节拆到下列命名方法，便于按步骤阅读
- `CreateCitizenArchetype()` / `CreateMirrorEntity()` — 创建 ECS 镜像结构与初始数据
- `GetSpawnCenter()` / `FindSpawnPosition()` — 计算出生中心并避开障碍/网格外
- `CreateCitizenGameObject()` / `InitializeAuthoring()` — 创建 GO，注入 renderer/animator/needs/初始 Wander
- `LoadBehaviorGraph()` / `CreateBehaviorAgent()` / `ConnectBehaviorAgents()` — 克隆运行时行为图并交给分片调度器
- `DestroyMirrorEntities()` / `ClearCitizenGameObjects()` — 将 Entity 与 GO 两侧的清理职责分开

---

## 七、UI

### `Hud : MonoBehaviour`
- 嵌套 `LegendEntry/InputEntry` + `kLegend/kInputs`;`_legendCounts[]/counts[]`
- `Start()` → CreateLegend + CreateInputHints
- `Update()` — FPS、Citizen/Threatened/Scale/BT ticks 文本、UpdateLegendCounts
- `UpdateLegendCounts()` — 按 GoalType 计数刷新图例右侧数字
- `CreateLegend()` — 左下角颜色图例(程序化 UGUI)
- `CreateInputHints()` — 右下角输入提示面板

### `ScaleDial : MonoBehaviour`
- 字段 `bootstrap/threatZone/threatMoveSpeed/raycastCamera`;常量 `TempZoneRadius=15/TempZoneDuration=3`;`Scales{100,500,2000,5000}`
- `Update()` — 1-4 切规模、T 开关威胁、WASD 移动威胁区、E 生成临时恐惧区
- `SpawnTempZoneAtMouse()` — 鼠标射线→地面生成恐惧区

### `FpsGraph : Graphic`
- `Push(fps)`、`ColorFor(fps)`、`OnPopulateMesh` — 环形缓冲画柱状图

### `FlowFieldDebugVisualizer : MonoBehaviour`
- 字段 `showGrid/showBlocked/showDirections/showInGame/gridLineWidth/refreshInterval`、颜色
- `Start()/Update()` — 每 0.25s 重建调试 mesh
- `EnsureRuntimeObjects()` — 生成 FlowFieldDebugMesh 子物体(挂 Ground,抵消父级缩放)
- `ParentWorldPos()` — 父物体世界位置
- `RebuildMesh(f)` — 构建网格线+blocked 方块 mesh(局部坐标)
- `OnDrawGizmos()` — Scene 视图 Gizmos
- `GetField/DrawGrid/DrawBlocked/DrawDirections` — 内部
- `static AddQuad(...)` — 画 quad

### `PoiLabel : MonoBehaviour`
- `LateUpdate()` — 对齐相机旋转 + 绕Y 180(文字正面朝相机)

### `CitizenBounce : MonoBehaviour`
- 字段 `squashAmount/duration/springOvershoot`
- `Awake()` — 找 Mesh(人形无则禁用)
- `Update()` — 检测 goal 切换触发弹跳 + 弹性曲线驱动
- `SpringY(t)` — 衰减正弦弹性曲线

---

## 八、CitizenSim.Tests(EditMode,90+ 个)

- **Math/**:`FlowFieldMathTests`(单/多源 BFS、8 邻域对角、不穿角、拥堵避开)、`PoiMathTests`、`SteeringMathTests`、`FlowFieldConfigTests`
- **Systems/**:`FlowFieldBuildTests`(MarkBlockedRect 旋转/相交)、`NeedsDecayTests`、`SteeringSystemTests`、`SyncLoopTests`、`SpatialGridTests`、`ThreatDetectionTests`、`NeedsRoundTripTests`
- **Behavior/**:`BtDecisionTests`、`BtSchedulerTests`

---

## 九、关键设计决策回顾

1. **GO↔ECS 混合**:GO 是 source of truth(身份/BT/渲染),ECS 只做高频模拟,Snapshot/Resolve 双向同步
2. **流场 8 邻域**:斜向寻路(StepCost 对角 √2 + 角落穿越检测)
3. **拥堵绕路**:每 0.25s 统计密度 → BFS 加拥堵成本(Strength=3)
4. **速度阻尼**:SteeringJob 低通滤波(SmoothTime=0.12),抑制密集抖动
5. **排斥饱和**:RepulsionFrom 1/d² 钳制,防近距离力爆炸
6. **障碍物旋转**:MarkBlockedRect 用 SAT 精确相交,blocked 贴合视觉
7. **市民体积/避让**:视觉 scale 0.5、AvoidRadius 0.5 匹配
8. **人形模型**:citizenPrefab→Citizen 1,ResolveSystem 驱动 Speed(≤0.5=slow run)+ 平滑转向 + 关闭根运动
9. **Wander 防抖**:新目标最小距离 5m
10. **数值**:恢复到满(0/0/0.9)+ 快恢复(0.7),POI 停留短
