using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace CitizenSim
{
    // 同步脊柱出口：读取 ECS 计算结果，移动 GO，并把 BT/展示层需要的状态镜像回 Authoring。
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(SteeringSystem))]
    public partial class ResolveSystem : SystemBase
    {
        const float k_TurnSmoothTime = 0.2f;
        const float k_MinTurnSpeed = 0.1f;
        const float k_MaxAnimatorSpeed = 0.5f;
        static readonly int k_AnimatorSpeed = Animator.StringToHash("Speed");

        protected override void OnUpdate()
        {
            var registry = CitizenRegistry.Instance;
            if (registry == null) return;

            float deltaTime = SystemAPI.Time.DeltaTime;
            var gameObjects = registry.GameObjects;
            var entities = registry.Entities;
            var authoring = registry.Authoring;

            // 三张流场共享 blocked 数据；选 FoodField 作为移动硬约束即可。
            var movementField = FlowFieldBuildSystem.FoodField;
            bool hasMovementField = movementField.blocked.IsCreated;

            for (int i = 0; i < gameObjects.Length; i++)
            {
                GameObject citizen = gameObjects[i];
                if (citizen == null) continue;

                Entity entity = entities[i];
                float3 velocity = EntityManager.GetComponentData<SimVelocity>(entity).Value;

                ApplyMovement(citizen.transform, velocity, deltaTime, movementField, hasMovementField);

                CitizenAuthoring citizenAuthoring = authoring != null ? authoring[i] : null;
                if (citizenAuthoring != null)
                    WriteBackToAuthoring(citizenAuthoring, citizen.transform, entity, velocity, deltaTime);
            }
        }

        static void ApplyMovement(
            Transform citizenTransform,
            float3 velocity,
            float deltaTime,
            in FlowField field,
            bool hasField)
        {
            Vector3 currentPosition = citizenTransform.position;
            Vector3 movement = new Vector3(velocity.x, velocity.y, velocity.z) * deltaTime;

            if (!hasField)
            {
                citizenTransform.position = currentPosition + movement;
                return;
            }

            if (IsCellBlocked(field, currentPosition))
            {
                // 已在 blocked 格内时必须允许 escape 速度把市民推出去。
                // 若继续检查新位置，小步移动会一直被取消，市民将永久卡住。
                citizenTransform.position = currentPosition + movement;
                return;
            }

            citizenTransform.position = SlideAlongBlockedCells(currentPosition, movement, field);
        }

        static Vector3 SlideAlongBlockedCells(Vector3 currentPosition, Vector3 movement, in FlowField field)
        {
            // 分别检查 X/Z，使被阻挡的轴停止、另一轴继续，形成沿障碍边缘滑动。
            Vector3 xCandidate = new Vector3(
                currentPosition.x + movement.x,
                currentPosition.y,
                currentPosition.z);
            Vector3 zCandidate = new Vector3(
                currentPosition.x,
                currentPosition.y,
                currentPosition.z + movement.z);

            Vector3 resolvedPosition = currentPosition;
            if (!IsCellBlocked(field, xCandidate)) resolvedPosition.x += movement.x;
            if (!IsCellBlocked(field, zCandidate)) resolvedPosition.z += movement.z;
            return resolvedPosition;
        }

        void WriteBackToAuthoring(
            CitizenAuthoring authoring,
            Transform citizenTransform,
            Entity entity,
            float3 velocity,
            float deltaTime)
        {
            // ECS -> GO 镜像：行为树读取 needs/threatened，展示层读取 moveSpeed。
            authoring.needs = EntityManager.GetComponentData<SimNeeds>(entity).Value;
            authoring.threatened = EntityManager.IsComponentEnabled<Threatened>(entity);

            float horizontalSpeed = math.length(new float2(velocity.x, velocity.z));
            authoring.moveSpeed = horizontalSpeed;

            UpdateAnimation(authoring.animator, horizontalSpeed);
            UpdateFacing(citizenTransform, velocity, horizontalSpeed, deltaTime);
            TickExitTimer(authoring, deltaTime);
        }

        static void UpdateAnimation(Animator animator, float horizontalSpeed)
        {
            if (animator == null) return;
            animator.SetFloat(k_AnimatorSpeed, math.min(horizontalSpeed, k_MaxAnimatorSpeed));
        }

        static void UpdateFacing(
            Transform citizenTransform,
            float3 velocity,
            float horizontalSpeed,
            float deltaTime)
        {
            if (horizontalSpeed <= k_MinTurnSpeed) return;

            Vector3 moveDirection = new Vector3(velocity.x, 0f, velocity.z).normalized;
            Quaternion targetRotation = Quaternion.LookRotation(moveDirection, Vector3.up);
            float interpolation = 1f - Mathf.Exp(-deltaTime / k_TurnSmoothTime);
            citizenTransform.rotation = Quaternion.Slerp(
                citizenTransform.rotation,
                targetRotation,
                interpolation);
        }

        static void TickExitTimer(CitizenAuthoring authoring, float deltaTime)
        {
            if (authoring.exitTimer <= 0f) return;
            authoring.exitTimer = Mathf.Max(0f, authoring.exitTimer - deltaTime);
        }

        // 网格外也视为 blocked，避免 GO 写回越过流场边界。
        static bool IsCellBlocked(in FlowField field, Vector3 position)
        {
            int2 cell = field.WorldToCell(position);
            if (!field.InBounds(cell)) return true;
            return field.blocked[field.CellIndex(cell)] == 1;
        }
    }
}
