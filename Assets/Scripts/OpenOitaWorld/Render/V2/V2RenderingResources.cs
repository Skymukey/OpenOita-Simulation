using System;
using UnityEngine;

namespace OpenOita.V2.Render
{
    /// <summary>
    /// Player-safe references for the V2 display path. Hosts should load this asset instead of
    /// relying on Shader.Find/Resources lookup, then pass its three references to the renderer
    /// constructor.
    /// </summary>
    [CreateAssetMenu(fileName = "V2RenderingResources", menuName = "OpenOita/V2 Rendering Resources")]
    public sealed class V2RenderingResources : ScriptableObject
    {
        [SerializeField] private ComputeShader _updateShader;
        [SerializeField] private Shader _tileShader;
        [SerializeField] private Shader _fireShader;

        public ComputeShader UpdateShader => _updateShader;
        public Shader TileShader => _tileShader;
        public Shader FireShader => _fireShader;

        public bool IsComplete => _updateShader != null && _tileShader != null && _fireShader != null;

        public void Validate()
        {
            if (_updateShader == null) throw new InvalidOperationException("V2 ComputeShader资源缺失。");
            if (_tileShader == null) throw new InvalidOperationException("V2材料Shader资源缺失。");
            if (_fireShader == null) throw new InvalidOperationException("V2火焰Shader资源缺失。");
        }
    }
}
