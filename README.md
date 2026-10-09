# v2rayN Monitor

以 **v2rayN 本机流量**为主要数据来源的 Windows 节点流量历史统计工具。

独立系统托盘负责常驻采集，HTML 页面用于查看各节点的上传、下载、合计，以及日、周、月、年趋势。服务商 CSV 仅作为偶尔导入的辅助账本；TrafficMonitor 插件作为可选显示入口。

> 当前处于计划阶段。本仓库初始化资料不包含可运行采集器或应用 Release，也没有已经通过的 Windows 兼容性声明。

## 产品原则

不导入任何服务商数据，也应能自动完成核心统计。只读访问 v2rayN 配置和内核统计，不接管代理连接、不重置内核计数。历史独立保存在本地；身份不明和采集缺口如实标注。

本机与服务商使用不同账本，不叠加、不覆盖。没有设备/IP/上下行数据的导入记录仍可保存，但不猜测归属。不设置服务商每两天导入的日常要求。

## 规划功能

| 主线 | 后续可选 |
|---|---|
| 自动发现或手动选择 v2rayN 实例 | 第二内核兼容扩展 |
| 原始字节采集、节点归属、历史持久化 | 服务商 CSV 手动导入与对照 |
| HTML 日/周/月/年统计与节点排行 | TrafficMonitor 薄插件 |
| 托盘常驻、分页记录与导出 | 需要时增加嵌入式窗口 |

计划采用 .NET 10、SQLite、HTML + TypeScript；首版在系统浏览器打开本地页面，无需让浏览器一直运行。具体支持的内核版本与模式必须经过 S0 和 Windows 实机验收后写入兼容矩阵。

## 开发资料

- [分阶段开发计划：8 个阶段、33 个任务节点](docs/development-plan.md)
- [架构与计量契约](docs/architecture.md)
- [支持矩阵](docs/compatibility.md) · [当前状态](docs/STATUS.md)
- [开发执行约定](AGENTS.md) · [源码与官方依据](docs/sources.md)

## 仓库与计划校验

公开仓库已建立：[lengmh/v2rayN-Monitor](https://github.com/lengmh/v2rayN-Monitor)。当前发布的是计划、初始化工具与合成样本，应用开发尚未开始；最新进度见 [当前状态](docs/STATUS.md)。

检查计划与公开文件白名单，不做网络写入：

```powershell
python scripts/check_plan.py
python scripts/bootstrap_github.py --dry-run
```

这些是计划/初始化检查，不是应用功能测试。

原始 [初始化脚本](scripts/bootstrap_github.py) 保留供参考。创建模式只适用于尚不存在的目标仓库，不应对本仓库重复执行。可选的 `--backlog-only` 模式可在已登录 GitHub CLI 的环境中生成 8 个 Milestone 和 33 个任务 Issue；本次仅发布文件，任务清单保存在 `docs/backlog.json`，尚未创建这些 GitHub 条目。

## 隐私与许可证

运行数据默认仅保存在本机。公开测试数据必须完全合成；不得提交真实订阅地址、密钥、节点明细、服务商导出或数据库。详见 [SECURITY.md](SECURITY.md)。

项目原创资料和后续原创代码默认采用 MIT 许可证。项目并非 v2rayN、Xray、sing-box 或 TrafficMonitor 官方组件，也不在初始化包中复制它们的源码；引入第三方实现前须分别核对许可证。
