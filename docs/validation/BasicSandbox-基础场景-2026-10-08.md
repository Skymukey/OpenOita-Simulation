# 基础场景创建与验证（2026年10月8日）

## 当前交付：9600×6400

用户进一步指定9600×6400后，在同一BasicSandbox.asset扩容，总面积61440000格，cellSize=0.1，世界范围960×640单位，448格原有示例材料及其标记保留。Scene视图定位全图，Main Camera保持左下角示例观察；详细操作见`docs/BasicSandbox-基础场景操作说明.md`。

代码扩展：ContractDefaults新增MaxWorldDimension=16384；WorldSourceLoader宽高及材料/固定/初燃坐标校验、ChunkStore边长校验复用该常量。world_config.schema.json与scene.schema.json同步到单边16384、最大坐标16383。默认材料容量、规则和256 MiB预算不变。

| 当前版本验证 | 实际结果 |
| --- | --- |
| 编译、Console错误 | 编译通过，最终Console错误0条 |
| EditMode M01.WorldSourceLoaderTests与M02.StateCoreTests | 115/115通过，0失败、0忽略；含新增8项大画布参数/行为用例 |
| 尺寸与坐标上限 | 16384维度合法；0、16385拒绝；9600×6400最右上角材料及固定/初燃标记严格保存重载通过 |
| 远端稀疏存储 | (9599,6399)空读不分配，写入只分配1块；越界拒绝；克隆分支写时复制隔离通过 |
| 正式IWorld连续1000 Tick | 配置9600×6400，Ready，402格，无Step失败 |
| Reset | generation从2到3，Tick=0，448格恢复 |
| 正式远端查询与推进 | 临时输入副本增加1格固定混凝土，100 Tick保持Ready；推进前后QueryPoint均命中(9599,6399)的102材料；临时格不写入资产 |
| 试玩前后SHA256 | 不变：3CADEBEEB48E2BF4D02E8EBBE0FEE4BC8CF5E83D7B416B47304F4AEF23D2A26C |
| 最终状态 | 停止Play，BasicSandbox场景与地图编辑窗口保留 |

证据保存于`Logs/BasicSandbox-20261008/9600-tests.json`、`9600-runtime.json`、`9600-corner-runtime.json`及9600试玩前后哈希文件。首轮回归发现旧宽上限负例和初态坐标4095限制，修正测试边界、解析器及场景Schema后115项全通过。正式推进在暂停PlayMode内调用IWorld.Step。本次未重建Player，不代表61440000格满图材料、零GC或帧率通过。

以下保留960×640阶段历史记录，不作为当前9600×6400的验收证据。

## 首次交付（960×640阶段）

- 场景：`Assets/Scenes/BasicSandbox.unity`及Unity生成的meta。
- 独立关卡：`Assets/OpenOita/Maps/BasicSandbox.asset`及Unity生成的meta。
- 操作说明：`docs/BasicSandbox-基础场景操作说明.md`。

该阶段画布960×640，cellSize=0.1，总面积614400格，相对首轮96×64格扩大100倍。448格初态材料保留在左下角，复用现有OpenOitaMap、WorldHost、地图编辑器和正式显示链。当时未新增或修改运行/编辑脚本、共享合同、材料规则或资源预算，未改构建场景列表。

地图绑定独立关卡、两份生产Shader和Main Camera。Host自动推进、默认禁转；Game摄像机保持局部示例观察，Scene编辑视图定位整幅大画布，绘制窗口可整数倍率和平移查看局部。摄像机、Directional Light、场景引用、资产及meta均通过Unity Editor/MCP创建。

## 验证

环境：Unity 6000.3.11f1 Windows Editor，MCP明确路由OpenOita-Simulation实例。首次发现另一套Unity工具连接PlayerBehaviorModel，只进行只读检查；全部资产操作在OpenOita-Simulation中执行。

| 检查 | 实际结果 |
| --- | --- |
| 960×640配置与正式世界创建 | Ready，448格 |
| 正式IWorld连续推进200 Tick | Ready；436格，蒸汽12格寿命结束；木块原位置查询无命中，承接平台上方查询命中104木头 |
| 正式IWorld连续推进400 Tick | Ready；402格；燃烧木梁34格全部燃尽，独立木块18格保留 |
| 600、800、1000 Tick | 均Ready；402格，水64、混凝土320、木头18，无推进失败 |
| 大画布Reset | generation由2增至3，Tick=0，Ready，恢复448格 |
| 大画布右上角编辑 | 独立内存副本对(959,639)绘制成功，严格导出成功；(960,639)被拒绝；不写入关卡 |
| 试玩前后关卡SHA256 | 不变：579BA525275DEC757750C3A5110B66CF8128A9BF3506AE65A0FA5A1C775B65F3 |
| 既有关卡保护 | 新地图.asset、GC_P2.asset哈希与任务开始时相同 |
| Console错误 | 创建小场景及早期试玩结束检查为0；最终大画布以正式Step返回结果及生命周期核对 |

大画布推进验证在暂停的Editor PlayMode中调用正式IWorld.Step，避开自动Update重复推进。局部布局相同的96×64版本另有实际自动驱动到Tick1598 Ready的观察；该自动驱动数字不冒充大画布实测。不是帧率、GC、峰值内存或Player验收。

首轮悬空木块离地过高，Tick25触发现有5 m/s速度上限并Faulted。最终布局加入固定承接平台，缩短下落距离；保留现有速度上限和规则。创建时NewScene卸载了尚未在场景引用的临时资产对象，随后从已保存路径重新加载并绑定，严格ValidateInput通过。最终交付不保留缺失关卡引用。

## 证据与边界

证据位于忽略目录`Logs/BasicSandbox-20261008/`，含大画布试玩前后哈希、最终1000 Tick结果、局部布局初态及运行截图。截图在扩大画布前采集，材料布局与最终关卡一致，仅覆盖Main Camera局部观察；不用于证明大画布全图像素覆盖。

最终停止Play，打开BasicSandbox场景并选中“基础沙盒地图”，保留地图编辑窗口。没有覆盖Test1、GC_P2、既有新地图或用户图片改动。本次验证只覆盖这份稀疏布局；画布扩大不自动增加65536格非空材料容量及256 MiB运行预算。
