# v2rayN Monitor

以 **v2rayN 本机流量**为主来源的 Windows 托盘统计工具。关闭浏览器后仍持续采集；历史独立保存在本机 SQLite，支持按节点查看日、周、月、年趋势。

> **初版预览代码。** 目标环境是 Windows x64、v2rayN **7.25.1 + Xray 普通单节点**。源码核对、合成测试、跨平台构建与 Windows 实机验收是不同证据；当前还没有真实 Windows/v2rayN 会话的验收记录。未发布正式 Release，不能当作计费凭证。

## 已实现的主流程

- WinForms 托盘常驻、单数据目录单实例、系统浏览器本地页面；便携包无需 WebView2
- 只读发现 Xray 进程与动态 metrics 端口，以进程代次、有效配置和节点身份核对归属
- 原始整数字节记账；第一条只建基线，切换/重启/未知边界留缺口，读取失败不写成零
- SQLite 原子游标与分钟历史，节点改名保留历史，日/周/月/年汇总、排行、分页、CSV 导出
- 回环监听、一次性打开链接、短期会话、Host/Origin 校验；无云端、无遥测、无 CDN
- 可选六列服务商 CSV 预览与事务导入，独立账本、同文件防重；不影响本机采集
- 完全合成的演示数据与针对性回归测试

不支持 sing-box、TUN、策略组、多跳链、自定义核心配置或多个 v2rayN 实例的精确统计。复杂或不确定配置会显示原因并暂停归属，不猜测出口。没有签名、安装器或自动更新器。

## 使用

[快速开始、统计设置与故障排查](docs/QUICKSTART.md) · [支持边界](docs/compatibility.md) · [当前验证状态](docs/STATUS.md)

CI 成功后可在对应 Actions 运行的 `windows-x64-preview` artifact 下载便携预览包。解压到可写目录，运行 `V2rayN-Monitor.exe`，选择包含 `guiConfigs` 和 `binConfigs` 的 v2rayN **数据根目录**。不需要导入 CSV。

默认数据位于 `%LOCALAPPDATA%\V2rayN-Monitor\data`。`--demo` 使用独立演示目录，不连接真实核心；`--data-dir "D:\MonitorData"` 可显式选择 Monitor 历史目录。托盘菜单可手动启用当前用户登录时启动。

## 开发与测试

需要 .NET 10 SDK、Node.js 24 和 Python 3：

```powershell
cd web
npm ci
npm test
npm run build
cd ..
python scripts/check_plan.py
dotnet build V2rayNMonitor.sln -c Release
dotnet test tests/Core.Tests/Core.Tests.csproj -c Release
dotnet test tests/Server.Tests/Server.Tests.csproj -c Release
dotnet publish src/Host/Host.csproj -c Release -r win-x64 --self-contained true -o artifacts/win-x64
```

Linux/macOS 可验证核心记账与本地 API，并运行合成演示（不能采集 Windows v2rayN）：

```sh
dotnet run --project src/Demo -- /tmp/v2rayn-monitor-demo
```

控制台提供两分钟有效、一次性演示链接，不要分享。真实应用从托盘打开，无需复制令牌。

## 数据口径与限制

本机上传/下载来自 Xray `proxy` outbound 的原始累计值，不与 direct/inbound 相加，也不等同于网卡线速或服务商扣费。身份采用实例与 IndexId，不按名字合并。

长期保存 UTC 分钟记录，短采样按时间比例守恒分配；分钟内分布是近似。查询边界精度为分钟，日界线按设置的时区、周从周一开始。连续会话长缺口恢复的总量单列“未分时”，不填入日期图表。缺口和未观察时段不能当作已证实的零流量。

服务商数值列须为非负整数字节；保留原始量和计费量，零倍率仍可能有原始量。相同文件重复导入不增加记录，文件内合法重复保留。无稳定记录 ID 的不同重叠文件在初版明确拒绝，不做猜测去重。仅展示最近 200 批导入记录，无批次撤销或本机节点自动匹配。

## 项目资料与隐私

[8 阶段/33 节点计划](docs/development-plan.md) · [架构](docs/architecture.md) · [固定源码依据](docs/sources.md) · [开发约定](AGENTS.md) · [安全边界](SECURITY.md)

只提交原创实现和完全合成测试。真实配置、订阅、密码、节点明细、数据库、CSV 和运行日志不得上传仓库。运行不修改 v2rayN 配置、不重启核心、不调用 reset 统计操作。原创资料与代码采用 [MIT](LICENSE)。本项目并非 v2rayN、Xray、sing-box 或 TrafficMonitor 官方组件。
