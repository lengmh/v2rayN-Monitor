# sing-box / AnyTLS 统计能力核验

核验日期：2026-10-13–14。基线：`32c5f02`。跟踪：[Issue #15](https://github.com/lengmh/v2rayN-Monitor/issues/15)。CSV 批次管理另见 [Issue #16](https://github.com/lengmh/v2rayN-Monitor/issues/16)。

## 当前结论

2026-10-14 用户已安装并手动重载 test.2，真实扩展、回环监听和 `reset=false` RPC 已核实。原 sing-box.exe 保留；原生事件方案仍有静默漏计与缓存清理限制。旧 preview.1 曾被实际复杂配置整体拒绝；用户退出后已启动真实 preview.2，独立验证库首接只建基线，持续持久化与被动字节守恒已确认。参考 RPC 前后夹测核对保存计数范围，未取得同刻精确读数。完整受控 proxy/direct、切换、重开及页面验收未完成，S0.3/S5.1–S5.3 仍 blocked。

PR #17 / S5.5 的 preview.2 先采集独立、可核实的 routed-proxy，持久披露其他范围未知，不删组、不改真实路由。test.2 按路由匹配 tag 计数；组选择 proxy 的量仍不属于该累计。多出站/实际成员由 #18 / S5.6、链/自定义配置由 #19 / S5.7 在后续独立 PR 中实现。见 [扩展范围](singbox-extension-scope.md)。

#18 的第一批已实现独立路由 tag 批量 RPC 读取，完整累计和缺失/重复/无效原因分别返回；原单 proxy 路径保持。真实核心回环副本通过多 tag、并发短连接和重复只读查询验证，路由组选择 proxy 只增长组 tag。生产采集器、扩展启用范围和账本未扩大，成员计量与多游标原子保存仍待实现；#19 尚未进入实现。API 接入、独立 proxy 读取及已记录的实机守恒继续保留通过，不因新扩展未完成而撤销。

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
| V2Ray API | 按 outbound 的进程内累计 bytes；不清零读取 | 可只统计 `proxy`；需构建和配置启用 | 原安装构建不可用；文件式扩展候选已通过 Windows 合成验证，实际客户端/节点未验收 |
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

另外，只读核对当前保存的活动路由含启用direct、proxy及block规则。没有启用完整配置模板。固定客户端生成器会遍历活动路由的启用业务规则并写入 sing-box 路由，但规则命中还受运行模式、顺序、规则类型和目标影响。

这些数量仅描述保存的路由配置，不能当作当前 AnyTLS 有效运行配置或某次直连命中的证明；当前真实核心仍是 Xray。副本测试使用合成路由，未调用客户端为真实 AnyTLS 节点生成配置。因此本轮没有宣称已复现真实节点的全部路由、TLS 默认值或节点归属，仍不能证明用户实际 AnyTLS 环境的统计只含 proxy。

## 实际 AnyTLS 会话的只读核验

AnyTLS 切换后作只读核验。确认活动 ProfileItem 类型为 AnyTLS，生成配置的单一 `proxy` 出站为 `anytls`，所选服务器、端口与认证在内存比较一致。实际 sing-box 为对应 v2rayN 的子进程；有限权限核对了两者映像路径、创建代次、标准启动命令 `run -c config.json --disable-color`，并确认 Clash 回环监听属于该核心 PID。配置生成时间早于核心创建，读取期间选择、配置、进程、监听与模式保持稳定。

接口报告 `sing-box 1.14.1`，TUN 关闭，Clash 实际模式为 `Rule`，默认出站为 `proxy`。有效生成配置的direct出站规则包含`Direct`模式及业务路由，匹配字段包括`domain_keyword`、`domain_suffix`、`ip_cidr`、`ip_is_private`和`rule_set`。这些是实际 AnyTLS 生成配置中的核心内路由，不是系统代理的绕过列表。

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

## 2026-10-14：文件式扩展核心原型

固定客户端 `95f68d9` 的 `CoreInfoManager` 按 `sing-box-client`、`sing-box` 顺序查找可执行文件。为保留 v2rayN 原配置生成流程，实验在公开 sing-box 固定提交 `1ac1a339` 增加有限的统计文件入口，并保留原 Windows 默认标签、增加 `with_v2ray_api`；Go 1.26.8 构建成功，版本明确标为 `1.14.1-monitor-test.1`，依赖版本未改。此核心是自行构建的实验版本，不是原安装核心的同 SHA256 副本，未宣称定制二进制全部源码与公开树等价。

拟安装仅新增 `bin/sing_box/sing-box-client.exe` 和旁边的 `monitor-stats.json`，保留原 `sing-box.exe` 与 v2rayN 主程序。原型仅在扩展文件名、单一主 `config.json` 且没有配置目录参数时读附加文件；附加文件只允许 `listen` 回环地址和非零端口，启用统计并固定只计 `proxy`。没有文件时统计不启用；未知字段、非回环地址、现有 V2Ray API 或缺少唯一 proxy 明确报错。测速/其他配置文件不读取此文件，主配置不写回。

| Windows 合成场景 | proxy 专用计数 | 验证 |
|---|---:|---|
| 本地 AnyTLS proxy 请求 | 上传 11,958 / 下载 9,733 bytes | 与核心全局增量相等，实际链路为 proxy |
| 核心内 direct 请求 | 0 / 0 bytes | 全局增加 11,958 / 9,733 bytes，实际链路为 direct |
| 绕过核心 | 0 / 0 bytes | 全局也不增加 |
| 不查询期间 1,200 个 proxy 短连接 | 上传 214,800 / 下载 157,200 bytes | 8 并发，全量保留，超过原生历史缓存边界 |
| 不查询期间 1,200 个 direct 短连接 | 0 / 0 bytes | 全局增加 214,800 / 157,200 bytes |
| 不查询期间 2,000 个等大小混合请求 | 上传 179,000 / 下载 131,000 bytes | proxy/direct 各 1,000 个，全局为两倍 |

查询始终使用兼容 V2Ray 的 StatsService、`reset=false`；各批次不查询期间没有统计 RPC，短连接结束后仍可取得累计，重复新查询进程不清零。计数仍是核心进程内存状态，重启从零开始；新代次必须建立新基线，不能据此宣称跨核心重启补回旧量。

同核心规范化比较确认除新增 `experimental.v2ray_api` 外 DNS、route、outbounds、inbounds、Clash 等字段一致，主配置源文件内容和修改时间未变。统计、Clash、入站监听同属扩展进程；仅有系统目录 conhost 控制台宿主，没有另一个代理子进程，停止后监听全部关闭。移开临时扩展文件后，同顺序规则选择原核心并成功运行合成 AnyTLS，原核心保持不变。文件选择测试是固定源码规则复现，没有启动第二份真实 v2rayN，不能代替真实客户端更新/退出/重载验收。

定向 Go 检查与合成运行测试通过。全部主动流量为本地合成 AnyTLS/HTTP，外网请求 0，未复制真实节点配置或认证。前后真实配置、选择、系统代理、监听归属及客户端/核心/Monitor 进程代次未变；所有临时核心已退出，安装目录未新增实验文件。

实验核心、示例端点文件、源码差异、许可证和摘要仅在专用临时目录的审阅包中，未作为正式资产发布。真实客户端加载、全部 TLS/传输等价、Monitor 发现/采集/事务/跨内核切换、托盘安装/卸载/升级及其他协议/TUN 均未由本轮验证。

## 历史接入前方案与确认边界

已验证的候选是文件式扩展核心 + V2Ray 逐 proxy 累计。下一节点先将其接入 Monitor 的只读发现、回环监听/进程代次核验、`reset=false` 读取及原子账本；保留 Xray 和核心切换基线。扩展的临时副本结果可以作为实现依据，不替代实际客户端/节点验收。

实际安装前检查客户端版本、已存在的优先核心文件、统计端口及当前文件占用；保留原核心和客户端设置，记录两个新增文件及摘要。必须经用户批准再安装和重新加载真实核心。回退是退出扩展核心后移开新增文件并重新加载原核心；客户端/核心升级后的优先文件选择与兼容性仍需核对，托盘安装流程尚未实现。

旧客户端启动参数补丁仅为历史草案，不再作为必须重建 v2rayN 的依据。原生事件方案仍不能保证累计完整；不采用速度积分、活动连接轮询或混合全局量冒充 proxy 累计。该方案写于安装前，当前安装与扩展加载以上方当前结论为准；S0.3/S5.1 完整真实节点验收仍 blocked；CSV #16 planned，插件 deferred。

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
- [客户端核心文件优先顺序](https://github.com/fqfqgo/v2rayN/blob/95f68d9955c2324518c5e1dc9485d73412102a95/v2rayN/ServiceLib/Manager/CoreInfoManager.cs)
- [核心读配置与合并入口](https://github.com/SagerNet/sing-box/blob/1ac1a339cb1223e9c70eae14c44411c75033c02d/cmd/sing-box/cmd_run.go)

公开记录仅包含版本、字段、合成实验数值与接口结论。真实节点名、地址、认证、IndexId、完整配置和凭据哈希不进入仓库或 Issue。

## Monitor 0.2.0-preview.1 接入

起点 a612ca5；同步 main 的 dc17de3 README 插图后实现 AnyTLS 采集，预览标识 0.2.0-preview.1。
新增精确客户端 95f68d9 的有效配置比较、有限 V2Ray QueryStats 读取和扩展 SHA256 检查。
仅读取 proxy 原始 int64，reset=false；不使用 Clash 全局量或原生连接历史补账。
复用原活动 IndexId、采样前后快照、PID/创建时间/监听绑定及原子账本，API/Schema 未变。

扩展更新为 1.14.1-monitor-test.2，新代次注册真实零计数，空闲即可建立基线。
源码补丁和构建说明在 extensions/sing-box；实验核心保留原 Windows 构建标签及依赖清单。

Windows 合成父进程 + 真实扩展/Xray 副本 + 生产 WindowsDiscovery + 独立 SQLite 验证通过：
AnyTLS 单次 proxy 增量上传 162 / 下载 149 bytes 与账本一致；100 次 direct 及绕过核心不增长；
采集器重开后找回 1,200 短连接上传 194,400 / 下载 178,800 bytes，记为恢复但未分时；
核心重启、新侧文件未重载和 sing-box → Xray → sing-box 均正确重新建立基线。
Xray 合成段原始增量为上传 0 / 下载 151 bytes，账本按实际返回值守恒；
这是切换验证，不补充宣称新的 Xray 双向场景验收。测试夹具曾被核心回环防护拒绝，
仅在临时服务端放行指定目标；未修改正式路由。

最终 Release 构建零警告零错误，Core 248 / Server 31 / 前端 49 本地回归通过；扩展补丁与编译源码一致性检查通过。
没有新增 NuGet/npm 依赖，没有数据库迁移。正式客户端/核心/Monitor 进程、配置、选择文件和
系统代理前后未变；临时进程全部退出。仍未启动真实 v2rayN 加载扩展，
实际 AnyTLS 的完整 TLS/传输、读数与真实账本、托盘安装/升级/退出待用户批准后验收。
S0.3/S5.1–S5.3 保留实际接入缺口；CSV #16 planned，插件 deferred，未合并或发布 v0.2.0。


[扩展源码补丁与构建说明](../extensions/sing-box/README.md)。
