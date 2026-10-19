# 核心内部探测独立计量

任务 S5.8，2026-10-15。用户已确认计费诊断结论，并要求后续覆盖 `urltest` 等内部接口，单独统计以便排查。真实账单和节点仅保留本机。

2026-10-19 用户将本项确定为 [扩展版可选能力](../../README.md#能力分级)，同时适用于 sing-box 和 Xray，不作为基础版、完整版及 #15 / #18 普通流量统计的必需验收项。完整版只要求已支持模式的普通统计，Xray 可保持原生；需要独立探测字节时再按核心分别接入探测能力。原生探测状态与独立探测字节区分，缺少字节接口继续保留未知。

已有 sing-box test.5 合并普通统计和探测补丁，保留已有能力与历史，不为分级重装核心、降级账本或改变启动模式。Xray 探测实验核心保持可选，新增启用仍按具体范围授权。

## Xray Observatory／Burst Observatory 扩展

2026-10-19 新增 [Xray 实验扩展](../../extensions/xray/README.md)，固定上游 `5ca6f4b7d4dc20a881d4330e498892697627ec0c`，Go 1.26.8。原安装二进制的公开源码身份无法核验，实验包不宣称等价，不自动替换。普通 Observatory 和 Burst Observatory 在强制 tag 的实际返回连接上分别累计读写；保留原请求、超时、TLS 和调度，读取统计不触发探测。目标 HTTP／TLS 失败前已返回的字节保留；返回连接前消耗、Burst 额外直连连通性检查及其他后台接口仍未覆盖。

同一 `/debug/vars` 新增 `monitor_probes`，含 `version:1`、`kind:"observatory"`、逐 tag `members` 及 `business`／`excluded`／`unknown` 三个独立 `totals`；上传／下载为整数 `uplink`／`downlink`。原生计数名使用 `monitor>>>probe>>>observatory>>>...`，标准 `stats` 导出排除这些新名，原出站计数不变。已知零来自真实处理器目录和显式零计数，缺失、重复、负值和溢出保持未知，不从延迟／次数估算。

Monitor 只在核心 SHA256 已核验时读取此版本化结构，从与业务相同的一次有界响应生成联合批次，复用 v3 `probe_urltest_*` 的 HTTP 探测历史。成员、总量和原出站分别查询，名称共用原名／组标签投影，不修改历史或 Schema。Xray 原出站计数与探测流字节位于不同计量层，可能重叠，不能相加或直接相减；普通 Xray 未提供此接口时出站继续采集，独立探测量保持未知。

固定原生回归、仅回环的两类核心请求、实际响应到隔离账本及干净源码重建已核实；精确提交 CI 和真实客户端接入分别记录在 STATUS／本机检查点，当前未部署。

## 首批实现

固定源码与已有三层补丁不变，叠加 `probe-stats.patch` 构建 `1.14.1-monitor-test.5`。仅显式 `monitor_members` 启用时注册探测计数；listen-only 保持原路由统计。首批仅保留 test.2/test.4 白名单，后续 test.5 白名单与实机接入见下文。

公共 `common/urltest.urlTest` 每次实际拨号创建独立计量上下文。策略组原生启动/周期探测、Clash 单节点延迟 API、守护服务的公共 URLTest 调用均可进入钩子。AnyTLS 预热与正式探测分别计量，不把一次顶层调用当成一次 HEAD。

Clash 延迟 API 原来使用 Background 上下文；补丁只附加原核心统计服务，保留既有超时、TLS 信任、目标及延迟算法。统计读取本身不触发探测。

| RPC 前缀（后接 `>>>traffic>>>uplink/downlink`） | 语义 |
|---|---|
| `monitor>>>probe>>>urltest>>>member>>>{tag}` | 已注册独立代理成员的探测流字节 |
| `monitor>>>probe>>>urltest>>>business` | 已知代理探测总量，只计一次 |
| `monitor>>>probe>>>urltest>>>excluded` | direct 探测量，单列排除 |
| `monitor>>>probe>>>urltest>>>unknown` | 链、未注册或无实际成员证据的探测量 |

每次连接独立绑定返回的实际叶成员，不读取 `Now()`，不借用预热连接的成员，不与业务/路由量相加。Monitor Core 新增 `ReadProbesAsync` / `TryReadProbesResponse` 和 `V2RayProbeStatistics`；复用只读 `reset=false`、HTTP/2、数值回环、65,536 bytes 上限和严格整数解析。缺失、重复及越界独立 unavailable；旧核心没有计数，不能转成零。

## 字节口径

计量点是成功返回的出站连接，上传为 Write、下载为 Read。HTTPS 包含目标 TLS 握手及 HEAD 交换；HTTP/TLS 失败前传输的字节保留。业务计量的入站方向与此相反。

不包括返回连接前的 DNS、TCP 建连、代理自身握手、外层填充、TCP/IP 头及重传。因此不是完整隧道量或服务商计费量；未返回连接的拨号失败消耗仍未知。普通入口的测速下载仍属业务字节，不能按大小或名称猜用途，也不归因其他客户端流量。

## 第二批：v3 探测历史账本

`ScopeLedger.CreateNew(path)` 默认仍创建 v2；显式 `includeProbes: true` 才创建 v3。打开现有文件只核对版本与对应 DDL，不自动升级。v3 复用原累计差分、分钟分片、未分时恢复、缺口、退休和原子批次，新增四个互斥查询维度：

| basis | 含义 |
|---|---|
| `probe_urltest_member` | 已核实成员的 URLTest 探测 |
| `probe_urltest_business` | 已知代理探测总量 |
| `probe_urltest_excluded` | direct 探测 |
| `probe_urltest_unknown` | 未核实归属的探测 |

成员维度与总量各自查询，不相加；业务维度的查询不包含探测。成员仍按可靠的 IndexId 或配置摘要/tag 建立身份，名称不参与身份关联。`ScopeCollector.ProjectWithProbes` 生成业务/探测联合完整批次，`Commit` 在同一事务保存各自字节、游标和缺口。未启用、缺失及坏计数保持 unavailable，不能补零；新探测首次只建基线。

独立 `ScopeLedgerMigration.MigrateProbes(historyPath, backupPath)` 仅接受完整支持的 v2。先只读预检、创建全新 SQLite backup，再在独占写事务内核对备份与当前库。按 [v3 SQL](scope-ledger-v3.sql) 重建范围目录，保留全部 scope ID 和历史；v1 原表、v2 的分钟/未分时、游标、批次水位/指纹、缺口及未改 DDL 均逐项对照。完整性/外键检查通过后才提交版本3，失败回滚 DDL、版本及数据。

SQLite 目录表重建仅在离线迁移连接中于事务前关闭外键执行，事务内检查所有外键，退出后恢复外键执行。全新空库初始化和正常记账保持外键开启。原业务游标连续性不因账本格式升级失效；新核心/配置代次变化仍按原有 guard 重新建基线，不能据此跨代回填。

v1 需先按原受控执行器升至 v2；不合并两步或默认自动升级。迁移要求调用方先停止所有写入者，真实迁移另行授权。v3 写入新历史后不能直接用旧 v2 程序打开，恢复旧备份会缺少迁移后的新增记录，不能称为自动无损降级。provider.db 独立且不迁移。

## 后续接入

第三批已接入 Windows 同一只读 RPC 响应的业务/探测联合读取及页面。`--probe-ledger` 显式创建全新 v3 或打开已受控迁移的 v3；普通 `--scope-ledger` 使用 v2，模式错配时拒绝启动，不自动迁移。开机启动命令保留模式和目录，实际启动项不由本批修改。

既有认证 `/api/scope-history?basis=probe_urltest_member` 保留；页面复用 `/api/history`、`/api/nodes`、`/api/export`，通过显式单个探测 `basis` 查询/导出，未指定时仍为业务。新增「探测流量」入口分别查看成员、代理总量、直连及未知归属，沿用非零明细、整数 bytes 和恢复/缺口说明。`earliestRecord` 是所选维度/节点最早保存区间，独立于查询日期；分钟记录使用分钟边界，不当作精确首次基线。账本未启用或核心不支持时，页面明确标记未知。

成员模式固定接受 CI `38751295368` 的 test.5 EXE SHA256 `595B4B3FEDE70FC23CA645DCF7B5A4AB6F91E5E7938DB0CA77D50EB0F8376695`；test.4 及默认 test.2 摘要保留。第三批核对并保存 artifact 外层摘要、内层 EXE 和 provenance，未自动部署。不同摘要需另行核对，不能仅凭 test.5 版本字符串接受。

隔离目录写权限已恢复；Release 构建零警告零错误，Core 336/Server 38/前端 56/浏览器 17 项通过，日志、失败修正及主代理目视的桌面/手机合成截图见 STATUS。`6c280ca` 的 Monitor push/PR CI 与扩展 PR CI 已核对成功；第四批真实接入见下文。

页面单列探测量、起点和覆盖边界；名称对照候选与历史覆盖说明继续完成。

## 第四批实机接入

2026-10-16 先备份并对v2副本演练迁移，再离线安装固定test.5、重开v2rayN。连续 3 轮生产只读发现均返回全部 业务/探测范围，探测能力 available。

确认 Monitor 已停止并取得同数据目录 mutex 后，创建最新备份并调用受控执行器，将原成员 history v2→v3。保留 72 个既有范围及全部旧值；独立只读检查核对 11 张历史表、provider 两表和设置，探测历史仍为空。初步快照不代替最新停机备份，provider 不参与迁移。

启用前提醒后，启动 `6c280ca` push CI 包，显式 `--probe-ledger` 接回原数据目录。采样批次中，首批不回填，后续 相邻区间全部 当前范围的累计差与账本增量一致。业务和直连有增长，本段探测增量为零；没有主动测活，非零探测事件仍需被动核对。当前范围无开放缺口，历史开放缺口保留。

实际网页和新探测资源 HTTP 200，未认证接口 401；新探测页的真实目视确认待用户。实际启动项未改，已迁 v3 需使用配套 Monitor/模式，不能直接用旧 v2 预览打开；不自动还原备份丢弃新增历史。真实数据、备份、离线回执和逐范围样本仅留 STATUS 指定的本机 TempFiles，S5.8 保持 in_progress。

DNS、远程规则集等内部请求逐条核实实际路径、计量位置和归属后接入；未覆盖明确披露，不用推测的后台总量填补差额。

## 验证与部署

使用纯回环合成 HTTP/TLS、AnyTLS/VMess，不触发真实节点测速。核对预热双测、实际返回成员、失败前字节、并发、未启用保持旧行为、业务不被污染；原生启动/周期与延迟 API 用隔离核心副本另验。

固定工具链、归档、Windows 标签与依赖不变，四层补丁重建并核对必需测试。CI 不自动部署或更新白名单。启用新预览前提醒用户；新的运行文件替换、重载及真实迁移需具体授权。
