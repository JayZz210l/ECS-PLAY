# ECS + 行为树 市民行为模拟 Demo — 设计规格

- 日期：2026-07-15
- 状态：已定稿，待实现
- 工程：`D:\Unity\UnityProject\ECS-PLAY`（Unity 6000.5.3f1，URP 17.5）
- 目标：面向简历的技术 demo

## 1. 概述

一个"商业游戏级工程质量"的市民行为模拟 demo。500 个市民在网格世界上按需求（饥饿/疲劳/娱乐）生活，遭遇威胁时会恐慌逃散。

核心卖点：**规模与性能**——用 ECS + Job/Burst 做高频模拟，用 Unity Behavior 包做行为树决策，并在 GameObject 主体架构与 DOTS 优化层之间给出清晰的同步边界与 profiling 证据。

### 架构原则（硬约束）

商业 Unity 游戏以 **GameObject 为脊柱**，ECS 作为**性能优化层**后挂，不做纯 ECS 架构。市民是 GameObject（身份、BT、配置、Transform、渲染的权威），ECS 镜像实体只承载每帧高频模拟计算。

### 决策汇总

| 维度 | 决定 |
|---|---|
| 核心卖点 | 规模与性能（ECS + Job/Burst + profiling） |
| 同屏规模 | 500 市民（可拉伸到 2000/5000 展示上限） |
| 行为树运行时 | 必须用 Unity Behavior 包（`com.unity.behavior` 1.0.16） |
| 画面 | 极简抽象（彩色胶囊 + 网格），颜色编码当前状态 |
| 行为范围 | 需求驱动日常 + 威胁逃散 |
| 时间预算 | 不限，按 ~4 周垂直切片设计 |
| ECS 形态 | 真 ECS 实体（Entities 1.0 / ISystem / IJobEntity / Burst），非 NativeArray+Job |

## 2. 世界与场景

- **地表**：100×100 网格地面，浅色 + 网格线，既是视觉参考也是空间哈希基底。无装饰物。
- **POI（兴趣点）**：食物点（若干）、家（若干住宅区点，每市民分配其中一个家点）、娱乐点（若干）。漫游无固定 POI。
- **市民**：500 个胶囊，出生在自家/住宅区。
- **状态着色**（抽象画面下让模拟可读，也是视频高光）：
  - 红 = 饥饿中/前往食物点
  - 蓝 = 疲劳中/回家
  - 黄 = 娱乐中
  - 绿 = 漫游/空闲
  - 白 = 威胁逃散（Flee）
- **相机**：固定高斜俯视角，看全图人群流动。不允许玩家操控。可加慢速轨道环绕作演示镜头。用 Cinemachine 虚拟相机锁定。
- **规模感**：地图大小调到 500 胶囊"密集但不糊成一团"，能看清个体流动也看出群体趋势。

## 3. ECS 数据与 System（DOTS 优化层）

市民是 GameObject，每个 GO 有一个**镜像 Entity**，用 `int citizenIndex` 一一映射（由 `CitizenRegistry` 管理）。

### 权威拆分

| 数据 | 权威 | 备注 |
|---|---|---|
| 身份/配置（家、阈值、速度参数） | GO (MonoBehaviour) | Inspector 可改 |
| Needs 值（饥饿/疲劳/娱乐） | GO 存储 / ECS Job 算衰减 | 每帧快照进 ECS，Job 衰减后回写 GO |
| 当前目标（类型 + 目标点） | GO（BT 产出） | BT 写 GO，同步进 ECS |
| Position | GO Transform 权威 | Job 算速度，主线程 apply 到 Transform |
| Velocity / 邻居 / 空间格 | ECS（纯中间量） | 不回写 GO |

GO 是 source of truth，ECS 只持有每帧快照 + 计算中间量。

### 镜像 Entity 组件（IComponentData）

`SimPosition(float3)` · `SimVelocity(float3)` · `SimNeeds(float3)` · `SimGoal(int type, float3 target)` · `SimRadius(float)` · `GridCell(int2)` · `CitizenIndex(int)` · `Threatened`（enableable 组件，见 §4）

### 实体创建

市民是运行时动态生成的 GO，镜像 Entity 也运行时 `EntityManager.CreateEntity` 创建。**不走 subscene/baking**（非预 authored）。市民一次性生成、不动态增删，结构变更只在 init/teardown——**每帧零结构开销**。

## 4. 行为树结构（Unity Behavior 包）

**核心模型**：BT 只负责**决策"当前去哪"**，把目标写进 GO；移动和"满足需求"由 ECS 执行。BT 不碰每帧移动。

### 树结构

```
Selector (root)
├─ Sequence [威胁]              // 反应式中断，最高优先级
│   ├─ Condition: IsThreatened
│   └─ Action: SetGoal(Flee, 远离威胁点)
├─ Sequence [饿了]
│   ├─ Condition: IsHungry     (hunger > 阈值)
│   └─ Action: SetGoal(SeekFood, 最近食物点)
├─ Sequence [累了]
│   ├─ Condition: IsFatigued   (fatigue > 阈值)
│   └─ Action: SetGoal(SeekHome)
├─ Sequence [无聊]
│   ├─ Condition: IsBored      (fun < 阈值)
│   └─ Action: SetGoal(SeekFun, 最近娱乐点)
└─ Action: SetGoal(Wander)      // 兜底漫游
```

目标类型 5 种：`Flee / SeekFood / SeekHome / SeekFun / Wander`。
转向模式 3 种：`seek / arrive / evade`。

### 自定义节点

C# 继承 Unity Behavior 的 `Action`/`Condition`，`[NodeInfo]` 暴露给图编辑器：
- `IsThreatened / IsHungry / IsFatigued / IsBored` Condition：从 GO 的 `CitizenNeeds` / 威胁标记读值。
- `SetGoal` Action：算最近 POI（或 Flee 的远离方向），写到 GO 的 `currentGoal` 字段（下一帧 `SnapshotSystem` 同步进 ECS）。

### 共享图 + 独立 blackboard

500 个市民共用**同一个 BehaviorGraph 资产**（树结构一份），每个 agent 各自一份 blackboard 实例（自己的 needs/阈值）。惯用法，省内存。

### 目标执行（ECS 侧）

- BT 设 `SeekFood` -> `SteeringSystem` 朝食物点走 -> 到达 -> `NeedsDecaySystem` 检测"在食物点"则**反向衰减**（吃，hunger 降）。回家=疲劳降，娱乐点=fun 升。
- hunger 降到阈值下，BT 下次 tick 自然切到下一需求，不 flip-flop。
- Wander 到达后由 ECS 随机重选附近点（非"决策"，不劳烦 BT）。

### BT 时间分片（性能关键 + 最大风险）

500 个 agent 不能每帧全 tick。目标：每市民约 0.5–1s 决策一次，调度器每帧 tick ~17 个，BT 开销有界。

**要尽早验证** Unity Behavior 1.0.16 的 agent 支持 Manual/手动 tick（理想：设 Manual，调度器调 `Evaluate`）。若只支持每帧自动，退路是 round-robin 启用/禁用 agent（能跑但略丑）。**这是整个 demo 第一个要 spike 的技术风险点。**

## 5. GO↔DOTS 同步边界（工程核心）

### 映射

`CitizenRegistry`（MonoBehaviour 单例）持有 `GameObject[] gos` + `Entity[] entities` + count，按下标一一对应。

### 每帧管线

| # | System | 线程 | 做什么 |
|---|---|---|---|
| 1 | SnapshotSystem | 主线程 | 遍历 GO：读 Transform/Needs/goal -> `EntityManager.SetComponentData` 写镜像实体 |
| 2 | NeedsDecaySystem | IJobEntity·Burst | 衰减 needs；在对应 POI 到达时反向衰减（吃/睡/玩） |
| 3 | SpatialGridSystem | Job·Burst | 按 SimPosition 建空间哈希网格 |
| 4 | ThreatDetectionSystem | IJobEntity·Burst | 每帧查威胁半径，设 `Threatened`（enableable） |
| 5 | SteeringSystem | IJobEntity·Burst | 按 goal 类型 seek/arrive/evade + 网格邻居避障 -> SimVelocity |
| 6 | ResolveSystem | 主线程 | 读 SimVelocity/SimNeeds -> apply 到 GO Transform、回写 needs |

Job 只碰实体组件（非托管），GO/Registry 只在主线程 1 和 6 碰——干净隔离。

### 威胁的反应性设计（关键决策）

BT 是低频分片 tick 的，但 Flee 要立刻反应。职责再切一刀：
- **ECS 每帧**做威胁检测（空间查询），写 `Threatened` 标记。
- **BT 调度器**给被标记的市民**插队**（当帧/次帧 tick，不受分片节流），其余照常分片。
- BT 的 `IsThreatened -> SetGoal(Flee)` 仍负责**下决策**，ECS 只喂条件 + 插队。

这样"BT 独占决策权"不破，反应延迟 1–2 帧（肉眼不可见），被威胁人数通常很少，插队开销有界。

### 主线程预算与诚实的上限

1 和 6 各 500 次 GO 读写，500 规模下远低于 1ms。但 GO Transform 写回是主线程操作——**这是 GO-centric 架构的天花板**：拉到 5000 时这两步成瓶颈（纯 ECS 能继续线性扩展）。这本身是 demo 的 talking point。

## 6. 性能、Profiling 与测试

### 性能目标

- 500 市民稳定 60fps（普通开发笔记本），sim+BT < ~5ms，渲染可忽略。
- 拉伸：2000 仍 60fps，5000 展示天花板拐点。

### Profiling 产物（demo / 简历证据，必做）

1. **游戏内 HUD**：实时帧时间、市民数、各 System 毫秒分解、BT ticks/帧。
2. **Unity Profiler 截图**：500/2000/5000 三档帧分解，标主线程 vs Job。
3. **agents-vs-frametime 曲线**：人数对帧时间，标 GO 同步天花板拐点。
4. **Burst on/off 对比数字**。

### 优化手段清单（简历关键词）

IJobEntity+Burst · enableable 组件免 archetype 碎片 · 空间哈希网格 O(1) 邻居（vs O(n²)）· ECS chunk SoA 缓存友好 · BT 时间分片 · GPU instancing 胶囊 · 每帧零 GC。

### 诚实的上限分析

- 500：轻松 60fps。
- ~2000–3000：主线程 GO 同步成瓶颈。
- 5000：GO-centric 撞墙，纯 ECS 可继续线性扩展。拐点画进曲线、写进 README。

### 测试与验证

- **单元测试**：BT 自定义节点决策正确性（给定 needs 选对 goal）、needs 衰减数学、空间网格查询、GO↔ECS 往返数据保真。
- **性能测试**（可选，加 Performance Testing Package）：CI 跑 N=500/2000 帧时间回归。
- **边界/错误处理**：找不到 POI -> Wander 兜底；威胁半径无人 -> no-op；BT manual tick 不支持 -> 退路 round-robin；GO 销毁只在 init/teardown 清 registry。

## 7. 可扩展刻度盘 + 交付物 + 里程碑

### 运行时刻度盘（demo 高光）

- 快捷键切市民数：100 / 500 / 2000 / 5000，实时重生 + HUD 帧时间响应——视频 climax 与性能实证。
- 快捷键召唤/移动威胁区（恐慌瞬间）。
- （可选）快捷键开关 BT 分片，直观对比其作用。

### 交付物

1. 可运行 Unity 6.5 工程（URP）。
2. demo 视频（~1–2 分钟）：人群流动 -> 需求行为(颜色切换) -> 恐慌瞬间 -> 刻度盘 500->5000 + HUD。
3. **README**：架构总览 + GO↔DOTS 同步图 + profiling 数字/曲线 + 天花板分析 + 运行说明。README 本身是核心简历材料。

### 里程碑（一人，~4 周垂直切片，每步可交付）

| 里程碑 | 周次 | 内容 | 交付证据 |
|---|---|---|---|
| M1 地基与最小闭环 | W1 | 场景+网格+500 胶囊 GO+相机；Registry+镜像实体；Snapshot->Steering(seek)->Resolve；HUD v1 | 500 胶囊经 ECS 镜像流畅移动，同步脊柱跑通 |
| M2 行为树接入 | W2 | Unity Behavior 图(先 hunger->SeekFood->Wander)；自定义节点；验证 manual tick + 分片调度；needs 衰减+到点吃；状态着色 | BT 驱动的需求闭环，分片生效 |
| M3 完整需求+空间网格 | W3 | fatigue/fun + 家/娱乐点，全 BT；空间哈希网格+邻居避障；Wander 重选 | 完整日常，市民不穿模 |
| M4 威胁+刻度盘+profiling | W4 | 威胁区+ThreatDetection+enableable+BT 插队+evade；刻度盘；HUD v2+Profiler 截图+曲线+Burst 对比；README+视频 | 完整 demo + 性能实证 |

单元测试随各系统落地织入 M2–M4。

## 8. 明确不做（YAGNI 收口）

- 无存档/持久化
- 无玩家相机操控
- 无非原始几何美术
- 无经济/职业系统（已选需求驱动）
- 无多人
- 仅 PC standalone 构建
- 威胁只做单个区域

## 9. 风险

1. **Unity Behavior manual tick API**：若 1.0.16 不支持手动/低频 tick，退路 round-robin 启用/禁用 agent。M2 起头先 spike。
2. **GO 同步主线程上限**：5000 规模下 Snapshot+Resolve 的 Transform 读写成瓶颈——这是架构固有 ceiling，作为 talking point 而非缺陷呈现。
3. **Unity Behavior 500 agent 实例开销**：即便分片，500 个 GameObject+BehaviorGraphAgent 的常驻开销需 profiling 确认；若超标，考虑 B 方案（子集 BT + ECS 大众）作为退路。
