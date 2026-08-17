# M2 行为树接入：Bug 与问题解决流程

> 记录 M2（Unity Behavior 接入）期间遇到的 Bug 与问题，以及排查/修复流程。
> 供后续会话参考调试方法论与具体踩坑。

## 一、通用调试方法论

整个会话解决问题的流程遵循这个循环：

```
1. 观察    - 采样运行时状态(读 GO/ECS 数据)、看 Console 报错
2. 隔离    - 关掉调度器/手动单步,排除并发干扰
3. 对比    - 找"能工作"与"不能工作"的差异(如 Restart 能 vs Tick 不能)
4. 读源码  - grep 包源码,定位内部机制(Parent/AwakeParents/烘焙器)
5. 反射验证 - execute_code 反射查 internal 字段,确认假设
6. 加日志  - 节点里加 Debug.Log,追踪实际执行路径
7. 假设->验证 - 形成根因假设,设计实验证伪/证实
8. 修复->回归 - 改代码,重测,确认不破坏其他
```

关键原则：
- **先证伪再动手**：不靠猜，用最小实验定位根因（如 `Restart()` 能切 SeekFood 就证明决策逻辑没问题，问题在重新评估）。
- **读包源码**：Unity Behavior 很多内部机制靠 `Library/PackageCache/com.unity.behavior@...` 源码确认，不靠文档/猜测。
- **execute_code 反射**：MCP 的 execute_code 无 IVT，查 internal 状态用反射，逐步打日志定位 NRE。
- **改一处验一处**：每次小改后编译 + 跑测试 + Play 验证，避免叠加多个改动难定位。

### 流程一图

```
现象 ──> 采样状态/看报错 ──> 找能工作 vs 不能工作的差异
                                      │
                                      ▼
                            形成根因假设 ──> 读包源码/反射验证
                                      │
                                      ▼
                            确认根因 ──> 最小修复 ──> 编译+测试+Play 回归
                                      │
                              (若假设错) 回到采样
```

核心心法：**不猜，用最小实验证伪**。`Restart()` 能切 SeekFood 这一发实验，直接把"决策逻辑错"排除、锁定"重新评估机制错"，省掉大量弯路。

---

## 二、具体问题清单

### 问题 1：市民跑 ~6 秒后全停下来不动

**症状**：M2 闭环 Play 验收时，市民移动 ~6 秒后集体冻结。所有 500 个 `needs.x=1.0`（饿）但 `goal=Wander`，目标=当前位置（dist=0）。

**排查**：
1. 采样运行时状态：发现 needs.x 已过阈值但 goal 没切 SeekFood。
2. 手动 `agent.Graph.Tick()` 单个市民：goal 不变。
3. 加日志到 `IsHungryCondition`/`SetGoalAction`：看到首个 tick 正常写 Wander 目标，之后无重新决策。
4. `agent.Graph.Restart()`：能切到 SeekFood -> **证明决策逻辑正确，问题在"重新评估"机制**。
5. 反射查运行时图：`selector.Parent = NULL`、`start.Parent = NULL`。
6. 读源码 `Node.cs`/`BehaviorGraphModule.cs`：`AwakeParents()->AwakeNode(Parent)`，Parent 为 null 则空操作；`Start.OnUpdate` 靠 selector 完成后 `AwakeParents` 唤醒才能 Repeat。

**根因**：手建运行时图的 `Node.Parent` 是 `[DontSerialize]`，`ScriptableObject.Instantiate`（agent.Init）深拷贝后丢失。Parent 为 null -> selector 完成无法唤醒 Start -> `Start.Repeat` 死锁 -> 首个决策后所有 `Tick()` 空转。市民拿到一个 Wander 目标跑到后（~6s）就永远停下。

**修复**：切换到 `BehaviorAuthoringGraph` + `BuildRuntimeGraph`，让编辑器管线 `GraphAssetProcessor` 烘焙（用 `parent.Add(child)` 正确设 Parent）。中间还试过在 `BtScheduler` 反射补 Parent 链（见问题 4）。

---

### 问题 2：CitizenBehavior 双击打不开

**症状**：资产双击无反应，不能在 Behavior 编辑器打开。

**排查**：读 `BehaviorWindow.cs`：`m_Asset` 是 `BehaviorAuthoringGraph`，`Open(BehaviorAuthoringGraph)`。查资产 YAML：`m_Script` GUID 是 `BehaviorGraph`（运行时），不是 `BehaviorAuthoringGraph`。

**根因**：资产是手建的运行时 `BehaviorGraph`，编辑器窗口只认源图 `BehaviorAuthoringGraph`。

**修复**：`BtGraphBuilder` 改为创建 `BehaviorAuthoringGraph`（源图），烘焙出运行时子资产。`CitizenBootstrap` 改用 `Resources.LoadAll<BehaviorGraph>` 取子资产。

---

### 问题 3：手动连线时 inspector 没有 goalType 字段

**症状**：在编辑器手动建 SetGoal 节点，inspector 只有 "Edit Definition/Edit Script"，没有 goalType 下拉。

**排查**：读 `NodeRegistry.GetStoryVariableNames`：story 字符串里 `[字段名]` 占位符才暴露字段。读 `DefaultNodeTransformer.ProcessNodeFields`：只处理 `BlackboardVariable` 字段。

**根因**：`SetGoalAction.goalType` 是普通枚举字段，story 没引用，且非 BlackboardVariable -> 不暴露、不烘焙。

**修复**：改为程序化连线。把 `SetGoalAction`（带 goalType）拆成 `SeekFoodAction` + `WanderAction` 两个独立 Action，goalType 隐含在类型里，烘焙天然正确。

---

### 问题 4：Parent 反射修复的 InvalidCastException

**症状**：`BtScheduler.FixupParentChain` 抛 `InvalidCastException`。

**排查**：反射代码对每个节点同时设 `Modifier.m_Parent`/`Composite.m_Parent`/`Action.m_Parent` 三个字段。这三个字段在不同叶子类，`SetValue` 类型不匹配抛异常。

**根因**：`m_Parent` 在每个叶子类各自声明，对错误类型实例 `GetValue`/`SetValue` 抛 TargetException。

**修复**：用 `is` 类型守卫，按节点实际类型只设对应字段：
```csharp
switch (node) {
    case Modifier m:  s_ModifierParent?.SetValue(m, parent); break;
    case Composite c: s_CompositeParent?.SetValue(c, parent); break;
    case Action a:    s_ActionParent?.SetValue(a, parent); break;
}
```
（后来切到创作图后这个 fixup 变成冗余安全网，但保留。）

---

### 问题 5：程序化建图出现两个 Start 根节点

**症状**：`BtGraphBuilder` 跑完 `Nodes=7, Roots=2`，预期 Roots=1。

**排查**：逐步打日志，发现 `AssetDatabase.CreateAsset` 后立即 `Nodes=1, Roots=1`--CreateAsset 触发导入校验，`EnsureAtLeastOneRoot` 在空图时自动加了一个 Start。然后我又 `CreateNode(StartNodeModel)` 建了一个 -> 2 个。

**根因**：没意识到 CreateAsset 会自动加 Start 根。

**修复**：复用自动加的 Start（`graph.Roots[0]`），不再自建。

---

### 问题 6：两个子节点都烘焙成 SeekFood（Wander 丢了）

**症状**：程序化建图后，反射查运行时树：`BranchingConditionComposite -> Children(2): SeekFoodAction, SeekFoodAction`。Wander 变成了 SeekFood。

**排查**：读 `NodeRegistry.PopulateTypeInfo`：`if (m_TypeIDToNodeInfo.ContainsKey(attribute.GUID)) continue;` -- GUID 重复的后注册节点被跳过。读 `NodeDescriptionAttribute`：`id` 默认 `""`，`GUID = new SerializableGUID("")`。`SeekFoodAction` 和 `WanderAction` 都没写 id -> 相同 GUID -> `WanderAction` 被跳过 -> `GetInfo(typeof(WanderAction))` 返回 null -> 烘焙成错误节点。

**根因**：`[NodeDescription]` 的 `id` 默认空串，多个节点不写 id 导致 GUID 冲突，后注册的被静默跳过。

**修复**：给 `SeekFoodAction`/`WanderAction`/`IsHungryCondition` 各加唯一 32 位 hex `id`。

---

### 问题 7：execute_code 无法引用 internal 类型

**症状**：execute_code 里 `as Unity.Behavior.BehaviorAuthoringGraph` 编译报 "inaccessible due to protection level"。

**根因**：MCP 的 execute_code 内存程序集没有 IVT（只有 Assembly-CSharp-Editor 有）。

**修复**：execute_code 里查 internal 状态一律用反射（`Type.GetType` + `GetField`/`GetProperty` + `BindingFlags`）。注意 `static` 字段要加 `BindingFlags.Static`，实例字段要加 `Instance`。

---

### 问题 8：NodeRegistry / PortModel 命名空间歧义

**症状**：`BtGraphBuilder.cs` 编译报 `CS0104 'NodeRegistry' is ambiguous`（`Unity.Behavior.NodeRegistry` vs `Unity.Behavior.GraphFramework.NodeRegistry`），`PortModel` 找不到。

**根因**：`NodeRegistry` 在两个命名空间都有；`PortModel`/`NodeModel`/`GraphAsset` 在 `Unity.Behavior.GraphFramework`，没 using。

**修复**：`using Unity.Behavior.GraphFramework;` + `using NodeRegistry = Unity.Behavior.NodeRegistry;` 别名。

---

### 问题 9：`Action` 名字歧义（CS0104）

**症状**：`public class SetGoalAction : Action` 编译报 `Action` 歧义。

**根因**：Unity 6 隐式 `using System;` 让 `System.Action` 与 `Unity.Behavior.Action` 冲突。

**修复**：`using Action = Unity.Behavior.Action;` 别名（CitizenSim 实际未开隐式 using，但 BtGraphBuilder 编辑器脚本曾遇到）。

---

## 三、问题分类与对应策略

| 问题类型 | 典型问题 | 策略 |
|---|---|---|
| 运行时行为异常（卡死/不切换） | 问题 1 | 采样状态 + 对比 Restart/Tick + 读源码确认机制 + 反射查 internal 字段 |
| 编辑器集成问题（打不开/字段不显示） | 问题 2、3 | 读编辑器窗口/属性源码，确认资产类型/字段暴露机制 |
| 反射类型不匹配 | 问题 4 | `is` 守卫，按实际类型选字段 |
| 程序化建图计数异常 | 问题 5、6 | 逐步打日志定位插入点；读注册表去重逻辑 |
| 编译/可见性问题 | 问题 7、8、9 | IVT/命名空间/别名 |

---

## 四、可复用的调试片段

### 反射查运行时图结构（execute_code）
```csharp
var bf = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
var fGraphs = typeof(Unity.Behavior.BehaviorGraph).GetField("Graphs", bf);
var module = ((IList)fGraphs.GetValue(agent.Graph))[0];
var root = module.GetType().GetField("Root", bf).GetValue(module);
// 递归 Child(Modifier) / Children(Composite) 遍历
```

### 验证 BT 决策逻辑（隔离调度器）
```csharp
BtScheduler.Instance.enabled = false;
ca0.needs.x = 0.95f; ca0.hungerThreshold = 0.5f;
agent.Graph.Restart();  // 强制重新评估,绕过同帧限制
// 检查 ca0.currentGoalType 是否切到 SeekFood
```

### 逐步日志定位节点插入
```csharp
Debug.Log($"[BtGraphBuilder] after X: Nodes={graph.Nodes.Count} Roots={graph.Roots.Count}");
```
