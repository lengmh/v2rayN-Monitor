# sing-box / AnyTLS 统计能力核验

核验日期：2026-10-13。基线：`32c5f02`。跟踪：[Issue #15](https://github.com/lengmh/v2rayN-Monitor/issues/15)。CSV 批次管理另见 [Issue #16](https://github.com/lengmh/v2rayN-Monitor/issues/16)。

## 当前结论

当前安装的 sing-box 只能确认 Clash 全局累计量，无法通过已暴露接口把 direct 排除后取得 proxy 累计量。因此 S0.3 与 S5.1 暂为 blocked，尚未开始 Monitor 的 AnyTLS 采集适配，不宣称步骤 1–3 全部完成。

本轮完成两条 Issue 登记、只读版本核验、固定源码核读及安装二进制的隔离合成实验。实际 v2rayN 仍运行 Xray；当前运行配置不是 AnyTLS 运行证据。节点、路由、客户端配置和运行核心没有由本轮操作改变。

## 精确版本与证据层级

| 对象 | 核实结果 | 证据 |
|---|---|---|
| 定制客户端 | `7.25.1+95f68d9955c2324518c5e1dc9485d73412102a95` | 本机 EXE ProductVersion；对应生成器源码 |
| 安装 sing-box | `1.14.1`；Revision `1ac1a339cb1223e9c70eae14c44411c75033c02d`；Go `1.26.8`；Windows amd64 | 本机二进制 `version` 输出 |
| Clash API | 构建标签含 `with_clash_api`，隔离进程可读 | 安装二进制合成运行 |
| V2Ray API | 构建标签未列出；加入该 API 的合成配置检查明确拒绝，提示未编入构建 | 安装二进制 `check`，未启动该配置 |
| 当前实际核心 | Xray；统计开关开启，TUN 关闭 | 本机配置字段与进程只读核对；不作为 AnyTLS 验收 |

二进制报告的 Revision 在公开上游可解析；固定源码用于解释接口行为，不凭版本串宣称整个定制二进制与公开树完全一致。关键计数行为另由安装二进制实验确认。

## 可用接口及限制

| 路径 | 原始语义 | 计量范围 | 本机节点账本结论 |
|---|---|---|---|
| Clash `/traffic` | 每个一秒周期的累计差 | 全局，包含 direct | 不当作累计计数；无法补齐离线周期，也不能排除 direct |
| Clash `/connections` 的 `uploadTotal` / `downloadTotal` | 原始整数累计 bytes；连接结束后保留 | 全局，包含 direct | 不能无条件归给活动 AnyTLS 节点 |
| Clash 活动连接列表 | 当前活动连接及各自计数 | 可显示链路，但已结束短连接不在列表 | 不通过轮询推断完整节点累计量 |
| V2Ray API | 源码提供按 outbound 计数与不清零读取 | 可按 `proxy` 统计；需构建和有效配置启用 | 当前安装构建不可用，尚无该路径实机证据 |

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

## 下一步接入方案与确认边界

要保留现有路由并实现可靠节点累计量，需要额外提供按 outbound 的累计统计能力。可优先核验同版本含 `with_v2ray_api` 的核心构建，并为客户端确定持久启用 `stats.outbounds=["proxy"]` 的接入点；所有统计查询使用 `reset=false`。

这条候选路径尚未完成构建、配置接入或实机验证。当前客户端没有核实过的仅追加统计设置入口，因此接入设计必须同时说明生成规则、入站/DNS/路由保留、监听绑定、备份与恢复方式，不能以完整模板或单次改写生成配置代替。

当前无需因核验而先更换核心。已完成同二进制副本的 proxy/direct 隔离实验；后续若保持严格逐 proxy 累计要求，再审阅是否准备额外核心构建及持久统计接入方案。若用户选择与 v2rayN 一致的全局量按活动节点归属口径，应先在 Issue 明确混入 direct 的限制，并更新验收条件，不能宣称该口径等同 Xray 的 proxy 计量。替换已安装核心、改动客户端设置、切换节点及重启真实核心仍分别按具体操作授权。

如果保持当前构建和配置，Issue #15 保留 blocked；不会改用速度积分、活动连接轮询或全局量冒充精确节点累计。CSV Issue #16 保持 planned，插件继续 deferred。

## 固定源码

- [客户端 Clash API 生成](https://github.com/fqfqgo/v2rayN/blob/95f68d9955c2324518c5e1dc9485d73412102a95/v2rayN/ServiceLib/Services/CoreConfig/Singbox/SingboxStatisticService.cs)
- [客户端完整模板语义](https://github.com/fqfqgo/v2rayN/blob/95f68d9955c2324518c5e1dc9485d73412102a95/v2rayN/ServiceLib/Services/CoreConfig/Singbox/SingboxConfigTemplateService.cs)
- [Clash 周期增量与 API 路由](https://github.com/SagerNet/sing-box/blob/1ac1a339cb1223e9c70eae14c44411c75033c02d/experimental/clashapi/server.go)
- [Clash 全局累计量与活动连接快照](https://github.com/SagerNet/sing-box/blob/1ac1a339cb1223e9c70eae14c44411c75033c02d/experimental/clashapi/connections.go)
- [累计量保留与合并](https://github.com/SagerNet/sing-box/blob/1ac1a339cb1223e9c70eae14c44411c75033c02d/common/trafficcontrol/manager.go)
- [连接计数包含匹配出站](https://github.com/SagerNet/sing-box/blob/1ac1a339cb1223e9c70eae14c44411c75033c02d/common/trafficcontrol/tracker.go)
- [V2Ray 按出站统计及 reset 语义](https://github.com/SagerNet/sing-box/blob/1ac1a339cb1223e9c70eae14c44411c75033c02d/experimental/v2rayapi/stats.go)

公开记录仅包含版本、字段、合成实验数值与接口结论。真实节点名、地址、认证、IndexId、完整配置和凭据哈希不进入仓库或 Issue。
