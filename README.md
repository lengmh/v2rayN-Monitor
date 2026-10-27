<p align="center">
  <img src="./docs/images/readme-header.svg" alt="v2rayN Monitor" width="1200" />
</p>

<p align="center">
  <strong>Your traffic history, organized by node.</strong><br />
  Track local usage over time and explore daily, weekly, monthly, and yearly totals.
</p>

<p align="center">
  <a href="https://github.com/lengmh/v2rayN-Monitor/releases"><img src="https://img.shields.io/github/v/release/lengmh/v2rayN-Monitor?style=flat-square" alt="Release" /></a>
  <a href="https://github.com/lengmh/v2rayN-Monitor/stargazers"><img src="https://img.shields.io/github/stars/lengmh/v2rayN-Monitor?style=flat-square" alt="Stars" /></a>
  <a href="https://github.com/lengmh/v2rayN-Monitor/blob/main/LICENSE"><img src="https://img.shields.io/github/license/lengmh/v2rayN-Monitor?style=flat-square" alt="License" /></a>
</p>

<p align="center">
  <a href="https://github.com/lengmh/v2rayN-Monitor/blob/main/docs/QUICKSTART.md">Quick Start</a> ·
  <a href="#已实现的主流程">Features</a> ·
  <a href="https://github.com/lengmh/v2rayN-Monitor/blob/main/docs/architecture.md">Docs</a> ·
  <a href="https://github.com/lengmh/v2rayN-Monitor/releases">Releases</a>
</p>

# v2rayN Monitor

以 **v2rayN 本机流量**为主来源的 Windows 托盘统计工具。关闭浏览器后仍持续采集；历史独立保存在本机 SQLite，支持按节点查看日、周、月、年趋势。

> **v0.2.0 正式版已发布。** 提供 basic／full／extended 三个独立 Windows x64 包，整合当前已支持的 Xray／sing-box 采集、成员历史、CSV 批次管理、图表下钻、主动刷新和可重建日汇总。支持范围与各层验证见 [支持矩阵](docs/compatibility.md)；不承诺所有协议、设备或切换组合均经过实机测试，统计不作为计费凭证。

**下载与升级：** [v0.2.0 Release](https://github.com/lengmh/v2rayN-Monitor/releases/tag/v0.2.0) 提供三档 ZIP、发布清单和 SHA256。正式包绑定 `aaf5bf1` 及精确 CI；数据目录／账本模式、组件安装和两目录受控合并见 [发布与升级说明](docs/release-v0.2.0.md)。下载或解压不自动覆盖核心、升级或合并数据库。

## 能力分级

v0.2.0 已按以下三档提供独立下载包，包名标明版本号与档位。三个包的 Monitor 程序文件相同，差异仅为随附组件；实际能力以已安装组件、运行核心和账本模式分别核验。

| 分级 | 普通流量统计 | 所需核心 | 独立探测计量 |
|---|---|---|---|
| 基础版 | v2rayN 使用原生 Xray 时的流量统计 | 保留原 Xray，读取已启用的原生统计接口 | 可选扩展，不要求启用 |
| 完整版 | 在基础版上增加 sing-box，覆盖当前支持的 Xray、sing-box 及其已支持模式 | Xray 保持原生；随包 `components/` 提供启用 V2Ray API 和成员统计能力的配套 `sing-box-client.exe`，经确认后安装 | 可选扩展，不作为完整版验收前置 |
| 扩展版 | 保留普通流量统计，按需增加内部探测的独立计量 | sing-box 使用带探测计数的配套核心；Xray 使用经过精确核验的探测实验核心 | 两种核心分别启用，字节口径和覆盖边界单独说明 |

流量探测的独立计量属于 S5.8 可选扩展，不作为 #15 / #18 普通流量统计的收口条件。这里的「全部内核」指当前支持的 Xray 和 sing-box，不扩大到未支持的 TUN、代理链、任意配置来源或插件。

当前 sing-box `test.5` 已将普通统计和探测扩展放在同一包中，能力分级不要求重新替换核心。完整版可以保留原生 Xray；只有需要 Xray 的独立探测字节时，才另行接入修改后的 Xray。已有探测能力和历史保留；分级不改变账本版本、启动模式或现有安装。

三档共用 Monitor 源码和软件版本号，发布资产分别为 `v2rayn-monitor-<version>-basic-windows-x64.zip`、`v2rayn-monitor-<version>-full-windows-x64.zip`、`v2rayn-monitor-<version>-extended-windows-x64.zip`。基础包提供 Monitor；完整版另提供配套 sing-box 核心；扩展版再提供探测能力及配套 Xray 实验核心。每包分别列明内容、核心版本、SHA256、许可与源码／补丁依据；配套核心由用户按说明安装，不自动覆盖正在运行的核心。正式资产来源、清单和升级流程见发布说明；随包组件不会自动替换当前核心。

## 已实现的主流程

- WinForms 托盘常驻、单数据目录单实例、系统浏览器本地页面；便携包无需 WebView2
- 只读发现当前支持的 Xray／sing-box 进程与统计接口，以进程代次、有效配置和节点／成员身份核对归属
- 原始整数字节记账；第一条只建基线，切换/重启/未知边界留缺口，读取失败不写成零
- SQLite 原子游标与分钟历史，节点改名保留历史，日/周/月/年汇总、排行、分页、CSV 导出
- 回环监听、一次性打开链接、短期会话、Host/Origin 校验；无云端、无遥测、无 CDN
- 可选六列服务商 CSV 预览、增量防重、批次引用和确认撤销，独立账本；旧库按说明显式升级，不影响本机采集
- 年→月→日图表下钻、保留筛选的主动刷新，以及适用条件下的可重建日级历史汇总
- 完全合成的演示数据与针对性回归测试

当前支持范围不包含 TUN、多跳代理链、任意配置来源或多个 v2rayN 实例。复杂或不确定配置显示原因并暂停归属，不猜测出口。配套核心提供受控下载／安装／恢复入口，操作仍需确认；Monitor 为便携包，没有签名、系统安装向导或自动更新器。

## 使用

[快速开始、统计设置与故障排查](docs/QUICKSTART.md) · [支持边界](docs/compatibility.md) · [当前验证状态](docs/STATUS.md)

版本包以 [GitHub Releases](https://github.com/lengmh/v2rayN-Monitor/releases) 页面及其 SHA256 为准；开发预览仍可在对应 Actions 运行的 `windows-x64-preview` artifact 下载。解压到可写目录，运行 `V2rayN-Monitor.exe`，选择包含 `guiConfigs` 和 `binConfigs` 的 v2rayN **数据根目录**。不需要导入 CSV。

默认数据位于 `%LOCALAPPDATA%\V2rayN-Monitor\data`。`--demo` 使用独立演示目录，不连接真实核心；`--data-dir "D:\MonitorData"` 可显式选择 Monitor 历史目录。托盘菜单可手动启用当前用户登录时启动。v0.2.0 提供可选的“v2rayN + Monitor 联动启动快捷方式”：只在从该新快捷方式运行时同时启动两者，Monitor 静默进入托盘并自动记录；不拦截原有 v2rayN 启动入口。见 [联动启动说明](docs/QUICKSTART.md#7-v2rayn--monitor-联动启动开发预览未包含在-v011-正式包)（Issue #24，已纳入 v0.2.0）。

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

本机上传／下载来自已核实核心接口的原始累计值。v1 保留 Xray `proxy` 路径；多范围账本的默认 `local_proxy` 视图读取 `actual_member` 与 `legacy_proxy`，不叠加 direct、独立探测或服务商数据，不等同于网卡线速或服务商扣费。按可靠实例、配置／成员身份保存历史，名称仅供展示，不按同名合并。首接建立基线，切换或无法确认的边界保留缺口。

长期保存 UTC 分钟记录，短采样按时间比例守恒分配；分钟内分布是近似。查询边界精度为分钟，日界线按设置的时区、周从周一开始。连续会话长缺口恢复的总量单列“未分时”，不填入日期图表。缺口和未观察时段不能当作已证实的零流量。

服务商原始量须为非负整数字节；计费量和倍率使用精确非负十进制数值。计费量以 CSV 为准，不用原始量乘倍率重算，也不取整。预览及批次列表显示精确值；零倍率仍可能有原始量。十进制值及批次总计须能以 96 位系数、最多 28 位小数无损表示，超限整批拒绝。每文件最多 4 MB、50,000 条数据记录（不含表头）。同一来源按重复导出快照比较规范化计费行（UTC 时间、节点名、倍率、原始量、精确计费量）及出现次数，跳过已有记录、只导入新增部分，保留文件内合法重复；traffic 显示文本不参与匹配。预览显示文件/新增/跳过数量和新增精确总量，确认提交时重新核对。相同时间和名称的计费字段变化且无法确认是否修订时提示冲突，不覆盖旧记录。这是约定快照语义下的有限去重，不宣称无记录 ID 时的普适精确识别。批次引用启用后，新引用支持预览及确认撤销；无引用的旧批次仍不可撤销，不补造引用。默认不保存原 CSV，可选择另存；不自动匹配本机节点。

## 项目资料与隐私

[8 阶段/38 节点计划](docs/development-plan.md) · [架构](docs/architecture.md) · [固定源码依据](docs/sources.md) · [开发约定](AGENTS.md) · [安全边界](SECURITY.md)

只提交原创实现和完全合成测试。真实配置、订阅、密码、节点明细、数据库、CSV 和运行日志不得上传仓库。运行不修改 v2rayN 配置、不重启核心、不调用 reset 统计操作。原创资料与代码采用 [MIT](LICENSE)。本项目并非 v2rayN、Xray、sing-box 或 TrafficMonitor 官方组件。
