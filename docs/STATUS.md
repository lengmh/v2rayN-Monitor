# 初版实现与验证状态

更新日期：2026-10-09。起始提交：`5167166717c081ff4ad4f43e32d62f276439adcd`。工作分支：`feat/initial-monitor-mvp`。

## 本次交付

已完成一条初版实现链：Windows 托盘 → 只读 v2rayN 7.25.1/Xray 采集 → 原子 SQLite 分钟历史 → 回环 API → 中文 HTML 日/周/月/年与节点统计。CSV 仅为可选独立账本，未代替本机采集。用户已授权提交与 Draft PR；合并和正式 Release 尚未执行。

已实现：

- 最小采样/身份契约、只读解析、进程与配置代次保护、双向重置检查
- 首读基线、重复读幂等、事务失败不推进游标、节点改名保留身份
- 中断/未知归属缺口、连续身份下未分时恢复、分钟守恒分配
- 时区/周一周起点、DST/闰日/跨年汇总、精确字节字符串、分页与安全导出
- 一次性启动凭据、会话/Host/Origin/写请求校验、文本渲染、无遥测和 CDN
- CSV 六列预览/事务导入、同文件去重、拒绝不同重叠文件、保留合法重复
- 独立合成演示、普通权限托盘、单实例、显式开机启动、便携构建

## 已执行验证

环境为 Linux 云工作区，.NET SDK **10.0.401**、Node.js **24**。未连接用户 Windows 电脑，未读取真实订阅或节点数据。

| 检查 | 结果 |
|---|---|
| `dotnet build V2rayNMonitor.sln -m:1 -c Release` | 通过，0 warnings / 0 errors（含 Windows 目标交叉编译） |
| `dotnet test tests/Core.Tests/Core.Tests.csproj -c Release` | 76 项通过，含 47 项 Xray 解析/代次回归 |
| `dotnet test tests/Server.Tests/Server.Tests.csproj -c Release` | 11 项通过，含真实 Kestrel HTTP 和非消息泵 UI 生命周期回归 |
| `npm test` / 生产构建 | 23 项通过，覆盖大字节、时区/DST、生产 DOM、竞态和导入确认 |
| `npm audit` | 0 vulnerabilities |
| NuGet 安全审计 | 已升级旧 SQLite 依赖；最终构建未禁用审计 |
| `python scripts/check_plan.py` / `git diff --check` | 通过 |
| `dotnet publish ... -r win-x64 --self-contained true` | 交叉发布成功，真实 Windows 启动仍未运行 |
| Chromium 视觉/CSP/移动端回归 | 本地运行环境受限，7 项已加入 Linux CI，不能称本地通过 |
| 一次有界主路径审查 | 未发现额外可复现 P0/P1；不代替运行验收 |

本地验证中已修复的关键问题：

1. 目标 7.25.1 使用 flat Xray settings，早期传统 vnext/servers 假设不适用。改为固定版本真实结构，并对所有普通协议补回归；旧形状明确拒绝。
2. 原生 SQLite 旧传递依赖触发高危审计。升级 Microsoft.Data.Sqlite 10.0.12 与 SQLitePCLRaw 3.0.5，不通过关闭审计掩盖。
3. Kestrel StopAsync 内部可能捕获 WinForms 同步上下文，单靠外层 ConfigureAwait(false) 仍会在同步退出时等待 UI。以不运行消息泵的线程重现，再将服务器生命周期入口隔离到线程池，保留回归测试。

## 确切提交 CI

Workflow 已配置 Linux/Windows 构建、单测、前端测试、Linux 浏览器回归及 Windows x64 自包含预览包。远端运行结果以该分支/PR 的确切提交为准；本文件不会预先宣称尚未完成的 CI 通过。

## 尚未完成与明确边界

- S0.1/S0.2/S0.4、S2.1–S2.3/S2.5、S3.5、S4.1/S4.3：代码与合成证据已有，真实 Windows/Xray 会话门槛仍 **blocked**。
- WMI/监听归属、真实发布 EXE 版本元数据、A/B 切换、核心/Monitor 重启、睡眠唤醒、关闭页面/托盘退出、开机启动、实际 CPU/内存和一年规模查询尚待实机测试。
- S3 的自动分组、所有组合下钻、Monitor 数据目录在线迁移未实现；当前可用范围/分桶、节点筛选、分页和导出。
- S6 只完成有限 CSV 补充；不同重叠文件明确拒绝，没有通用行级去重、批次撤销或来源节点映射。
- S5 sing-box 与 S7 TrafficMonitor deferred；TUN、链、策略组、自定义配置和多实例不支持。
- 未签名、未发布正式 Release、未合并 PR。Windows 实机通过前，不把预览代码标成正式 v0.1.0 已验收。

下一步只做：检查当前 PR 的 CI 到终态，修复明确失败；再按 [支持矩阵](compatibility.md) 运行有界 Windows 实机验收，不扩展第二内核或插件。
