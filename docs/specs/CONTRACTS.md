# 跨 Agent 公共合同

版本：1.10｜2026年10月6日水汽暂存设计修订。1.7原子液体链、1.5逐像素、1.8几何/租约/预算和1.9显示候选/ChangeSet净差异继续有效，立即排开及无恢复目标Faulted条款由[M06-F01水汽暂存与恢复](M06-F01-水汽暂存与恢复.md)替代。F01已接入正式World并通过本轮Editor功能验证，结果见[实现记录](../validation/M06-F01-实现与验证.md)；历史M06–M08证据不自动覆盖新规则。JSON格式、默认数值和性能阈值保持不变。

必须同时阅读 [主方案](<../OpenOita 初步实现方案 草案.md>)。主方案与三份 [Schema](../schemas/materials.schema.json)、[世界 Schema](../schemas/world_config.schema.json)、[场景 Schema](../schemas/scene.schema.json) 是既有行为基线；本页负责模块边界和未写细的技术约定。发现冲突先记录并修正规格，不能自行降级首版范围。下文 C10 是本次拆分新增的技术默认，必须在 M00 形成测试，不声称来自原方案的逐项确认。

## C01 数据与坐标

2026年10月6日临时观察模式：正式场景的WorldHost及M08试玩默认开启“禁止刚体旋转”。该设置在创建世界时固定，Reset沿用；关闭后重新创建即可恢复旋转。仅约束物理旋转自由度，保留连续平移、重力、碰撞、拆分及原有几何/查询/显示合同。禁转状态角速度保持0，结构重建和子体继续使用相同约束；几何计划与内部测试入口拒绝非零角速度，避免状态与求解器不一致。底层`WorldSimulation(..., freezeBodyRotation: false)`保留原默认行为，Host和试玩入口显式传入设置。此项不修改三文件JSON或角速度预算，不替代原有旋转验收，也不保证解决平移像素对齐或排水无解。见[禁转记录](../validation/临时禁止刚体旋转-2026-10-06.md)。

- 材料 ID：0=空；101水、102混凝土、103蒸汽、104木头。kind 只分类，所有行为来自显式标签。
- RuleId/位号：structure=1/0、liquid_flow=2/1、gas_drift=3/2、burnable=4/3、extinguishes_fire=5/4。标签加载后转为 ulong RuleMask 与共享参数，不逐格持有字符串。
- 主网格按区块字典定位，区块边长128，块内索引 y×128+x。读取合法缺块返回空且不分配；尾块不能写到实际宽高之外。
- X右、Y上，格中心 `origin + cellSize * (x+0.5,y+0.5)`，点转格向下取整；世界左闭右开。Host 旋转0、缩放1，运行 origin 不变。
- 动态体局部格保存连续位姿，不把旋转后采样回主网格。局部几何原点与质心分离。世界位置通过体变换计算，查询逆变换后命中真实非空格。
- generation、committedTick、BodyId 和令牌是身份，不是数组下标。BodyId 在同 generation 内不复用；所有公开操作限所属主线程。

### 像素粒度与显示（1.5补充）

统一术语：**一个逻辑像素 = 一个模拟格 = 一份独立材料状态 = 一个原始材料纹理像素（texel）**。文档中的“格”均指这一最小单位，不能解释为多个逻辑像素共享状态的大格。空格仍为MaterialId=0，可隐式存储；不要求逐像素创建对象。

- 每个有效坐标独立保存材料及适用规则状态，可单独查询、绘制、擦除、点燃、燃尽或参与流动。Chunk的128×128只用于存储和批处理；碰撞矩形与刚体归组保留全部逐像素状态。固体共用刚体位姿，仍可逐像素改材料、烧损和拆分，不要求每个固体像素有独立速度。
- `width/height`是逻辑像素数；`cellSize`是一个逻辑像素的世界长度，与屏幕缩放无关。提高固定世界范围内的模拟精度须增加逻辑分辨率、相应调整cellSize并重评容量；单独改变cellSize或放大画面不会增加独立状态。
- 材料基础纹理在主网格或材料体局部坐标中逐像素对应，空位透明；允许分块、图集及等价批量绘制，但有效材料区域不能用一个纹素代表多个逻辑像素。图集隔离边与火焰等附加效果不计入逻辑分辨率。
- 显示缩放独立于世界配置。标准材料预览默认1:1，提供显式整数倍放大；1:1下对齐的主网格一个逻辑像素覆盖一个最终游戏画面像素，k倍显示覆盖k×k画面像素且仍只有一份状态。窗口不够时裁剪或平移，不能静默拉伸填满；编辑器自由视角或可选适应窗口视图必须标明倍率并能切回1:1。
- 精确像素视图使用Point采样、像素边界对齐，不经插值、mipmap或后处理重采样混合相邻材料。默认完整填色、不留格间缝；网格线是默认关闭的独立调试叠加层。1:1验收使用未被Game View或截图工具二次缩放的输出，记录实际输出尺寸与倍率；UI逻辑尺寸不能代替画面像素计数。
- 动态体保持连续位姿。旋转或亚像素平移后的屏幕覆盖不要求逐点1:1，但源状态及局部纹素仍逐像素对应，不能为了对齐画面修改物理位姿或回写旋转采样。单像素命中按原有逆变换合同执行。
- 编辑、正式试玩和配置预览使用同一份已校验尺寸及cellSize，不以硬编码参数覆盖输入。独立模块探针可以采用明确标示的自有配置；如将一个布局标记展开为8×8，必须实际生成64份独立状态，并标示展开后的逻辑尺寸，不能称为显示放大。

详细实现与验收见[主方案5.3](<../OpenOita 初步实现方案 草案.md#53-像素粒度与显示约定>)、[M07B](M07B-显示与火焰.md)的08–11、[M08](M08-编辑器与保存.md)的09，以及[示例解释](../examples/README.md)。独立模块预览的Editor 1:1/3倍实际输出见[整改记录](../validation/像素粒度-整改与验证.md)；正式生产显示及编辑/试玩Editor联验已执行，范围见[M07记录](../validation/M07-命令查询与显示实现验证.md)、[M08记录](../validation/M08-编辑器与保存实现验证.md)。完整Player逐像素验收及性能仍待验，不能用单格状态测试替代显示验收。本次状态同步不修改合同版本或条款。

## C02 权威状态与快照

CellState 字段为 ushort MaterialId、ushort Flags、uint FuelTicksRemaining、uint SpreadCountdown、uint LifetimeTicksRemaining、uint MoveCountdown、ulong IgnitedTick；字段可为对齐重排，实际大小目标不超过32字节。Flags bit0 为 Burning，其余为0；不保留逐格 Velocity。初始化数值严格按主方案3.1。

完整CellState是唯一材料事实源，分别存放于主网格、材料体或暂存记录。MaterialIds、空间索引、矩形碰撞、火焰和颜色均为派生缓存。每份非空材料只属于主网格、一个材料体或一条水汽暂存记录；完整状态随移动、提取、拆分、暂存和恢复搬运。暂存由M02唯一持有，不参与活动几何；材料总量为三者合计。Replace 是新实例，重置全部状态并取消旧固定约束；Remove 清空全部状态；水灭火不补充燃料。

内部工作视图可在成功 Tick 发布前变化；公开查询与显示只读已提交版本。Faulted 后普通查询禁用，显示保留最后成功版本；不能拿工作数组充作公开快照。快照可以使用双缓冲或有界复制，由 M02 统一实现并计入内存预算。

## C03 类型与模块接口责任

下列内部服务名是建议命名；M00 应一次冻结实际 C# 签名和文件路径。各模块可以调整私有算法，不得自行创建不兼容的共享 DTO。

| 数据/服务 | 所有者 | 输入 → 输出与约束 |
| --- | --- | --- |
| WorldSources / WorldConfig / SceneInitialData | M00类型，M01实现加载 | 三份文本和诊断文件名 → 不可变、已校验配置；不依赖目录扫描 |
| MaterialRuntimeTable / RuleRegistry | M01 | ID、标签、参数 → 稳定紧凑索引、RuleId、阶段/触发方式描述；不包含执行委托或每格对象。M03提供RuleId到执行器的唯一绑定，M02装配时核对描述与执行器齐全，不反向让Data依赖Rules |
| CellKey / WorldVersion / CellHit | M00 | generation、归属、局部坐标及版本；不得携带可写内部数组 |
| WorkingWorld / CommittedWorldView | M02 | 分别供事务与公开读取，包含暂存权威状态；普通材料查询只返回活动材料；生命周期和释放由 M02 管理 |
| 水汽暂存记录 / 恢复候选 / 数量诊断 | M00冻结，存储与采用M02，覆盖及恢复规划M06，寿命意图M03 | 完整状态、稳定记录身份、原坐标、进入Tick；与活动状态/位姿/实例/预算一起采用，同版本统计；签名已按F01第8节冻结，不另建可写事实源 |
| BodyIdReservation | M00签名，M02唯一序列，M05消费 | 数量及Span缓冲 → 整段非零递增BodyId；绑定所属generation，成功预留不回收；作为IMaterialMutationPreparer必需输入 |
| IMaterialBodyStorage / MaterialOwnershipPreparation | M00合同，存储M05，准备与采用M02 | 全部PlannedCell、旧新映射、几何/存储和撤销ID → 合入同一材料候选；准备成功移交存储，全部参与者预检后一次Apply，Tick发布另行完成 |
| RuleBatch / MutationIntent | M00类型，M03产出 | 阶段快照 → 候选移动、状态、点燃、移除意图；调度器裁决 |
| StructurePlan | M00类型，M04产出 | 候选状态及变更 → 全部分量、明确Disposition、原归属稳定成员顺序及RetiredBodyIds；不直接创建物理体 |
| PreparedMaterialMutation / BodyGeometryPlan | M05 | 分量及材料变化 → 全部新归属、几何、质量、速度、ID映射；失败可释放 |
| PreparedPhysicsMutation / PhysicsStepResult | M06 | 几何计划/步长 → 已准备物理资源或候选位姿；准备成功不等于发布 |
| CellGeometry / ContactQuery / OccupancyView | M06 | 真实格正方形和连续位姿 → 精确命中、接触、占据；M03/M07A共用 |
| CommandQueue / QueryService | M07A | 外部命令/已提交视图 → 令牌、命中和结果；真实变更交 M02 |
| ChangeSet / CommittedRenderView | M00类型，M02产出 | 一个成功版本 → 变化记录、体映射、脏范围及只读显示数据 |

准备对象必须有单一拥有者、明确 Prepared/Committed/Aborted 状态及释放入口。M02 负责协调 prepare → 全部预检 → apply → Tick publish；模块不得从内部发布自己的成功版本。接口需能注入失败用于验证原子边界。

## C04 Tick 固定阶段

`workingTick = committedTick + 1`，推进前检查溢出。严格依次：

1. 按令牌序号执行外部命令，每条独立事务；新点燃格记录 workingTick。
2. 建立精确邻域，记录流动前湿接触。
3. 水移动：阶段快照、候选、统一竞争、完整状态搬运。
4. 活动和暂存蒸汽每Tick各减一次寿命，到0自然消散；只有活动蒸汽按移动周期处理，暂存移动倒计时暂停。
5. 合并流动后湿接触，先熄灭并抑制受水目标点燃。
6. 已燃烧且 IgnitedTick < workingTick 的格扣燃料、传播；燃尽格本步不再传播。
7. 对裁决后的规则变化准备全部结构、几何与物理变更，通过后一起进入工作世界。
8. 推进物理子步；逐子步校验速度、边界、固体占据并记录接触，原子暂存被覆盖的活动水汽。全部子步成功后尝试恢复旧暂存记录，并补充恢复后的湿接触；当Tick新暂存最早下一Tick恢复。
9. 全部成功后发布版本、结果与 ChangeSet，发送提交通知；帧末显示合并脏区。

湿集合按材料实例追踪，一 Tick 只增加；流走的水仍能熄灭其流动前接触的材料。物理阶段新接触立即熄灭，不回滚此前燃料消耗。初始燃烧 IgnitedTick=0，Tick1 可消耗；当步新点燃最早下一 Tick 消耗。

## C05 原子边界与故障

| 情况 | 允许结果 | 禁止行为 |
| --- | --- | --- |
| 解析/创建失败 | 无可用 World，完整诊断及资源清理 | 部分注册表、部分结构 Ready |
| 外部命令参数/占据/容量预检失败 | 单条拒绝；工作状态不变，其后命令仍可处理 | 删除一半材料、只生成能放下的碎块 |
| 暂存水汽本Tick没有合法恢复目标 | 保持暂存，世界Ready，成功Tick可正常发布 | 丢弃记录、覆盖材料或仅因无目标Faulted |
| 规则批次或物理结果无法满足预算 | Faulted；不发布新 Tick；只允许 Reset/Dispose及诊断 | 丢格、截断速度、漏分量后报告成功 |
| 内部应用/资源故障 | 记录阶段，按可恢复边界清理；不可保证一致时 Faulted | 对外暴露未完成工作状态 |
| Reset 失败 | Faulted，不恢复旧运行世界 | 保留半新半旧版本 |
| 提交通知订阅者抛异常 | 记录异常，Tick仍已提交 | 因通知异常回滚成功版本 |

事务覆盖一条命令或一个裁决后的规则批次，不承诺整个 Tick 或 Unity 求解器回滚。尚未公开的本步命令在故障时不得作为成功结果返回。旧碰撞在下一次求解前立即失效，不以延迟 Destroy 充当撤销完成。Retry属于Faulted状态下允许的历史结果/诊断读取，遵循D09，不能被普通材料查询的故障门禁一并屏蔽。

## C06 外部 API

公共入口沿用主方案7节：Create(WorldSources, Vector2)、Enqueue(in MaterialCommand)、Retry(in CommandToken)、Step()、QueryPoint(Vector2)、QueryRegion(in WorldRect, Span<CellHit>)、QuerySegment(Vector2,Vector2,Span<CellHit>)、Reset()、返回 WorldResult 的 Dispose()。M00 验证项目所用 Unity/C# 配置可编译这些类型；不通过时先记录兼容替代方案和所有调用者变更。

- Spawn/Replace 提供非零 MaterialId，其余命令为0。命令矩形有限、正面积、完整位于世界内；不裁切。
- Spawn 枚举主网格格中心，任一已有材料或与动态固体正面积重叠即整拒。Remove/Replace/Ignite 用真实材料格中心选中并去重。动态体 Replace 只支持 structure 固体，否则整条 UnsupportedOperation。
- 暂存水汽不作为CellHit，不参与Remove/Replace/Ignite的选格；Spawn只检查活动占据与真实固体，但容量包含暂存。数量诊断按提交版本区分主网格、体内、暂存及总量，恢复不能覆盖后来生成的材料。
- QueryPoint 检查真实格面；QueryRegion 按格中心；QuerySegment 检查真实格正方形交集并按沿线距离和稳定键排序。孔洞不可由 AABB 命中。
- CellHit 含 generation/committedTick、材料和只读状态、ownerKind、BodyId、局部坐标。点多命中时主网格优先，其次 BodyId、局部y/x；区域及线段按格身份去重。
- 查询缓冲不足返回 BufferTooSmall/requiredCount；缓冲内容不得当作部分成功结果，合法空结果同样带版本。
- QueryMaterialCounts返回最近成功提交版本的活动/暂存/逐材料数量不可变值快照，可保存；Faulted仍只读最后成功计数，Disposed/执行中分别返回Disposed/Busy。实际字段和签名见F01第8节。
- StepResult 的命令结果视图有效至下一次 Step/Reset；通知 ChangeSet 仅回调期间有效，长期保存由调用者复制。
- 自动/手动驱动互斥，不改宿主全局时间或物理设置。执行阶段不允许重入；提交通知允许查询/入队，新命令下一步执行，Step/Reset/Dispose 返回 Busy。

错误码至少覆盖主方案所列全部18项：InvalidConfig、InvalidArgument、UnsupportedVersion、UnknownMaterial、UnknownRule、IncompatibleRule、OutOfBounds、Occupied、CapacityExceeded、UnsupportedOperation、InvalidToken、ResultExpired、StaleGeneration、BufferTooSmall、Busy、NotReady、Faulted、Disposed。

## C07 容量与性能

默认 width/height=256、chunkSize=128、cellSize=0.1、stepSeconds=0.02、gravityY=-9.81、seed=1；maxMaterialCells=65536、maxDynamicBodies=64、maxShapesPerBody=256、maxTotalShapes=4096、maxChangesPerTick=65536、maxLinearSpeed=5、maxAngularSpeedDegrees=180、maxPhysicsSubsteps=8、fluidDisplacementRadius=16。

全部范围与字段以 Schema/主方案2节为准，不将样例默认值变成更窄的格式上限。maxMaterialCells包含主网格、体内及暂存材料；fluidDisplacementRadius沿用字段名/数值/范围，改为暂存恢复的四邻接最短路径上限。预算预检包括暂存记录与快照、恢复搜索、实例映射、工作副本、索引、准备对象和形状；4个边界形状另计，不计材料查询。参数上限不代表已测可用规模。

P2/P3 Step p95≤10ms、p99≤16ms，P4峰值≤50ms、稳态 Step GC Alloc=0、模拟CPU资源峰值≤256MiB，均为待测目标。结构/初始化允许受限分配，单列峰值；显示与GPU不混入Step统计。

## C08 生命周期及资源

创建顺序：解析和跨文件校验 → 临时材料表及布局 → 全部结构与体 → 几何/物理/显示准备 → 一次发布 Ready、Tick0。Reset 使用创建时保存的初态副本，增加 generation、清命令、全部暂存记录和旧引用并重建；新Tick0暂存为空，场景JSON不保存暂存运行状态；Dispose 可重复，之后其他操作返回 Disposed。

暂存集合及其候选/提交副本、NativeArray、工作缓冲和世界副本归 M02；体局部存储及几何准备由 M05 明确移交；物理对象/独立场景归 M06；Texture/Buffer/Material/Mesh归 M07B；SceneMaterialData 编辑态归 M08。Create中途失败沿已取得资源的逆依赖顺序释放；测试要覆盖20轮生命周期。

## C09 确定性范围

主网格按 y/x，体按 BodyId 再局部 y/x。字典、HashSet、线程完成顺序不参与决策。相同规则输入、种子及阶段快照应给出相同结果。Unity 物理只按误差预算验收，不承诺跨机器或跨后端逐位一致；不能以这一点放宽规则和命令顺序。

## C10 本次拆分补充的技术默认

以下补齐实现所需细节，不新增玩法。M00 应把它们变成断言/样例；若实现探针证明不成立，记录变更并同步相关 spec，不能隐式更改。

| 编号 | 补充约定 |
| --- | --- |
| D01 实例与湿集合 | 不增加永久 CellState 字段。每Tick内部临时实例句柄通过移动/提取/拆分映射到新CellKey；1.10暂存/恢复还须在活动位置与暂存记录身份间受控迁移，暂存只读视图与映射签名由M00统一冻结，不将记录ID当作长期有效的Tick句柄；Replace/Spawn生成新句柄，Remove使旧句柄失效。湿接触跟随句柄，不能仅绑定世界坐标或旧BodyId。 |
| D02 变更计数 | 权威位置键为 `(ownerKind, BodyId, localX, localY)`；主网格BodyId=0。一次Tick中实际状态/材料实例/归属写入去重计数；倒计时/燃料等变化计入，值和实例及归属均未改变才不计。同ID Replace即使状态字节相同仍计一次实例替换。搬运计源和目标，提取/拆分计旧、新归属位置；同键多写一次。纯体位姿、派生缓存更新不算逐格写入。暂存记录采用独立逻辑键`(暂存, 记录ID)`，绑定generation；暂存计源与记录，恢复计记录与目标，寿命/消散计记录，同键去重。无目标的等待不计写入。计数覆盖全部命令/规则/暂存/恢复，不能每批重置。Create/Reset的初态建构不算Tick写入。 |
| D03 新体顺序 | 按原归属键（主网格先、再旧BodyId），随后按原归属坐标系内、重定局部原点前的分量最小y/x排序分配新BodyId。主网格自由分量一律新非零ID；仅既存动态体剩单分量时保留旧ID，多分量全部用新ID。准备时可预留候选ID以构建计划/计数，提交才发布；准备失败允许消耗号段，预留过的ID也不再复用。生成稳定的旧新映射，不依赖容器顺序。 |
| D04 移动层级 | 水先按M03-L01选取原子液体链；未入链源再按下、首选斜下、次选斜下、有效首选/次选横向候选处理。蒸汽保持原五层。水斜下、链首的BFS左右邻居及蒸汽平局使用`workingTick+x+y+seed`偶数左、奇数右；普通同层按目标y/x、源y/x裁决。水横移须有可达低处依据，不能仅按奇偶无效往返。 |
| D05 查询边界 | 非有限输入返回InvalidArgument。点在逻辑范围外返回OutOfBounds；区域/线段要求完整位于逻辑世界闭合几何范围，超出返回OutOfBounds，不裁切；区域正面积且中心筛选左闭右开。线段端点可在世界边界上，实际命中仍按格几何。零长度线段返回该点所有真实格命中，以稳定键排序；与QueryPoint只取首项的返回语义区分。动态点查询在逆变换局部采用左闭右开格面，线段按闭合格形相交并按身份去重。 |
| D06 查询排序 | QueryRegion 按主网格优先、BodyId、局部y/x返回；线段按首次交点参数t升序，等距按同一稳定键。重复穿过同一格只保留首次命中。 |
| D07 静态形状 | maxShapesPerBody限制动态材料体；静态形状只受maxTotalShapes约束，4个边界另计。静态碰撞可按实现容器组织，但不能靠重新分容器绕过全局预算。 |
| D08 子步估计 | 对每体估计 `d = norm(v)*h + abs(gravityY)*h*h + abs(omega)*r*h`，r为质心到最远占据格顶点距离，omega为弧度/秒；取全体最大值，N=max(1,ceil(d/(cellSize/2)))。N超限或预测/实际速度超限即故障，不裁剪。每子步实际位移、速度、边界仍需复核，不能只依赖此预测。 |
| D09 故障令牌 | 本Tick取出的全部令牌即使内部已应用，也统一返回Faulted而非公开成功；尚未执行的Pending令牌在Faulted世界也返回Faulted。此前成功Tick的缓存仍返回原结果，已淘汰为ResultExpired。Reset后全部旧generation为StaleGeneration。令牌不允许跨World实例使用，M00以不透明持有者标识或等价方式防止碰撞。 |
| D10 重复Replace | 即使材料ID相同，Replace也创建新实例、重置状态并撤销原固定标记；每个命中格计affectedCount=1。Remove只计实际移除，Ignite只计本命令新点燃的格；环境随后熄灭不倒改命令自身affectedCount。 |

connectionGroup采用大小写敏感序数比较。编辑器画笔覆盖已有格沿用新实例替换语义，清旧固定/初燃标记，重新标记由工具显式完成。装配时使用一个规则描述表和一个执行绑定表，二者都以同一稳定RuleId关联，不能各自重新注册或重排标签。

### D04 的液体平衡与原子链（1.7）

水必须在开放平底槽中摊平、从平台流向可达低处，静态平衡后停止搬运。固定夹具、期限、误差、完整排序及算法验证边界见[M03-L01](M03-L01-液体扩散与液面平衡.md)。链首为自由表面，按y降/x升选取；在同材可移动水中沿下/首选水平/次选水平作有界BFS，选择最低原空终点、再最短路径和稳定发现顺序。链节点预留后再生成普通重力与有效横移候选。

每个实例每阶段仍最多走一个相邻格。普通目标必须阶段原空；链内目标可为同一完整原子链的下一源，但链尾必须原空且低于链首。只允许Water阶段同MaterialId、带LiquidFlow的主网格实例构成下/左/右正交链。禁止上移、环、纯水平链、异材、未释放占据目标、分叉/重叠或提交半链；不把此例外扩展到蒸汽或刚体排水。

M02先检查全部来源、目标、完整路径、最终写入及累计预算，再在候选实例表中按尾到头迁移；统一清源/写目标，链内同字节状态的身份转移仍按D02计数。一个链末端可被后续普通候选竞争时，规则侧整链预留先胜，不能让通用裁决器留下断链。所有边均实际单步，湿集合按实例迁移，仍由流动前后接触合并，不增加跨Tick缓存或公开DTO。

普通横向扩散沿连续空通道寻找可达低处，无落差依据时停止；具体方向排序见M03-L01。缓冲受材料容量约束、复用并计入CPU预留；BFS有界不代表P2耗时已经达标。Schema仅更新语义说明，不新增配置项；M06的fluidDisplacementRadius按1.10用于暂存恢复，不替代L01自身的搜索约束。此修订替代1.6对所有水目标必须原空的限制；旧五层测试记录不覆盖新增液面平衡或链事务。

### D01 的实例查询与规则签名（1.1补齐）

在现有 `ITickInstanceMap` 增加 `bool TryGetInstance(in CellKey key, out CellInstanceHandle instance)`。只查询本Tick已登记实例，不创建实例、不增加序号、不改变湿集合；缺失键、旧generation或已结束的Tick租约返回false且out为default。成功时返回本Tick该位置当前材料实例，Move/提取/拆分后旧位置不再命中、新位置仍返回原句柄；Remove使查询失效，Replace同ID也必须返回新句柄。

现有 `IRuleExecutor` 的唯一执行签名冻结为：

```csharp
IRuleBatch Execute(IWorkingWorldView snapshot, IMaterialRuntimeTable materials,
    ITickInstanceMap instances, IContactQuery contacts, in TransactionContext context);
```

`instances` 是必需输入，不保留不带实例表的旧重载。M02各阶段传入同一个本Tick实例表，其对象在事务应用中保持稳定；阶段视图与context的generation/workingTick必须一致，输出MutationIntent引用查询取得的现存句柄，不按坐标生成身份。规则执行器只使用TryGetInstance、TryResolve、IsWet，不调用Create、Move、Invalidate或MarkWet；实例与湿集合修改由M02受控提交/接触阶段负责。该限制是模块职责合同，现有ITickInstanceMap继续保留M02使用的写入方法，没有另建平行实例接口。

M03-A的测试接触夹具可提供所需接触查询；此签名补齐不代表M06精确接触、规则执行实现或完整世界Step已经完成。文件Schema、默认参数、公开IWorld签名及玩法语义不变。

### M04 结构计划与候选视图（1.2补齐）

沿用 `TransactionContracts.cs` 中的现有类型，唯一公共签名如下：

```csharp
public enum StructureDisposition : byte
{
    RetainFixedGrid = 0,
    ExtractFreeGrid = 1,
    RetainBody = 2,
    CreateChildBody = 3
}

StructureComponent(CellPositionKey originalMinimum, string connectionGroup,
    bool isFixed, StructureDisposition disposition, IEnumerable<CellKey> members);
StructurePlan(IEnumerable<StructureComponent> components,
    IEnumerable<ulong> retiredBodyIds = null);

// IPreparedMutation 新增只读属性，继续使用同一个 IWorkingWorldView。
IWorkingWorldView CandidateWorld { get; }
```

`StructureComponent.Disposition` 是只读属性。固定主网格分量为RetainFixedGrid，自由主网格分量为ExtractFreeGrid；原动态体恰剩一个分量为RetainBody，多个分量全部为CreateChildBody。新ID仍由M02预留/M05消费，M04和DTO不分配ID。成员与OriginalMinimum使用原归属及重定原点前坐标；成员同有效generation、同原归属，按y/x严格递增，OriginalMinimum等于第一个成员键。连接组非空且按Ordinal比较。旧四参数构造入口已由显式分类的五参数入口替换，生产者和测试必须同步，不保留可能误判拆分的隐式分类入口。

`StructurePlan.Components` 按Grid先、原BodyId、原最小y/x严格递增。`RetiredBodyIds` 为复制后升序唯一的非零旧体ID；null等同空输入。原体剩零分量时仅在撤销清单中出现，不构造零成员分量；多分量时旧体必须列入撤销，单分量保留体不得列入。计划构造会拒绝分类/撤销矛盾及混合generation。清单里没有分量的ID视为删空原体，其确实属于原目录由M04基于候选Bodies验证，DTO不持有第二套体目录。输入集合均复制为只读集合，下一次分析不修改旧计划；计划键不延长候选状态或实例的租约。M04负责全量覆盖和四邻接正确性，DTO防御校验不替代搜索。非法DTO构造参数抛ArgumentException或其子类，不返回部分计划。

`IPreparedMutation.CandidateWorld` 由该准备对象持有。材料候选在Prepared期间提供编辑后状态、全部占据、候选有效固定标记和原体目录，包括删空体；不提供材料候选的物理/其他参与者返回null。调用方在应用前检查非null及租约，使用既有IStructurePlanner.Plan完成全量分析，再交M05/M06预检。Apply/Abort/Dispose、工作修订改变、Tick结束或世界故障后视图失效；失效Read返回失败及default状态，枚举/配置等无结果码入口可抛InvalidOperationException。不能通过CandidateWorld取得可写数组，也不能把它用于公开查询或显示。返回只读视图不代表准备已应用或Tick已发布。

此签名关闭M04-I03的视图承载缺口。M02-I02的BodyId预留在1.3补齐，候选实例/写入及受控材料归属提交在1.4补齐。正式Create/Step、物理/显示参与者和完整生命周期集成仍需另做实测。

### D03 的统一 BodyId 预留（1.3补齐，M05-I01）

```csharp
public delegate WorldResult BodyIdReservation(int count, Span<ulong> destination);

// M02 WorkingWorld 唯一受控入口，绑定为上述回调；不增加公开 IWorld 操作。
WorldResult ReserveBodyIds(int count, Span<ulong> destination);

// 1.4统一为编辑后的准备对象输入，保留唯一BodyId回调；不保留旧重载。
PreparationResult<IPreparedMaterialMutation> Prepare(IPreparedMutation candidate,
    StructurePlan structure, BodyIdReservation reserveBodyIds, ITickChangeCounter changes,
    MaterialOwnershipPreparation prepareOwnership, in TransactionContext context, IFailureInjector failures);
```

M02在每个WorkingWorld建构时持有一个从0开始的私有IdentitySequence；BodyId从1分配，主网格始终为0。该序列只属于此世界的generation，不因BeginTick、发布、候选复制/Abort/Dispose或旧体撤销而重建。Reset重建新generation时创建新序列并释放旧世界；绑定旧世界的回调不能对新世界分配。回调必须非null，所属世界与输入视图/context同generation；M02只向材料准备参与者提供，M04、规则及公开查询不取得分配权限，M05不持有独立序列或自行猜测ID。

入口先检查世界生命周期及所属线程，再依次检查count、缓冲和剩余号段：负count为InvalidArgument；destination.Length<count为BufferTooSmall；完整号段超出ulong.MaxValue为CapacityExceeded。所有失败保持缓冲及序列不变，不返回部分预留。count=0在有效世界内成功且不写入、不耗号，即使序号已耗尽也成立；故障/释放/错线程的零请求仍返回相应Faulted/Disposed/InvalidArgument。成功只写destination前count项，依次为上一已预留ID+1至+count，尾部保持；ulong.MaxValue可作为最后合法非零ID，再申请正数量失败，不能溢出到0。

M05先统计完整StructurePlan中ExtractFreeGrid与CreateChildBody的数量，预检已知体预算，再一次请求全部新ID；按Components的D03顺序仅给这两类分量依次消费。RetainFixedGrid用0，RetainBody保留旧ID且不耗号；多个分量全部新建并沿RetiredBodyIds撤销旧体，删空体仅撤销。已成功预留的ID立即消耗；后续材料/几何/物理/显示准备失败、容量拒绝或候选释放都不回收，不能仅凭当前体目录没有该ID就再次分配。入口自身预检失败未曾预留，因此不耗号。预留不创建体、不改材料、实例、Tick计数或公开版本；最终体/形状/内存预算及发布仍由统一事务完成，预留数量不是当前存活体数限制。

例：同generation已预留到7，体7删除中格后拆成两个分量，申请2个ID得到8、9；计划撤销7并映射7→8及7→9。如果准备失败，重试须使用10、11。删除端格仅剩一个分量则保留7，不调用正数量预留。

1.3关闭M05-I01的ID部分；1.4补齐以下候选租约和受控提交。两者均不代表完整Create/Step、真实物理、碰撞撤销或Player验收通过。材料ID101–104和JSON格式均不变。

### D01/D02 的候选租约与统一归属准备（1.4补齐，M05-I02/I03）

```csharp
// 原 IPreparedMutation 的材料候选租约；非材料参与者为null/空。
ITickInstanceMap CandidateInstances { get; }
ReadOnlySpan<CellPositionKey> CandidateWrites { get; }

public interface IMaterialBodyStorage : IDisposable
{
    bool IsDisposed { get; }
    BodySnapshot Snapshot { get; }
    BodyGeometryPlan Geometry { get; }
    ReadOnlySpan<CellPositionKey> OccupiedPositions { get; }
    bool TryRead(in CellPositionKey position, out CellSnapshot state);
}

public delegate PreparationResult<IPreparedMaterialMutation> MaterialOwnershipPreparation(
    ReadOnlySpan<PlannedCell> cells, ReadOnlySpan<BodyIdMapping> mappings,
    IReadOnlyList<BodyGeometryPlan> geometry, IReadOnlyList<IMaterialBodyStorage> bodies,
    ReadOnlySpan<ulong> retiredBodyIds, in TransactionContext context);
```

`CandidateInstances`由原准备对象持有，只能TryGetInstance/TryResolve/IsWet；使用既有ITickInstanceMap的只读门面，Create/Move/Invalidate/MarkWet全部抛InvalidOperationException，不能取得原可写实例表。Spawn读取新候选句柄；Move在目标位置读取原句柄及湿标记，源位置已失效；同材料ID Replace也读取新句柄且不继承旧湿标记。候选尚未应用时原工作实例表及状态不变。键与句柄必须同generation/workingTick；缺失、旧generation、错线程或失效租约的查询返回false/default。Apply/Abort/Dispose、Tick结束、世界故障/释放及修订变化后租约失效。

`CandidateWrites`是编辑后实际状态/实例写入的稳定y/x键集，同键去重，同ID Replace也计入；Move包含源与目标。归属准备成功后更新为原编辑键与归属迁移源/目标的联合集合。读取属性时检查Prepared租约，失效抛InvalidOperationException；Span和CellInstanceHandle均不延长租约，跨阶段保存由调用者在有效期内复制且不能继续用于原候选。初态Tick0也有同一候选实例服务，但写入集为空，提取不计Tick写入。

M02对裁决后的编辑先Prepare，再以CandidateWorld调用M04完整结构分析；原IMaterialMutationPreparer唯一Prepare签名见D03。M05读取原候选三项租约，用M02传入的BodyIdReservation、只允许Count/Preflight的ITickChangeCounter及绑定此候选的MaterialOwnershipPreparation完成计算。M05不能通过计数服务Record，也不通过CandidateInstances写入。原resolvedChanges参数由权威编辑候选替代，不再让M05推断或重新初始化编辑后实例；规则MutationIntent裁决仍归M02。

受控归属入口输入必须包含全部结构幸存格及其完整状态/实例、全部结果几何/体存储、精确旧新映射和RetiredBodyIds。M02验证完整覆盖、源状态/实例、目标唯一性、分量分类、当前候选预留ID顺序、撤销/映射、矩形真实覆盖、存储占据/状态、位姿及几何版本，并联合预检全部材料/体/形状/工作集和整个Tick写入。新体版本1，现有M05重建的保留体版本为旧版本+1。GeometryVersion向M06的直接承载仍属M05-I05，不因存储采用自动关闭。

准备只构建候选：清除待移出的候选网格格、准备完整体目录和实例目标、合并固定/占据与写入键、预先建立计数集合。成功返回**同一个原准备对象**的IPreparedMaterialMutation，并由其拥有体存储；M05释放计算计划时不再释放已移交存储。失败返回null、原工作状态不变，未移交存储仍由M05释放；M02材料准备调度在失败时中止整个编辑候选，命令可拒绝，规则错误则冻结世界。已预留BodyId不回收。归属完成后重复Prepare或覆盖结构计划返回Busy，不破坏已准备结果。

协调器只Own一次这个合并材料参与者，并先与物理/显示等参与者全部Preflight；随后同一次Apply采用网格、全部体存储、固定与占据、稳定原Tick实例表对象中的新映射及同一个计数器中的联合集合。旧体存储在采用后释放；未应用的候选Abort/Dispose只释放候选存储，不释放旧工作体。动态编辑没有归属准备时不能直接Apply。Tick发布仍在全部后续阶段成功后进行；提交视图独立复制体快照与局部材料状态，应用候选不会改变上次成功版本。世界Dispose释放已采用的体；新generation重建使用保存初态，完整IWorld.Reset联验仍待完成。

1.4时M05-I02和M05-I03在M02/M05状态核心关闭，物理、显示与其余接口仍开放。1.8的补齐及当前接线边界如下；历史状态不代表当前缺口。M02合并材料Budget.ChangedPositions使用真实联合计数，不使用M05独立计划的保守上界。

### M06 的几何版本、空间租约与位姿采用（1.8补齐）

`BodyGeometryPlan`的唯一构造器在`boundingRadius`后显式接收`ulong geometryVersion`，并提供只读`GeometryVersion`。新动态体为1；结构重建保留体为旧版本加1，溢出整拒；单纯规则状态更新及物理位姿采用保留版本。静态分量计划使用非零版本1，不形成BodyId。M05生产计划、存储和M02验证必须一致，下游不猜测版本。

现有`IContactQuery`、`IOccupancyView`及`IPhysicsStepView`增加`SpatialLease Lease`，包含generation、workingTick、stage、工作revision。M06的`WorldOccupancyIndex.Refresh`绑定当前只读世界及可选完整候选位姿，不改变材料；相同发布版本不能证明工作缓存新鲜。M02每次阶段和子步重新绑定；状态采用、Tick结束、故障、释放及错线程使旧查询失败。物理候选还在下一子步或物理资源重建后失效。旧模块测试接触桩的零generation租约仅保留其隔离测试语义，正式装配全部使用非零租约。

1.9实现中的M02内部`WorkingWorld.PreparePhysics`消费`IPhysicsStepView`和完整`MutationIntent`排开批次。1.10设计要求改为候选位姿、整批水汽暂存状态及完整实例映射的联合准备，并在最终物理阶段单独准备恢复；全部仍由M02整体预检后一次Apply采用。暂存记录/寿命/恢复写入加入同一个D02计数器，公开发布仍待完整Tick成功；不承诺回滚已推进的Unity求解器。实际共享签名已按F01第8节统一冻结，旧排开入口不能作为F01完成证据。

`ITickChangeCounter.ProjectedCount`承载最近一次成功Preflight的完整联合位置数，候选门面只允许读取和预检。`IPhysicsMutationPreparer.Prepare`在mappings之后增加`in ResourceBudget worldBudget`，材料/体/形状/ChangedPositions均报告完整候选世界总量；这些计数逐参与者校验而不相加，CPU为每个拥有者的保留及准备工作集，统一相加。四边界另计4个形状。M02另计规则、结构、空间、暂存集合及恢复搜索预留；规则缓冲按实际材料数建立并在增长前预检，不能把配置最大材料容量的规则缓冲重复分配给每个服务。估算不代表Profiler峰值或P2/P3达标。

Preflight失败时`ProjectedCount`恢复已记录的Count，不能暴露半批投影。`IPhysicsStepper.StepSubstep`在substepIndex后接收`long availableCpuBytes`，求解/候选接触工作集分配前检查剩余预算；完整跨归属候选接触以8N输出、64N宽阶段格对作有界上限，超限整拒不截断。空间索引失效后仍保留的字典容量继续计入预算，重建前保守预留4096N及头部；1.10恢复搜索按世界范围与半径交集预留访问集，再加完整暂存记录/候选副本及恢复输出/目标预留；记录增长和全部参与者同时存活的工作集都须预检。以上为保守容量合同，允许明确拒绝，不能解释为最大配置一定可在256MiB内运行。

`FailurePoint`在原末尾追加`AfterRectanglesPrepared`、`AfterMassPrepared`、`AfterBodyPrepared`和`AfterSpatialPrepared`，保持已有枚举值。使用同一`IFailureInjector`，允许计次指定第几个分量，不新增平行注入服务。准备失败仍沿拥有权逆序释放。

### M07 的显示候选、ChangeSet与查询（1.9补齐）

`IWorldRenderer`继续保留，M06观察器只承担隔离提交观察。生产M07B实现同文件`WorldViews.cs`的`ITransactionalWorldRenderer`，新增`IPreparedWorldDisplay PrepareCommit(ICommittedRenderView candidate, in ChangeSet changes)`。返回候选有`Result/CpuBytes/Adopt/Dispose`；Prepare完成全部有界CPU缓存、字典容量及新增不可见Unity资源，Adopt只移交已准备资源，不分配、不上传且必须无失败。未采用的Dispose释放新增资源，采用后所有权归renderer。候选视图只供准备消费，禁止公开或长期持有。

M02在所有规则/物理成功后构建独立候选快照和ChangeSet，调用显示准备并合并预算；失败冻结，不发布版本/命令成功/显示。成功后采用同一快照和显示，发布结果与通知；显示的逻辑版本即时采用，GPU版本只在帧末更新。同帧多个Step合并材质、火焰和位姿脏标记，只上传最终版本。`FailurePoint`末尾追加`AfterDisplayTilePrepared`，使用原IFailureInjector，不重编号。

ChangeSet是上次成功提交至本次成功提交的净变化，不是逐条命令的事件日志；同位置反复写入合并，整Tick去重写入范围仍包含没有最终可见差异的位置。字段完整提供格前后状态/归属键及原因、体创建/撤销/旧新映射、主网格/体脏范围。搬运、提取和拆分沿原Tick实例追踪，Replace在后续提取时仍保留新实例语义；仅体位姿改变不增加逐格写入数，但更新体显示范围。保留BodyId而几何版本变化提供同ID映射。ChangeSet门面仅在M02通知回调期间有效，跨回调/跨线程读取拒绝；长期保存逐项复制。

M07A预留4096队列、最近4096结果和按maxMaterialCells建立的有界查询缓冲，计入M02统一CPU预算。BufferTooSmall不写入部分内容；排序/边界沿D05/D06，零长线段使用真实半开格面返回全部命中。世界上界先存成Vector2以使用可表示float坐标，避免Mono表达式扩展精度误拒合法尾格，不增越界容差。

默认工厂/Host装配生产M07B；正式独立物理世界在Unity PlayMode或Player创建。EditMode仍可运行状态/命令/查询纯核心测试，但工厂严格加载后返回NotReady/Assembly/PlayMode，而不触发运行时SceneManager错误。Reset清理旧模拟后重建新代次；新显示失败进入Faulted，保留最后成功显示，成功后才释放旧显示。Dispose可重复。

SAT法轴直接由`(cosθ,sinθ)`及`(-sinθ,cosθ)`生成，消除静态共边的三角函数残差；正面积判定仍严格`>0`。详见[M07实现与验证](../validation/M07-命令查询与显示实现验证.md)。JSON、默认参数、原验收编号和性能阈值不变。

## C11 已知版本差异的处理

### 水汽暂存与恢复（1.10设计修订）

详细规则、模块边界和新增验收以[M06-F01水汽暂存与恢复](M06-F01-水汽暂存与恢复.md)为准。它取代1.8/1.9中的立即排开及无目标Faulted预期，不改其余物理误差、故障发布、预算和通知租约。暂存记录身份与Tick实例映射由M02维护；暂存蒸汽每Tick寿命恰更新一次；普通查询/显示只消费活动材料；Create/Reset的Tick0暂存为空。本轮已同步源码、共享接口和受影响测试，结果见[实现记录](../validation/M06-F01-实现与验证.md)；历史验证报告仍是旧行为证据，新验收不得继承旧通过数量。

### M08接入说明（1.9合同内实现补充，2026年10月6日）

编辑模型`Data/SceneMaterialData`仅保存材料ID/坐标、固定与初燃标记；`Snapshot`复制并按y/x冻结，导出经M01统一序列化及严格加载校验。`SceneEditingDocument`失败加载不替换模型，保存前通过M01 `ReadSourcesWithScene`校验目标两个引用文件与当前定义一致，只原子发布scene.json。文件失败报告本次唯一临时路径；清理仅删除该单一文件。

M00的`GridSelection`统一编辑点转格、格中心及矩形中心选格，边界使用Unity实际可表示float坐标，不放宽越界范围。M08覆盖/擦除清除两个旧标记；标记和容量失败整组选格不变。标准预览复用M07B只读显示副本及精确像素摄像机，不创建模拟世界；GUI点通过DPI与实际像素换算，倍率不改变初态。

试玩通过M02 `WorldHost.CreateWorld/Step/ResetWorld/CloseWorld`与正式工厂；初始化自由分量仍由M04–M06处理，Editor没有固定点补丁或自有物理推进。M07B显示层参数用于工具的专用摄像机隔离，默认生产显示层保持0。Editor程序集仅包含Editor平台，运行模型不依赖UnityEditor。按编号结果见[M08记录](../validation/M08-编辑器与保存实现验证.md)。JSON、默认数值、性能阈值和现有公开IWorld接口不变。

历史起点的 packages-lock 曾间接解析 Newtonsoft 3.2.2，主方案要求直接锁3.2.1。该差异已由M01处理：manifest直接声明3.2.1，Unity包管理器解析的lock同为3.2.1、depth=0，严格加载回归已有证据，见[M01执行记录](../validation/M01-执行结果与交接.md)。当前使用3.2.1，不再将该项列为待安装或待决策。

后续若依赖约束要求升级，由M00记录最小技术修订并同步主方案，再通过Unity包管理器固定实际解析版本及运行严格加载回归；禁止手工伪造lock、回退既有包或同时保留矛盾的依赖结论。

文件Schema用“米”描述长度，主方案用“Unity世界单位”；实现统一按cellSize对应Unity世界单位换算，测试默认按1世界单位=1游戏米解释，不修改数据数值。
