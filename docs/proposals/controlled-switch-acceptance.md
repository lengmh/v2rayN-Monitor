# #18 / S5.6：组内轮换与旧长连接验收准备

## 当前结论

2026-10-23 继续完成标准控制入口核验，真实组内轮换和旧 TCP 长连接测试仍为 `NOT_RUN`，当前受控制入口与冻结范围阻塞。本次不修改配置、路由或真实账本，不调用控制 RPC，不切组或重启核心。普通采集、速率、文案及历史维度入口既有验收继续有效；正式 Monitor 当前为 `0650ac6`。

### 2026-10-23 固定客户端源码与最短模型验证

- 当前客户端版本标记仍是 `7.25.1+95f68d9955c2324518c5e1dc9485d73412102a95`。只读检查实际核心配置无 `api`／`RoutingService`、有 metrics、固定 proxy 和两个附加组，没有主 `proxy-balancer`；核对期间核心与配置未变。
- 新核对六份公开源码：`CoreConfigV2rayService`、`V2rayConfigTemplateService`、`CoreConfigContextBuilder`、`CoreConfigContext`、`CoreConfigHandler`、`V2rayConfig`。全部文件的 Git blob 与 SHA256 已核验，并从固定提交的实际根树再次逐文件确认。
- `CoreConfigV2rayService.cs:33-76` 先反序列化到 `V2rayConfig`，再生成路由、统计并应用最终修饰。`V2rayConfig.cs:3-17` 没有 `api` 或保存未知字段的成员。直接编译这份原始 DTO，输入全合成 `api.services=["RoutingService"]` 后做 JSON 往返，输出不再含 `api`，metrics／stats／统计策略仍保留。此为模型级验证，未执行完整客户端生成器或真实核心。
- `V2rayConfigTemplateService.cs:86-222` 的 `ApplyFullConfigTemplate` 可以保留任意顶层内容，但它返回整份模板并合入出站、组和探测配置，不是单独启用控制服务的标准开关。该路径属于用户冻结的 #19 整份配置来源适配范围，尚未验证 Monitor 在该模式下的来源与身份边界。

因此不直接修改客户端生成文件、不启用整份模板、不修改客户端源码来补本矩阵。当前范围内尚无已验证的无重启强制轮换入口；如保持 #19 冻结，先保留本用例，普通 proxy/direct、节点及跨核心生命周期可在各自授权窗口继续，不把它们当作本用例的替代证据。

## 后续窗口清单

以下项目分别申请具体目标与影响范围，不合成一次笼统的「全部验收」。表中版本／配置是历史证据，执行前重新核对；本次整理没有执行其中任何操作。

| 窗口 | 待补证据 | 执行条件 |
|---|---|---|
| 普通 proxy 与 direct | 固定实际路由下的受控上传、下载、空闲／直连对照，累计与账本逐范围一致 | 用户确认核心、节点、测试端及流量范围；不为制造流量调用清零统计接口 |
| 节点／核心生命周期 | A→B→A、核心重开、sing-box↔Xray，记录新基线、代次边界、未知窗口及旧历史保留 | 用户安排可能断网的切换窗口；不把同代次 Monitor 重开重复当待验 |
| 同代次组内轮换 | 下文的新旧 TCP 连接跨 A/B 轮换 | 先完成下文隔离前置；普通 GUI 改选会重启核心，不能冒充本项 |
| Windows 生命周期 | 尚未覆盖的睡眠／唤醒、冷启动／UAC、开机项与固定目录衔接 | 指明具体用例和恢复原入口的方法，不自动提权、移动现用目录或改启动项 |
| 常驻开销 | 关闭页面后的 CPU／工作集／库增长与年度首屏 | 单独约定测量窗口和同口径数据，保持普通采集；隔离查询基准不能替代 |

只对新修复及仍缺的对应证据补验；已确认的普通采集、持久化、旧数据保留、页面、速率、文案和历史入口不重开。Xray 探测核心安装及 S5.9 真实安装另有独立窗口，不夹带在普通计量验收中。

| 项目 | 本次证据 |
|---|---|
| 客户端 | `7.25.1+95f68d9955c2324518c5e1dc9485d73412102a95` |
| 实际核心版本输出 | `Xray 26.7.28 ... ced1f55 (go1.26.5 windows/amd64)`；未取得对应源码身份，不视为上游同标签的等价二进制 |
| 当前配置形态 | 固定 `proxy`，另有两个 `leastLoad` 组，分别匹配 4 / 24 个代理出站；没有选中策略组对应的主 `proxy-balancer` |
| 原生只读发现 | `connected`，28 个 `actual_member` 和 1 个 `legacy_proxy`，29 个范围均有计数；与此前 64 成员接入窗口分别记录 |
| 统计与控制接口 | 有回环 metrics；没有 `api` 配置或 `RoutingService`，不能把 metrics 端口当作控制 API |
| 实际 CLI | `version`、`help api bi`、`help api bo` 均成功；只执行帮助，没有发送查询或覆盖 RPC |

客户端版本标记对应的公开提交位于 `fqfqgo/v2rayN`。已缓存固定提交的五个相关源码文件，逐一核对 Git tree、contents API 和本地 Git blob 摘要。`V2rayStatisticService.cs:5-22` 只生成 stats、metrics 和出站统计策略；不能从开启实时统计推断控制服务已开启。

固定源码的普通节点选择路径为 `ProfilesViewModel.cs:569-589` → `MainWindowViewModel.cs:290-293,745-851` → `CoreManager.cs:93-124`，生成配置后先 `CoreStop()`、再 `CoreStart()`。因此，GUI 改选节点／策略组可以用于另一个生命周期用例，不能用于证明「同核心代次的旧长连接跨组内轮换」。

## 执行前置条件

以下条件尚未完成；本文件不是本轮执行授权。

1. 在隔离环境验证客户端支持的标准配置生成方式，确认能保留原统计与业务路由、只增加回环 `RoutingService`，且 Monitor 仍可核实实例与成员。不得直接修改会被客户端覆盖的生成文件，也不切换到未支持的整份自定义配置来源。
2. 取得真实配置变更、必要重载、指定组与成员、受控测试流量及恢复操作的具体窗口。配置准备造成的重载必须发生在长连接建立之前。
3. 核实测试请求实际命中所选 balancer。当前有附加组，不等于默认代理请求使用这些组；不按组名、IP 或页面选择猜测归属。
4. 记录原 override（包括原先没有 override 的情况），确认两个可用叶成员 A / B，并准备不自动重连、可以识别同一条 TCP 连接的测试端。具体端点和流量范围在执行前确定，不使用真实配置或凭据作公开 fixture。

官方 API 文档和实际 CLI 帮助均要求启用 `RoutingService`。本轮只确认命令存在，未验证当前二进制的 override 执行路径，也未验证可用的客户端配置生成入口；不能将下述条件流程当作已就绪脚本。

## 条件满足后的最短流程

由实施人员在已授权窗口执行；每一步保留时间与脱敏证据。

1. 完成配置准备并核实回环控制服务后，记录核心 PID／创建时间、配置摘要、实例、会话、代次、组成员、原 override 和累计计数。由 Monitor 按原机制采集；验收工具不直接向真实账本写入数据。
2. 在所选组上固定 A，查询确认实际 override，再建立旧 TCP 长连接，确认持续传输及可靠的 A 归属证据。
3. 不改配置或重启核心，仅将同一组 override 改为 B，查询确认。保留旧连接，并建立新的 B 连接，分别记录连接身份与传输结果。
4. 核对旧连接未重连且仍归属 A、新连接归属 B，核心会话与配置代次保持。并行核对逐范围累计差与 Monitor 入账差；并发业务和内部探测不能冒充测试连接的独立归属证据，应用负载字节不直接等同出站计数字节。
5. 恢复原 override 并查询确认；只有原先没有 override 时才移除 override。核实普通代理和 Monitor 恢复正常，不恢复旧数据库覆盖新历史。

若旧连接自动重连、归属证据不足、核心或配置代次变化，停止本用例，保留结果为未通过或证据不足，不以当前组选中项补推旧连接。恢复失败时停止进一步切换，按已授权恢复方案处理。

通过条件为：同代次旧连接持续归属 A、新连接可靠归属 B、逐范围累计与入账一致、原 override 恢复。单纯切换页面、出现 B 流量或 API 返回成功均不足以通过。

## 下一节点与证据

当前没有已验证的无重启强制轮换入口，不为补矩阵临时启用控制服务或展开 #19。此前 PR #30 整合已经完成；当前 PR #35／#38 为父子草稿分支，后续合并按具体授权执行。下一可执行实机窗口是独立的普通 proxy/direct 或节点／跨核心生命周期，执行前确定具体目标、受控流量和恢复步骤；S5.6 保持 `in_progress`。PR #33 不合并，可选 S5.8 不夹带进入普通统计验收。

本机证据位于 `<evidence-root>/v2rayn-monitor-controlled-switch-readiness-20261022`：`readiness-safe.json`、规范化后的 `native-confirmation-safe.json`、CLI 帮助、固定公开源码及摘要清单。第一份汇总中的空 API 集合曾序列化为 `[null]`，以后续原生确认的「无 api / services=[]」为准。私有进程身份及文件摘要仅留本机，不提交节点、配置、计数明细或数据库。历史接入与本次只读发现分开，详见 [STATUS](../STATUS.md)。

2026-10-23 新证据位于 `<evidence-root>/v2rayn-monitor-e1-closeout-20261023`：`controlled-readiness-current.json`、`routing-dto-probe.json`、六份公开源码及其摘要清单、固定提交与实际根树。合成验证程序只编译 DTO，不启动核心、调用控制 RPC 或读取真实节点资料。

来源：

- [Xray 官方 API 文档](https://xtls.github.io/config/api.html)
- [固定客户端统计生成源码](https://github.com/fqfqgo/v2rayN/blob/95f68d9955c2324518c5e1dc9485d73412102a95/v2rayN/ServiceLib/Services/CoreConfig/V2ray/V2rayStatisticService.cs)
- [固定客户端节点选择源码](https://github.com/fqfqgo/v2rayN/blob/95f68d9955c2324518c5e1dc9485d73412102a95/v2rayN/ServiceLib/ViewModels/ProfilesViewModel.cs)
- [固定客户端重载入口源码](https://github.com/fqfqgo/v2rayN/blob/95f68d9955c2324518c5e1dc9485d73412102a95/v2rayN/ServiceLib/ViewModels/MainWindowViewModel.cs)
- [固定客户端核心生命周期源码](https://github.com/fqfqgo/v2rayN/blob/95f68d9955c2324518c5e1dc9485d73412102a95/v2rayN/ServiceLib/Manager/CoreManager.cs)
- [固定客户端 Xray 配置模型](https://github.com/fqfqgo/v2rayN/blob/95f68d9955c2324518c5e1dc9485d73412102a95/v2rayN/ServiceLib/Models/CoreConfigs/V2rayConfig.cs)
- [固定客户端整份模板处理](https://github.com/fqfqgo/v2rayN/blob/95f68d9955c2324518c5e1dc9485d73412102a95/v2rayN/ServiceLib/Services/CoreConfig/V2ray/V2rayConfigTemplateService.cs)
