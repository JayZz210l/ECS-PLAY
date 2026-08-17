using UnityEngine;

namespace CitizenSim
{
    // 市民状态恢复完成时的 Q 弹动效:检测 currentGoalType 从 Seek*(恢复中)翻转为
    // 非 Seek(吃饱/休息够/玩够离开 POI),视觉 Mesh 沿 y 压扁再弹性回弹。
    // 只对刚恢复的少数市民改 scale,5000 规模开销可忽略。
    public class CitizenBounce : MonoBehaviour
    {
        [Tooltip("压扁幅度:y 缩到 1-该值(0.45 -> 缩到 0.55)")]
        public float squashAmount = 0.45f;
        [Tooltip("回弹总时长(秒)")]
        public float duration = 0.4f;
        [Tooltip("弹性过冲:回弹超过 1 的比例(0.15 -> 冲到 1.15)")]
        public float springOvershoot = 0.15f;

        Transform _mesh;
        float _timer = -1f;   // <0 = 无动画
        GoalType _lastGoal;
        CitizenAuthoring _ca;

        void Awake()
        {
            _mesh = transform.Find("Mesh");
            _ca = GetComponent<CitizenAuthoring>();
            // 人形市民无胶囊 Mesh 子物体,Q 弹压扁不适用,禁用。
            if (_mesh == null) enabled = false;
        }

        void OnEnable()
        {
            if (_ca != null) _lastGoal = _ca.currentGoalType;
        }

        void Update()
        {
            if (_ca == null || _mesh == null) return;

            // 任意 goal 切换都触发:进入 Seek*(开始恢复)或离开 Seek*(恢复完成)。
            // POI 精简到边缘后恢复切换稀疏,只等"完成"几乎不可见,故两种都弹。
            var g = _ca.currentGoalType;
            if (_lastGoal != g)
                _timer = 0f;
            _lastGoal = g;

            // 动画驱动
            if (_timer >= 0f)
            {
                _timer += Time.deltaTime;
                if (_timer >= duration)
                {
                    _mesh.localScale = Vector3.one;
                    _timer = -1f;
                }
                else
                {
                    float t = _timer / duration;
                    float y = SpringY(t);
                    // 压扁时 x/z 略增,保持体积感
                    float inv = 1f / Mathf.Sqrt(y);
                    _mesh.localScale = new Vector3(inv, y, inv);
                }
            }
        }

        // 弹性曲线:y = 1 - A*exp(-k*t)*cos(omega*t) - B*exp(-k*t)*sin(omega*t)
        // 先压扁(<1)再过冲(>1)最终衰减回 1,果冻/弹簧感。
        float SpringY(float t)
        {
            const float k = 4.5f;          // 衰减率
            const float omega = 11f;       // 振荡频率
            float damp = Mathf.Exp(-k * t);
            float cos = Mathf.Cos(omega * t);
            float sin = Mathf.Sin(omega * t);
            // A 主导压扁, B 产生过冲
            float a = squashAmount;
            float b = squashAmount * 0.35f + springOvershoot;
            return 1f - damp * (a * cos + b * sin);
        }
    }
}
