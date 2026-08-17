using UnityEngine;

namespace CitizenSim
{
    // POI 文字标签完整 billboard:每帧朝向主相机(正对相机,任何视角都可见)。
    // TextMeshPro 文本平面默认朝 +Z,故在相机朝向基础上绕 Y 转 180°,让文字正面朝相机。
    public class PoiLabel : MonoBehaviour
    {
        void LateUpdate()
        {
            var cam = Camera.main;
            if (cam == null) return;
            // 对齐相机旋转 + 绕 Y 180°:文字正面朝相机(不反向、不倾斜)。
            transform.rotation = cam.transform.rotation * Quaternion.Euler(0f, 180f, 0f);
        }
    }
}
