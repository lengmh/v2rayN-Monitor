# 多范围账本 v2 迁移提案

状态为 proposal，仅用于 #18 / S5.6 的审阅与隔离验证。生产 `Ledger` 仍使用 v1，本文件和 [SQL](scope-ledger-v2.sql) 不由应用加载，也未执行真实迁移。提出 Schema 不等于批准真实账本变更。

## 目标与选择

同一真实实例需要保存多个成员的独立游标，也需要保存 business、excluded、unknown 等独立维度。v1 的 `cursors.instance_id` 主键无法表示这些范围。v2 使用范围表和以 `scope_id` 为主键的游标，保留真实 `instance_id`，不添加伪造实例后缀。

实际启动路径将本机记录保存到 `history.db`，服务商记录保存到独立的 `provider.db`，见 `MonitorServer` 构造顺序。本提案只迁移 `history.db`。新增 `measurement_scopes`、`scope_cursors`、`scope_batches`、`scope_minutes`、`scope_unallocated`、`scope_gaps`，复制旧本机历史到明确的 `legacy_proxy` 范围。旧五表保留，便于核对与失败恢复；v2 后续本机写入只进入新表，不能同时向新旧表写入或从两套表累计同一历史。这个选择会暂时增加历史占用，未来清理另行评估，本批不删除旧表。`provider.db` 不执行本迁移 SQL、不更改版本标记或计费列。

v1 没有保存核心类型或计量 basis。不能从节点名称、协议猜测旧量是 Xray、routed-proxy、实际成员或全量业务；全部标记 `legacy_proxy`，说明为「历史活动配置 proxy，具体核心维度未存储」。旧 history node key 和 IndexId 原样保留，不强行升级证据。

## 范围身份与代次

`scope_id` 为本库分配的整数标识，不带节点含义。唯一身份为 `(instance_id,basis,identity_kind,identity_value)`，实例、维度和身份三者均必须来自独立核实的采集上下文。不能从 RPC 返回的名称构造已核实身份。

| basis | identity_kind / identity_value | node_key / outbound_tag | 查询语义 |
|---|---|---|---|
| `legacy_proxy` | `legacy_node` / 原 node_key | 原 node_key / NULL | 保留旧历史口径 |
| `actual_member` | `index_id` / 已核实 IndexId | 原实例+IndexId 节点键 / 当前 tag | 按已核实逻辑配置分账 |
| `actual_member` | `outbound` / 下述配置身份元组 | NULL / 原 tag | 独立成员身份，不能冒称订阅节点 |
| `routed_outbound` | `outbound` / 下述配置身份元组 | NULL / 原 tag | 路由组或出站维度，不能冒称实际成员 |
| `business`、`excluded`、`unknown` | `aggregate` / 与 basis 相同 | NULL / NULL | 已知代理业务、direct 排除、归属未知字节 |

独立 outbound 的 `identity_value` 为 UTF-8 JSON 两元素字符串数组 `[configurationContentDigest,outboundTag]`。内容摘要来自独立读取的生效配置，使用已有 `XrayParser.Hash`，不使用时间戳、PID 或节点名；JSON 编码避免分隔符碰撞。配置内容变化产生新独立身份，核心仅重启且配置字节未变则保留历史身份。目录尚未证明跨配置的逻辑节点相同，因此不会自动合并这两段历史。

配置内容摘要、原 tag 和 IndexId 仅在本机保存，属于敏感身份材料，不能作为公开日志、上传诊断或真实 fixture。不保存端点、口令、完整配置或凭据摘要。用于字节归属的实际成员须同时满足已核实配置、支持类型、无依赖及有效统计证据；目录候选本身不证明计数可用。

游标分别保存 `session_id`、`generation`、`at_ticks`、原始整数累计、`interrupted` 和 `continuity_valid`。核心/配置/身份边界改变后建立新基线；即使新累计大于旧累计也不跨边界相减。已核实 IndexId 的配置变更保留同一历史范围，但 generation 改变强制基线。独立 outbound 内容变更换范围，旧范围仍保留历史。

## v1 映射

| 原表 | 新表 | 映射及核对 |
|---|---|---|
| `nodes` | `measurement_scopes` | 每个原 node_key 一条 legacy 范围；原 nodes 不变 |
| `minutes` | `scope_minutes` | 所有 ticks、bytes、approximate、synthetic 原样复制 |
| `unallocated` | `scope_unallocated` | 原 id、起止 ticks、bytes 和 quality 原样复制 |
| `gaps` | `scope_gaps` | 原 id、时间、reason、is_open 保留，origin=legacy |
| `cursors` | `scope_cursors` | 保留累计、时间、session、generation、interrupted；continuity_valid=0 |
| `cursors` | `scope_batches` | 按真实 instance 保存最大观察 ticks；迁移 fingerprint=NULL，不承诺旧批次可重复识别 |
| 独立 `provider.db` 的 `import_batches`、`provider_records` | 独立库保留 | 文件及逐行值不变；精确十进制仍为 TEXT，不导入本机新表 |

原 `gaps.node_key=NULL` 没有实例或出站证据，映射后 `scope_id=NULL`，保持历史全局未知。不能把这类缺口分摊或复制给各成员。旧缺口的 is_open 保留，但 origin=legacy，未来不能让一个成员成功采样关闭所有旧缺口。页面单独披露历史遗留开放缺口，关闭须有单独可记录的证据，本批不自动关闭。

旧游标原值保留用于审计，但不能证明新的成员/basis 连续性。迁移将 `continuity_valid` 置零，v2 首接各范围只建基线，旧累计不回填。原节点键不变，新成员 scope 与 legacy scope 不共享游标。旧 Xray 后续仍可使用 legacy_proxy 范围，但迁移后的首次读数也建立基线。

迁移预检须核实 user_version 恰为 1、原五表的列名/类型/主键与当前 v1 一致、foreign_key_check 和 integrity_check 通过，以及本机字节/ticks 为合法 int64。原 v1 某些列未检查 typeof；存在 REAL、负数、非法标记、孤立历史或 cursor.instance_id 与 node 实例不一致时，整次迁移拒绝，不能转换、截断或跳过。目标含服务商表或不是历史库时拒绝，不能把 `provider.db` 当作历史库迁移。服务商库缺失不阻断本机迁移；存在时只在已获授权范围内核对文件身份和只读快照，绝不调用 `ProviderImports` 构造器来替一般只读核验，因为构造器可能初始化/迁移其表。

## 迁移执行契约

实际执行前需要具体授权，并正常停止所有使用该账本的应用写入。本批仅在新建合成库中执行以下契约。

1. 核实目标库完整绝对路径与真实文件身份，确认关闭所有写入者。不得通过当前活动应用连接迁移。
2. 使用 SQLite backup API 生成新文件历史快照，核对快照 integrity_check、user_version、逐表行数与内容；不能只复制活动 WAL 库的主文件。若授权同时备份服务商库，单独生成它的快照，不能与本机历史合并。
3. 在目标连接启用 foreign_keys 与项目既有同步设置；预检 v1 结构和全部支持字段。预检失败时停止，不写 DDL。
4. 开始单个写事务，执行完整提案 SQL。SQL 自身不开始或提交事务，由调用方负责回滚。
5. 按原业务主键逐行核对新表到旧表的映射，禁止只比较总量、行数或把重建后的 rowid 当作历史身份。核实旧五表未变，所有外键与完整性检查通过，游标 continuity_valid 全为零。独立服务商库不参与写事务，核对迁移过程未更改其文件/记录。
6. 只有全部核对通过才提交事务。任何 DDL、INSERT、约束、核对、I/O 或溢出失败都回滚，不能吞错后推进 user_version。
7. 成功后，新应用只读取新本机表，服务商表仍独立。已有 `Ledger` 对 user_version=2 明确拒绝，且现有启动流程先打开 Ledger 再打开 ProviderImports。新版须保持版本门禁顺序，不能先清空/重建库后继续。

提案 SQL 的 user_version=2 位于同一事务中，核对失败回滚后须仍为 1，新增表和索引也应全部撤销。重复执行不是幂等操作：只接受 v1 和尚不存在的新表，v2 或部分未知结构都拒绝。无历史空库也需通过同一结构核验。

## 批量记账契约

每批绑定一个真实 instance、观察时间及前后稳定的核心/配置快照，包含各已注册范围的完整上下行对或不可用原因。同实例已知游标若未出现在当前目录，必须在本批显式退休并持久失效连续性；不能让旧范围在 A→B→A 后恢复不确定增量。跨实例范围、重复 scope、冲突观察时间或非法累计拒绝整批，不默默丢项。

一个事务内完成范围注册/名称更新、已知字节、游标、范围缺口以及退休/连续性失效。原始 RPC 不是跨维度同刻原子快照；「批量原子」只承诺本地持久化全成或全败。

`scope_batches` 保存每实例的最后完整批次时间及规范化输入指纹。同一时间只有完整输入与指纹都相同才返回 duplicate；倒退或不同输入拒绝整批。指纹包含核心/配置代次、所有范围身份/名称/累计/不可用原因及退休清单，规范化范围顺序后在本机计算；不上传原输入或指纹。这样，范围中断但游标未推进期间，也不会接受早于最近已提交批次的输入。批次时间和指纹与所有范围数据同事务更新。

| 输入/边界 | 对该范围的行为 | 对其他完整范围的行为 |
|---|---|---|
| 首次出现或 continuity_valid=0 | 建立基线，关闭该范围 collector 缺口；不回填 | 正常处理 |
| 相同 session/generation 且正常短间隔 | 用原始 int64 差分并分配分钟 | 正常处理 |
| missing/duplicate/invalid counter 或接口中断，身份仍可靠 | 保留游标、interrupted=1；开放/延长该范围 collector 缺口 | 正常处理 |
| 身份/配置/核心变化、计数回退、退休 | 持久失效或重建基线，保存该范围边界；不能差分恢复 | 正常处理 |
| 连续性可靠，中断恢复或间隔超过既有 10 秒 | 差分保存为 recovered-unallocated，不伪造分钟精度 | 正常处理 |
| 与已有游标观察时间冲突/倒退 | 拒绝整个批次，不推进其他范围 | 全部保持原状 |
| 同时刻所有范围/元数据/缺口均完全相同 | 可返回 duplicate，不重复累加；局部相同不能掩盖其他冲突 | 全批统一判定 |
| 任何写入/约束/checked 溢出失败 | 所有范围字节、游标、缺口及元数据回滚 | 全部保持原状 |

每条新 collector 缺口有 scope_id，单个范围成功只关闭该范围的 origin=collector 开放缺口，不能使用 v1 的全局 `UPDATE gaps ... WHERE is_open=1`。未知某范围不能清空其他游标。missing 仍保留计数缺口；配置中不采集的范围另以 coverage 缺口披露，不能用 0 计数替代。

分钟分配复用 v1 的累计整数比例与最后片余数，保持字节守恒；跨分钟仍标 approximate。先计算本片差值，再用 C# checked int64 将差值加到原分钟值，避免对「原分钟值 + 全段累计 - 已分配量」的中间量误报溢出，同时拒绝 SQLite 自动提升为 REAL。查询聚合用 BigInteger 并返回十进制字符串，不使用 SQLite SUM 合并可能超过 int64 的多行。本机与服务商始终分离。

## 查询与页面前置

新版查询须显式选择一个 basis，默认旧 API 仅查 legacy_proxy。节点明细、范围筛选、CSV 导出与质量标记一起携带 scope/basis；同名不同身份保留独立条目。actual_member 可以在同一维度内汇总互斥成员；business 单独作为核心已核实代理业务量。不能把 legacy_proxy、business、actual_member 或 routed_outbound 相加当全部使用量，不能再把 excluded 或 unknown 加入已知代理业务。

identity_kind=outbound 的跨配置范围不承诺逻辑节点延续；没有证据时不能自动合并到同名成员。恢复量只有完全包含查询区间才进入确定总量，部分重叠单列且不按比例摊分；缺口披露不能被明细分页裁掉。

实现生产查询、旧 API 兼容、跨午夜/时区及分页仍是后续验收，不由本批 SQL 或临时记账原型替代。

## 回滚与恢复

事务尚未提交：回滚同一事务并核对 user_version=1、旧表与历史备份一致、新表不存在，独立服务商库未变。磁盘或进程异常后先检查 SQLite 恢复结果，不能直接重复迁移未知结构。

迁移已提交但尚未写入 v2 新数据：停止新应用，保留完整失败库与日志，用经核对的迁移前 backup 在独立恢复路径启动旧应用。不得改 user_version 或 DROP 新表来伪装成功降级。

已经写入 v2 新数据：同样保留失败库。迁移前 backup 只能恢复到快照时刻，不能包含后来的成员/业务记录；不得将各维度相加塞回旧 proxy 历史。实际恢复前说明新增历史的处理及损失边界并取得授权。新记录导出或后续兼容恢复另行设计，不宣称无损自动降级。

## 本批验证边界

验证使用生产 Ledger 和 ProviderImports 分别创建合成 v1 历史库与独立服务商库、同一提案 SQL、项目 Microsoft.Data.Sqlite，以及 TempFiles 中独立记账原型。覆盖空库/历史/精确计费值保留、非法旧结构拒绝、迁移中途失败全回滚、多成员基线/中断/重开/重启、局部缺失、整批失败与维度分离。原型不接入 Host/Server，也不执行真实迁移；完整目录退休检查、正式查询/分页、真实进程重开、断电恢复和实机账本尚未验收。实际结果及未覆盖点见 [STATUS](../STATUS.md)。
