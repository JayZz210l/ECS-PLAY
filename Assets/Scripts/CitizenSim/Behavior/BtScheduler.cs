using System.Collections;
using System.Reflection;
using Unity.Behavior;
using UnityEngine;

namespace CitizenSim
{
    // 时间分片调度器:每帧只 tick 一批 agent,使每市民约每 0.5s 决策一次。
    // Design A:agent.enabled=false 阻止其 Update() 自动 tick,由本调度器 round-robin 接管。
    //
    // 关键修复:手建图的 Node.Parent 字段虽是 [SerializeReference],但在我们用反射直接
    // 组装节点树后,ScriptableObject.Instantiate 的深拷贝没有还原 Parent 引用(Unity Behavior
    // 的编辑器管线 GraphAssetProcessor 才会补这条链)。Parent 为 null 时 AwakeParents() 是空操作,
    // selector 完成后无法唤醒 Start,Start.Repeat 死锁,首个决策之后 Tick() 永远空转。
    // 所以 SetAgents 时遍历节点树手动补全 Parent,之后 Tick()+Repeat 才能持续重新决策。
    public class BtScheduler : MonoBehaviour
    {
        public static BtScheduler Instance { get; private set; }

        BehaviorGraphAgent[] agents;
        int cursor;
        int[] lastTick;          // 每 agent 上次 tick 的 frameCount,防 double-tick + 插队去重
        // 本帧 tick 总数(插队 + round-robin),供 HUD v2 显示 BT ticks/帧。
        public int LastTickCount { get; private set; }

        // Parent 字段在每个叶子类上各自声明(Modifier/Composite/Action),Node 基类没有。
        static readonly FieldInfo s_ModifierParent;
        static readonly FieldInfo s_CompositeParent;
        static readonly FieldInfo s_ActionParent;
        static readonly PropertyInfo s_ModifierChild;
        static readonly PropertyInfo s_CompositeChildren;
        static readonly FieldInfo s_Graphs;

        static BtScheduler()
        {
            var bf = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            s_ModifierParent  = typeof(Modifier).GetField("m_Parent", bf);
            s_CompositeParent = typeof(Composite).GetField("m_Parent", bf);
            s_ActionParent    = typeof(Action).GetField("m_Parent", bf);
            s_ModifierChild   = typeof(Modifier).GetProperty("Child", bf);
            s_CompositeChildren = typeof(Composite).GetProperty("Children", bf);
            s_Graphs = typeof(BehaviorGraph).GetField("Graphs", bf);
        }

        void OnEnable() => Instance = this;
        void OnDisable() { if (Instance == this) Instance = null; }

        public int AgentCount => agents?.Length ?? 0;

        public void SetAgents(BehaviorGraphAgent[] a)
        {
            agents = a;
            cursor = 0;
            lastTick = a != null ? new int[a.Length] : null;
            LastTickCount = 0;
            if (a == null) return;
            for (int i = 0; i < a.Length; i++)
            {
                var ag = a[i];
                if (ag == null || ag.Graph == null) continue;
                FixupParentChain(ag.Graph);
                if (!ag.Graph.IsRunning) ag.Graph.Start();
                ag.enabled = false;
            }
        }

        static void FixupParentChain(BehaviorGraph graph)
        {
            if (s_Graphs == null) return;
            var modules = (IList)s_Graphs.GetValue(graph);
            if (modules == null || modules.Count == 0) return;
            var module = modules[0];
            var fiRoot = module.GetType().GetField("Root",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (fiRoot == null) return;
            FixupNode(fiRoot.GetValue(module), null);
        }

        static void FixupNode(object node, object parent)
        {
            if (node == null) return;

            // 补 Parent:按实际类型选对应叶子类的 m_Parent 字段(用 is 守卫避免 GetValue 类型异常)。
            if (parent != null)
            {
                switch (node)
                {
                    case Modifier m:  s_ModifierParent?.SetValue(m, parent); break;
                    case Composite c: s_CompositeParent?.SetValue(c, parent); break;
                    case Action a:    s_ActionParent?.SetValue(a, parent); break;
                }
            }

            // 递归子节点。同样用 is 守卫,只在类型匹配时取 Child/Children。
            if (node is Composite comp && s_CompositeChildren != null)
            {
                var children = (IList)s_CompositeChildren.GetValue(comp);
                if (children != null)
                    foreach (var child in children)
                        FixupNode(child, node);
            }
            else if (node is Modifier mod && s_ModifierChild != null)
            {
                var child = s_ModifierChild.GetValue(mod);
                if (child != null) FixupNode(child, node);
            }
        }

        // 500 agent @60fps -> ~16/帧,每 agent ~0.5s 决策一次。抽成纯函数便于单测。
        public static int ComputePerFrame(int agentCount)
            => agentCount > 0 ? Mathf.Max(1, agentCount / 30) : 0;

        // 插队判定(纯函数,可单测):受威胁且本帧未 tick -> 应插队。
        public static bool ShouldPreempt(bool threatened, int lastTickFrame, int currentFrame)
            => threatened && lastTickFrame != currentFrame;

        void Update()
        {
            if (agents == null || agents.Length == 0)
            {
                LastTickCount = 0;
                return;
            }
            int frame = Time.frameCount;
            int perFrame = ComputePerFrame(agents.Length);
            int ticked = 0;

            // 插队:受威胁 agent 立即 tick(不受分片节流,规格§5 威胁反应性)。
            // 用 CitizenRegistry 缓存的 Authoring[] 免 per-frame GetComponent。
            var authoring = CitizenRegistry.Instance != null ? CitizenRegistry.Instance.Authoring : null;
            if (authoring != null && lastTick != null)
            {
                int n = Mathf.Min(agents.Length, authoring.Length);
                for (int i = 0; i < n; i++)
                {
                    bool threatened = authoring[i] != null && authoring[i].threatened;
                    if (ShouldPreempt(threatened, lastTick[i], frame))
                    {
                        TickAgent(i);
                        lastTick[i] = frame;
                        ticked++;
                    }
                }
            }

            // round-robin 批次(跳过本帧已插队 tick 的,保 cursor 节奏)。
            for (int k = 0; k < perFrame; k++)
            {
                if (cursor >= agents.Length) cursor = 0;
                if (lastTick == null || lastTick[cursor] != frame)
                {
                    TickAgent(cursor);
                    if (lastTick != null) lastTick[cursor] = frame;
                    ticked++;
                }
                cursor++;
            }
            LastTickCount = ticked;
        }

        void TickAgent(int i)
        {
            var ag = agents[i];
            if (ag != null && ag.Graph != null) ag.Graph.Tick();
        }
    }
}
