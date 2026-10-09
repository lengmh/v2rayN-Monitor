# 架构与数据契约（计划）

状态：待 S0 实机冻结。以下是实现边界，不表示对应代码已存在。总计划见 [development-plan.md](development-plan.md)。

## 进程与界面

一个普通权限 Windows 宿主进程承载托盘、采集循环、SQLite 写入及回环 HTTP 服务。HTML 静态资源与宿主一起发布，默认在系统浏览器打开。不要要求浏览器一直开着。首版不启动 Windows Service，不引入 Electron，不要求 WebView2。

HTTP 仅绑定 `127.0.0.1`，启动时确定端口并由托盘打开正确地址。只靠随机端口不是访问控制；会话令牌及 Host/Origin 检查保护读写接口，访问链接不得长期携带明文令牌。可以用 URL fragment 携带一次性启动凭据，页面交换短期会话后清除 fragment；实现需验证外站脚本不能读取或操作账本。日志不能输出启动凭据。

## 采集适配器

适配器返回 `instance_id, session_id, config_generation, observed_at_utc, scope, node_key?, outbound_tag?, upstream_bytes, downstream_bytes, value_kind, quality`。`value_kind` 明确累计值或已证明的区间增量；速率估算单独标识。不得仅凭字段叫 `up/down` 就当作累计计数。

Xray 优先原始 metrics；sing-box 根据实际构建采用已证明的统计路径。现有 gRPC stats 查询只读且 `reset=false`。不要为了集成盲目新增 V2Ray API，因为接口是否启用、构建标签和与已有统计的交互须由 S0 证明。

节点映射包含 `instance_id + IndexId` 与显示快照，不长期保存连接凭证。运行配置只为确认端口、标签映射和配置代次读取；无需复制完整配置到 Monitor 数据库。first_attach、core_restart、mapping_change 等事件写成诊断，不冒充流量。

## 最小数据库

| 数据组 | 内容 | 首次引入 |
|---|---|---|
| instances / nodes | 实例、IndexId、显示名、删除状态 | S1 |
| collector_sessions / cursors | 会话代次、最后已提交累计值、观察时间 | S1 |
| local_intervals / local_minute_totals | 有界诊断区间、长期分钟级本机账本 | S1–S3 |
| daily_totals | 可由下层重建的日汇总 | S3 |
| collection_events / gaps | 断连、重置、未知归属与未分时恢复量 | S2 |
| provider_records / import_batches / import_batch_records | 服务商独立账本、批次引用关系 | S6；不得预先实现 |
| settings | 实例路径、统计时区、界面偏好、数据目录 | 按需要 |

具体表名可在实现时收紧，不以表数量作为架构目标。单写入路径即可；使用 SQLite 原生事务，不造另一层提交协议。写入增量、更新汇总、推进游标必须同事务。失败时数据库保持上一个已提交采样。

默认保留分钟或小时历史和日汇总；每秒诊断短期保留并可关闭。清理诊断不删除仍需用于年/月下钻的分钟/小时汇总。数据库增长以实测数据规模说明，而不是承诺固定文件大小。

## 时间与汇总

时间以 UTC 存储，查询时按所选统计时区得到本地边界。周从周一开始；范围为半开区间 `[start, end)`，避免重复边界记录。日看小时、周/月看日、年看月；自定义范围可选择按日/周/月/年分桶。

短采样横跨边界可作守恒的整数分配，并标注为分时近似。缺口内恢复的总量只有在计数连续性被确认后才能保留；无法定位具体日期的部分放到“未分时”而不是随意填补图表。每个查询都返回缺口/估算/未分时说明，UI 不以零替代未知。

统计 API 传字节十进制字符串，避免前端超过安全整数后悄悄舍入。GB=10^9 bytes，GiB=2^30 bytes；单位只在显示层选择。

## 最小 API 方向

| 接口 | 用途 |
|---|---|
| GET /api/status | 采集状态、内核、最近采样和质量提示 |
| GET /api/nodes | 节点列表和状态 |
| GET /api/summary | 选定来源、节点和时间范围的总量 |
| GET /api/series | 按分桶粒度返回序列 |
| GET /api/records | 后端分页明细，不一次加载全年数据 |
| GET /api/export | 同口径导出；必要时流式写出 |
| POST /api/settings | 显式设置变更，受会话与来源检查保护 |
| POST /api/imports/preview、commit | S6 才增加的预览与事务导入 |

路径为计划，不要求为了追求 REST 完整性添加无关端点。HTML 节点名称/导入内容按文本显示，不插入可执行 HTML。CSV 导出中防止把名称作为表格公式执行。

## TrafficMonitor

S7 才增加一个由宿主原子替换的本地 `summary.json`。插件只读简短摘要，不直接写库、不自行采集、不保存令牌。包含更新时间和 schema 版本，过期时显示陈旧。插件仅显示本机今日/本月和节点信息，复杂功能在 HTML。

## 服务商来源

导入 records 独立保存服务商原始量、计费量、倍率、原始时间及解释时区。IP/设备/方向未提供即为 null。设备未知不能变成本机；本机缺失计费倍率不能填 0。

同文件按哈希防重；重叠文件按被明确验证的多重集合语义比较，不宣称所有无 ID 账单都能无误去重。计费修订和记录数变化需提示冲突。共享记录的批次撤销使用引用关系，不粗暴删除其他有效批次仍拥有的记录。

## 证据与待验证项

架构输入见 [sources.md](sources.md)。最关键的待验证项是目标内核的累计计数/单位、出站范围、节点映射、统计 API 并发读取影响，以及第一主内核的实机环境。它们是 S0 的任务，不是 README 可以提前写成支持承诺的事实。
