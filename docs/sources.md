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
