# sing-box 扩展计量范围

2026-10-14 用户要求支持多代理出站、策略组、代理链和自定义配置。原 #15 的普通单节点交付保留；新增范围分批实现、独立验收。构建、源码核读和合成链路不替代真实客户端/节点验收。正式发布仍须单独授权。

## 分批交付

| 任务 | Issue / PR | 计量范围 | 当前边界 |
|---|---|---|---|
| S5.5 | #15 / Draft PR #17 | test.2 的路由直接匹配 `proxy` 的累计 | preview.2 代码/隔离回归/CI通过；真实首接基线与持续被动守恒通过，受控实机及页面验收待补；其他出站未知 |
| S5.6 | #18 / Draft PR #20 | 多出站、策略组/urltest 的实际成员 | in_progress；批量 RPC 与成员核心原型已有隔离证据，生产成员读取和多游标账本未实现 |
| S5.7 | #19 / 后续独立 PR | 单次业务总量、链/各跳与自定义配置身份 | planned；实际生效来源和必要迁移先隔离验证 |

S5.5 使用已核实的 test.2，生成配置不写回。只对唯一 AnyTLS `proxy` 比较所选 ProfileItem 的端点、认证、TLS 及默认值，再核对父子进程、标准参数、监听、二进制摘要和文件代次。其他出站不参与 IndexId 证明，也不会按当前选中节点归账。当前仍拒绝目标 `proxy` 的 detour、TUN、非空 endpoints、已有自定义 V2Ray API，以及未核实连接字段。

test.2 在路由层按匹配的 outbound tag 包装连接。路由匹配 selector 或 urltest 时，其成员即使为 `proxy`，也不进入 `outbound>>>proxy`。这是路由范围与成员范围的区别；S5.5 的合计只是已核实部分，不是全部代理流量。其他出站没有使用证据时也不伪造零使用量。

`scope.basis=routed_proxy` 描述这一范围；`scope.isPartial` 与未采集出站/组的数量进入状态 API。样本的覆盖标记和已知字节/游标在同一事务保存，未知范围以 `uncollected_outbounds` 留在现有缺口表；这种覆盖缺口不打断已核实 proxy 的连续差分。历史明细的 `partialCoverage` 和 CSV quality 的 `;uncollected_outbounds` 保留限制，Monitor 重开或回到简单配置后仍能查到。没有 Schema 迁移。

## 后续验收要求

- 多出站独立取数、保存；可靠的 instance + IndexId 分账，只有 outbound 证据时保留独立身份。未知部分不无条件阻断独立已核实范围。
- 组、实际成员和业务总量分别表达，不相加。旧长连接跨切换保留旧成员；组选 direct 排除。可靠归属与累计留在核心处理连接的路径，不能依赖可丢事件消费队列。
- 业务总量只计一次；链和各跳是独立维度。各跳不相加为总使用量，也不宣称业务字节等于加密传输量或服务商计费量。
- 自定义配置按真实生效来源、启动参数和代次保存历史；不保证自动识别订阅节点。脚本、插件和动态注入未验证时明确限定。
- 首接基线、计数回退、核心/配置/映射边界及原子提交沿用现有规则；只有可证明连续的中断才恢复为未分时量。本机与 CSV 不相加。
- 必要 Schema 方案先可审阅、再隔离旧库验证；真实账本迁移另行授权。

## S5.6 第一批：批量取数接口

`V2RayStatistics.ReadOutboundsAsync` 接收由调用方核实的出站 tag，使用一次兼容服务的 `QueryStats` RPC，仍为 `reset=false`、数值回环、HTTP/2 和最多 65,536 bytes 响应。`V2RayOutboundStatistics.Basis` 固定为 `routed_outbound`；`Counters` 按 tag 保存完整的上下行原始整数累计，`UnavailableOutbounds` 保存各 tag 的不可用原因：

- `missing_counter`：一个或两个计数不存在。实际 proto3 零值省略 value 字段仍是零；整个计数不存在不能补零。
- `duplicate_counter`：同一方向重复返回，即使值相同也不采用。
- `invalid_counter`：值超出非负 int64 范围，包括负数的 protobuf 编码。

某 tag 缺失、重复或数值无效不丢弃其他独立完整 tag。压缩帧、截断、无效 wire/UTF-8、超限或 RPC 错误仍使整个响应不可用；不能从不能可靠解析的帧拼出成功结果。tag 区分大小写，拒绝空值、重复请求及统计分隔符 `>>>`。

原 `ReadAsync` / `TryReadResponse` 复用批量解析，但仍只返回 `proxy`，缺失或无效时保持拒绝。生产 WindowsDiscovery、账本和页面未切换到多出站采集；现装 test.2 的文件式扩展仍只启用 `proxy`。批量接口既不证明 IndexId，也不证明组实际成员，不提供相加后的业务总量。一次 RPC 不是所有计数在同一时刻的原子快照，后续采样仍须前后核实身份/配置/核心代次，并将已知增量和游标同事务保存。

隔离实验在 test.2 的同摘要副本中显式配置原生 V2Ray API，以纯回环 SOCKS、echo 和合成路由验证多个 tag。这不是实际客户端的自动多出站接入，也不是 #19 的自定义配置来源验收。路由组选择 `proxy` 时只增长组 tag，证明不能拿路由组累计冒充成员累计。

### 多游标 Schema 迁移前置

当前 v1 的 `cursors.instance_id` 是主键，一个实例只能保存一个活动游标。不能逐次调用现有 `Accept` 写同一实例的不同出站，也不能通过伪造 instance_id 后缀绕开真实实例和归属规则。

后续迁移拟增加独立计量范围表，并将游标主键改为 scope_key：范围表明确保存真实 instance_id、计量 basis、outbound 身份与可空的 IndexId 关联；旧 Xray / routed-proxy 范围保留既有节点键、分钟记录与未分时历史。业务维度与组/链维度不得混入同一总量查询。缺口及连续性失效也要按范围处理，未知某一范围不能清空其他已核实范围的游标。

这是可审阅的迁移方向，不是已执行的迁移。落地前须提供完整 DDL、v1 → 新版映射与回滚步骤，使用合成旧库验证字节/节点键保留、多范围首次基线、部分不可用、重开恢复及批次失败回滚；真实旧账本迁移另取具体授权。本批不改变 user_version、Schema 或任何真实数据库。

## S5.6 第二批：核心实际成员原型

test.3 候选使用 [member-stats.patch](../extensions/sing-box/member-stats.patch)，叠加 test.2 补丁，依旧固定公开源码 `1ac1a339`、Go 1.26.8、Windows 默认标签加 with_v2ray_api。只在隔离配置显式启用 `stats.monitor_members`；文件式 sidecar 和生产 Monitor 白名单未改动。

成员证据来自实际返回连接，ConnectionManager 从最终采用的 TCP/UDP 连接绑定后才开始复制业务字节；直接 handler 分支绑定实际转交的叶成员。嵌套组保留叶证据，并行拨号的未采用连接不会绑定；已有连接的归属冻结，不随后续选择重标。读写路径直接累计，关闭的短连接不依赖事件历史或消费者队列。

原路由统计继续使用 `outbound>>>`，新成员统计使用 `monitor>>>member>>>`；`monitor>>>business>>>` 是已知代理的单次业务量，direct 单列 excluded，链/未知/未绑定前的字节留 unknown。未注册成员、非候选类型和有 outbound Dependencies 的叶不会混给已知成员。第三批另交付新维度读取接口，生产入口与多范围账本仍未接入，不能仅从候选核心通过推断生产已支持。

本地回归包含 4 个顶级 Go 测试、15 个子用例；真实候选副本验证 selector 的旧 TCP/UDP、嵌套组、8 并发 1,200 短连接、切换期间 200 请求、urltest 旧 TCP/新成员、direct 排除和链留 unknown。另用未改动同摘要 test.2 服务端，对照 AnyTLS/TLS 与 VMess：旧连接跨切换，8 并发的 1,200 AnyTLS / 200 VMess 短连接逐原始业务整数 bytes 守恒；候选核心重启所有新累计真实归零。不是 Windows 实际节点或 Monitor 重开/账本验收。

首轮 UDP 夹具只接受 SOCKS IPv4 地址，遇到合法的其他地址形式断开；失败记录保留。只修正临时服务的地址解析，在新目录完成重跑。原始源码归档 + 两层补丁的 1,558 个文件与测试源码一致，重建二进制摘要与联测候选一致；go.mod/go.sum 未变。用户已授权新增独立核心 CI，固定源码归档摘要及 Go 1.26.8，应用补丁后回归并构建 Windows 候选；具体结果见 STATUS。现有 Monitor CI 仅核对 Monitor，不作为 Go 补丁验收，CI 不部署新核心或迁移账本。

## S5.6 第三批：成员读取与配置绑定身份目录

`ReadMembersAsync` / `TryReadMembersResponse` 使用单次相同的只读 QueryStats；`V2RayMemberStatistics` 分别保存 `Members`、`UnavailableMembers`、`Totals`、`UnavailableTotals`。Totals 的键仅为 business / excluded / unknown，不与成员或路由组相加。完全不存在的计数仍是 missing_counter，proto3 省略 value 的实际计数才是零。旧 test.2 路由计数不能作为成员证据；任何不能可靠解析的 wire 仍拒绝整个响应。空成员列表允许仅检查三总量，原路由 API 仍要求非空 tag。

`TryReadMemberCatalog` 的输入必须是独立核实的快照和对应配置字节；校验 sing-box 及配置代次匹配后，投影目录的 InstanceId / Generation / ProcessIdentity 与候选 tag / protocol。既有 AnyTLS proxy 的 IndexId 可沿用该核实快照，其余候选保持空 IndexId，哪怕名称或连接字段相同也不匹配订阅。selector/urltest 不当成员，direct 排除，detour/未支持类型/统计分隔符 tag 留不可用；TUN、endpoints 和重复 tag/字段拒绝目录。目录没有持久账本键，不伪造实例后缀，不保留端点、口令或完整配置。候选存在不证明成员统计已启用或计数可用，也不替代 Windows 进程/监听、完整连接字段与前后快照核实。

test.3 的 `stats.monitor_members` 是显式 tag 数组，不是布尔开关。纯回环新读取器联测使用 `["fixture-a","fixture-b"]`：旧长连接跨组切换保留 A，A/B 每方向 114/97 bytes，business 211、excluded 42，而 route-group 为 253；只在流量停止后比较，不宣称 RPC 跨维度同刻原子。两个新读取器并发重读稳定，核心重启改变代次并真实归零；test.2 路由组 17 bytes 可读，新维度全部 missing_counter。身份目录使用合成快照，不能包装为完整生产多出站发现。首轮夹具误用 bool 的失败保留，修正夹具后在新目录通过。

本批 WindowsDiscovery 仍调用单 proxy API，现装 test.2、sidecar、二进制白名单、v1 Schema、账本与页面均不变。完整多出站自动身份及批量持久采样仍待后续，下一批先完成完整 DDL/映射/回滚方案与隔离账本验证；真实账本不自动迁移。

## 实机边界

test.2 安装与此前用户手动重载已经完成。用户退出旧 preview.1 后，已按授权启动真实 preview.2，沿用原独立验证目录并确认首次基线和持续被动守恒，不重复要求此次部署确认。后续代码、隔离实验、Issue/PR、提交推送及 CI 可推进；新一轮预览/扩展运行文件替换、核心重载、节点切换和真实账本迁移仍需要对应授权。保持原路由和当前节点，不通过删除组或简化配置取得通过。

真实节点、地址、口令、IndexId、完整配置和运行数据库只在本机按必要范围读取。公开记录只包含原创代码、说明与明确标记的合成数据。
