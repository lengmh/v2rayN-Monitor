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

## 后续接入

当前仅有核心累计及 Core 读取，未接 Windows 采样、历史、查询 API 或页面；关闭核心后累计仍会丢失。现有 v2 严格 Schema 没有探测维度，不能借用业务 basis 或改 DDL 后继续标为 v2。

下一批先隔离验证带版本标记的账本扩展，区分业务与 `probe:urltest`，保持单维查询、增量/游标同事务、首接不回填、恢复未分时与缺口语义。既有 v1/v2 和独立 provider.db 保留，真实迁移另行授权。页面单列探测量、起点和覆盖边界；名称对照候选与历史覆盖说明继续完成。

DNS、远程规则集等内部请求逐条核实实际路径、计量位置和归属后接入；未覆盖明确披露，不用推测的后台总量填补差额。

## 验证与部署

使用纯回环合成 HTTP/TLS、AnyTLS/VMess，不触发真实节点测速。核对预热双测、实际返回成员、失败前字节、并发、未启用保持旧行为、业务不被污染；原生启动/周期与延迟 API 用隔离核心副本另验。

固定工具链、归档、Windows 标签与依赖不变，四层补丁重建并核对必需测试。CI 不自动部署或更新白名单。启用新预览前提醒用户；新的运行文件替换、重载及真实迁移需具体授权。
