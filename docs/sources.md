# 来源与验证记录

核读日期：2026-10-09。本文仅使用上游源码与官方文档，不转载真实用户流量数据。固定源码提交用于准确复查，不宣称它必然等于用户安装版本。

## R1 · v2rayN Xray 统计路径

[StatisticsXrayService.cs](https://github.com/2dust/v2rayN/blob/5ea8ae6420a4d89ff2945c35f4b87c6c1c307a6b/v2rayN/ServiceLib/Services/Statistics/StatisticsXrayService.cs)

该实现请求回环 `/debug/vars`，读取 outbound 的 uplink/downlink，以 ProxyTag 识别代理项；在 GUI 统计路径中作 1024 缩放并差分。这支持“直接读取原始计数再独立记账”的优先级，不证明所有版本端口相同或所有标签都对应一个最终出口。

## R2 · v2rayN sing-box 统计路径

[StatisticsSingboxService.cs](https://github.com/2dust/v2rayN/blob/5ea8ae6420a4d89ff2945c35f4b87c6c1c307a6b/v2rayN/ServiceLib/Services/Statistics/StatisticsSingboxService.cs)

该实现读取回环 WebSocket `/traffic`，解析 up/down，向 GUI 统计传递前有 1000 缩放。不能只凭该代码确定目标 sing-box 构建的 API 返回值是何种累计/速率语义，也不能推断其必然只含当前代理节点。

## R3 · v2rayN 统计归属与跨日

[StatisticsManager.cs](https://github.com/2dust/v2rayN/blob/5ea8ae6420a4d89ff2945c35f4b87c6c1c307a6b/v2rayN/ServiceLib/Manager/StatisticsManager.cs)

管理逻辑按配置 IndexId 累加并按日重置 today 值；SaveTo 执行批量更新。本次未穷举所有调用者，所以不把“只在退出写数据库”作为产品必须依赖的事实。独立历史和精确单位仍需自行保存与核验。

## R4 · TrafficMonitor 插件接口

[PluginInterface.h](https://github.com/zhongyang219/TrafficMonitor/blob/930f17533d6098989ebad62f210aa97d75ef174b/include/PluginInterface.h)

上游接口包含显示项目、周期数据请求、鼠标事件与设置对话框。本阶段只据此确认薄插件形态可讨论；开发时使用目标正式版对应接口，不把主分支 API 当作所有发行版都支持。

## R5 · Xray 官方指标与统计文档

[Metrics](https://xtls.github.io/en/config/metrics.html) · [Statistics](https://xtls.github.io/en/config/stats.html)

官方文档描述 metrics 的监听配置与统计启用要求。Monitor 不应假设所有用户已经启用它，更不能为方便读取自动暴露公网监听。

## R6 · sing-box 官方统计接口

[V2Ray API](https://sing-box.sagernet.org/configuration/experimental/v2ray-api/) · [Clash API](https://sing-box.sagernet.org/configuration/experimental/clash-api/)

文档提供可选 API 和统计配置。目标构建是否包含、是否启用，以及与原有统计能否共存仍需实机证明；这是 S0/S5 工作，而不是已支持承诺。

## R7 · .NET 官方支持周期

[.NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core)

核查日官方表将 .NET 10 列为 LTS、支持至 2028-11-14；.NET 8/9 支持至 2026-11-10。因此新项目改选 .NET 10。实现时锁定可用补丁，不在本规划硬编码下载旧补丁。

## R8 · GitHub CLI 初始化依据

[gh repo create](https://cli.github.com/manual/gh_repo_create) · [gh api](https://cli.github.com/manual/gh_api)

官方 CLI 支持非交互指定 --public、--source、--remote 和 --push。初始化脚本借助本机已经登录的 CLI 执行，不提取、保存或要求用户粘贴 token。本次仓库发布未通过该 CLI 脚本执行；仓库发布状态见 [STATUS.md](STATUS.md)。

## 用户格式约束（不公开真实附件）

本轮只读本地附件的 CSV 表头，确认六字段：`node_name, rate, origin_traffic, origin_x_rate, traffic, log_time`。公开仓库仅含人为合成的 fixtures，没有真实节点名称、流量记录、IP 或设备信息。此前聊天中出现的记录数或日汇总不作为开发验收真值。

## 初版实现补充核对（2026-10-09）

用户目标版本为 v2rayN 7.25.1，其固定提交为 `77c462ec7b5adb8d0ad65c0a3b6452cd3c6cb70a`。本次只核读源码，未读取真实用户配置。

- [统计设置和 metrics 生成](https://github.com/2dust/v2rayN/blob/7.25.1/v2rayN/ServiceLib/Services/CoreConfig/V2ray/V2rayStatisticService.cs)：统计或速度显示开启后生成动态回环 metrics。
- [Xray 原始读取路径](https://github.com/2dust/v2rayN/blob/7.25.1/v2rayN/ServiceLib/Services/Statistics/StatisticsXrayService.cs)：`/debug/vars`，Monitor 不照搬界面的 1024 缩放。
- [核心启动顺序和命令行](https://github.com/2dust/v2rayN/blob/7.25.1/v2rayN/ServiceLib/Manager/CoreManager.cs)：配置生成可能先于旧核心停止；标准 Xray 命令为 `run -c config.json`。
- [数据根目录与文件路径](https://github.com/2dust/v2rayN/blob/7.25.1/v2rayN/ServiceLib/Common/Utils.cs)：GUI 配置、SQLite 和 binConfigs；环境变量可改变数据根目录。
- [SQLite 数据库路径](https://github.com/2dust/v2rayN/blob/7.25.1/v2rayN/ServiceLib/Helper/SqliteHelper.cs) · [ProfileItem 字段](https://github.com/2dust/v2rayN/blob/7.25.1/v2rayN/ServiceLib/Models/Entities/ProfileItem.cs)：以 IndexId 定位；Password/Username 为当前字段，不能使用旧版 Id/Security 假设。
- [.NET 10 下载与支持](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)：本地验证 SDK 10.0.401。
- [原生 SQLite 旧依赖漏洞](https://github.com/advisories/GHSA-2m69-gcr7-jv3q)：初始依赖审计发现旧本地库受影响，改用 Microsoft.Data.Sqlite 10.0.12 与 SQLitePCLRaw.bundle_e_sqlite3 3.0.5（SQLite 3.53.4），不关闭最终构建的安全审计。

## V2free 7.25.1 精确版本补充（2026-10-10）

此前采用官方 7.25.1 源码作为初版输入；本轮兼容修复改用用户报告的 fork 构建标识逐项核对，不将两者混为同一个二进制。

- [Hysteria2 服务器字段](https://github.com/fqfqgo/v2rayN/blob/95f68d9955c2324518c5e1dc9485d73412102a95/v2rayN/ServiceLib/Services/CoreConfig/V2ray/V2rayOutboundService.cs#L178-L216)及[transport auth](https://github.com/fqfqgo/v2rayN/blob/95f68d9955c2324518c5e1dc9485d73412102a95/v2rayN/ServiceLib/Services/CoreConfig/V2ray/V2rayOutboundService.cs#L572-L578)：协议为 `hysteria`，两处 version 为 2，凭据来自 ProfileItem.Password。
- [协议支持集合](https://github.com/fqfqgo/v2rayN/blob/95f68d9955c2324518c5e1dc9485d73412102a95/v2rayN/ServiceLib/Global.cs#L362-L389)和[核心选择优先级](https://github.com/fqfqgo/v2rayN/blob/95f68d9955c2324518c5e1dc9485d73412102a95/v2rayN/ServiceLib/Manager/AppManager.cs#L666-L679)：AnyTLS 是 sing-box-only，不通过 Xray 适配器推测计量。
- [AnyTLS 配置生成](https://github.com/fqfqgo/v2rayN/blob/95f68d9955c2324518c5e1dc9485d73412102a95/v2rayN/ServiceLib/Services/CoreConfig/Singbox/SingboxOutboundService.cs#L316-L319)：sing-box 的 type/server/server_port/password；这只能证明客户端生成规则，不能证明实际核心版本和计数接口语义。
- 用户报告的活动 Xray 标识为 `26.7.28 ced1f55`。公开上游未能解析 `ced1f55`；官方同版本 tag 当前指向另一提交。因此未将官方 tag 当成该二进制的精确源码证明，也未借用附近的 26.9.9 版本或旧客户端 7.24.4。

本轮仍只创建合成 fixtures。真实配置、订阅、地址、口令及用户目录不进入源码、测试或日志。

### 普通权限进程信息回退

- [QueryFullProcessImageNameW](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-queryfullprocessimagenamew) 与 [GetProcessTimes](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-getprocesstimes)：支持 `PROCESS_QUERY_LIMITED_INFORMATION` 句柄。与 WMI 创建时间在微秒精度核对，输出保持同一精度，不能因取证来源改变而伪造一次核心重启。
- [psutil 7.0.0 的 Windows 实现](https://github.com/giampaolo/psutil/blob/release-7.0.0/psutil/arch/windows/proc_info.c#L436-L474)：有限查询权限句柄配合 `NtQueryInformationProcess` 的 `ProcessCommandLineInformation`（class 60）。这里只作为接口使用的主实现证据；Monitor 未添加 psutil 依赖或复制其实现。
- [Microsoft 对 NtQueryInformationProcess 的说明](https://learn.microsoft.com/en-us/windows/win32/api/winternl/nf-winternl-ntqueryinformationprocess)：内部接口可能改变，应运行时解析。Monitor 从 System32 解析入口，限定返回缓冲区并验证字符串范围；不读 PEB，不申请 VM_READ、调试权限或管理员权限。不可用时返回未知命令行，不能跳过原来的配置归属检查。
