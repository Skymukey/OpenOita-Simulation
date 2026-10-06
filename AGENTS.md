# 仓库协作指南

## 项目结构与模块职责

OpenOita 是 Unity 6000.3.11f1 / URP 2D 世界模拟原型。2026年10月6日状态同步：M00–M08主功能已接通，支持编辑、保存、正式试玩及Reset；M09完整交付验收尚未完成。当前状态见 `docs/specs/README.md`；2026年10月5日的核对记录仅作历史基线。

正式入口已装配M02–M07：结构提取/拆分、独立PhysicsScene2D、子步/接触、4096命令/结果缓存、有界查询、完整ChangeSet及生产显示。F01已用水汽暂存与恢复替换旧立即排开，无合法恢复目标时保持Ready；Editor功能回归与Windows64 Mono专项运行见 `docs/validation/M06-F01-实现与验证.md`。175格Player最后采样Tick10753 Ready，控制无空位场景Tick11750 Ready；这些是F01构建证据，尚不覆盖后续增量优化。M05Preview仍为独立模块预览，不能替代正式验收。

M08已支持空白场景、1–128格圆形/方形连续画笔、橡皮擦、矩形填充/擦除、三文件另存及正式试玩。菜单 `OpenOita/场景绘制工具`，保留 `OpenOita/编辑初态与正式试玩`；见 `docs/validation/M08-场景绘制工具-2026-10-06.md` 和 `docs/M08-编辑初态操作说明.md`。M08旧报告中的Tick263后排开故障是F01之前的历史证据。

增量优化已实施区块写时复制、实例/占据目录按需复制、同修订空间索引及查询缓冲复用。短时Editor测量：预览Step平均104.173→68.745ms，正式175格21.303→9.977ms；不是P1–P4达标证据，预览仍默认10Tick/s。最新回归为EditMode569项（568通过、0失败、1既有GC忽略），最后受影响定向103/103及最终PlayMode97/97通过，见 `docs/validation/增量更新优化-2026-10-06.md`。最新优化尚未重建Player；完整T1–T9、P1–P4、可靠GC/峰值内存、IL2CPP及真实游戏接入待验。

- `Assets/Scripts/OpenOitaWorld/`：`Core/Contracts` 定义公共合同，`Data` 处理严格配置读写，`Simulation` 管理状态、快照和事务，`Rules` 处理材料规则，`Structure` 处理连通、提取及几何，`Host` 提供正式入口。`Preview` 是独立模块观察器，`Render`含M07B正式显示并保留旧GPU原型参考。
- `Assets/Scenes/M05Preview.unity`：当前模块预览；`Assets/Scenes/SampleScene.unity`：正式Host场景，PlayMode创建完整准备成功后返回Ready；`Assets/Shaders/`：原型着色器。
- `Assets/StreamingAssets/OpenOita/`：正式三文件配置源；`Assets/Data/Materials/`：保留旧 stone/sand 数据；`Assets/Tests/OpenOita/`：Fixtures、EditMode、PlayMode 及对应程序集。
- `Packages/`、`ProjectSettings/`：依赖与编辑器配置；`docs/specs/`、`docs/schemas/`、`docs/examples/`：开发规格、数据约束及示例。规格中的规划功能不代表已实现。

## 开发、构建与运行

使用上述 Unity 版本，在仓库根目录执行 PowerShell；按本机安装位置调整路径：

```powershell
$unityEditor = 'D:\Unity\Editors\6000.3.11f1\Editor\Unity.exe'
& $unityEditor -projectPath (Get-Location).Path
```

观察模块行为时打开 `M05Preview` 并进入 Play Mode；该场景复用真实状态、规则和结构核心，但不实现正式 `IWorld` 或物理调度。`SampleScene` 的 `WorldHost` 已改用严格三文件配置，默认读取 `StreamingAssets/OpenOita`；`WorldSimulation.Create`在PlayMode/Player装配正式M02–M07，完整准备后返回Ready；EditMode返回NotReady/PlayMode，不启动旧核心块演示。

构建使用 `File > Build Profiles`；当前 Windows 构建列表仅含 `SampleScene`，无项目自动构建脚本。PC三文件启动及F01 Windows64 Mono非Development专项运行已有证据，包位于 `Builds/F01Validation/OpenOita.exe`。该包早于最新增量优化，完整Player功能、性能及IL2CPP仍待验，不能以模块预览或Editor测试代替。

## 代码风格与命名

C# 使用 4 空格缩进、大括号独占一行；类型、方法、属性用 `PascalCase`，参数与局部变量用 `camelCase`，私有状态通常用 `_camelCase`。序列化字段和 JSON DTO 沿用既有名称。文件名与主要类型一致；未配置统一格式化或 lint 工具，遵循相邻代码。编辑器 API 须置于编辑器程序集或 `#if UNITY_EDITOR` 内。

## 测试要求

已声明 Unity Test Framework 1.6.0（NUnit），Newtonsoft 已直接锁定 3.2.1。已有 `OpenOita.Runtime`、`OpenOita.Tests.Fixtures`、`OpenOita.Tests.EditMode`、`OpenOita.Tests.PlayMode` 四个程序集。新增测试沿用 `Assets/Tests/OpenOita/EditMode/`、`PlayMode/` 及其模块子目录，采用 `<Type>Tests.cs` 命名。

通过 `Window > General > Test Runner` 运行；也可关闭该项目的编辑器并执行：

```powershell
& $unityEditor -batchmode -projectPath (Get-Location).Path -runTests -testPlatform EditMode -testResults Logs/editmode-results.xml -logFile Logs/editmode.log
```

将平台改为 `PlayMode` 可运行对应测试，并另设结果路径。实际画面像素读回用例在 batchmode 下会忽略，须用可渲染的 Editor Game View 验证。最新全量原始结果为 `Logs/Optimization-20261006/editmode-full.xml`（568通过、0失败、1既有GC忽略）及 `playmode-full.xml`（97/97通过）；EditMode全量后补充修改的受影响定向回归103/103通过。各阶段测试数不相加为独立覆盖数，历史报告不自动覆盖后来行为。GC、完整Step性能和完整Player验收仍未完成。无覆盖率阈值；重点覆盖坐标边界、缺失材料、资源释放和变更行为，记录通过、失败与未测项。

## 提交与 Pull Request

历史仅有初始化及 `feat: 搭建…` 提交；新提交建议使用 `feat:`、`fix:`、`docs:` 加中文摘要。PR 说明目的、关联 issue 或规格编号、验证结果及未完成项；显示变更附截图。Unity 资产须连同 `.meta` 提交；不提交 `Library/`、`Temp/`、`Logs/` 或自动生成的 `.csproj`、`.sln`。

## 协作与操作约束

开发前检查 `git status`，保留已有改动；先读 `docs/specs/README.md`、`docs/specs/CONTRACTS.md` 及对应模块规格。共享接口和场景修改按模块归属协调，行为变更同步规格、Schema 与示例。

文档与批准请求使用中文。禁止批量删除文件或目录，包括 `del /s`、`rd /s`、`rmdir /s`、`Remove-Item -Recurse`、`rm -rf`。每次只能删除一个明确路径的文件；需要批量清理时停止操作，请用户手动删除。


