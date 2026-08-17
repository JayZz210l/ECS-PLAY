# Unity Behavior 使用指南（AI 查阅版）

> 浓缩自 `com.unity.behavior@1.0.16` 的 `Documentation~`，并补充本会话逆向出的内部 API 与踩坑经验。
> 供后续 AI 会话快速查阅。包版本：1.0.16（Unity 6000.5）。

## 1. 核心概念

**两套图资产（关键区别）：**
| 类型 | 命名空间/程序集 | 作用 | 可编辑 |
|---|---|---|---|
| `BehaviorAuthoringGraph` | `Unity.Behavior`（Authoring 程序集，internal） | **源图**，`.graph`/`.asset` 主资产，编辑器可视化编辑 | ✅ 双击在 Behavior 编辑器打开 |
| `BehaviorGraph` | `Unity.Behavior`（Runtime 程序集，public） | **运行时图**，由 `GraphAssetProcessor` 从源图烘焙出的子资产 | ❌ |

- `BehaviorWindow`（编辑器窗口）**只打开 `BehaviorAuthoringGraph`**。
- 源图烘焙时 `GraphAssetProcessor` 用 `parent.Add(child)` 连接运行时节点（**正确设置 Parent**）。
- 运行时 `BehaviorGraphAgent` 持有 `BehaviorGraph`（运行时实例），不是源图。

**节点类层次（运行时，`Unity.Behavior` 命名空间）：**
```
Node (abstract, internal ctor)          // 基类
├─ Action        (public abstract)      // 叶子动作，无子节点
├─ Composite     (public abstract)      // 多子节点：Selector/Sequence/...
├─ Modifier      (public abstract)      // 单子节点：Repeat/Inverter/Start/Abort/...
└─ Join                                // 多父单子：WaitForAll/WaitForAny
```
- 控制流节点（`Start`/`SelectorComposite`/`SequenceComposite`/`ConditionalGuardModifier`/`BranchingConditionComposite` 等）全是 **internal**，但 `Action`/`Condition`/`Node`/`Composite`/`Modifier` 基类是 public。
- `Node` 构造函数 internal → 不能直接继承 `Node`，但可继承 `Action`/`Condition`（它们有可用的 protected 构造路径）。

**节点状态（`Node.Status` 枚举）：** `Uninitialized` / `Running` / `Success` / `Failure` / `Waiting` / `Interrupted`。

## 2. 节点生命周期回调

| 回调 | 触发时机 | 用途 |
|---|---|---|
| `OnSetup()` | 运行时图实例初始化一次 | 缓存引用、一次性准备 |
| `OnStart()` | 节点每次启动 | 进入运行态、开始操作 |
| `OnUpdate()` | 运行时每次 tick | 推进逻辑 |
| `OnEnd()` | 节点每次停止 | 停止当次逻辑 |
| `OnTeardown()` | 运行时图实例释放一次 | 注销回调、释放资源 |
| `OnSerialize()`/`OnDeserialize()` | 运行时图序列化前后 | 保存/恢复运行态 |

`OnSetup`/`OnTeardown` 成对，管理运行时图实例生命周期内的资源。

## 3. 创建自定义节点

### 自定义 Action
```csharp
using Unity.Behavior;
using Unity.Properties;
using UnityEngine;

[NodeDescription("Seek Food", "move toward the nearest food point", "seek the nearest food",
    id: "a1b2c3d4e5f6478a9b0c1d2e3f4a5b6c")]  // id 必须唯一!
public class SeekFoodAction : Action
{
    protected override Status OnStart()
    {
        var ca = GameObject.GetComponent<CitizenAuthoring>();  // GameObject 是 agent 的 GO
        GoalDecision.SetGoal(ca, GoalType.SeekFood, foods);
        return Status.Success;
    }
    protected override Status OnUpdate() => Status.Success;
}
```

### 自定义 Condition
```csharp
[Condition("IsHungry", "checks if the citizen is hungry", "the agent is hungry",
    id: "c3d4e5f647a8b9c0d1e2f3a4b5c6d7e8")]  // id 必须唯一!
public class IsHungryCondition : Condition
{
    public override bool IsTrue()
        => GoalDecision.IsHungry(GameObject.GetComponent<CitizenAuthoring>());
}
```

### `[NodeDescription]` / `[Condition]` 属性
```
NodeDescription(name, description, story, icon, category, id, hideInSearch, filePath)
Condition(name, description, story, category, id, filePath)
```
- `name`：节点面板显示名。
- `story`：**模板字符串**，用 `[字段名]` 占位符把字段暴露到 inspector（见下）。
- `id`：**唯一 GUID 字符串**（32 位 hex）。⚠️ 默认 `""`，多个节点不写 id 会 GUID 冲突，后注册的被静默跳过！
- `category`：节点分类路径，如 `"Action/Move"`。
- `hideInSearch`：true 则不在搜索面板显示（如内置 `ConditionalGuardModifier`）。

### 字段暴露（story 模板 + BlackboardVariable）
- story 里的 `[FieldName]` 占位符 → inspector 渲染对应字段的内联编辑器（枚举渲染成下拉框）。
- **只有 `BlackboardVariable<T>` 类型的字段会被烘焙器传递**到运行时节点。普通字段（如 `public GoalType goalType`）不会传递，烘焙后用默认值。
- `BlackboardVariable<T>` 的 T 支持：`UnityEngine.Object` 子类、基元（int/float/bool）、**枚举**、`Util.GetSupportedTypes()`（Vector3 等）。
- 若节点配置是固定枚举/简单值且无需黑板联动，**拆成多个 Action 子类**（如 SeekFoodAction + WanderAction）比用 BlackboardVariable 字段更简单。
- 节点字段访问 agent GO：`GameObject` 属性（`Node.GameObject => Graph.GameObject`，运行时由 agent 设置）。

## 4. 内置节点速查

**Flow（控制流）：**
| 节点 | 说明 |
|---|---|
| Try In Order (Selector) | 依次尝试子节点，失败继续，成功停止 |
| Sequence | 依次执行，成功继续，失败停止 |
| Random | 随机执行一个子节点 |
| Run In Parallel | 并行执行所有子节点 |
| Repeat / Succeeder / Inverter / Time Out / Cooldown | 修饰子节点 |
| Conditional Branch | if/else：条件真跑 True 分支，假跑 False 分支 |
| Switch / Switch Flag | 按枚举值分支 |
| Abort / Restart | 条件真时中止/重启分支 |
| Priority Abort | 专用于优先级中断 |
| Wait For All / Wait For Any | Join 节点 |

**Events：** `On Start`（图根节点）、`Start On Event Message`、`Send Event Message`、`Wait for Event Message`。

**Action 类别：** Animation、Blackboard（Set Variable Value）、Debug（Log）、Delay（Wait Seconds/Frames）、Find（WithTag）、GameObject（Instantiate/Destroy/Active）、Navigation（NavMeshAgent）、Physics（Force/Collision/Trigger）、Resource（Audio/ParticleSystem）、Scene、Transform。

**Subgraphs：** `Run Subgraph`（静态，烘焙进父图）、`Run Subgraph Dynamically`（运行时 BlackboardVariable<Subgraph>）。

## 5. Blackboard 变量

- 每个图有自己的 Blackboard；可额外引用共享 Blackboard 资产（多个图读写同一组变量）。
- 变量选项：**Expose**（ inspector 可见可改）、**Shared**（跨图实例共享）。
- ⚠️ Shared 变量不能直接传给子图；子图要用自己的 Blackboard 资产做接口。
- ⚠️ 不要用 `RuntimeBlackboardAsset` 在代码里编辑非共享变量（每图实例独立）。
- BBV 支持隐式转换：`float→int`、`GameObject→Component`、`ComponentA→ComponentB`（自动 GetComponent）。

## 6. 条件节点与 Observer Abort

**Conditional Branch**：if/else 分支，`Check if` = Any/All Are True。
**Conditional Guard (Action)**：条件真放行，假返回 failure。
**Switch**：按枚举分支（非条件）。

**Observer Abort（优先级中断）：** 高优先级分支条件变真时自动中断低优先级分支。
- 优先级：**左 = 高，右 = 低**（子节点索引顺序）。
- Observer 类型：
  - `None`：仅一次性条件检查（无 observer 行为）
  - `Self`：运行中条件变化则 guard 失败
  - `LowerPriority`：优先级中断低优先级兄弟
  - `Both`：Self + LowerPriority
- 推荐用 `Priority Abort` 节点做纯优先级中断。

## 7. BehaviorGraphAgent 与 C# 交互

**Agent 生命周期：** `Awake→Init()`（AcquireInstance 克隆源图 + OnSetup）→ `Update()` 自动 `Start()`+`Tick()` → `OnDestroy` 释放。
- `agent.Graph`：运行时 `BehaviorGraph` 实例（public）。
- `agent.Init()`：手动初始化（public）。
- `agent.Graph.Start()/Tick()/End()/Restart()`：public，可手动驱动。
- `agent.enabled = false`：阻止自动 Update tick，可由外部调度器手动 `Graph.Tick()`（分片调度）。
- `agent.SetVariableValue(name, value)` / `GetVariable<T>(name, out var)`：读写黑板变量。
- `BlackboardVariable.OnValueChanged`：订阅变量变化。
- 初始禁用的 agent 要在创建时显式 `enabled=false`。

**C# 绑定示例：**
```csharp
[SerializeField] BehaviorGraphAgent m_Agent;
BlackboardVariable<StateExample> m_StateBBV;
void OnEnable() {
    if (m_Agent.GetVariable("StateToReact", out m_StateBBV))
        m_StateBBV.OnValueChanged += OnStateValueChanged;
}
void OnDisable() { if (m_StateBBV != null) m_StateBBV.OnValueChanged -= OnStateValueChanged; }
```

**Event Channel：** 全局通信，`Create > Behavior > Event Channel`，`m_EventChannel.SendEventMessage(value)` / `m_EventChannel.Event += handler`。

## 8. 运行时图生命周期（`BehaviorGraph`）

| API | 行为 |
|---|---|
| `AcquireInstance(owner, sourceGraph)` (internal) | 克隆源图 + 初始化模块/节点（OnSetup），设 `module.GameObject = owner` |
| `Start()` | 启动根节点 + 注册 shared 黑板回调 |
| `Tick()` | 推进一步 |
| `End()` | 停止根 + 重置模块 + 注销回调 |
| `Restart()` | End + Start |
| `ReleaseInstance(instance)` (internal) | End + OnTeardown |

- `BehaviorGraph` 是 `ScriptableObject`；`AcquireInstance` 用 `ScriptableObject.Instantiate` 深拷贝（保留 `[SerializeReference]` 节点树和内部引用）。
- `agent.Init()` → `AcquireInstance(gameObject, m_Graph)` → `InitializeInstance(owner)`：遍历 module 设 `GameObject=owner`、`InitializeNodes()`（遍历 Root 树设 `node.Graph=module` + `OnSetup`）。

## 9. 创作图内部 API（程序化构建，编辑器脚本）

> 仅 `Assembly-CSharp-Editor`（无 asmdef 的 Editor 脚本）可用，靠 `Unity.Behavior`/`Unity.Behavior.Authoring`/`Unity.Behavior.GraphFramework` 三程序集对 `Assembly-CSharp-Editor` 的 `[InternalsVisibleTo]`。

**关键类型（全 internal，IVT 可访问）：**
- `BehaviorAuthoringGraph : GraphAsset`（源图）
- `GraphAsset.CreateNode(Type nodeModelType, Vector2 pos, PortModel connectedPort, object[] args)`：创建节点模型，可选自动连线
- `NodeModel` / `BehaviorGraphNodeModel` / `StartNodeModel` / `BranchingConditionNodeModel` / `ActionNodeModel` / `CompositeNodeModel` / `ModifierNodeModel`
- `PortModel.ConnectTo(port)` / `nodeModel.FindPortModelByName(name)` / `nodeModel.OutputPortModels`
- `NodeRegistry.GetInfo(typeof(RuntimeNode))` / `GetInfoFromTypeID` / `NodeInfos`
- `ConditionUtility.GetInfoForConditionType(typeof(Condition))`
- `ConditionModel(nodeModel, condition, info)` + `((IConditionalNodeModel)node).ConditionModels.Add(...)`
- `graph.ValidateAsset()` / `graph.BuildRuntimeGraph(true)` / `graph.EnsureAssetHasBlackboard()`

**程序化建图流程（见 `Assets/Editor/CitizenSim/BtGraphBuilder.cs`）：**
```csharp
var graph = ScriptableObject.CreateInstance<BehaviorAuthoringGraph>();
AssetDatabase.CreateAsset(graph, AssetPath);  // 触发导入校验,自动加 Start 根
var start = (StartNodeModel)graph.Roots[0];   // 用自动加的 Start,别再建一个
start.Repeat = true;
PortModel startOut = start.OutputPortModels.First();

var branch = (BranchingConditionNodeModel)graph.CreateNode(
    typeof(BranchingConditionNodeModel), new Vector2(0,160), startOut,
    new object[]{ NodeRegistry.GetInfo(typeof(BranchingConditionComposite)) });

var cond = new ConditionModel(branch, new IsHungryCondition(),
    ConditionUtility.GetInfoForConditionType(typeof(IsHungryCondition)));
((IConditionalNodeModel)branch).ConditionModels.Add(cond);

PortModel truePort = branch.FindPortModelByName("True");
graph.CreateNode(typeof(ActionNodeModel), new Vector2(-220,340), truePort,
    new object[]{ NodeRegistry.GetInfo(typeof(SeekFoodAction)) });
// ... False -> WanderAction

graph.ValidateAsset();
graph.BuildRuntimeGraph(true);  // 烘焙运行时子资产
AssetDatabase.SaveAssets();
```

**节点发现：** `NodeRegistry.PopulateTypeInfo()` 用 `TypeCache.GetTypesWithAttribute<NodeDescriptionAttribute>()` 扫**所有程序集**，`[NodeDescription]` 的 Action/Composite/Modifier 子类自动注册到面板；`[Condition]` 的 Condition 自动注册到条件选择。

## 10. 踩坑清单（本会话经验）

1. **`[NodeDescription]`/`[Condition]` 的 `id` 必须唯一**：默认 `""`，多个节点不写 id → GUID 冲突 → 后注册的被 `PopulateTypeInfo` 静默跳过（`m_TypeIDToNodeInfo.ContainsKey(GUID)` 时 continue）。症状：两个不同 Action 烘焙后变成同一个。**修复：每个节点写唯一 32 位 hex id。**

2. **手建运行时图丢失 Parent**：直接 `new Start{Child=...}` 拼运行时节点树（绕过 `GraphAssetProcessor`），`Node.Parent`（`[DontSerialize]`）在 `ScriptableObject.Instantiate` 后丢失 → `AwakeParents()` 空操作 → selector 完成无法唤醒 `Start.Repeat` → 首个决策后图死锁，`Tick()` 永远空转。**修复：用创作图 + `BuildRuntimeGraph`（编辑器管线正确设 Parent），或在运行时反射补 Parent 链。**

3. **普通字段不烘焙**：`ProcessNodeFields` 只处理 `BlackboardVariable` 字段。普通 `public GoalType goalType` 烘焙后用默认值。**修复：用 BlackboardVariable<T> + story 暴露，或拆成多个 Action 子类。**

4. **`AssetDatabase.CreateAsset` 触发 `EnsureAtLeastOneRoot`**：空创作图创建时自动加一个 Start。程序化建图时**用自动加的 Start**，别再 `CreateNode(StartNodeModel)` 建一个（会变成 2 个根）。

5. **`Action` 名字歧义**：Unity 6 若开启 `using System;` 隐式 using，`Action` 在 `Unity.Behavior.Action` 与 `System.Action` 间歧义（CS0104）。用 `using Action = Unity.Behavior.Action;` 别名，或确认项目未开隐式 using。

6. **`NodeRegistry`/`PortModel` 跨命名空间**：`NodeRegistry` 在 `Unity.Behavior` 和 `Unity.Behavior.GraphFramework` 都有；`PortModel`/`NodeModel`/`GraphAsset` 在 `Unity.Behavior.GraphFramework`。编辑器脚本要 `using Unity.Behavior.GraphFramework;` + `using NodeRegistry = Unity.Behavior.NodeRegistry;`。

7. **`execute_code`（MCP）无 IVT**：Unity MCP 的 `execute_code` 内存程序集**没有** IVT，不能直接引用 internal 类型，需反射。编辑器脚本（Assembly-CSharp-Editor）才有 IVT。

8. **`ConditionalGuardModifier` 隐藏**（`hideInSearch=true`）：面板搜不到。条件通过节点 inspector 的 "Add Condition" 流程加（`AddConditionToNodeCommand` 会隐式包 Guard）。程序化构建可用 `Conditional Branch`（可见）替代 Selector+Guard。

## 11. 本项目（CitizenSim）的用法

- **资产**：`Assets/Resources/CitizenBehavior.asset` 是 `BehaviorAuthoringGraph`（可双击编辑）。树：`Start(Repeat) → Conditional Branch[IsHungry] → True:SeekFoodAction / False:WanderAction`。
- **构建**：`Tools/CitizenSim/Build CitizenBehavior Graph Asset` 菜单（`BtGraphBuilder.cs`）程序化重建该资产。改树结构后重跑此菜单。
- **运行时加载**：`CitizenBootstrap` 用 `Resources.LoadAll<BehaviorGraph>("CitizenBehavior")` 取烘焙的运行时子资产，每 citizen `AddComponent<BehaviorGraphAgent>` + `agent.Init()`。
- **分片调度**：`BtScheduler`（Design A）`agent.enabled=false` 阻止自动 tick，round-robin 手动 `agent.Graph.Tick()`（~16/帧，每 agent ~0.5s 决策一次）。
- **架构**：GO-centric 混合 ECS。BT 只决策"去哪"→写 `CitizenAuthoring.currentGoal` → `SnapshotSystem` 同步进 ECS → ECS 执行移动/需求。GO 是 source of truth。
- **自定义节点**：`SeekFoodAction`/`WanderAction`（Action）、`IsHungryCondition`（Condition），决策纯函数在 `GoalDecision`。

## 12. 参考文档（包内 `Documentation~`）

核心：`behavior-graph.md`（概念）、`node-types.md`（节点速查）、`create-custom-node.md`、`blackboard-variables.md`、`conditional-node.md`、`behavior-agent.md`、`runtime-lifecycle-overview.md`、`bind-c.md`（C# 交互）、`observer-abort.md`、`behavior-graph-assets.md`。
高级：`observer-abort-mechanics.md`、`enum-dependency-tracking.md`、`serialization.md`（运行时图序列化保存/加载）、`event-nodes.md`、`debug.md`。
