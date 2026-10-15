# 核心内部探测独立计量

任务 S5.8，2026-10-15。用户已确认计费诊断结论，并要求后续覆盖 `urltest` 等内部接口，单独统计以便排查。真实账单和节点仅保留本机。

## 首批实现

固定源码与已有三层补丁不变，叠加 `probe-stats.patch` 构建 `1.14.1-monitor-test.5`。仅显式 `monitor_members` 启用时注册探测计数；listen-only 保持原路由统计。当前 test.2/test.4 白名单不变，候选未部署。

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

当前 Core 具备探测持久化/单维查询与联合投影；既有认证 `/api/scope-history?basis=probe_urltest_member` 可查询显式 v3 中的探测，默认历史、节点列表和导出仍只用业务维度。Windows 采样尚未调用新投影，实际预览仍不会保存探测历史；下一批接入同代次批量读取、Windows 采样、探测能力说明与页面，启用前提醒。

页面单列探测量、起点和覆盖边界；名称对照候选与历史覆盖说明继续完成。v3 本轮只在合成数据库验证，未升级真实 v2。

DNS、远程规则集等内部请求逐条核实实际路径、计量位置和归属后接入；未覆盖明确披露，不用推测的后台总量填补差额。

## 验证与部署

使用纯回环合成 HTTP/TLS、AnyTLS/VMess，不触发真实节点测速。核对预热双测、实际返回成员、失败前字节、并发、未启用保持旧行为、业务不被污染；原生启动/周期与延迟 API 用隔离核心副本另验。

固定工具链、归档、Windows 标签与依赖不变，四层补丁重建并核对必需测试。CI 不自动部署或更新白名单。启用新预览前提醒用户；新的运行文件替换、重载及真实迁移需具体授权。
