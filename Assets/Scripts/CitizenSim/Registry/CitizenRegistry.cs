using System;
using Unity.Entities;
using UnityEngine;

namespace CitizenSim
{
    public class CitizenRegistry : MonoBehaviour
    {
        public static CitizenRegistry Instance { get; private set; }

        // 运行时由 Register() 填充，不参与序列化。
        [NonSerialized] public GameObject[] GameObjects;
        [NonSerialized] public Entity[] Entities;
        // 缓存:消掉 Snapshot/Resolve/Coloring 每帧 GetComponent 开销(M3 优化)。
        [NonSerialized] public CitizenAuthoring[] Authoring;
        [NonSerialized] public Renderer[] Renderers;

        public int Count => GameObjects != null ? GameObjects.Length : 0;

        void OnEnable() => Instance = this;
        void OnDisable() { if (Instance == this) Instance = null; }

        public void Register(GameObject[] gos, Entity[] ents)
        {
            // OnEnable 在 EditMode 不触发；Register 时兜底设置单例，保证测试可用。
            if (Instance == null) Instance = this;
            GameObjects = gos;
            Entities = ents;
            int n = gos != null ? gos.Length : 0;
            Authoring = new CitizenAuthoring[n];
            Renderers = new Renderer[n];
            for (int i = 0; i < n; i++)
            {
                Authoring[i] = gos[i] != null ? gos[i].GetComponent<CitizenAuthoring>() : null;
                Renderers[i] = Authoring[i] != null ? Authoring[i].capsuleRenderer : null;
            }
        }
    }
}
