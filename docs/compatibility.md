# 支持矩阵

## 当前状态

产品状态截至 2026-10-27：v0.2.0 已发布，38 项为 done 34／in_progress 0／planned 1／deferred 3。G1–G5、S5.8 和发布后交付已完成，#19 冻结，插件 deferred。公开版本／CI 见 [STATUS](STATUS.md)，验收层级见 [统一清单](acceptance-reconciliation.md)，包与升级要求见 [发布说明](release-v0.2.0.md)。

支持目标为 Windows x64、固定 v2rayN／V2free 7.25.1 标准生成配置及已核实的 Xray／配套 sing-box。basic 使用原生 Xray；full 增加配套 sing-box；extended 才按需启用独立探测。包内文件存在或摘要匹配不代表组件已运行。test.5 已含普通统计与探测，不因能力分级重新安装。

| 环境／场景 | 实现及验证层级 | 限制 |
|---|---|---|
| Windows 托盘／本地 HTML | 页面使用、闭页继续采集、单实例及退出重开已有人工／实机证据；S0 唤醒及累计对账通过 | 不是所有 Windows／权限组合通过；启动项前后不存在，未实测启用→禁用往返 |
| 可选联动快捷方式 | 保留数据目录及账本模式；实际客户端已运行时自动采集、重复点击单写者与对账通过 | 冷启动、仅 Monitor 运行及 UAC 允许／取消未新增逐项实测；不监听原入口 |
| Xray 普通单节点 | 只读发现、原始累计、活动配置身份；Hysteria2 路径已有实机证据 | VMess／VLESS／Trojan／Shadowsocks／SOCKS／HTTP 解析器有合成回归，未据此宣称各协议均有实机记录 |
| Xray 独立 proxy 与多出站／balancer | legacy_proxy 部分覆盖与后续标准多成员记账均有实机对账；组不重复相加 | 单 proxy 计数包含组选中该出站的连接；v1 入口仍只采 proxy，探测与普通出站分开 |
| Xray PolicyGroup（101） | 订阅筛选／显式叶成员配置绑定，actual_member 不借用 legacy_proxy；速率、页面及历史入口已验收 | 链、嵌套组和其他配置来源不扩大；普通 GUI 改选造成重启不能证明同代次旧 TCP 轮换 |
| Xray 同代次轮换／旧 TCP | 同二进制合成回环 roundRobin 证明新 A/B、旧 TCP 留原成员；固定客户端存在标准生成路径 | 真实客户端完整联合专项未确认，仅可选；RoutingService 不是必要前置，不推进 #19 |
| 原生 sing-box API | Clash 全局累计可含 direct，事件流有静默漏计／缓存清理限制，不能代替可靠成员累计 | 不宣称所有 sing-box 构建都支持精确统计 |
| sing-box test.2 routed-proxy | 唯一独立 AnyTLS proxy、部分覆盖披露，真实首接／持续持久化守恒通过 | 组、其他出站、direct 不混入；参考 RPC 前后夹测不是同刻精确对照 |
| sing-box test.4／test.5 实际成员 | 标准 selector／urltest／独立出站成员、分离汇总、同代次重开恢复与持续记账已有实机证据 | 仍需已核实的独立 AnyTLS proxy 锚点；其他成员按配置摘要／tag，不猜 IndexId；不把被动窗口写成所有成员受控测试 |
| 同账本跨核心历史／普通历史维度 | 默认代理、成员、业务总量、直连、未知、单 proxy 和路由出站独立查询；目录／分页／导出一致，页面已确认 | 查询历史不增加当前核心覆盖，旧单节点实例隐藏入口；不将不同维度相加 |
| 共用 UI／倍率快照 | 四视图、可信名称／组标签、配对对照、部分估算、精确复制、下钻与刷新已交付，页面及隔离回归通过 | 无可靠身份不归并；旧未知倍率不补，恢复不估算，服务商计费不重算；旧库元数据须显式离线初始化 |
| 服务商六列 CSV | 精确小数、预览、事务导入、同来源稳定快照按出现次数增量去重；有限语义与合成回归通过 | 无设备／方向保持未知，不推断差额归属，不承诺任意账单修订的普适去重 |
| CSV 批次引用／撤销／原文件另存 | occurrence、token 与共享保护、旧库原生备份升级及页面交互已验收；原 File／BOM 合成字节回归通过 | 不强制升级真实 provider；旧批次未知引用不可撤销。showSaveFilePicker 支持／取消／失败分别报告，人工另存确认不冒称独立文件 SHA 核验 |
| sing-box URLTest 独立探测 | test.5＋显式 v3；自然非零成员与独立总量、历史／恢复、页面已有实机证据，S5.8 done | 已知零与缺失未知分开，历史缺口保留；目标 TLS／HTTP 流字节不等于服务商计费 |
| Xray Observatory／Burst Observatory | monitor-test.1 精确身份、同响应记账及 v3；原生回归／回环核心／隔离账本通过 | 尚无真实客户端启用结论；原定制二进制源码身份未核实，不承诺等价替换。与出站可能重叠，不相加或直接相减；额外直连检查、DNS／远程规则集等未覆盖 |
| 核心下载／安装／恢复、sidecar、离线交接 | 固定下载及逐文件校验、可见原生确认、E1 合成 v1→v2 重开、长路径及隔离联合路径通过，S5.9 done | G4 未执行正式核心安装／sidecar 写入／重载；文件身份、账本模式、运行统计和探测分别核验。安装仍要求可信目标、停止、备份及原生确认 |
| 日级历史汇总 | 真实完整输出对账、跨进程复用及闭页资源通过，全年合成输出／分页／单日重算通过，S4.3 done | 仅显式初始化的 v3 展示账本、local_proxy、无筛选、上海时区、月／年非零首屏；其他路径及后续页保留原始查询。空汇总首次计算／构建成本单列，非浏览器绘制计时 |
| 代理链／整份配置来源 | #19／S5.7 planned／冻结 | 同连接串联代理与完整模板／其他文件／启动参数来源待未来独立需求；目标 proxy 有 detour 仍拒绝，其他链可共存但不计其流量 |
| TUN／多实例／TrafficMonitor／WebView2 | TUN／多实例精确归属不支持；插件 deferred；运行不依赖 WebView2 | 不扩展当前交付前置 |

G1 按分层证据组合完成，不称完整真实客户端矩阵；A/B 全过程完整同步回执未定位及实际同代次旧 TCP 联合专项未确认两项仅列可选。旧版本的阻塞、待验或未启用记录不回滚当前已完成状态。v0.1.0／v0.1.1 当时仅覆盖 Xray 普通单节点，整体发布确认不补造逐协议或最终整合包的逐项实机记录。

## 普通单节点入口的发现与身份边界

以下说明 v1 单 proxy 路径；多成员模式按上表及独立范围契约核验，不借单节点口径扩大覆盖。

指定数据根目录，不做全盘扫描；不修改 v2rayN。只读取 GUI 选择、ProfileItem 最小字段和生成配置，核对 v2rayN 父进程、Xray 子进程、创建时间、标准命令行、回环监听所属 PID、配置哈希/时间以及当前 IndexId。节点协议/服务器身份只在内存中比较，不保存凭据或完整配置。SQLite 连接以 ReadOnly 打开；SQLite 仍可参与已有库的共享内存锁协调。

本机采用“按 v2rayN 活动配置归属”的口径，历史身份为 `instance_id + IndexId`。同订阅或跨订阅中的重复连接配置不单独构成拒绝条件；各 IndexId 分账，名称仅用于展示。此口径不承诺从核心侧独立区分连接特征完全相同的逻辑配置。

每次 metrics 读取前后复核活动项、有效配置、核心进程与监听归属。首次接入和 IndexId 改变后建立基线；即使生成配置内容相同，也不跨 IndexId 沿用旧累计。已观察到选择、配置或映射变化时，明确保留缺口，并持久失效旧游标的连续性，避免 A → B → A 后把不确定增量恢复给 A。仅统计接口暂时失联且身份仍连续时，才允许将恢复量单列为未分时。

生成配置晚于进程创建、同一进程的有效配置或监听绑定变化、活动项与配置不符或归属信息不足时暂停采集。G1 已按分层证据完成验收；上述保护不代表已证明所有时序，未完整记录的客户端联合专项仍为可选。

有效连接比较除端点和认证外，还核对所选协议参数、传输及 TLS/reality 字段，并按固定源码应用 GUI 默认值；包括 VLESS encryption、XHTTP path 与 extra。mKCP、自定义 PEM 证书、自定义 Finalmask、Hysteria Realm URL，以及绑定网卡并使用 XHTTP downloadSettings 的组合尚未纳入比较，当前明确返回 `unsupported_connection`。普通 Hysteria2 的带宽、端口跳跃及 Salamander 选项按固定生成规则比较。

统计需在 v2rayN 手动启用流量统计或实时速度显示，并由 v2rayN 正常生成配置。HTTP 只访问该核心的回环 `/debug/vars`，不经过系统代理、不跟随重定向、不调用 reset。只取 `proxy` outbound，不加上 direct、inbound 或链路各跳。


## V2free 7.25.1 本地兼容修复

客户端源码依据为 `fqfqgo/v2rayN` 的 `95f68d9955c2324518c5e1dc9485d73412102a95`，对应 ProductVersion `7.25.1+95f68d9955c2324518c5e1dc9485d73412102a95`。历史实机报告的核心标识为 Xray `26.7.28 ced1f55 windows/amd64`，并成功只读获得 `stats.outbound.proxy` 上下行计数。这里只记录观察来源；开发环境未连接该 Windows 电脑，未核验该核心二进制的完整源码。

Hysteria2 的 `ConfigType=7` 对应 `protocol="hysteria"`。匹配内容包括 `settings.address/port/version=2`、`streamSettings.network="hysteria"`、`hysteriaSettings.version=2/auth`；`auth` 与选中 ProfileItem 的 `Password` 在内存中比较。错误端点、口令、协议、版本或内核偏好不能通过归属检查。持久节点键仍是实例与 `IndexId`，不使用协议名、端口或显示名称替代。

AnyTLS 的 `ConfigType=11` 不属于 Xray 白名单。在该 fork 中，核心选择先将 AnyTLS 归到 sing-box，再考虑配置的内核偏好；因此仅看到数据库 `CoreType=2` 也不能证明其运行在 Xray。第二内核必须独立核实完整版本、有效配置、监听归属及计数语义；已完成的配套扩展路径见本页矩阵，不能据此宣称原生 API 足够。

普通权限 Monitor 会尝试以 `PROCESS_QUERY_LIMITED_INFORMATION` 读取提升权限进程的映像路径、创建时间和命令行，补足 WMI 的空字段。命令行回退使用动态解析的 Windows 原生查询；该查询不是所有 Windows 版本都保证支持的公开 API。路径、命令行、WMI/原生创建时间冲突，或任何必要信息仍无法核实时，显示明确的未知状态并保留缺口，不仅凭进程名或监听端口建立归属。`a3f2d80` 已有 Windows 人工使用报告；其他系统版本和权限组合未据此宣称通过。不自动提权，不要求默认管理员运行。


## test.2 历史路径的比较规则

test.2 的统计文件纳入有效配置代次，改动须重载。仅核实唯一独立 AnyTLS proxy；拒绝目标 proxy 的 detour、TUN、非空 endpoints、自定义传输／会话参数及已有自定义 V2Ray API。其他出站可以共存，但不借 test.4／test.5 能力补写其覆盖。

TLS 比较按固定生成器处理 SNI／Host、ALPN、显式 uTLS、insecure、可识别 SPKI Base64 pins、fragment／record_fragment。无显式 Fingerprint 时不把 DefFingerprint 误加到 sing-box；固定生成器丢弃 Xray hex pin，Monitor 遵循同一规则。证书 PEM、ECH、绑定网卡／源地址仍需额外等价核验，返回 unsupported_connection；AnyTLS 未使用的数据库字段不充当运行归属证据。
