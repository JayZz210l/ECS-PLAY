# Unity3D 客户端（Gameplay方向）校招面试准备 — 市民行为模拟 Demo

> 目标岗位：Unity3D 客户端开发（Gameplay 方向）校招
> 项目：`ECS + 行为树 市民行为模拟 Demo`（Unity 6000.5.3f1 · URP 17.5 · Entities 1.0 · Unity Behavior 1.0.16）
> 使用说明：本文基于项目真实代码与性能数据整理。每个问题给出"回答要点"（该讲的核心逻辑）与"可能追问/应对"（面试官往下挖的方向）。**不要背，要讲清楚为什么**——追问会逐层挖到你没准备的地方，只有理解了才能接住。

---

## 第一部分：简历怎么写这段经历

### 1. 简历条目（中文，可直接用）

> **Unity 大规模市民行为模拟系统（个人技术 Demo）**　2026.07 - 2026.08
>
> 使用 Unity 6000 / DOTS 实现 100~5000 名市民的自主行为模拟（需求驱动决策 + 威胁逃散），核心是 **GameObject 为脊柱、ECS 为优化层** 的混合架构与清晰的同步边界。
>
> - **架构设计**：市民由 GameObject 持有身份/行为树/配置（source of truth），挂载 ECS 镜像实体承载每帧高频模拟（需求衰减/空间网格/避障/转向）。设计并实现 6 步主线程-Burst 交错管线：`Snapshot → NeedsDecay → SpatialGrid → ThreatDetect → Steering → Resolve`，GO 只在主线程入口/出口被访问，Job 只碰非托管数据。
> - **行为树**：基于 Unity Behavior 集成行为决策（饥饿/疲劳/娱乐/漫游/逃散五状态），实现共享 BehaviorGraph + 独立 blackboard（5000 agent 不复制树）、时间分片调度（每帧 `N/30` 个 + 受威胁者插队），威胁反应延迟 ≤2 帧。
> - **性能优化**：4 个 Burst Job 走多线程；`Threatened` 用 `IEnableableComponent` 标记，toggle 零 archetype 变更；空间哈希网格 + 9 邻域避障；需求阈值/威胁半径滞回防边界抖动。用 FrameTimingManager 产出 profiling 报告：500=111FPS、2000=52FPS、5000=29FPS，并诚实标注瓶颈为主线程 GO Transform 回写。
> - **工程实践**：EditMode 单测覆盖转向/决策/调度/检测纯函数（全绿）；设计文档、里程碑规划、性能报告齐全。

### 2. 简历上的三个原则

1. **主打"规模 + 性能 + 架构决策"，不要主打画面**。这是你区分于其他校招候选人的点——大多数人做的是功能 demo，你做的是带瓶颈分析和工程证据的性能 demo。
2. **每个技术点带一个"为什么"**。简历上只写结论，但面试要能讲出决策过程（见下）。"诚实标注 5000 是天花板"这句话非常加分，体现工程判断力。
3. **关键词要准**：`DOTS`、`Entities 1.0`、`ISystem / IJobEntity`、`Burst`、`Job System`、`IEnableableComponent`、`行为树`、`空间哈希`、`避障 Steering`、`Unity Behavior`、`MaterialPropertyBlock`、`Profiling`。这些是你面试被追问的范围，写到简历上的每个词都要能撑住追问。

### 3. 常见简历错误

- ❌ 写"用 ECS 实现了 5000 人" —— 面试官会问"纯 ECS？"，而你实际上是 GO+ECS 混合，答不上来就露馅。要主动写清楚混合架构。
- ❌ 只列功能不列数字 —— 行为树/避障是人人都会的，数字（FPS 曲线、5000 天花板、每帧 tick 数）才是你的记忆点。
- ❌ 写"流畅运行 5000 人" —— 你实测是 29FPS。被追问实测数字会对不上。写"5000 可达 29FPS 并分析瓶颈"反而显专业。

---

## 第二部分：面试题（按必问优先级排序）

### 模块 A：项目概述（开场必问）

**Q1. 介绍一下你这个项目是做什么的？**

回答要点（30~60 秒版）：
- 一句话：一个 100~5000 名市民自主生活的模拟 demo，市民有饥饿/疲劳/娱乐三种需求，会自己觅食、回家、找娱乐，遇到威胁会恐慌逃散。
- 一句话定位：这是一个**技术 demo，核心卖点是规模与性能**，不是画面。要证明"当 agent 数量上千时，传统纯 GameObject 写法会卡，我的混合架构在哪些环节优化、瓶颈又在哪里"。
- 一句话架构：GameObject 当脊柱（身份/决策/渲染），ECS 当每帧高频计算的优化层，两者用快照/回写同步。

**Q2. 为什么选行为模拟这个题材？**

- 游戏行业的真实场景（人口模拟、军团、群鸟、丧尸潮），能同时展示 AI 决策 + 大规模性能两件事。
- 数据驱动、可量化的性能对比（100→5000 的 FPS 曲线），适合作为简历上的工程证据。

---

### 模块 B：GO+ECS 混合架构（最高频，决定成败）

**Q3. 为什么不用纯 ECS？为什么坚持 GameObject 是 source of truth？**

回答要点：
- 纯 ECS 的代价：开发成本高（一切都要绕开 MonoBehaviour）、与 Unity 生态割裂（Inspector 配置、行为树、动画组件都难直接挂）、调试心智负担重。
- 我的取舍：**把"低频、身份性、需要引擎组件支撑"的数据放 GO**（身份、配置、行为树、Transform、渲染），**把"每帧高频、纯计算、Cache 友好"的部分放 ECS**（需求衰减、空间网格、避障、转向）。
- 商业项目里 ECS 几乎都是"局部优化层"而不是全部推翻重来，这个 demo 就是在模拟这种真实工程形态。
- 一个数字支撑：5000 时瓶颈在主线程 GO 回写（23ms），而 ECS 的 Burst Job 只占帧时间一小部分——说明 ECS 层本身扩展性是好的，天花板来自"要不要回写 GO"这个架构选择。

**追问：那为什么不把 Transform 也搬进 ECS？**

- 因为渲染要读 Transform，而且 Unity 的 Transform 回写（即使走 `SetPositionAndRotation`）本质上还是主线程/并行写 GO 内存。要完全绕开得用 Hybrid Renderer 的直接 ECS 渲染管线，那等于走纯 ECS 路线，和我的架构目标不符。
- 诚实版：这是我标的明确天花板。如果目标是 5000+ 且主线程回写不够，下一步就是把渲染也 DOTS 化（`EntitiesGraphics` / HybridRenderer），把 GO 层瘦身到只剩决策。

**Q4. 你这 6 步管线是怎么设计的？为什么要交错主线程和 Job？**

回答要点（画图 + 讲数据流向）：
```
GO(权威) →①Snapshot(主线程) → ECS镜像 → ②③④⑤ Burst Job → ⑥Resolve(主线程)→ GO
```
- ① Snapshot：把 GO 的 position/needs/goal 拷进 ECS 镜像组件。必须主线程——要读 GameObject。
- ②③④⑤：NeedsDecay / SpatialGrid / ThreatDetect / Steering 四个 `[BurstCompile]` Job，全部跑 worker 线程，只碰非托管 NativeArray / 纯组件。
- ⑥ Resolve：读 SimVelocity/SimNeeds 回写 GO Transform 和 needs。必须主线程——要写 GameObject。
- 关键设计：**Job 层和 GO 层永不混触**，中间没有任何锁，靠"主线程只在两端碰 GO"保证一致性。

**追问 1：Snapshot 和 Resolve 每帧各 O(N) 遍历，为什么能接受？**

- 因为这是架构决策：GO 是 source of truth，同步成本就是混合架构的显式代价。
- 而且这两个循环很简单（数组顺序遍历、缓存了 `GameObject[]/Entity[]/Authoring[]`，免 GetComponent），不是真正的性能杀手——5000 时它们加起来是主线程主体，但这是"可预期、可分析"的成本，不是隐藏的复杂度。

**追问 2：为什么不把这些拷贝也 Job 化？**

- ECS 侧 job 不能直接读写 `UnityEngine.Object`（Transform 是托管对象），必须主线程。这是引擎约束，不是偷懒。
- 所以真正可优化的方向不是"Job 化拷贝"，而是"减少要回写的数据量"（比如让 ECS 直接驱动渲染，见 Q3 追问）。

**追问 3：SpatialGridSystem 里 `state.Dependency.Complete()` 会不会拖主线程？**

- 会同步等待，但在 500 规模下构建网格极快，代价可接受。
- 代码里留了注释：如果 5000 规模成为瓶颈，可以改成跨系统依赖链（`Schedule` 返回值传给下一个 system）而不 Complete，让 job 流水线化。这是"先用最简单正确实现，测量后再优化"的思路。

**Q5. `IEnableableComponent` 是什么？为什么用 `Threatened` 这个 enableable bit？**

回答要点：
- 普通组件的"有/没有"是 **archetype 结构**决定的。给实体 AddComponent/RemoveComponent 会把实体**迁移到另一个 chunk**（chunk 是相同组件组合的一批实体，内存连续）。
- 如果每帧让几百人"进入威胁区→加威胁组件 / 离开→删组件"，每帧都在搬 chunk 内存，是**碎片化 + 缓存失效**的灾难。
- `IEnableableComponent` 允许**组件结构不变，只翻转 enabled 位**——实体留在原 chunk，只改一个 bit，零迁移。这就是"零 archetype 变更"。
- 我在主线程用 `SetComponentEnabled<Threatened>` 每帧切换，Burst Job 只算"是否在威胁区"这个布尔，bit 翻转很便宜。

**追问 1：enableable 的实体还能被查询到吗？**

- 能。默认 EntityQuery 包含 enableable 组件**无论开关**（除非显式 `WithDisabled`/`WithEnabled`）。这是个容易踩的坑——我代码里专门有注释：检测 job 必须按 `SimPosition + CitizenIndex` 迭代全体，**不能**按 `Threatened` 过滤，否则 disabled 的市民会从查询里消失、永远进不了检测。

**追问 2：为什么不直接给 GO 上一个 `threatened` bool 完事？**

- 那是"结果"。问题是"谁算这个 bool"。如果每帧主线程遍历全部市民算距离，5000 人就白烧主线程时间。
- 正确分工：**Burst Job 并行算**（每个市民查威胁区距离，纯数学，快），**主线程只做 bit 翻转和镜像回写**。ECS 承担计算，GO 只读结果。

**Q6. 空间哈希网格怎么做的？为什么不用 Unity Physics 或 OverlapSphere？**

回答要点：
- 每帧把所有市民按格子坐标 `floor(pos / CellSize)` 放进 `NativeParallelMultiHashMap<int2, int>`（格子 → 市民下标），同时缓存一份 `NativeArray<float3> positions`。
- 避障时只需要查**自己格子 + 周围 8 个格子**（9 邻域），拿到邻居下标后算排斥力，把复杂度从 O(N²) 降到 O(N × 平均邻居数)。
- 不用物理引擎：`Physics.OverlapSphere` 每帧对 5000 人做是 5000 次物理查询，开销大且不必要——这里只需要"谁离我近"，不需要真正的碰撞响应。
- 用 ECS 自己构建还顺便练了 DOTS 数据流（NativeContainer + IJobEntity），比引引擎黑盒更可控。

**追问：网格 CellSize 为什么取 1.0？**

- 大致等于避障半径的 2 倍（`AvoidRadius = 1f`）。这样任意相邻两人的相对位置最多跨 2×2 个格子，查 9 邻域必然覆盖所有可能碰撞的邻居，不会有漏网。这是空间哈希的经典参数选择：**格子尺寸 ≈ 查询半径**。

**追问：`NativeDisableParallelForRestriction` 是干什么的？**

- 每个市民写 `positions[idx.Value]`，但 job 迭代下标和 `CitizenIndex` 不一致，Burst 编译器默认禁止"按非迭代下标写数组"（怕并行冲突）。这个 attribute 是声明"每个 idx 只会被一个线程写，放心"——因为我们保证 `CitizenIndex` 全局唯一，不会有两个市民写同一格。

**Q7. 避障和转向是怎么算的？（Steering）**

回答要点：
- 目标驱动：Wander/Seek 用 `Arrive`（接近目标时线性减速到 0），Flee 用 `Evade`（全速远离威胁中心）。
- 避障：累加 9 邻域内所有邻居的排斥力，方向背离邻居、按 `1/d²` 衰减、超过 `AvoidRadius` 不计。
- 合力 `arrive + rep * strength`，超过 `Speed` 就归一化限速，防止"推挤叠加"产生超速。

**追问：`Evade` 里 `d < 1e-4` 返回 `(speed,0,0)` 是为什么？**

- 当市民正好站在威胁中心，`away` 向量是零向量，`normalize` 会除零。给一个任意方向避免零向量卡死（不然这个人会原地不动）。这是防御性边界处理，也是单测覆盖的数学函数。

---

### 模块 C：行为树

**Q8. 为什么用行为树？不用 FSM / 状态机 / Utility AI？**

- 行为树适合"**条件判断 + 层级分解**"的决策：先判断饿不饿，饿了走觅食分支，不饿再看累不累……天然表达需求优先级。
- FSM 在状态多、转移条件复杂时状态爆炸；Utility AI 适合连续评分，但这里决策是离散目标（去吃饭/回家/去玩/闲逛），BT 更直观。
- 商业项目里 BT 是主流（Unity Behavior、Behavior Designer、Unreal BT 都用），体现工程对接能力。

**追问：决策是"每帧"还是"低频"？**

- 低频。每帧 tick 5000 棵行为树是浪费——需求以秒为单位变化。我用**时间分片**：每帧只 tick `N/30` 个，每人约 0.5 秒决策一次。
- 但威胁必须立刻反应，所以**受威胁者插队**：`BtScheduler` 每帧先扫一遍（用缓存的 `Authoring[]` 读 threatened，不 GetComponent），受威胁且本帧还没 tick 的立即 tick。反应延迟 1~2 帧，肉眼不可见。

**追问：插队会不会导致某个人被跳过？**

- 有 `lastTick[]` 记录每人上次 tick 的 frameCount。插队后立即标记"本帧已 tick"，round-robin 遇到就不重复 tick；`ShouldPreempt` 是纯函数（`threatened && lastTickFrame != currentFrame`），有单测覆盖"本帧已 tick 不重复插队"。

**Q9. 5000 个 agent 各有一棵行为树？内存不会爆炸吗？**

回答要点：
- 不会。**所有 agent 共享同一份 BehaviorGraph 资产，各自只有独立 blackboard**。
- 树结构（selector/composite/action 和它们的连接）是只读共享的，只有每人的需求值、当前目标这类状态在 blackboard 里独立。5000 人共用一份结构，只复制数据不复制树。
- 这是 Unity Behavior 的一个关键使用方式，也回应了"大规模 agent 的 AI 成本"这个经典问题。

**追问：行为树为什么不直接驱动移动？**

- 决策和执行分离。**BT 只产出"当前去哪"（goal）**，写进 `CitizenAuthoring.currentGoalType/currentGoalTarget`，Snapshot 同步进 ECS；**移动由 ECS Steering 每帧算**。
- 好处：BT 低频（省 CPU）、执行高频（平滑移动）；BT 不用知道避障细节，ECS 不用知道决策逻辑。职责清晰。

**Q10. 你提到反射修复 Parent 链，具体是什么坑？**

回答要点（这个坑很有说服力，面试官爱听真实的坑）：
- Unity Behavior 的树节点 `Parent` 字段是 `[SerializeReference]`，正常流程由编辑器管线 `GraphAssetProcessor` 补全。
- 我为了运行时组装/复用图，用 `ScriptableObject.Instantiate` 深拷贝图——**但深拷贝不会还原 Parent 引用**。Parent 为 null 时 selector 完成后无法唤醒 `Start` 节点，导致 `Start.Repeat` 死锁，首个决策之后 Tick 永远空转（表现为：市民只做一次决策就原地愣住）。
- 解决：`SetAgents` 时用反射遍历节点树手动补 Parent（按实际类型写各自类的 `m_Parent` 字段，因为 Node 基类没有该字段）。
- 教训：**第三方插件在"编辑器管线 vs 运行时手动组装"两套路径下行为可能不一致**，这种坑要靠断点和日志定位，也说明单测价值（`BtSchedulerTests` 覆盖了调度逻辑）。

---

### 模块 D：性能与 Profiling（你的差异化优势区）

**Q11. 你的性能数据是怎么测的？**

回答要点：
- 运行时用 `FrameTimingManager` 采 CPU frame / 主线程 / GPU 时间，每种规模稳定后采 3 次取中位数。
- 刻度切换先修改 `CitizenBootstrap.count`，再调用与热键相同的 `Spawn()` 清场重生路径，保证测量的是真实路径。
- 明确区分"威胁 OFF 的日常闭环曲线"和"威胁 ON 的峰值观测"，分开测分开报。

**追问：Editor Play mode 的数值能代表真实设备吗？**

- 不能完全代表，Editor 有额外开销，数值偏悲观。但**曲线形态和瓶颈归属是有代表性的**——我要证明的是"主线程回写是瓶颈"这个结构性结论，不是某个绝对值。真实 player build 数字会更好，我 README 里也诚实标注了。

**Q12. 5000 人 29FPS 的瓶颈到底在哪？怎么证明是主线程而不是 ECS？**

回答要点：
- 证据链：FrameTiming 显示 5000 时主线程 23ms（占 33ms 预算的 69%），而 GPU 才 1.7ms。
- 结构性分析：瓶颈是 `SnapshotSystem` + `ResolveSystem` 两个 O(N) 主线程 GO 回写循环。四个 Burst Job 跑 worker 线程、**不在主线程关键路径上**，5000 时只占帧时间一小部分。
- 架构结论：这证明 ECS 优化层本身扩展性良好，"GO 要不要回写"才是天花板，这是混合架构的显式取舍，不是没优化的隐藏问题。

**追问：那 5000 还能怎么优化？（开放的优化清单）**

由易到难：
1. **减少回写量**：needs 只有变化时才回写 GO（脏标记），而不是每帧全量。
2. **缓存与批处理**：GO 回写循环已用缓存数组免 GetComponent；`ColoringSystem` 用 `MaterialPropertyBlock` 避免实例化材质（每帧 new 一个 MPB 是每帧分配，可复用）。
3. **并行 GO 写**：`transform.SetPositionAndRotation` 比 `transform.position =` 少一次计算；多个 job 在主线程用 `JobHandle.CombineDependencies` 并行……但写 `UnityEngine.Object` 仍受限。
4. **彻底方案**：渲染走 Hybrid Renderer（`EntitiesGraphics`），ECS 直接驱动渲染，GO 层瘦身成"只剩决策的几百个对象"。这会移除主线程回写天花板（这就是纯 ECS 路线）。

**追问：从数据看，100→500 涨 1.5 倍、500→2000 涨 2.2 倍，为什么不是线性？**

- 100→500 主要吃**固定开销**（渲染、系统调度），agent 数占比小，所以 5 倍人数只涨 1.5 倍。
- 500→2000 进入 agent 主导区，Job + GO 循环随 N 线性扩展，所以接近线性。
- 2000→5000 增长放缓（1.75 倍）是因为**已经开始主线程 bound**——主线程回写饱和，再多 agent 主要在 worker 线程排队，帧时间不再线性涨。这条曲线本身就是"瓶颈转移"的证据。

**Q13. Burst 开关为什么没测？关掉 Burst 会怎样？**

- 架构上 Burst 只影响四个 worker job，**不影响主线程 GO 回写**，所以关 Burst 不会移动 5000 天花板；只会在 100~2000（job 时间占比大）有温和回退。
- 实测没做干净：这个 Unity 版本 Editor 里 `Jobs > Use Burst Jobs` 菜单项没注册，运行时改 `BurstCompilerOptions` 又只控 FunctionPointer 不控 IJobEntity，还会弄挂在线 job 管线。最后诚实结论是"干净的对比需要 player build，列为后续"。
- 加分点：**能清晰说清"什么影响、什么不影响"**，比一句"Burst 很牛"强得多。

**追问：`[BurstCompile]` 加在 System 上还是 Job 上？为什么 SteeringSystem 没加？**

- `SteeringJob` 加了 `[BurstCompile]`，`SteeringSystem.OnUpdate` **没加**——因为它要读 `SpatialGridSystem` 的**静态** `Grid/Positions`，Burst 不支持非只读静态字段。
- 这是"该 Burst 的部分 Burst，不该 Burst 的部分不硬上"的取舍，避免为了加 Burst 而破坏结构。

**Q14. spawn 5000 为什么要 1 秒？（一次性成本）**

- 每人生成要 `Instantiate` prefab（GO 完整生命周期：Awake/OnEnable、Transform 挂树）+ `CreateEntity` + 写 8 个组件 + `BehaviorGraphAgent.Init()` 克隆图实例。
- `Instantiate` 是 5000 个托管对象 + 引擎对象的创建，成本天然在。演示时这是可接受的一次性代价（换刻度时的停顿），且 2000 只要 110ms、500 只要 12ms，刻度越低越无感。

---

### 模块 E：数据流与一致性

**Q15. 同一份数据在 GO 和 ECS 两边都有，怎么保证不一致时以谁为准？**

回答要点：
- 明确"权威"划分（这是整个架构的灵魂）：
  - **身份/配置/阈值** → GO 唯一权威，ECS 只读。
  - **needs** → 计算权威在 ECS（Job 衰减），但存储要回写 GO 供 BT/渲染用 → 以 ECS 计算结果为准，Resolve 回写。
  - **goal** → GO（BT 产出）是权威，Snapshot 同步进 ECS。
  - **position** → GO Transform 是权威（渲染/物理要读），ECS 算 velocity，Resolve 把位移 apply 到 Transform。
  - **velocity/邻居/网格/威胁 bit** → ECS 纯中间量，不回写 GO（Threatened 例外，镜像回去供 BT 读）。
- 一句话：**谁产出、谁权威，另一侧只是镜像，且方向明确**。

**追问：一帧内会不会读到"上一帧的值"？**

- 会，但这正是设计。管线顺序固定：`Snapshot(读GO当前值) → Job(算) → Resolve(写回GO)`，一帧内两端看到的是同一帧的数据，下一帧重新快照。这是"一帧延迟"的经典缓存一致性模型，对行为模拟完全可接受（连威胁这种急事都只要 1~2 帧）。

**Q16. 为什么需求衰减和威胁检测要做滞回（hysteresis）？**

- 需求：`hungerThreshold=0.7` 开始觅食，但要降到 `fullThreshold=0` 才算吃饱。如果不滞回，0.7 附近每帧来回跳"饿→不饿"，市民会在 POI 门口反复横跳。
- 威胁：进入用半径 `r`，但已受威胁的要**退出到 `r×1.3`** 才算安全。否则在边界上"Flee/Seek"每帧翻转（前面 commit 里专门修过这个 edge thrash）。
- 通用原理：**判断阈值 ≠ 退出阈值，中间留滞回带**，避免边界抖动。这是 AI/控制系统的通用技巧，很能体现工程思维。

---

### 模块 F：工程实践

**Q17. 你的测试策略？为什么只测纯函数？**

- EditMode 单测覆盖**纯数学函数**（SteeringMath、ThreatMath、PoiMath）+ **BT 调度纯逻辑**（ComputePerFrame、ShouldPreempt、RoundRobin 覆盖性）+ 系统往返（SyncLoopTests）。
- 为什么选纯函数：纯函数无副作用、脱离 ECS 可单测、跑得快、断言明确——这是 DOTS 时代推荐的可测试形态。
- ECS 集成层（系统调度、chunk 布局）靠 PlayMode 手测 + SyncLoopTests 的集成测试兜底，而不是盲目给所有系统写单测。

**追问：`BtSchedulerTests` 里 RoundRobin 测试怎么验证的？**

- 不用真实 agent，纯复刻 cursor 推进逻辑：500 agent、每帧 16 个、跑 40 帧（640 > 500），断言每个 agent 都被 tick 过。验证分片在足够帧数内**覆盖全部**，不遗漏。

**Q18. 单人项目，怎么做过程管理？**

- 里程碑切 4 个可交付的垂直切片：M1 同步脊柱（GO↔ECS 镜像 + Burst 转向）→ M2 行为树 → M3 完整日常闭环（三需求 + 空间网格避障）→ M4 威胁 + 刻度 + 性能交付。每阶段可运行可演示。
- 设计与实现分离：先写 spec（架构原则、同步边界、风险清单 §9），再按里程碑写计划，最后按 TDD 实施。
- 文档即交付物：设计规格、性能报告、行为树调试日志、README，全部入库——这正是工程习惯的证据。

---

### 模块 G：开放延伸题（Gameplay 方向考察）

**Q19. 如果让你加一个"丧尸咬人传播"或"死亡/重生"系统，怎么扩展？**

（开放性，考察架构可扩展性。参考答法：）
- 死亡 = 需求（比如 hunger 到 1.0）+ 生命周期：在 ECS 加 `Lifecycle` 组件，job 检测死亡条件，用 **ECB（EntityCommandBuffer）** 在主线程销毁实体 + 销毁 GO（因为要动 registry 数组，必须主线程安全）。
- 传播 = 把威胁区从"静态区域"推广成"动态感染源"：感染者每帧在 ECS 里写"自身位置"到感染源列表（类似 ThreatZoneRegistry 的动态版），NeedsDecay/Threat 的检测逻辑复用。
- 这类问题考察的是**你是否能顺着现有架构把新系统插进去**，答案是"复用空间查询 + 复用 enableable 标记 + ECB 安全销毁"。

**Q20. 纯 ECS 重写这个项目会怎样？**

- 优势：移除 GO 回写天花板，能到 20000+；渲染走 Hybrid Renderer。
- 代价：行为树/配置/调试全要 ECS 化，开发成本显著上升；Unity Behavior 这类组件没法直接用。
- 结论：**在"要交付的中型项目"里，GO+ECS 混合是更务实的工程选择**——这也是我设计的初衷，演示的就是这种真实取舍。

**Q21. 你从这个项目里学到最重要的东西是什么？**

（真诚比技巧重要。参考：）
- "性能瓶颈要靠测量而不是猜。" 我原以为 Burst Job 是瓶颈，实测发现主线程 GO 回写才是，结论完全反过来了。
- "架构决策要付代价并诚实标注。" 5000 的天花板不是我避而不谈的缺陷，而是我主动设计成 demo 的 talking point。
- "分层解耦让扩展变容易。" 威胁功能（M4）几乎没动 M1~M3 的日常闭环代码，只是加了检测系统 + BT 分支，因为决策/执行/同步早就分开。

---

## 第三部分：被追问到底时，必须真正吃透的底层知识

面试官大概率往这几个方向深挖，答不上会穿帮。每个都值得你在面试前补一补：

| 知识点 | 要懂到什么程度 |
|---|---|
| ECS 三要素 | Entity / Component / System 是什么，chunk 内存布局，为什么缓存友好 |
| Archetype | 组件组合决定 archetype，Add/Remove 组件 = 搬 chunk；`IEnableableComponent` 不搬 |
| Job System | IJob / IJobParallelFor / IJobEntity 区别；依赖链 `state.Dependency`；`ScheduleParallel` |
| Burst | 编译原理一句话（把 C# 编译成高性能 native 码）；什么能 Burst、什么不能（托管、静态字段、UnityEngine.Object） |
| NativeContainer | NativeArray / NativeParallelMultiHashMap / Allocator 生命周期；`[ReadOnly]`/`[WriteOnly]`/`NativeDisableParallelForRestriction`；泄漏与安全系统 |
| SystemBase vs ISystem | 托管 vs 结构系统；SystemBase 能访问托管（Registry），ISystem 更 Burst 友好 |
| 空间哈希 | 原理、格子尺寸选择、9 邻域查询、`NativeParallelMultiHashMap` |
| Steering | Seek / Arrive / Evade / 排斥力 / 限速，数学推导能讲 |
| 行为树 | Selector / Sequence / Modifier / Action / blackboard / Repeat / 分片 tick |
| 滞回（hysteresis） | 阈值滞回与空间滞回，为什么防抖动 |
| 渲染管线 | MaterialPropertyBlock 与 GPU instancing 的区别（避坑：MPB 会破坏自动合批，但能保颜色独立） |
| FrameTimingManager | CPU/GPU/mainThread 帧时间怎么取，UWA/Profiler 对比 |

---

## 第四部分：你的三个"必答漂亮"开场句

准备这几句话，面试任何问题都能自然地抛回你的优势：

1. **"我这个项目的核心不是画面，而是规模与性能，以及一个清晰的架构边界。"**（定位）
2. **"5000 人的瓶颈在主线程 GO 回写，不在 ECS——我用 FrameTiming 数据证明的。"**（测量驱动）
3. **"我做了明确的架构取舍：GO 当脊柱、ECS 当优化层，并诚实标注了它的天花板。"**（工程判断力）

---

## 附：面试现场演示要点（如果让当场跑 demo）

1. **先讲再跑**：先讲架构图和数据流，再进 Play——面试官看画面是看不懂架构的，先建立心智模型。
2. **演示脚本固定**：`2`（500人）起步 → 看日常色（红/蓝/黄/绿流动）→ `T` + `WASD` 移威胁区入群（变白四散，观察反应快）→ `4` 拉 5000 → 指 HUD FPS 曲线变红 + 受威胁计数。
3. **遇到卡顿别慌**：5000 换刻度有 ~1s 停顿（一次性 spawn 成本），先说"这里是一性次生成，正常"，再切回 2000 展示流畅区。
4. **把 HUD 当证据讲**：左上角 FPS/市民数/受威胁数/BT ticks 每帧数，是对着面试官讲性能数据的现场依据。
