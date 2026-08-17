# M1: ECS 同步脊柱 实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 跑通 GameObject 市民 ↔ ECS 镜像实体的每帧双向同步闭环：500 个胶囊朝一个固定目标移动，经 ECS+Job+Burst 计算，证明 §4 的同步脊柱成立。

**Architecture:** GO 权威（Transform/配置），ECS 镜像实体持有每帧快照与计算中间量。每帧管线：SnapshotSystem(主线程, GO->Entity) -> SteeringSystem(IJobEntity+Burst, 算 SimVelocity) -> ResolveSystem(主线程, Entity->GO Transform)。BT/需求/威胁在后续模块加，M1 不涉及。

**Tech Stack:** Unity 6000.5.3f1, Entities 6.5.0, Burst 1.8.29, Mathematics 1.4.0, Unity Test Framework 1.7.0 (NUnit)。

**本计划范围：** 仅 M1（同步脊柱）。M2(行为树)/M3(完整需求+空间网格)/M4(威胁+刻度盘+profiling) 各自有独立计划文档。

---

## 文件结构

```
Assets/Scripts/CitizenSim/
  CitizenSim.asmdef                 # 运行时程序集，引用 Entities/Burst/Mathematics/Collections/UGUI
  Components/SimComponents.cs       # IComponentData: SimPosition/SimVelocity/SimGoal/SimRadius/CitizenIndex + GoalType 枚举
  Math/SteeringMath.cs              # 纯静态: Seek/Arrive（可单测）
  Systems/SteeringSystem.cs         # ISystem + IJobEntity + Burst
  Systems/SnapshotSystem.cs         # SystemBase(托管): GO -> Entity
  Systems/ResolveSystem.cs          # SystemBase(托管): Entity -> GO Transform
  Registry/CitizenRegistry.cs       # MonoBehaviour 单例: gos[]/entities[]/count
  Registry/CitizenAuthoring.cs      # 每个 citizen GO 上的组件: Index（M2 起扩展 needs/home）
  Bootstrap/CitizenBootstrap.cs     # 场景入口: 生成 N 个胶囊 GO + 镜像 Entity
  UI/Hud.cs                         # FPS + 市数 HUD
Assets/Scripts/CitizenSim.Tests/
  CitizenSim.Tests.asmdef           # 测试程序集, Editor-only, TestAssemblies
  Math/SteeringMathTests.cs         # NUnit EditMode
  Systems/SteeringSystemTests.cs    # NUnit EditMode, 手工建 World
```

每个文件单一职责：纯数学可单测、系统只做管线一段、Registry 只管映射、Bootstrap 只管生成。

---

## Task 1: 程序集与命名空间

**Files:**
- Create: `Assets/Scripts/CitizenSim/CitizenSim.asmdef`
- Create: `Assets/Scripts/CitizenSim.Tests/CitizenSim.Tests.asmdef`

- [ ] **Step 1: 创建运行时 asmdef**

在 Unity 编辑器：Project 窗口右键 `Assets/Scripts` -> Create -> Assembly Definition，命名为 `CitizenSim`，移到 `Assets/Scripts/CitizenSim/CitizenSim.asmdef`。内容：

```json
{
    "name": "CitizenSim",
    "rootNamespace": "CitizenSim",
    "references": [
        "Unity.Entities",
        "Unity.Burst",
        "Unity.Mathematics",
        "Unity.Collections",
        "UnityEngine.UI"
    ],
    "includePlatforms": [],
    "excludePlatforms": [],
    "autoReferenced": true
}
```

- [ ] **Step 2: 创建测试 asmdef**

右键 `Assets/Scripts` -> Create -> Assembly Definition，命名 `CitizenSim.Tests`，移到 `Assets/Scripts/CitizenSim.Tests/CitizenSim.Tests.asmdef`。内容：

```json
{
    "name": "CitizenSim.Tests",
    "rootNamespace": "CitizenSim.Tests",
    "references": [
        "CitizenSim",
        "Unity.Entities",
        "Unity.Mathematics"
    ],
    "includePlatforms": ["Editor"],
    "excludePlatforms": [],
    "optionalUnityReferences": ["TestAssemblies"],
    "autoReferenced": false
}
```

- [ ] **Step 3: 验证编译**

Run: Unity 编辑器等编译完成，控制台无错误。
Expected: 无报错。（此时两个 asmdef 还没代码，只是空程序集。）

- [ ] **Step 4: Commit**

```bash
git add Assets/Scripts/CitizenSim/CitizenSim.asmdef Assets/Scripts/CitizenSim.Tests/CitizenSim.Tests.asmdef
git commit -m "chore(m1): add CitizenSim and CitizenSim.Tests assemblies"
```

---

## Task 2: 镜像实体组件

**Files:**
- Create: `Assets/Scripts/CitizenSim/Components/SimComponents.cs`

- [ ] **Step 1: 写组件定义**

```csharp
using Unity.Entities;
using Unity.Mathematics;

namespace CitizenSim
{
    public enum GoalType
    {
        Wander = 0,
        SeekFood = 1,
        SeekHome = 2,
        SeekFun = 3,
        Flee = 4
    }

    public struct SimPosition : IComponentData { public float3 Value; }
    public struct SimVelocity : IComponentData { public float3 Value; }
    public struct SimGoal : IComponentData { public GoalType Type; public float3 Target; }
    public struct SimRadius : IComponentData { public float Value; }
    public struct CitizenIndex : IComponentData { public int Value; }
}
```

说明：`SimGoal` 用完整的 `Type + Target` 结构（与规格 §3 一致），M1 里 Steering 只用 `Target`，`Type` 在 M2+ 才被消费。这样 M2 不用改组件结构。

- [ ] **Step 2: 验证编译**

Run: 编译，无错误。
Expected: 无报错。

- [ ] **Step 3: Commit**

```bash
git add Assets/Scripts/CitizenSim/Components/SimComponents.cs
git commit -m "feat(m1): add mirror entity components and GoalType"
```

---

## Task 3: SteeringMath 纯函数（TDD）

**Files:**
- Create: `Assets/Scripts/CitizenSim/Math/SteeringMath.cs`
- Create: `Assets/Scripts/CitizenSim.Tests/Math/SteeringMathTests.cs`

- [ ] **Step 1: 写失败测试**

```csharp
using CitizenSim;
using NUnit.Framework;
using Unity.Mathematics;

namespace CitizenSim.Tests
{
    public class SteeringMathTests
    {
        [Test] public void Seek_PointsTowardTarget_AtFullSpeed()
        {
            var v = SteeringMath.Seek(new float3(0, 0, 0), new float3(10, 0, 0), 2f);
            Assert.AreEqual(new float3(2, 0, 0), v);
        }

        [Test] public void Seek_ZeroDistance_ReturnsZero()
        {
            var v = SteeringMath.Seek(new float3(1, 1, 1), new float3(1, 1, 1), 2f);
            Assert.AreEqual(float3.zero, v);
        }

        [Test] public void Arrive_FarFromTarget_FullSpeed()
        {
            var v = SteeringMath.Arrive(new float3(0, 0, 0), new float3(10, 0, 0), 2f, 1f);
            Assert.AreEqual(new float3(2, 0, 0), v);
        }

        [Test] public void Arrive_InsideSlowRadius_SlowedProportionally()
        {
            // dist=0.5, slowRadius=1 -> v = 2 * (0.5/1) = 1
            var v = SteeringMath.Arrive(new float3(9.5f, 0, 0), new float3(10, 0, 0), 2f, 1f);
            Assert.AreEqual(new float3(1, 0, 0), v);
        }

        [Test] public void Arrive_AtTarget_ZeroVelocity()
        {
            var v = SteeringMath.Arrive(new float3(5, 0, 0), new float3(5, 0, 0), 2f, 1f);
            Assert.AreEqual(float3.zero, v);
        }
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

Run: Unity 顶部菜单 Test Runner -> EditMode -> 跑 `CitizenSim.Tests`。
Expected: 5 个 FAIL，原因 `SteeringMath` 未定义。

- [ ] **Step 3: 写最小实现**

```csharp
using Unity.Mathematics;

namespace CitizenSim
{
    public static class SteeringMath
    {
        // 朝目标全速前进，不做减速。
        public static float3 Seek(float3 pos, float3 target, float speed)
        {
            float3 dir = math.normalizesafe(target - pos);
            return dir * speed;
        }

        // 朝目标前进，进入 slowRadius 后线性减速到 0。
        public static float3 Arrive(float3 pos, float3 target, float speed, float slowRadius)
        {
            float3 toTarget = target - pos;
            float dist = math.length(toTarget);
            if (dist < 1e-4f) return float3.zero;
            float3 dir = toTarget / dist;
            float v = speed;
            if (dist < slowRadius)
                v = speed * (dist / slowRadius);
            return dir * v;
        }
    }
}
```

- [ ] **Step 4: 运行测试确认通过**

Run: Test Runner EditMode 跑 `SteeringMathTests`。
Expected: 5 个 PASS。

- [ ] **Step 5: Commit**

```bash
git add Assets/Scripts/CitizenSim/Math/SteeringMath.cs Assets/Scripts/CitizenSim.Tests/Math/SteeringMathTests.cs
git commit -m "feat(m1): add SteeringMath Seek/Arrive with unit tests"
```

---

## Task 4: SteeringSystem（ISystem + IJobEntity + Burst，TDD）

**Files:**
- Create: `Assets/Scripts/CitizenSim/Systems/SteeringSystem.cs`
- Create: `Assets/Scripts/CitizenSim.Tests/Systems/SteeringSystemTests.cs`

- [ ] **Step 1: 写失败测试（手工建 World）**

```csharp
using CitizenSim;
using NUnit.Framework;
using Unity.Entities;
using Unity.Mathematics;

namespace CitizenSim.Tests
{
    public class SteeringSystemTests
    {
        private World world;
        private EntityManager em;

        [SetUp] public void Setup()
        {
            world = new World("SteeringTest");
            em = world.EntityManager;
        }

        [TearDown] public void TearDown() => world.Dispose();

        [Test] public void Steering_SetsVelocityTowardTarget()
        {
            var sys = world.GetOrCreateSystem<SteeringSystem>();
            var e = em.CreateEntity();
            em.AddComponentData(e, new SimPosition { Value = new float3(0, 0, 0) });
            em.AddComponentData(e, new SimVelocity { Value = float3.zero });
            em.AddComponentData(e, new SimGoal { Type = GoalType.SeekFood, Target = new float3(10, 0, 0) });

            world.Update();

            var vel = em.GetComponentData<SimVelocity>(e);
            Assert.Greater(vel.Value.x, 0f, "velocity.x 应朝 +x 目标为正");
            Assert.AreEqual(0f, vel.Value.z, "z 方向无分量");
        }

        [Test] public void Steering_AtTarget_ZeroVelocity()
        {
            world.GetOrCreateSystem<SteeringSystem>();
            var e = em.CreateEntity();
            em.AddComponentData(e, new SimPosition { Value = new float3(7, 7, 7) });
            em.AddComponentData(e, new SimVelocity { Value = new float3(9, 9, 9) });
            em.AddComponentData(e, new SimGoal { Type = GoalType.SeekFood, Target = new float3(7, 7, 7) });

            world.Update();

            var vel = em.GetComponentData<SimVelocity>(e);
            Assert.AreEqual(float3.zero, vel.Value);
        }
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

Run: Test Runner EditMode 跑 `SteeringSystemTests`。
Expected: FAIL，原因 `SteeringSystem` 未定义 / 编译错误。

- [ ] **Step 3: 写最小实现**

```csharp
using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;

namespace CitizenSim
{
    [BurstCompile]
    public partial struct SteeringJob : IJobEntity
    {
        public float Speed;
        public float SlowRadius;

        void Execute(ref SimVelocity vel, in SimPosition pos, in SimGoal goal)
        {
            // M1: 只 seek 目标点，忽略 goal.Type（M2+ 才按类型分 seek/evade）。
            vel.Value = SteeringMath.Arrive(pos.Value, goal.Target, Speed, SlowRadius);
        }
    }

    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct SteeringSystem : ISystem
    {
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            state.Dependency = new SteeringJob
            {
                Speed = 2f,
                SlowRadius = 0.5f
            }.ScheduleParallel(state.Dependency);
        }
    }
}
```

- [ ] **Step 4: 运行测试确认通过**

Run: Test Runner EditMode 跑 `SteeringSystemTests`。
Expected: 2 个 PASS。

若失败提示 `world.Update()` 未 tick 系统：确认 `SteeringSystem` 上的 `[UpdateInGroup(typeof(SimulationSystemGroup))]` 已写入（SimulationSystemGroup 是 World 自动创建的根组）。

- [ ] **Step 5: Commit**

```bash
git add Assets/Scripts/CitizenSim/Systems/SteeringSystem.cs Assets/Scripts/CitizenSim.Tests/Systems/SteeringSystemTests.cs
git commit -m "feat(m1): add Burst SteeringSystem with IJobEntity"
```

---

## Task 5: CitizenRegistry + CitizenAuthoring + CitizenBootstrap

**Files:**
- Create: `Assets/Scripts/CitizenSim/Registry/CitizenRegistry.cs`
- Create: `Assets/Scripts/CitizenSim/Registry/CitizenAuthoring.cs`
- Create: `Assets/Scripts/CitizenSim/Bootstrap/CitizenBootstrap.cs`

- [ ] **Step 1: 写 CitizenRegistry 单例**

```csharp
using Unity.Entities;
using UnityEngine;

namespace CitizenSim
{
    public class CitizenRegistry : MonoBehaviour
    {
        public static CitizenRegistry Instance { get; private set; }

        public GameObject[] GameObjects;
        public Entity[] Entities;

        public int Count => GameObjects != null ? GameObjects.Length : 0;

        void OnEnable() => Instance = this;
        void OnDisable() { if (Instance == this) Instance = null; }

        public void Register(GameObject[] gos, Entity[] ents)
        {
            GameObjects = gos;
            Entities = ents;
        }
    }
}
```

- [ ] **Step 2: 写 CitizenAuthoring（每个市民 GO 上的组件）**

```csharp
using UnityEngine;

namespace CitizenSim
{
    // 每个 citizen GameObject 挂一个。M1 只存 Index；M2 起扩展 needs/home/goal。
    public class CitizenAuthoring : MonoBehaviour
    {
        public int Index;
    }
}
```

- [ ] **Step 3: 写 CitizenBootstrap（场景入口，生成 GO + 镜像 Entity）**

```csharp
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace CitizenSim
{
    public class CitizenBootstrap : MonoBehaviour
    {
        public GameObject citizenPrefab;
        public int count = 500;
        public Transform fixedTarget;          // 拖一个空 GO 作目标点
        public float spawnRadius = 40f;

        void Start()
        {
            Spawn();
        }

        public void Spawn()
        {
            var world = World.DefaultGameObjectInjectionWorld;
            if (world == null)
            {
                Debug.LogError("CitizenBootstrap: 默认 ECS World 不存在，请确认 Entities 初始化开启。");
                return;
            }
            var em = world.EntityManager;

            var archetype = em.CreateArchetype(
                typeof(SimPosition),
                typeof(SimVelocity),
                typeof(SimGoal),
                typeof(SimRadius),
                typeof(CitizenIndex));

            var gos = new GameObject[count];
            var ents = new Entity[count];

            float3 target = fixedTarget != null
                ? fixedTarget.position
                : new float3(0, 0, 0);

            for (int i = 0; i < count; i++)
            {
                var go = Instantiate(citizenPrefab, transform);
                var rnd = UnityEngine.Random.insideUnitCircle * spawnRadius;
                go.transform.position = new Vector3(rnd.x, 0, rnd.y) + transform.position;
                var ca = go.GetComponent<CitizenAuthoring>();
                if (ca != null) ca.Index = i;

                var e = em.CreateEntity(archetype);
                em.SetComponentData(e, new SimPosition { Value = go.transform.position });
                em.SetComponentData(e, new SimVelocity { Value = float3.zero });
                em.SetComponentData(e, new SimGoal { Type = GoalType.SeekFood, Target = target });
                em.SetComponentData(e, new SimRadius { Value = 0.5f });
                em.SetComponentData(e, new CitizenIndex { Value = i });

                gos[i] = go;
                ents[i] = e;
            }

            var registry = GetComponent<CitizenRegistry>();
            if (registry == null)
            {
                Debug.LogError("CitizenBootstrap: 同物体上需要挂 CitizenRegistry。");
                return;
            }
            registry.Register(gos, ents);
        }
    }
}
```

- [ ] **Step 4: 验证编译**

Run: 编译，无错误。
Expected: 无报错。

- [ ] **Step 5: Commit**

```bash
git add Assets/Scripts/CitizenSim/Registry/ Assets/Scripts/CitizenSim/Bootstrap/
git commit -m "feat(m1): add CitizenRegistry, CitizenAuthoring, CitizenBootstrap"
```

---

## Task 6: SnapshotSystem + ResolveSystem（同步脊柱，TDD）

**Files:**
- Create: `Assets/Scripts/CitizenSim/Systems/SnapshotSystem.cs`
- Create: `Assets/Scripts/CitizenSim/Systems/ResolveSystem.cs`
- Create: `Assets/Scripts/CitizenSim.Tests/Systems/SyncLoopTests.cs`

- [ ] **Step 1: 写失败测试（端到端同步闭环）**

```csharp
using CitizenSim;
using NUnit.Framework;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace CitizenSim.Tests
{
    public class SyncLoopTests
    {
        private World world;
        private EntityManager em;
        private GameObject registryGo;
        private GameObject citizenGo;

        [SetUp] public void Setup()
        {
            world = new World("SyncLoopTest");
            em = world.EntityManager;
            world.GetOrCreateSystem<SnapshotSystem>();
            world.GetOrCreateSystem<SteeringSystem>();
            world.GetOrCreateSystem<ResolveSystem>();

            registryGo = new GameObject("Registry");
            var registry = registryGo.AddComponent<CitizenRegistry>();

            citizenGo = new GameObject("Citizen");
            citizenGo.transform.position = Vector3.zero;

            var e = em.CreateEntity(
                typeof(SimPosition), typeof(SimVelocity), typeof(SimGoal),
                typeof(SimRadius), typeof(CitizenIndex));
            em.SetComponentData(e, new SimPosition { Value = float3.zero });
            em.SetComponentData(e, new SimVelocity { Value = float3.zero });
            em.SetComponentData(e, new SimGoal { Type = GoalType.SeekFood, Target = new float3(100, 0, 0) });
            em.SetComponentData(e, new SimRadius { Value = 0.5f });
            em.SetComponentData(e, new CitizenIndex { Value = 0 });

            registry.Register(new[] { citizenGo }, new[] { e });
        }

        [TearDown] public void TearDown()
        {
            Object.DestroyImmediate(citizenGo);
            Object.DestroyImmediate(registryGo);
            world.Dispose();
        }

        [Test] public void SyncLoop_MovesCitizenTowardTarget()
        {
            float xBefore = citizenGo.transform.position.x;
            world.PushTime(new Unity.Core.TimeData(0.1f, 0.016f));
            world.Update();
            float xAfter = citizenGo.transform.position.x;
            Assert.Greater(xAfter, xBefore, "市民应朝 +x 目标移动");
        }
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

Run: Test Runner EditMode 跑 `SyncLoopTests`。
Expected: FAIL，原因 `SnapshotSystem`/`ResolveSystem` 未定义。

- [ ] **Step 3: 写 SnapshotSystem（GO -> Entity）**

```csharp
using Unity.Entities;
using UnityEngine;

namespace CitizenSim
{
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateBefore(typeof(SteeringSystem))]
    public partial class SnapshotSystem : SystemBase
    {
        protected override void OnUpdate()
        {
            var registry = CitizenRegistry.Instance;
            if (registry == null) return;

            var em = EntityManager;
            var gos = registry.GameObjects;
            var ents = registry.Entities;
            for (int i = 0; i < gos.Length; i++)
            {
                var go = gos[i];
                if (go == null) continue;
                em.SetComponentData(ents[i], new SimPosition { Value = go.transform.position });
            }
        }
    }
}
```

- [ ] **Step 4: 写 ResolveSystem（Entity -> GO Transform）**

```csharp
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace CitizenSim
{
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(SteeringSystem))]
    public partial class ResolveSystem : SystemBase
    {
        protected override void OnUpdate()
        {
            var registry = CitizenRegistry.Instance;
            if (registry == null) return;

            float dt = SystemAPI.Time.DeltaTime;
            var em = EntityManager;
            var gos = registry.GameObjects;
            var ents = registry.Entities;
            for (int i = 0; i < gos.Length; i++)
            {
                var go = gos[i];
                if (go == null) continue;
                var vel = em.GetComponentData<SimVelocity>(ents[i]);
                var v = vel.Value;
                go.transform.position += new Vector3(v.x, v.y, v.z) * dt;
            }
        }
    }
}
```

- [ ] **Step 5: 运行测试确认通过**

Run: Test Runner EditMode 跑 `SyncLoopTests`。
Expected: PASS（市民朝 +x 移动）。

若 `world.PushTime` 在你的 Entities 版本签名不同：`PushTime(TimeData)` 是 Entities 1.0+ 的标准 API；如报错确认 `using Unity.Core;` 已加（TimeData 在该命名空间）。

- [ ] **Step 6: Commit**

```bash
git add Assets/Scripts/CitizenSim/Systems/SnapshotSystem.cs Assets/Scripts/CitizenSim/Systems/ResolveSystem.cs Assets/Scripts/CitizenSim.Tests/Systems/SyncLoopTests.cs
git commit -m "feat(m1): add SnapshotSystem/ResolveSystem sync spine with integration test"
```

---

## Task 7: 场景、胶囊 Prefab、HUD v1、验收

**Files:**
- Create: `Assets/Scripts/CitizenSim/UI/Hud.cs`
- Create: `Assets/Scenes/CitizenSim/CitizenSimScene.unity`（新建场景）
- Create: `Assets/Prefabs/Citizen.prefab`（胶囊预制体）

- [ ] **Step 1: 写 Hud**

```csharp
using UnityEngine;
using UnityEngine.UI;

namespace CitizenSim
{
    public class Hud : MonoBehaviour
    {
        public Text text;
        private int frames;
        private float timer;
        private int fps;

        void Update()
        {
            frames++;
            timer += Time.unscaledDeltaTime;
            if (timer >= 0.5f)
            {
                fps = Mathf.RoundToInt(frames / timer);
                frames = 0;
                timer = 0f;
            }
            int count = CitizenRegistry.Instance != null ? CitizenRegistry.Instance.Count : 0;
            if (text != null)
                text.text = $"FPS {fps} | Citizens {count}";
        }
    }
}
```

- [ ] **Step 2: 建 Citizen 胶囊 Prefab**

编辑器步骤：
1. Hierarchy -> Create -> 3D Object -> Capsule，命名 `Citizen`。
2. Add Component -> `CitizenAuthoring`。
3. 旋转 `Transform.Rotation = (90, 0, 0)` 让胶囊躺平贴地（俯视像圆点）。
4. 新建材质 `Assets/Materials/CitizenMat.mat`，Shader 用 URP/Lit，颜色绿，勾选 "Enable GPU Instancing"。赋给胶囊 MeshRenderer。
5. 把 Hierarchy 里的 `Citizen` 拖进 `Assets/Prefabs/` 生成 `Cititizen.prefab`，然后删除场景里的实例。

- [ ] **Step 3: 建 CitizenSimScene 场景**

新建空场景 `Assets/Scenes/CitizenSim/CitizenSimScene.unity`，搭：
1. 一个 Plane（100x100）作地面，浅色材质。
2. 一个空 GO 命名 `SimRoot`，Add Component `CitizenRegistry` + `CitizenBootstrap`：
   - `CitizenBootstrap.citizenPrefab` = `Citizen.prefab`
   - `CitizenBootstrap.count` = 500
   - `CitizenBootstrap.fixedTarget` = 拖一个地面中央的空 GO（如 `Target` at (0,0,0)）
   - `CitizenBootstrap.spawnRadius` = 40
3. 相机：高斜俯，看向地面中央，看全 80x80 区域。可加 Cinemachine 虚拟相机固定。
4. Canvas + Text（uGUI），Add Component `Hud`，`Hud.text` = 那个 Text。

- [ ] **Step 4: 播放验收**

Run: 进入 Play Mode。
Expected:
- 500 个绿色胶囊从环形随机点朝中央目标聚拢，到目标点附近停下（Arrive 减速）。
- HUD 显示 `FPS ~60 | Citizens 500`。
- Console 无报错。

Run: Test Runner EditMode 全跑 `CitizenSim.Tests`。
Expected: 全 PASS（SteeringMath 5 + SteeringSystem 2 + SyncLoop 1 = 8）。

- [ ] **Step 5: 性能抽查**

Run: Play Mode 打开 Window -> Analysis -> Profiler，看 CPU。
Expected: 主线程 Snapshot+Resolve 两段 + Steering Job，500 规模下整帧远低于 16ms。把 Profiler 截图存档（M4 整理进 README）。

- [ ] **Step 6: Commit**

```bash
git add Assets/Scripts/CitizenSim/UI/Hud.cs Assets/Scenes/CitizenSim/ Assets/Prefabs/Citizen.prefab Assets/Materials/CitizenMat.mat ProjectSettings/TagManager.asset
git commit -m "feat(m1): scene, capsule prefab, HUD v1; 500 citizens seek target via ECS sync spine"
```
（按实际改动的文件 add；场景/prefab 路径以编辑器实际生成为准。）

---

## M1 验收清单

- [ ] 500 胶囊朝固定目标聚拢并停下
- [ ] HUD 显示 FPS + 数
- [ ] EditMode 测试全 PASS（8 个）
- [ ] Profiler 整帧 < 16ms
- [ ] 同步脊柱代码可读：Snapshot(GO->E) / Steering(Job) / Resolve(E->GO) 三段清晰

完成后：M1 结束，进入 M2（行为树接入）。M2 计划文档届时另写，且 M2 第一步是 spike Unity Behavior manual-tick API（见规格 §4 风险）。

---

## Self-Review

**Spec 覆盖**：本计划覆盖规格 M1 全部内容（场景/网格/500 胶囊/相机、Registry+镜像实体、Snapshot->Steering->Resolve、HUD v1）。规格 §1 的状态着色、POI、BT、威胁均属 M2–M4，不在本计划。规格 §4 的同步管线六段中，M1 实现 1/5/6（Snapshot/Steering/Resolve），2/3/4（NeedsDecay/SpatialGrid/ThreatDetection）在 M3/M4。-- 一致。

**占位符扫描**：无 TBD/TODO。代码均完整。`CitizenAuthoring` 注释说明 M2 扩展，非占位。`SimGoal.Type` 在 M1 未被消费是设计意图（M2+ 消费），已注明。

**类型一致性**：`SteeringMath.Arrive(pos, target, speed, slowRadius)` 签名在 Task 3/4/6 一致。`SimGoal{Type, Target}` 在 Task 2/4/5/6 一致。`CitizenRegistry.Register(GameObject[], Entity[])` 在 Task 5/6 一致。`GoalType.SeekFood` 在 Task 2/4/5/6 一致。

**已知 API 风险**（实现时若报错按此处理）：
- `IJobEntity.ScheduleParallel(state.Dependency)`：Entities 1.0/6.5 标准扩展，返回 JobHandle 赋回 state.Dependency。
- `world.PushTime(TimeData)` + `using Unity.Core;`：测试推进时间用。
- `World.DefaultGameObjectInjectionWorld`：运行时取默认 World。
