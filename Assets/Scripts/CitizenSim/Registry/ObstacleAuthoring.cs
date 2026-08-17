using UnityEngine;

namespace CitizenSim
{
    // 障碍物 GO 配置。size(x,z 米)驱动 transform.localScale,视觉 + BoxCollider + 流场 blocked 三者一致。
    // isMoving/lastPosition/stationaryTime 由 ObstacleRegistry 每帧检测(M5 Task 5 动态障碍物)。
    public class ObstacleAuthoring : MonoBehaviour
    {
        [Tooltip("占地尺寸(x,z 米)。驱动 transform.localScale 的 x/z,视觉+碰撞+流场一致。")]
        public Vector2 size = new Vector2(2f, 2f);

        [Tooltip("高度(y 米)。只读参考——实际由 Transform scale.Y 控制,拖动 scale.Y 不会被重置。")]
        public float height = 2f;

        [HideInInspector] public bool isMoving;
        [HideInInspector] public Vector3 lastPosition;
        [HideInInspector] public float stationaryTime;

#if UNITY_EDITOR
        // 只在 size(占地 x/z)变化时同步 scale 的 x/z。scale.Y 由用户直接控制,不被 height 覆盖
        // (否则进 Play 时 OnValidate 会把用户手动拖高的障碍物压回 height 旧值)。
        Vector2 _lastSize;
        void OnValidate()
        {
            if (_lastSize == Vector2.zero) _lastSize = size;  // 首次不触发,避免覆盖初始 scale
            if (_lastSize != size)
            {
                var s = transform.localScale;
                transform.localScale = new Vector3(size.x, s.y, size.y);
                _lastSize = size;
            }
            height = transform.localScale.y;  // 反推显示,只读
        }
#endif

        // 运行时/初始化用:强制同步 x/z(若代码改了 size)。
        public void SyncSize()
        {
            var s = transform.localScale;
            transform.localScale = new Vector3(size.x, s.y, size.y);
        }
    }
}
