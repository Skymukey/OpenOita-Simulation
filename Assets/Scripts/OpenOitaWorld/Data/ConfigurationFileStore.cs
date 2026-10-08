using System;
using System.IO;
using System.Text;
using OpenOita.Contracts;

namespace OpenOita.Data
{
    public sealed class ConfigurationFileStore
    {
        private readonly long _memoryBudgetBytes;
        private readonly Action<string, string> _writeTemporary;
        private readonly Action<string, string> _publishTemporary;
        public ConfigurationFileStore(long memoryBudgetBytes = WorldSourceLoader.DefaultMemoryBudgetBytes)
            : this(memoryBudgetBytes, WriteTemporary, PublishTemporary) { }

        // 仅用于模块故障注入；正式入口使用刷盘及同目录原子替换。
        internal ConfigurationFileStore(long memoryBudgetBytes, Action<string, string> writeTemporary, Action<string, string> publishTemporary)
        {
            if (memoryBudgetBytes <= 0) throw new ArgumentOutOfRangeException(nameof(memoryBudgetBytes));
            _memoryBudgetBytes = memoryBudgetBytes;
            _writeTemporary = writeTemporary ?? throw new ArgumentNullException(nameof(writeTemporary));
            _publishTemporary = publishTemporary ?? throw new ArgumentNullException(nameof(publishTemporary));
        }

        public WorldResult ReadSources(string directory, out WorldSources sources)
            => ReadSourcesCore(directory, null, out sources);

        // M01：M08首次保存预检复用有界读取；scene文本已经由统一序列化器生成。
        public WorldResult ReadSourcesWithScene(string directory, string sceneText, out WorldSources sources)
        {
            if (sceneText == null)
            {
                sources = null;
                return WorldResult.Failure(WorldErrorCode.InvalidArgument, new WorldDiagnostic("配置读取", "scene.json", "场景文本不能为空。"));
            }
            return ReadSourcesCore(directory, sceneText, out sources);
        }

        private WorldResult ReadSourcesCore(string directory, string sceneText, out WorldSources sources)
        {
            sources = null;
            string file = directory;
            try
            {
                if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException("必须显式提供配置目录。");
                string folder = Path.GetFullPath(directory);
                string[] paths = { Path.Combine(folder, "materials.json"), Path.Combine(folder, "world_config.json"), Path.Combine(folder, "scene.json") };
                string[] texts = new string[3];
                long remainingBytes = Math.Max(0, (_memoryBudgetBytes - 131072) / 128);
                if (sceneText != null)
                {
                    if (sceneText.Length > remainingBytes) throw new ConfigurationException(paths[2], "$", "场景文本超过输入读取预算。", WorldErrorCode.CapacityExceeded, "配置读取");
                    texts[2] = sceneText;
                    remainingBytes -= sceneText.Length;
                }
                for (int i = 0; i < paths.Length; i++)
                {
                    if (i == 2 && sceneText != null) break;
                    file = paths[i];
                    // 单文件打开后按实际流长度预检，并限制读取量，避免增长中的文件绕过预算。
                    using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        if (stream.Length > remainingBytes) throw new ConfigurationException(file, "$", "配置文件超过输入读取预算。", WorldErrorCode.CapacityExceeded, "配置读取");
                        using (var reader = new StreamReader(stream, new UTF8Encoding(false, true), true))
                        {
                            var text = new StringBuilder(); var buffer = new char[4096]; int count;
                            while ((count = reader.Read(buffer, 0, buffer.Length)) > 0)
                            {
                                if ((long)text.Length + count > remainingBytes) throw new ConfigurationException(file, "$", "配置文件增长超过读取预算。", WorldErrorCode.CapacityExceeded, "配置读取");
                                text.Append(buffer, 0, count);
                            }
                            texts[i] = text.ToString();
                            remainingBytes -= Math.Max(stream.Length, text.Length);
                        }
                    }
                }
                sources = new WorldSources(texts[0], texts[1], texts[2], paths[0], paths[1], paths[2]);
                return WorldResult.Success();
            }
            catch (ConfigurationException ex) { return ex.Result; }
            catch (Exception ex) when (IsFileFailure(ex))
            {
                return WorldResult.Failure(WorldErrorCode.InvalidConfig, new WorldDiagnostic("配置读取", "$", ex.Message, file));
            }
        }

        public WorldLoadResult LoadDirectory(string directory)
        {
            WorldResult result = ReadSources(directory, out WorldSources sources);
            return result.IsSuccess ? SceneMaterialData.LoadBySchemaVersion(sources, _memoryBudgetBytes) : new WorldLoadResult(result);
        }

        public WorldResult SaveScene(string directory, WorldConfig config, SceneInitialData scene, IMaterialRuntimeTable materials)
        {
            WorldResult result = ConfigurationSerializer.Serialize(config, scene, materials, out WorldSources sources);
            return result.IsSuccess ? WriteAtomically(directory, "scene.json", sources.SceneText) : result;
        }

        // 单文件原子保存。三文件不是跨文件事务，集成方不能在运行中热替换整套配置。
        public WorldResult WriteAtomically(string directory, string fileName, string text)
        {
            string target = directory, temporary = null, stage = "文件路径";
            try
            {
                if (fileName != "materials.json" && fileName != "world_config.json" && fileName != "scene.json")
                    throw new ArgumentException("仅允许三份固定配置文件名。");
                if (string.IsNullOrWhiteSpace(directory) || text == null) throw new ArgumentException("目录和文本不能为空。");
                target = Path.Combine(Path.GetFullPath(directory), fileName);
                temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
                stage = "临时写入"; _writeTemporary(temporary, text);
                stage = "目标替换"; _publishTemporary(temporary, target);
                return WorldResult.Success();
            }
            catch (Exception ex) when (IsFileFailure(ex))
            {
                string message = ex.Message;
                if (temporary != null) message += $" 本次临时路径：{temporary}。";
                // 清理只针对本次创建的一个明确临时路径；清理失败保留可诊断路径。
                if (temporary != null)
                {
                    try { if (File.Exists(temporary)) File.Delete(temporary); }
                    catch (Exception cleanup) when (IsFileFailure(cleanup)) { message += $" 临时文件保留于 {temporary}：{cleanup.Message}"; }
                }
                return WorldResult.Failure(WorldErrorCode.InvalidConfig, new WorldDiagnostic(stage, "$", message, target));
            }
        }
        private static bool IsFileFailure(Exception ex) => ex is IOException || ex is UnauthorizedAccessException ||
            ex is ArgumentException || ex is NotSupportedException || ex is System.Security.SecurityException;
        private static void WriteTemporary(string path, string text)
        {
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                byte[] bytes = new UTF8Encoding(false, true).GetBytes(text);
                stream.Write(bytes, 0, bytes.Length); stream.Flush(true);
            }
        }
        private static void PublishTemporary(string temporary, string target)
        {
            if (File.Exists(target)) File.Replace(temporary, target, null);
            else File.Move(temporary, target);
        }
    }
}
