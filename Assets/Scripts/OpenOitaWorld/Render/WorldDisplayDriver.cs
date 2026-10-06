using UnityEngine;

namespace OpenOita.Render
{
    // 所有自动/手动Step完成后，在帧末上传最终已提交版本。
    public sealed class WorldDisplayDriver : MonoBehaviour
    {
        internal CommittedWorldRenderer Renderer;
        private void LateUpdate() { Renderer?.FlushFrame(); }
    }
}
