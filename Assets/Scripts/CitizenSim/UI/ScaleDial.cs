using UnityEngine;
using UnityEngine.InputSystem;

namespace CitizenSim
{
    // 刻度盘 + 威胁热键(规格§7 刻度盘)。热键 1/2/3/4 切 100/500/2000/5000,T 开关威胁,
    // WASD 移动威胁区(M4 单区域)。E 在鼠标处生成 15m 临时恐惧区(8s,受 T 开关统一控制)。
    // Esc 退出 Player；Editor 中停止 Play Mode，便于以同一输入验证。
    // 刻度切换调 Bootstrap.Clear()+Spawn() 重生。
    // 用新 Input System(项目 Player Settings 已切 Input System package,旧 UnityEngine.Input 抛异常)。
    public class ScaleDial : MonoBehaviour
    {
        public CitizenBootstrap bootstrap;
        public ThreatZoneRegistry threatZone;
        public float threatMoveSpeed = 20f;
        [Tooltip("E 键射线投射用的相机,未配则用 Camera.main")]
        [SerializeField] Camera raycastCamera;
        const float TempZoneRadius = 15f;
        const float TempZoneDuration = 3f;
        static readonly int[] Scales = { 100, 500, 2000, 5000 };

        void Update()
        {
            var kb = Keyboard.current;
            if (kb == null) return;

            if (kb.escapeKey.wasPressedThisFrame)
            {
#if UNITY_EDITOR
                UnityEditor.EditorApplication.isPlaying = false;
#else
                Application.Quit();
#endif
                return;
            }

            if (bootstrap == null) return;

            if (kb.digit1Key.wasPressedThisFrame) { bootstrap.count = Scales[0]; bootstrap.Spawn(); }
            else if (kb.digit2Key.wasPressedThisFrame) { bootstrap.count = Scales[1]; bootstrap.Spawn(); }
            else if (kb.digit3Key.wasPressedThisFrame) { bootstrap.count = Scales[2]; bootstrap.Spawn(); }
            else if (kb.digit4Key.wasPressedThisFrame) { bootstrap.count = Scales[3]; bootstrap.Spawn(); }

            if (threatZone != null)
            {
                if (kb.tKey.wasPressedThisFrame)
                    threatZone.active = !threatZone.active;

                float mx = (kb.dKey.isPressed ? 1f : 0f) - (kb.aKey.isPressed ? 1f : 0f);
                float mz = (kb.wKey.isPressed ? 1f : 0f) - (kb.sKey.isPressed ? 1f : 0f);
                if (mx != 0f || mz != 0f)
                {
                    Vector3 m = new Vector3(mx, 0f, mz) * (threatMoveSpeed * Time.deltaTime);
                    if (threatZone.zones != null && threatZone.zones.Length > 0 && threatZone.zones[0] != null)
                        threatZone.zones[0].position += m;
                }

                if (kb.eKey.wasPressedThisFrame && threatZone.active)
                    SpawnTempZoneAtMouse();
            }
        }

        void SpawnTempZoneAtMouse()
        {
            var mouse = Mouse.current;
            if (mouse == null) return;
            Vector2 sp = mouse.position.ReadValue();
            // 鼠标在 game 窗口内才生效(屏幕坐标范围近似)。
            if (sp.x < 0f || sp.x > Screen.width || sp.y < 0f || sp.y > Screen.height) return;

            Camera c = raycastCamera != null ? raycastCamera : Camera.main;
            if (c == null) return;
            Ray ray = c.ScreenPointToRay(new Vector3(sp.x, sp.y, 0f));
            var plane = new Plane(Vector3.up, Vector3.zero); // y=0 地面
            if (plane.Raycast(ray, out float dist))
            {
                Vector3 worldPos = ray.GetPoint(dist);
                threatZone.SpawnTempZone(worldPos, TempZoneRadius, TempZoneDuration);
            }
        }
    }
}
