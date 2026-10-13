# sing-box / AnyTLS 统计能力核验

核验日期：2026-10-13。基线：`32c5f02`。跟踪：[Issue #15](https://github.com/lengmh/v2rayN-Monitor/issues/15)。CSV 批次管理另见 [Issue #16](https://github.com/lengmh/v2rayN-Monitor/issues/16)。

## 当前结论

当前真实配置只启用 Clash API，但安装的 sing-box 1.14.1 也能运行原生 API。原生连接事件在隔离测试中能分类 proxy/direct 并保留短连接最终字节；慢接收实测会在连接未断开时丢失记录，关闭历史还会随 GC 清理且最多保留 1,000 条。尚无已验证的持久逐 proxy 累计和完整断开恢复路径，S0.3 与 S5.1 仍为 blocked；Monitor 的 AnyTLS 采集适配尚未实现，不宣称步骤 1–3 全部完成。

已完成两条 Issue 登记、版本核验、固定源码核读、安装二进制的隔离合成实验及用户手动切到 AnyTLS 后的只读实机观察。初次实验时实际核心仍为 Xray；后续实际 AnyTLS 证据见对应章节，不能把早期 Xray 配置当成 AnyTLS 运行证据。本轮原生 API 和并发实验仅使用同二进制副本、合成认证及回环目标，真实节点、路由、配置和运行核心未改变。

## 精确版本与证据层级

| 对象 | 核实结果 | 证据 |
|---|---|---|
| 定制客户端 | `7.25.1+95f68d9955c2324518c5e1dc9485d73412102a95` | 本机 EXE ProductVersion；对应生成器源码 |
| 安装 sing-box | `1.14.1`；Revision `1ac1a339cb1223e9c70eae14c44411c75033c02d`；Go `1.26.8`；Windows amd64 | 本机二进制 `version` 输出 |
| Clash API | 构建标签含 `with_clash_api`，隔离进程可读 | 安装二进制合成运行 |
| V2Ray API | 构建标签未列出；加入该 API 的合成配置检查明确拒绝，提示未编入构建 | 安装二进制 `check`，未启动该配置 |
| 原生 API | 同二进制副本运行并读取成功，API version 4；实际配置未启用 | 回环合成 AnyTLS 运行，不是实际节点原生 API 验收 |
| 当前实际核心 | 用户已手动切至 AnyTLS / sing-box，TUN 关闭 | 基础活动配置、父子进程与 Clash 监听只读核对；完整 TLS/传输等价未验收 |

二进制报告的 Revision 在公开上游可解析；固定源码用于解释接口行为，不凭版本串宣称整个定制二进制与公开树完全一致。关键计数行为另由安装二进制实验确认。

## 可用接口及限制

| 路径 | 原始语义 | 计量范围 | 本机节点账本结论 |
|---|---|---|---|
| Clash `/traffic` | 每个一秒周期的累计差 | 全局，包含 direct | 不当作累计计数；无法补齐离线周期，也不能排除 direct |
| Clash `/connections` 的 `uploadTotal` / `downloadTotal` | 原始整数累计 bytes；连接结束后保留 | 全局，包含 direct | 不能无条件归给活动 AnyTLS 节点 |
| Clash 活动连接列表 | 当前活动连接及各自计数 | 可显示链路，但已结束短连接不在列表 | 不通过轮询推断完整节点累计量 |
| V2Ray API | 源码提供按 outbound 计数与不清零读取 | 可按 `proxy` 统计；需构建和有效配置启用 | 当前安装构建不可用，尚无该路径实机证据 |
| 原生 `SubscribeStatus` | 全局整数累计 bytes 与周期增量 | 与 Clash 相同，包含 direct | 不能把总量全部算给活动节点 |
| 原生 `SubscribeConnections` | 创建/更新/关闭事件及出站、链路、最终字节 | 可分类已观察连接；历史缓存会被清理，慢接收会丢事件 | 候选分类路径，尚非持久逐 proxy 累计；诊断对账仅在静止状态验证 |

精确客户端生成器只添加 Clash API。当前核读的完整配置模板机制会替换整份配置并追加出站，并非只追加统计字段；不能把启用整份模板当作不影响入站、DNS 和路由的开关。直接修改生成的 `config.json` 也不是持久接入方案。

## 隔离实验

使用安装的 sing-box 二进制，在专用临时目录启动一个仅回环测试进程。配置含合成 AnyTLS proxy 与 direct；唯一测试请求按显式规则走 direct，未访问真实代理节点或外网。HTTP 请求和响应均为合成固定字节。

结果：

- direct 请求使全局累计上传增加 `15,511` bytes、下载增加 `19,590` bytes。
- 请求结束后活动连接数为 `0`，累计量仍保留。
- 连续只读查询没有清零或修改累计量。
- 实际返回字段为 `connections`、`downloadTotal`、`memory`、`uploadTotal`，没有逐出站累计字段。
- 独立测试进程已结束；真实 v2rayN/Xray 的进程及创建代次未改变。

该结果证明当前二进制的全局量会混入 direct，不是实际 AnyTLS 节点的采集验收，也不是 Xray/sing-box 切换验收。实验没有写入 Monitor 正式账本。

## 临时副本的 AnyTLS 三路径验证

用户要求保持当前节点运行，用拷贝临时版测试。本轮将安装的 `sing-box.exe` 复制到独立临时目录并核对二进制 SHA256 相同，启动本地 AnyTLS 服务端和客户端两份临时进程，分别使用独立回环端口。使用合成认证、本地测试证书和本地 HTTP 目标，外网请求数为 `0`。未启动第二份 v2rayN、复制真实订阅或使用真实节点凭据。

| 合成测试路径 | 活动连接链路 | Clash 全局上传增量 | Clash 全局下载增量 |
|---|---|---:|---:|
| 进入临时核心，再经本地 AnyTLS | `proxy` | 82,098 bytes | 65,670 bytes |
| 进入临时核心，按测试规则直连 | `direct` | 82,098 bytes | 65,670 bytes |
| HTTP 客户端直接访问目标，绕过临时核心 | 无 | 0 bytes | 0 bytes |

每次请求在响应返回前保持活动状态，读取 `chains` 确认实际路径；请求结束后活动连接数为 `0`，全局累计量仍保留，连续读取不清零。结果确认 AnyTLS 短连接的量会保留在全局累计中；限制是无法从该累计量拆出已结束连接的 proxy/direct 分类，不能把活动连接轮询的漏计限制套到 `/traffic` 的持续全局增量上。

测试结束后两份临时核心均退出。前后检查确认真实 v2rayN、Xray 和 Monitor 的 PID/创建代次、选中 IndexId、有效配置及系统代理设置均未改变；真实账本未用于实验。

另外，只读核对当前保存的活动路由：一条启用的路由配置含 12 条启用 direct 规则、11 条启用 proxy 规则及 1 条 block 规则。没有启用完整配置模板。固定客户端生成器会遍历活动路由的启用业务规则并写入 sing-box 路由，但规则命中还受运行模式、顺序、规则类型和目标影响。

这些数量仅描述保存的路由配置，不能当作当前 AnyTLS 有效运行配置或某次直连命中的证明；当前真实核心仍是 Xray。副本测试使用合成路由，未调用客户端为真实 AnyTLS 节点生成配置。因此本轮没有宣称已复现真实节点的全部路由、TLS 默认值或节点归属，仍不能证明用户实际 AnyTLS 环境的统计只含 proxy。

## 实际 AnyTLS 会话的只读核验

用户随后手动切换到 AnyTLS，允许只读核验。确认活动 ProfileItem 类型为 AnyTLS，生成配置的单一 `proxy` 出站为 `anytls`，所选服务器、端口与认证在内存比较一致。实际 sing-box 为对应 v2rayN 的子进程；有限权限核对了两者映像路径、创建代次、标准启动命令 `run -c config.json --disable-color`，并确认 Clash 回环监听属于该核心 PID。配置生成时间早于核心创建，读取期间选择、配置、进程、监听与模式保持稳定。

接口报告 `sing-box 1.14.1`，TUN 关闭，Clash 实际模式为 `Rule`，默认出站为 `proxy`。有效生成配置包含 13 条 direct 出站规则：1 条用于 `Direct` 模式，另外 12 条为业务路由，匹配字段包括 `domain_keyword`、`domain_suffix`、`ip_cidr`、`ip_is_private` 和 `rule_set`。这些是实际 AnyTLS 生成配置中的核心内路由，不是系统代理的绕过列表。

被动观察约 12 秒、读取 24 次，只捕捉到有字节变化的 proxy 活动连接，没有捕捉到 direct 连接。全局上下行累计量有效且单调增加；没有清零、修改配置、重启核心、切换节点或主动产生外网流量。该结果证明实际 AnyTLS 会话的 Clash 接口可读，不能由短时未捕捉到 direct 推断接口只统计 proxy，也不能排除快照之间已结束的 direct 连接或未来命中直连规则。

本轮只核对基础服务器/认证匹配与运行绑定，未完成全部 TLS、传输字段和客户端默认值的等价比较；未做受控 direct 命中、proxy 字节守恒、跨内核切换或 Monitor 新适配器测试。Monitor 适配仍未实现，严格逐 proxy 累计门槛保持原状态。用户真实字节增量和运行标识仅保存在本机脱敏核验产物，不进入公开记录。

## 原生 API 与慢接收隔离核验

后续对已安装二进制再次执行 version 和合成配置 check：Clash 与原生 API 配置通过，V2Ray API 明确提示未编入。原生 API 不是 V2Ray API，不能用缺少 `with_v2ray_api` 推断该二进制只支持 Clash。

同 SHA256 二进制副本以独立回环端口、本地合成 AnyTLS 服务端、合成认证和证书运行；外网测试请求为 0。原生 `GetVersion` 返回 1.14.1 / API version 4。首次 16 条 proxy 与 16 条 direct 短连接均收到关闭事件及最终计数，分类上传/下载之和与核心全局累计相同；`SubscribeStatus` 总量仍混合 proxy/direct。

断开订阅后重连，默认 GC 条件下 44 条已结束连接只返回 21 条分类记录。仅在临时进程设置 `GOGC=off` 的诊断对照恢复全部 44 条；继续增加本地短连接后只保留最新 1,000 条。该环境设置用于验证缓存清理原因，不作为产品接入建议，没有修改真实核心或系统环境。

补测仍使用默认 GC：

| 合成场景 | 实际结果 |
|---|---|
| 8 并发、256 条短连接 | proxy/direct 各 128 条均分类；上传 39,680 bytes、下载 33,536 bytes，与全局累计相同 |
| proxy/direct 各一条长连接 | 22 个周期更新事件；最终分类字节与核心总量相同，未将更新差分和最终总量重复计入 |
| 小接收缓冲并暂停读取约 3.5 秒 | 2,000 条突发加 1 条唤醒请求全部成功结束，新增分类记录仅 358 条；缺少上传 254,665 bytes、下载 215,233 bytes |
| 慢接收时原生订阅状态 | 连接保持、无 Reset 或错误提示；诊断端用全局总量对账发现差额 |
| 人为丢弃一条关闭事件的对照 | 诊断对账发现额外上传 155 bytes、下载 131 bytes 差额；该注入对照不是队列溢出的运行证据 |

固定源码的事件总队列为 256 条、订阅者队列为 64 条，满时可不报告地丢弃。运行结果证明慢接收会静默失去分类记录，但未定位丢失发生在哪一层队列。对账仅在测试流量停止、活动连接归零后验证；持续背景流量下的实时完整性判断、正式缺口处理和账本仍未实现或验收。

真实配置中的原生 API 保持关闭，本轮没有直接读取实际 AnyTLS 会话的原生 API。前后复核真实配置、活动选择、系统代理、统计监听归属与客户端/核心/Monitor 进程代次均未变；全部临时核心已退出。合成结果不升级为真实客户端持久接入、实际节点原生 API 或 Monitor 验收。

## 原生 API 启动草案的合成检查

安装核心支持 `-c 主配置 -c 附加配置`。仅含一个原生 API 服务的附加配置完成 merge 与多配置 check。merge 会规范化单元素列表和读取证书，不能直接用原始 JSON 字节相同判断等价；以同核心对主配置单独规范化的结果作基准，合成 DNS、route、outbounds、inbounds、experimental、log 相同。已有服务项目和相对顺序保留，新 API 放在列表前部。

固定客户端启动参数由源码生成，没有已确认的可编辑附加参数入口。专用临时目录内准备了只作用于主 sing-box 的局部客户端补丁草案，约束附加文件仅为带认证的回环 API 服务；当前已安装客户端不具备该草案入口。补丁未编译、应用或提交，不提供用户直接修改整份配置模板的指令。长期设置/UI、正式接入和后续 Monitor 的双配置文件/进程绑定核验仍待实现。

## 下一步接入方案与确认边界

要保留现有路由并实现可靠节点累计量，需要额外提供按 outbound 的累计统计能力。可优先核验同版本含 `with_v2ray_api` 的核心构建，并为客户端确定持久启用 `stats.outbounds=["proxy"]` 的接入点；所有统计查询使用 `reset=false`。

这条候选路径尚未完成构建、配置接入或实机验证。当前客户端没有核实过的仅追加统计设置入口，因此接入设计必须同时说明生成规则、入站/DNS/路由保留、监听绑定、备份与恢复方式，不能以完整模板或单次改写生成配置代替。

当前无需因核验而先更换核心。已完成同二进制副本的 proxy/direct、原生事件和慢接收实验。原生 API 是额外候选入口，但静默丢失、缓存清理及持续流量对账尚未解决，不能因其可运行而宣称严格逐 proxy 累计门槛已通过。若保留该门槛，仍需审阅持久计数接入或额外核心构建方案。若用户选择与 v2rayN 一致的全局量按活动节点归属口径，应先在 Issue 明确混入 direct 的限制，并更新验收条件，不能宣称该口径等同 Xray 的 proxy 计量。替换已安装核心、改动客户端设置、切换节点及重启真实核心仍分别按具体操作授权。

如果保持当前构建和配置，Issue #15 保留 blocked；不会改用速度积分、活动连接轮询或全局量冒充精确节点累计。CSV Issue #16 保持 planned，插件继续 deferred。

## 固定源码

- [客户端 Clash API 生成](https://github.com/fqfqgo/v2rayN/blob/95f68d9955c2324518c5e1dc9485d73412102a95/v2rayN/ServiceLib/Services/CoreConfig/Singbox/SingboxStatisticService.cs)
- [客户端完整模板语义](https://github.com/fqfqgo/v2rayN/blob/95f68d9955c2324518c5e1dc9485d73412102a95/v2rayN/ServiceLib/Services/CoreConfig/Singbox/SingboxConfigTemplateService.cs)
- [Clash 周期增量与 API 路由](https://github.com/SagerNet/sing-box/blob/1ac1a339cb1223e9c70eae14c44411c75033c02d/experimental/clashapi/server.go)
- [Clash 全局累计量与活动连接快照](https://github.com/SagerNet/sing-box/blob/1ac1a339cb1223e9c70eae14c44411c75033c02d/experimental/clashapi/connections.go)
- [累计量保留与合并](https://github.com/SagerNet/sing-box/blob/1ac1a339cb1223e9c70eae14c44411c75033c02d/common/trafficcontrol/manager.go)
- [连接计数包含匹配出站](https://github.com/SagerNet/sing-box/blob/1ac1a339cb1223e9c70eae14c44411c75033c02d/common/trafficcontrol/tracker.go)
- [V2Ray 按出站统计及 reset 语义](https://github.com/SagerNet/sing-box/blob/1ac1a339cb1223e9c70eae14c44411c75033c02d/experimental/v2rayapi/stats.go)
- [原生 API 协议与分类字段](https://github.com/SagerNet/sing-box/blob/1ac1a339cb1223e9c70eae14c44411c75033c02d/daemon/started_service.proto)
- [原生事件、关闭缓存与状态读取](https://github.com/SagerNet/sing-box/blob/1ac1a339cb1223e9c70eae14c44411c75033c02d/daemon/started_service.go)
- [事件队列满时丢弃](https://github.com/SagerNet/sing/blob/v0.9.4/common/observable/subscriber.go)
- [GC 清理回调](https://github.com/SagerNet/sing/blob/v0.9.4/common/cleanup/cleanup.go)
- [客户端核心启动参数](https://github.com/fqfqgo/v2rayN/blob/95f68d9955c2324518c5e1dc9485d73412102a95/v2rayN/ServiceLib/Manager/CoreManager.cs)

公开记录仅包含版本、字段、合成实验数值与接口结论。真实节点名、地址、认证、IndexId、完整配置和凭据哈希不进入仓库或 Issue。
