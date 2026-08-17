using Unity.Entities;
using UnityEngine;

namespace CitizenSim
{
    // 按 goal.Type 着色:SeekFood=红, SeekHome=蓝, SeekFun=黄, Wander=绿, Flee=白。
    // 主线程 MaterialPropertyBlock,用 Registry 缓存的 Renderers[](免 per-frame GetComponent)。
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(ResolveSystem))]
    public partial class ColoringSystem : SystemBase
    {
        static readonly Color Hungry = new Color(0.9f, 0.2f, 0.2f);
        static readonly Color Home = new Color(0.2f, 0.4f, 0.9f);
        static readonly Color Fun = new Color(0.9f, 0.8f, 0.2f);
        static readonly Color WanderColor = new Color(0.2f, 0.8f, 0.2f);
        static readonly Color FleeColor = new Color(0.95f, 0.95f, 0.95f);

        protected override void OnUpdate()
        {
            var reg = CitizenRegistry.Instance;
            if (reg == null) return;
            var em = EntityManager;
            var ents = reg.Entities;
            var renderers = reg.Renderers;
            if (renderers == null) return;
            var mpb = new MaterialPropertyBlock();
            for (int i = 0; i < renderers.Length; i++)
            {
                var r = renderers[i];
                if (r == null) continue;
                var goal = em.GetComponentData<SimGoal>(ents[i]);
                mpb.SetColor("_BaseColor", ColorFor(goal.Type));
                r.SetPropertyBlock(mpb);
            }
        }

        public static Color ColorFor(GoalType type)
        {
            switch (type)
            {
                case GoalType.SeekFood: return Hungry;
                case GoalType.SeekHome: return Home;
                case GoalType.SeekFun: return Fun;
                case GoalType.Flee: return FleeColor;
                default: return WanderColor; // Wander
            }
        }
    }
}
