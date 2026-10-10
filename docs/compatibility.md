# 支持矩阵

初版预览目标为 Windows x64 + v2rayN **7.25.1** + Xray 普通单节点。没有已经通过真实 Windows/v2rayN 会话的支持组合；编译成功和合成测试不能替代实机证明。

| 环境 / 场景 | 当前实现 | 验证层级 |
|---|---|---|
| Windows x64 托盘 / 本地 HTML | 已实现、待实机验收 | 交叉编译及 CI，状态见 STATUS |
| v2rayN 7.25.1 + Xray 普通单节点 | 严格只读发现 / 原始累计量 | 固定源码 + 合成回归，实机 blocked |
| VMess / VLESS / Trojan / Shadowsocks / SOCKS / HTTP | 仅对应单 proxy 出站映射 | 解析器合成回归；实机待验收 |
| V2free 7.25.1 的 Hysteria2 + Xray | 按精确 fork 生成规则匹配服务器和 transport auth | 固定源码 + 合成回归；用户只读观察不等于 Monitor 实机验收 |
| V2free 7.25.1 的 AnyTLS | 该 fork 强制使用 sing-box；当前采集器未实现 | 实际活动核心、版本及统计接口待核实 |
| sing-box | 未实现 | deferred |
| TUN / 策略组 / 多跳链 / 自定义配置 / 多实例 | 拒绝精确归属 | 不支持 |
| 服务商六列 CSV | 预览、独立账本、保守去重 | 合成输入测试 |
| TrafficMonitor / WebView2 | 未实现，也不依赖 | deferred |

## 发现与身份边界

指定数据根目录，不做全盘扫描；不修改 v2rayN。只读取 GUI 选择、ProfileItem 最小字段和生成配置，核对 v2rayN 父进程、Xray 子进程、创建时间、标准命令行、回环监听所属 PID、配置哈希/时间以及当前 IndexId。节点协议/服务器身份只在内存中比较，不保存凭据或完整配置。SQLite 连接以 ReadOnly 打开；SQLite 仍可参与已有库的共享内存锁协调。

每次 metrics 读取前后复核快照。生成配置晚于进程创建、相同进程绑定变化、重复服务器身份或无法确定目录时，保留缺口，不将旧进程流量归给新节点。首次接入只建立基线。该设计仍需要受控实机切换验证，不能当作“已证明所有时序”。

统计需在 v2rayN 手动启用流量统计或实时速度显示，并由 v2rayN 正常生成配置。HTTP 只访问该核心的回环 `/debug/vars`，不经过系统代理、不跟随重定向、不调用 reset。只取 `proxy` outbound，不加上 direct、inbound 或链路各跳。

## 待完成的有界实机验收

记录：Windows 版本/位数、v2rayN 7.25.1、实际 Xray 完整版本、普通代理模式、确切测试提交。

1. 全新 Monitor 数据目录，不导入 CSV；选择 v2rayN 后产生上传、下载和空闲流量。
2. 同一会话两端原始计数差与账本字节一致；首次接入不回填此前累计。
3. A/B 节点切换、Xray 重启（含新累计超过旧累计）、Monitor 重启、短断连与睡眠/唤醒。
4. 界面关闭继续采集，托盘退出停止，重复启动不双写；开机启动显式开关。
5. 跨午夜/时区查询、导出、大约一年实际尺度查询、空闲 CPU/内存与数据库增长。

以上尚未运行；完成前不发布正式 v0.1.0，不填写虚构的性能数字或兼容声明。

## V2free 7.25.1 本地兼容修复

客户端源码依据为 `fqfqgo/v2rayN` 的 `95f68d9955c2324518c5e1dc9485d73412102a95`，对应 ProductVersion `7.25.1+95f68d9955c2324518c5e1dc9485d73412102a95`。用户报告活动核心为 Xray `26.7.28 ced1f55 windows/amd64`，并成功只读获得 `stats.outbound.proxy` 上下行计数。这里只记录观察来源；开发环境未连接该 Windows 电脑，未核验该核心二进制的完整源码。

Hysteria2 的 `ConfigType=7` 对应 `protocol="hysteria"`。匹配内容包括 `settings.address/port/version=2`、`streamSettings.network="hysteria"`、`hysteriaSettings.version=2/auth`；`auth` 与选中 ProfileItem 的 `Password` 在内存中比较。错误端点、口令、协议、版本或内核偏好不能通过归属检查。持久节点键仍是实例与 `IndexId`，不使用协议名、端口或显示名称替代。

AnyTLS 的 `ConfigType=11` 不属于 Xray 白名单。在该 fork 中，核心选择先将 AnyTLS 归到 sing-box，再考虑配置的内核偏好；因此仅看到数据库 `CoreType=2` 也不能证明其运行在 Xray。继续适配前，至少还需核实实际 sing-box 完整版本、普通单节点有效配置和监听归属，以及只读统计的单位、累计/速率语义与代理范围。当前不新增未经验证的第二内核适配器。

普通权限 Monitor 会尝试以 `PROCESS_QUERY_LIMITED_INFORMATION` 读取提升权限进程的映像路径、创建时间和命令行，补足 WMI 的空字段。命令行回退使用动态解析的 Windows 原生查询；该查询不是所有 Windows 版本都保证支持的公开 API。路径、命令行、WMI/原生创建时间冲突，或任何必要信息仍无法核实时，显示明确的未知状态并保留缺口，不仅凭进程名或监听端口建立归属。此回退仍需真实 Windows 验收；不自动提权，不要求默认管理员运行。
