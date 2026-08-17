using Unity.Behavior;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace CitizenSim
{
    // 场景入口：让 Citizen GameObject 与 ECS 镜像实体一对一同生共死。
    // 这里只负责生命周期和装配；每帧模拟仍从 SnapshotSystem 进入、从 ResolveSystem 返回。
    [RequireComponent(typeof(CitizenRegistry))]
    public class CitizenBootstrap : MonoBehaviour
    {
        static readonly Vector3 k_DefaultNeeds = new Vector3(0f, 0f, 0.5f);

        [Header("Spawn")]
        public GameObject citizenPrefab;
        public int count = 500;
        public float spawnRadius = 40f;

        [Tooltip("出生范围跟随地面中心(网格中心)。未勾选用本物体位置。")]
        public bool spawnFollowsGround = true;

        void Start() => Spawn();

        // 清场：销毁全体市民 Entity + GO，并断开 Registry 与 BT Scheduler。
        // ScaleDial 热切规模时，Spawn() 会先走这里。
        public void Clear()
        {
            DestroyMirrorEntities();
            ClearCitizenGameObjects(GetComponent<CitizenRegistry>());
            BtScheduler.Instance?.SetAgents(null);
        }

        public void Spawn()
        {
            Clear();

            var world = World.DefaultGameObjectInjectionWorld;
            if (world == null)
            {
                Debug.LogError("CitizenBootstrap: 默认 ECS World 不存在，请确认 Entities 初始化开启。");
                return;
            }
            if (citizenPrefab == null)
            {
                Debug.LogError("CitizenBootstrap: 未配置 citizenPrefab。", this);
                return;
            }

            var prefabAgent = citizenPrefab.GetComponent<BehaviorGraphAgent>();
            if (prefabAgent == null || prefabAgent.Graph == null)
            {
                Debug.LogError(
                    "CitizenBootstrap: citizenPrefab 必须自带 BehaviorGraphAgent 并配置 BehaviorGraph。",
                    citizenPrefab);
                return;
            }

            var registry = GetComponent<CitizenRegistry>();
            var entityManager = world.EntityManager;
            var archetype = CreateCitizenArchetype(entityManager);
            var spawnCenter = GetSpawnCenter();
            var obstacles = ObstacleRegistry.Instance;

            var gameObjects = new GameObject[count];
            var entities = new Entity[count];
            var agents = new BehaviorGraphAgent[count];

            for (int i = 0; i < count; i++)
            {
                Vector3 spawnPosition = FindSpawnPosition(spawnCenter, obstacles);
                GameObject citizen = CreateCitizenGameObject(i, spawnPosition, out CitizenAuthoring authoring);
                Entity mirror = CreateMirrorEntity(entityManager, archetype, i, spawnPosition, authoring);

                gameObjects[i] = citizen;
                entities[i] = mirror;
                agents[i] = GetBehaviorAgent(citizen);
            }

            registry.Register(gameObjects, entities);
            ConnectBehaviorAgents(agents);
        }

        static EntityArchetype CreateCitizenArchetype(EntityManager entityManager)
        {
            return entityManager.CreateArchetype(
                typeof(SimPosition),
                typeof(SimVelocity),
                typeof(SimGoal),
                typeof(SimRadius),
                typeof(CitizenIndex),
                typeof(SimNeeds),
                typeof(GridCell),
                typeof(Threatened),
                typeof(SimExit));
        }

        Vector3 GetSpawnCenter()
        {
            var flowConfig = FlowFieldConfig.Instance;
            if (spawnFollowsGround && flowConfig != null && flowConfig.ground != null)
                return flowConfig.ground.position;
            return transform.position;
        }

        Vector3 FindSpawnPosition(Vector3 center, ObstacleRegistry obstacles)
        {
            // 最多重试 50 次，避免出生在障碍物内或流场网格外。
            Vector3 position;
            int tries = 0;
            do
            {
                Vector2 random = UnityEngine.Random.insideUnitCircle * spawnRadius;
                position = center + new Vector3(random.x, 0f, random.y);
                tries++;
            }
            while (tries < 50 && !IsValidSpawnPosition(position, obstacles));

            return position;
        }

        static bool IsValidSpawnPosition(Vector3 position, ObstacleRegistry obstacles)
        {
            bool insideObstacle = obstacles != null && obstacles.IsInObstacle(position);
            return !insideObstacle && FlowFieldBuildSystem.IsWorldInBounds(position);
        }

        GameObject CreateCitizenGameObject(
            int index,
            Vector3 spawnPosition,
            out CitizenAuthoring authoring)
        {
            var citizen = Instantiate(citizenPrefab, transform);
            citizen.transform.position = spawnPosition;
            authoring = citizen.GetComponent<CitizenAuthoring>();
            if (authoring != null) InitializeAuthoring(authoring, citizen, index, spawnPosition);
            return citizen;
        }

        static void InitializeAuthoring(
            CitizenAuthoring authoring,
            GameObject citizen,
            int index,
            Vector3 spawnPosition)
        {
            authoring.Index = index;

            // 展示层引用只查一次，之后由 CitizenRegistry 缓存。
            var skinnedRenderer = citizen.GetComponentInChildren<SkinnedMeshRenderer>();
            authoring.capsuleRenderer = skinnedRenderer != null
                ? skinnedRenderer
                : citizen.transform.Find("Mesh")?.GetComponent<Renderer>();
            authoring.animator = citizen.GetComponentInChildren<Animator>();
            if (authoring.animator != null) authoring.animator.applyRootMotion = false;

            // 打散初始状态，避免全部市民同时进入同一种需求。
            authoring.needs = new Vector3(
                UnityEngine.Random.Range(0f, 0.7f),
                UnityEngine.Random.Range(0f, 0.7f),
                UnityEngine.Random.Range(0.3f, 0.9f));

            // BT 首次 tick 会接管目标；出生点作为初始目标可避免集体冲向原点。
            authoring.currentGoalType = GoalType.Wander;
            authoring.currentGoalTarget = spawnPosition;
            authoring.lastGoalType = GoalType.Wander;
            authoring.lastGoalTarget = spawnPosition;
        }

        static Entity CreateMirrorEntity(
            EntityManager entityManager,
            EntityArchetype archetype,
            int index,
            Vector3 spawnPosition,
            CitizenAuthoring authoring)
        {
            Entity entity = entityManager.CreateEntity(archetype);
            entityManager.SetComponentData(entity, new SimPosition { Value = spawnPosition });
            entityManager.SetComponentData(entity, new SimVelocity { Value = float3.zero });
            entityManager.SetComponentData(entity, new SimGoal
            {
                Type = GoalType.Wander,
                Target = spawnPosition,
            });
            entityManager.SetComponentData(entity, new SimRadius { Value = 0.5f });
            entityManager.SetComponentData(entity, new CitizenIndex { Value = index });
            entityManager.SetComponentData(entity, new SimNeeds
            {
                Value = authoring != null ? authoring.needs : k_DefaultNeeds,
            });

            // Enableable 组件创建时默认 enabled；市民出生时明确设为未受威胁。
            entityManager.SetComponentEnabled<Threatened>(entity, false);
            return entity;
        }

        static BehaviorGraphAgent GetBehaviorAgent(GameObject citizen)
        {
            var agent = citizen.GetComponent<BehaviorGraphAgent>();
            if (agent == null || agent.Graph == null)
            {
                Debug.LogError(
                    "CitizenBootstrap: 生成的 Citizen 缺少已配置的 BehaviorGraphAgent。",
                    citizen);
                return null;
            }

            // Agent 来自 prefab；Awake 已为该市民克隆独立图实例。
            // 此处只收集引用，后续由 BtScheduler 禁用自动 Update 并分片 Tick。
            return agent;
        }

        void ConnectBehaviorAgents(BehaviorGraphAgent[] agents)
        {
            if (BtScheduler.Instance != null)
            {
                BtScheduler.Instance.SetAgents(agents);
                return;
            }

            Debug.LogWarning("CitizenBootstrap: 场景中无 BtScheduler，BT 未接入，市民将仅 Wander。", this);
        }

        static void DestroyMirrorEntities()
        {
            var world = World.DefaultGameObjectInjectionWorld;
            if (world == null) return;

            var entityManager = world.EntityManager;
            var query = entityManager.CreateEntityQuery(ComponentType.ReadOnly<CitizenIndex>());
            entityManager.DestroyEntity(query);
            query.Dispose();
        }

        static void ClearCitizenGameObjects(CitizenRegistry registry)
        {
            if (registry == null) return;

            if (registry.GameObjects != null)
            {
                foreach (var citizen in registry.GameObjects)
                    if (citizen != null) Destroy(citizen);
            }

            registry.GameObjects = null;
            registry.Entities = null;
            registry.Authoring = null;
            registry.Renderers = null;
        }
    }
}
