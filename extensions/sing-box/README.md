# 可选 sing-box 统计扩展

Issue #15 的实验核心 `1.14.1-monitor-test.2`，针对公开 sing-box
`1ac1a339cb1223e9c70eae14c44411c75033c02d`，使用 Go 1.26.8 / Windows amd64 / CGO=0。
这是单独构建的 GPLv3 核心，许可见 [LICENSE](LICENSE)；Monitor 原创代码继续采用 MIT。
原客户端无需重建，原安装的 sing-box.exe 保留。此目录只有源码补丁，没有第三方二进制。

## 改动

- 按固定客户端的优先规则使用 sing-box-client.exe。
- 仅主 config.json 读取程序旁 monitor-stats.json；文件只允许 listen。
- 端点固定为数值回环地址和非零端口；统计仅启用 proxy 出站，源配置不写回。
- 新核心先注册两个真实零计数，便于空闲基线；累计仍在当前核心内存中，重启需新基线。
- 计数按路由匹配 tag；selector/urltest 选中 proxy 也不进入 proxy 累计。preview.2 可在其他出站存在时独立采集 routed-proxy，并明确披露未知范围；全量/成员统计另见 [扩展计划](../../docs/singbox-extension-scope.md)。

Monitor 默认 v1 路径只接受经核实的 test.2 文件 SHA256：

`C053F7C9D82C7FC241E60045ADFB399A44F4FDEB4E12CEF91C5F95244E3301BA`

## 构建复现

在上述固定源码目录使用 `git apply --unidiff-zero monitor.patch` 应用 [monitor.patch](monitor.patch)。使用原
release/DEFAULT_BUILD_TAGS_WINDOWS 加 with_v2ray_api，保持 go.mod/go.sum 不变。
以下 PowerShell 命令使用已准备好的 Go 1.26.8，不安装或升级依赖工具：

```powershell
$env:CGO_ENABLED = '0'
$env:GOOS = 'windows'
$env:GOARCH = 'amd64'
$env:GOTOOLCHAIN = 'local'
$tags = (Get-Content release/DEFAULT_BUILD_TAGS_WINDOWS -Raw).Trim() + ',with_v2ray_api'
$flags = (Get-Content release/LDFLAGS -Raw).Trim()
go test -mod=readonly -ldflags $flags -tags $tags -run '^TestMonitorStatsEndpoint$' ./cmd/sing-box
go build -mod=readonly -tags $tags -trimpath -ldflags ($flags + ' -s -w -X github.com/sagernet/sing-box/constant.Version=1.14.1-monitor-test.2') -o sing-box-client.exe ./cmd/sing-box
```

## 安装与回退边界

2026-10-14 用户已批准安装 test.2，并手动重载实际客户端，回环 RPC 可读。旧 Monitor preview.1 因其他出站整体拒绝，真实记账尚未通过。后续新扩展安装前确认空闲端口、文件摘要和实际客户端版本，再经具体授权更新运行文件并择时让 v2rayN 正常重载；此前安装授权不自动涵盖新部署。
文件示例为 `{"listen":"127.0.0.1:待选端口"}`，必须将端口替换为核实后的数字。
不会写订阅、节点数据库、路由或 guiNConfig.json。

回退需先正常退出扩展核心，移开本次新增的两个文件，再由客户端加载原 sing-box.exe。
不要在核心运行时覆盖文件。真实客户端选择/升级/退出和真实 AnyTLS 节点仍待验收。

## Issue #18：实际成员实验（未部署）

`member-stats.patch` 是叠加于 test.2 的独立实验补丁，不替换 `monitor.patch`。候选版本为 `1.14.1-monitor-test.3`；现装 test.2、侧文件的仅 listen 约束和 Monitor 的二进制白名单均不变。只有隔离配置显式设置 `experimental.v2ray_api.stats.monitor_members` 时才启用新路径；现有文件式侧文件没有开启成员统计。

```powershell
git apply --unidiff-zero monitor.patch
git apply --unidiff-zero member-stats.patch
# 使用上文同一固定源码、Go 和 Windows 构建标签
go test -mod=readonly -ldflags $flags -tags $tags -run '^TestMonitor' -count=1 -v ./cmd/sing-box
go build -mod=readonly -tags $tags -trimpath -ldflags ($flags + ' -s -w -X github.com/sagernet/sing-box/constant.Version=1.14.1-monitor-test.3') -o sing-box-client.exe ./cmd/sing-box
```

核心在 selector/urltest 的实际拨号结果上附加成员证据，ConnectionManager 只从最终用于传输的返回连接绑定归属；并行拨号的成功但未采用连接不能提前绑定业务流。selector 直接转交成员 handler 时绑定那个实际 handler。嵌套组保留返回连接的叶成员，首次绑定后不随组选择改变。累计在同一业务连接的读写路径直接更新，不使用 `Now()`、活动连接轮询或事件消费队列。

| RPC 前缀 | 语义 |
|---|---|
| `outbound>>>{tag}>>>traffic` | 保留原路由 tag 维度；组可包含 direct，不是实际成员 |
| `monitor>>>member>>>{tag}>>>traffic` | 已核实独立叶成员的业务连接字节 |
| `monitor>>>business>>>traffic` | 已知代理业务字节，只计一次；不能再加成员或组量 |
| `monitor>>>excluded>>>traffic` | 独立 direct 的排除量，不计代理业务 |
| `monitor>>>unknown>>>traffic` | 未注册/未解析成员、链及未绑定前已出现字节；不猜归属 |

成员候选类型限制为核心已有的 SOCKS、HTTP、Shadowsocks、VMess、VLESS、Trojan、AnyTLS、Hysteria2、TUIC；叶出站有 Dependencies 时归为 unknown，不解释链的实际最终出口。这里只验证普通 TCP/UDP 连接，TUN flow、动态注入、插件/脚本及任意协议不在范围。RPC 仍不是跨维度同刻原子快照，组、成员、业务、排除与未知维度不得相加当总使用量。

本地定向回归与真实核心隔离证据：旧 TCP/UDP 连接跨 selector 切换、嵌套组、1,200 短连接及切换期间并发、urltest 旧连接/新成员、direct 排除、链留 unknown；另用未改动 test.2 服务端对照 AnyTLS/TLS 和 VMess 的原始业务字节，核心重启恢复真实零。合成 TLS 使用合成证书和 insecure，不是实际节点 TLS/证书验收。完整源码重构建与测试二进制 SHA256 相同，详见本轮本机证据及 [扩展范围](../../docs/singbox-extension-scope.md)。

test.3 本机候选 SHA256 为 `547ede7cdc822fbe967392bdd26589ba60e947d5d7380d48b8572892108823aa`，只描述这个本地实验构建，不自动授权安装。第二批当时尚未接入 Monitor；后续成员读取、v2 账本与 Windows opt-in 源码进度见下文和 STATUS。Monitor CI 不编译 Go 补丁，不能用它的绿灯替代核心验证。

## test.4：显式 sidecar 成员开关与 Windows 接入（未部署）

第七批在前两层上叠加 [member-sidecar.patch](member-sidecar.patch)，候选为 `1.14.1-monitor-test.4`。sidecar 仍不允许写节点、路由或配置覆盖；新增唯一可选字段 `monitor_members`，值为明确的成员 tag 数组。缺省时保留原 listen-only 路由计数，不启用成员统计；拒绝重复、空、统计分隔符、未注册 tag、direct 或策略组 tag。

合成示例为 `{"listen":"127.0.0.1:10814","monitor_members":["proxy","fixture-member"]}`。实际端口和 tag 必须另行核实，不得直接复制示例到实际客户端。未知或未启用的成员计数保持 unavailable，不补零。Monitor opt-in 目录仍要求既有可核实的独立 AnyTLS proxy；proxy 外的独立成员按配置摘要/tag 保存，不猜订阅 IndexId。

本地已核实的 test.4 SHA256 为 `B1C6F3AA48F21AE070EF283D10AE946C189421F7C9F498CCAA5657D2B0E7E15E`。只有新增 `--scope-ledger` 源码入口接受此候选；默认入口的 test.2 白名单不变。Go 5 个顶级测试/22 个子用例及真实核心隔离启动→sidecar→只读成员 RPC→v2 首接零基线通过；该联测使用合成 Windows 绑定，不是实际 v2rayN 身份或实机全量验收。

新模式必须使用新建 v2 验证目录或经授权离线迁移的 v2 文件。已有 v1 正常启动明确拒绝，不自动迁移；实际核心文件替换、sidecar 更新、重载、预览启动和真实迁移仍需具体授权。候选文件和过程证据仅 TempFiles；CI 构建可能有不同的 Go build ID/二进制摘要，不自动把 CI 产物加入白名单。

## 独立扩展 CI

2026-10-14 用户授权新增 [扩展工作流](../../.github/workflows/sing-box-extension.yml)，不改现有 Monitor CI。第七批构建脚本扩展为按序应用 monitor/member-stats/member-sidecar 三层补丁，核实五个必需顶级测试后构建 test.4；工作流配置不变。Windows runner 仍固定 Go 1.26.8、公开源码提交及归档 SHA256 `8420c7723828a8d9d062c3fafee28c7b7e0d20a4c904fa7ff283f6e884edd537`，保留原 Windows 标签/依赖版本/`-mod=readonly` 和 go.mod/go.sum 摘要核对，不自动切换工具链。`.gitattributes` 固定补丁 LF。

复用同一 [构建脚本](build-ci.ps1) 的本地命令如下；需已有固定 Go 环境，输出目录必须尚不存在。可通过 `-SourceArchive` 指定本地已下载归档，仍执行同一摘要校验。

```powershell
./extensions/sing-box/build-ci.ps1 -OutputRoot 'F:\AI playground\TempFiles\本轮独立构建目录'
```

工作流只在扩展目录或工作流自身改动时触发 push / PR 检查。候选 artifact 包含 EXE、原始源码归档、三层补丁、GPLv3 LICENSE、说明及构建脚本；`build-provenance.json` 记录实际 checkout、CI 提交/运行 ID、源码/补丁/二进制摘要、Go 版本、标签和构建参数。PR checkout 可能是合并测试提交，不是 PR HEAD。artifact 不是正式 Release，不自动部署或更新白名单。远端结果以 [STATUS](../../docs/STATUS.md) 和具体运行记录为准，核心回归/构建不替代实机验收。
