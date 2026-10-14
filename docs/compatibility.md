# 支持矩阵

v0.1.0 目标为 Windows x64 + v2rayN **7.25.1** + Xray 普通单节点。2026-10-12 无图标预览版已有整体人工使用确认，未提供新的逐项测试记录。下表保留此前按场景记录的证据，不将整体确认推断为每种协议、切换时序或设备组合都经过实测。新增图标仅有构建/资源验证；编译和合成测试不能替代实机证明。

2026-10-13 的 `v0.1.1` 修复版延续上述支持边界，只整合日期默认分桶与重叠 CSV 增量导入，不增加协议/核心兼容范围。相关修复已有整体人工使用确认，正式发布范围已确定；这是基于此前预览使用的整体认可，不补充推断逐项实机结果。

| 环境 / 场景 | 当前实现 | 验证层级 |
|---|---|---|
| Windows x64 托盘 / 本地 HTML | 已实现 | 用户报告 `a3f2d80` 人工页面检查、关闭页面继续采集通过；无图标预览整体确认见上文，未追加逐项记录 |
| v2rayN 7.25.1 + Xray 普通单节点 | 只读发现 / 原始累计量 / 按活动配置归属 | 固定源码 + 合成回归；用户报告 `a3f2d80` 的重复配置切换失败，新规则的逐项实机记录未补充 |
| VMess / VLESS / Trojan / Shadowsocks / SOCKS / HTTP | 仅对应单 proxy 出站映射 | 解析器合成回归；实机待验收 |
| V2free 7.25.1 的 Hysteria2 + Xray | 按精确 fork 生成规则匹配服务器和 transport auth | 用户报告 `a3f2d80` 普通权限 Monitor 采集提升权限客户端、首次基线、连续记账、Monitor 重启恢复及字节守恒通过 |
| V2free 7.25.1 的 AnyTLS | 该 fork 强制使用 sing-box；指定 test.2 扩展的只读路径已实现 | 已安装并重载扩展；真实 preview.2 已通过首接基线、持续持久化与被动守恒，完整受控节点验收仍未完成 |
| sing-box 的独立 AnyTLS `proxy` 与其他出站共存 | preview.2 / S5.5 仅采集直接路由到 `proxy` 的累计，披露部分覆盖 | 解析/账本/API/DOM、真实核心副本与 c8ea870 双平台 CI 通过；实际配置不再整体拒绝，4轮参考 RPC 范围核对通过，未取得同刻精确对照；组/其他出站仍未知 |
| selector / urltest 实际成员及全量多出站 | #18 / S5.6 planned | 现有 test.2 按路由组 tag 计数，组选 `proxy` 也不进入 routed-proxy 累计；组/其他出站未知 |
| 代理链 / 自定义配置 | #19 / S5.7 planned | 独立 `proxy` 有 detour 仍拒绝；其他链的存在可与部分采集共存，但不计其流量；任意配置来源和订阅身份未核实 |
| TUN / 多实例 | 拒绝精确归属 | 不支持 |
| 服务商六列 CSV | 精确十进制计费量、预览、独立账本、同来源导出快照按出现次数增量去重 | 合成输入/事务回归；真实 CSV 未导入开发库或上传；相关修复已有整体人工使用认可，未补充逐项真实CSV提交记录 |
| TrafficMonitor / WebView2 | 未实现，也不依赖 | deferred |

## 发现与身份边界

指定数据根目录，不做全盘扫描；不修改 v2rayN。只读取 GUI 选择、ProfileItem 最小字段和生成配置，核对 v2rayN 父进程、Xray 子进程、创建时间、标准命令行、回环监听所属 PID、配置哈希/时间以及当前 IndexId。节点协议/服务器身份只在内存中比较，不保存凭据或完整配置。SQLite 连接以 ReadOnly 打开；SQLite 仍可参与已有库的共享内存锁协调。

本机采用“按 v2rayN 活动配置归属”的口径，历史身份为 `instance_id + IndexId`。同订阅或跨订阅中的重复连接配置不单独构成拒绝条件；各 IndexId 分账，名称仅用于展示。此口径不承诺从核心侧独立区分连接特征完全相同的逻辑配置。

每次 metrics 读取前后复核活动项、有效配置、核心进程与监听归属。首次接入和 IndexId 改变后建立基线；即使生成配置内容相同，也不跨 IndexId 沿用旧累计。已观察到选择、配置或映射变化时，明确保留缺口，并持久失效旧游标的连续性，避免 A → B → A 后把不确定增量恢复给 A。仅统计接口暂时失联且身份仍连续时，才允许将恢复量单列为未分时。

生成配置晚于进程创建、同一进程的有效配置或监听绑定变化、活动项与配置不符或归属信息不足时暂停采集。上述保护仍需要新提交的受控实机切换验证，不能当作已证明所有时序。

有效连接比较除端点和认证外，还核对所选协议参数、传输及 TLS/reality 字段，并按固定源码应用 GUI 默认值；包括 VLESS encryption、XHTTP path 与 extra。mKCP、自定义 PEM 证书、自定义 Finalmask、Hysteria Realm URL，以及绑定网卡并使用 XHTTP downloadSettings 的组合尚未纳入比较，当前明确返回 `unsupported_connection`。普通 Hysteria2 的带宽、端口跳跃及 Salamander 选项按固定生成规则比较。

统计需在 v2rayN 手动启用流量统计或实时速度显示，并由 v2rayN 正常生成配置。HTTP 只访问该核心的回环 `/debug/vars`，不经过系统代理、不跟随重定向、不调用 reset。只取 `proxy` outbound，不加上 direct、inbound 或链路各跳。

## 待完成的有界实机验收

记录：Windows 版本/位数、v2rayN 7.25.1、实际 Xray 完整版本、普通代理模式、确切测试提交。

1. 全新 Monitor 数据目录，不导入 CSV；选择 v2rayN 后产生上传、下载和空闲流量。
2. 同一会话两端原始计数差与账本字节一致；首次接入不回填此前累计。
3. A/B 节点切换、Xray 重启（含新累计超过旧累计）、Monitor 重启、短断连与睡眠/唤醒。
4. 界面关闭继续采集，托盘退出停止，重复启动不双写；开机启动显式开关。
5. 跨午夜/时区查询、导出、大约一年实际尺度查询、空闲 CPU/内存与数据库增长。

用户在 `a3f2d80` 已报告首次基线、连续记账、Monitor 重启恢复、字节守恒、页面操作和关闭页面继续采集通过；A/B 切换失败触发修复。`v0.1.0` 已按 2026-10-12 的整体验收与授权发布，未覆盖的逐项场景继续保留为限制；`v0.1.1` 已发布，2026-10-13的整体人工确认保留；未新增最终整合包的本机运行或逐项实测记录，不填写虚构的性能数字或兼容声明。

## V2free 7.25.1 本地兼容修复

客户端源码依据为 `fqfqgo/v2rayN` 的 `95f68d9955c2324518c5e1dc9485d73412102a95`，对应 ProductVersion `7.25.1+95f68d9955c2324518c5e1dc9485d73412102a95`。用户报告活动核心为 Xray `26.7.28 ced1f55 windows/amd64`，并成功只读获得 `stats.outbound.proxy` 上下行计数。这里只记录观察来源；开发环境未连接该 Windows 电脑，未核验该核心二进制的完整源码。

Hysteria2 的 `ConfigType=7` 对应 `protocol="hysteria"`。匹配内容包括 `settings.address/port/version=2`、`streamSettings.network="hysteria"`、`hysteriaSettings.version=2/auth`；`auth` 与选中 ProfileItem 的 `Password` 在内存中比较。错误端点、口令、协议、版本或内核偏好不能通过归属检查。持久节点键仍是实例与 `IndexId`，不使用协议名、端口或显示名称替代。

AnyTLS 的 `ConfigType=11` 不属于 Xray 白名单。在该 fork 中，核心选择先将 AnyTLS 归到 sing-box，再考虑配置的内核偏好；因此仅看到数据库 `CoreType=2` 也不能证明其运行在 Xray。继续适配前，至少还需核实实际 sing-box 完整版本、普通单节点有效配置和监听归属，以及只读统计的单位、累计/速率语义与代理范围。当前不新增未经验证的第二内核适配器。

普通权限 Monitor 会尝试以 `PROCESS_QUERY_LIMITED_INFORMATION` 读取提升权限进程的映像路径、创建时间和命令行，补足 WMI 的空字段。命令行回退使用动态解析的 Windows 原生查询；该查询不是所有 Windows 版本都保证支持的公开 API。路径、命令行、WMI/原生创建时间冲突，或任何必要信息仍无法核实时，显示明确的未知状态并保留缺口，不仅凭进程名或监听端口建立归属。用户已报告 `a3f2d80` 在其 Windows 环境可用；其他系统版本和权限组合未据此宣称通过。不自动提权，不要求默认管理员运行。

## 0.2.0-preview.2 有界支持

精确客户端 ProductVersion 为 7.25.1+95f68d9955c2324518c5e1dc9485d73412102a95；
扩展二进制摘要及源码见 [可选扩展](../extensions/sing-box/README.md)。
只核实生成配置中的唯一独立 AnyTLS `proxy`。其他代理、selector、urltest 或其他链可共存，但其流量不采集，状态、历史和 CSV 持续披露未知范围。拒绝目标 `proxy` 的 detour、TUN、非空 endpoints、自定义传输/会话参数和已有自定义 V2Ray API。统计文件纳入有效配置代次，改动后须重载；本轮不修改侧文件或重载真实核心。

TLS 比较按固定生成器处理 SNI/Host、ALPN、显式 uTLS、insecure、可识别 SPKI Base64 pins、
fragment/record_fragment。无显式 Fingerprint 时不把 DefFingerprint 误加到 sing-box；
固定生成器会丢弃 Xray hex pin，Monitor 采用相同规则。证书 PEM、ECH 和绑定网卡/源地址
需要额外等价核验，当前返回 unsupported_connection。AnyTLS 未使用的数据库字段不充当运行归属证据。

test.2 已实际加载；用户退出旧 preview.1 后启动真实 preview.2，独立验证库首接只建基线，持续累计差与账本增量守恒。参考 `reset=false` RPC 前后夹测核对保存计数范围，不是同刻精确对照。受控实际 proxy/direct、切换、重开恢复及真实页面目视验收仍待完成；当前页面仅核对 HTTP/版本/会话保护。扩大范围、分批口径和授权见 [扩展范围](singbox-extension-scope.md)。
正式 v0.1.1 仍保持既有支持矩阵，预览功能不等于 v0.2.0 正式发布。
