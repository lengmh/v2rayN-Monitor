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

Monitor 当前只接受经核实的实验文件 SHA256：

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

本轮只准备预览，尚未安装到真实客户端。安装前确认空闲端口、文件摘要和实际客户端版本，
再增加 bin/sing_box/sing-box-client.exe 及 monitor-stats.json，并择时让 v2rayN 正常重载。
文件示例为 `{"listen":"127.0.0.1:待选端口"}`，必须将端口替换为核实后的数字。
不会写订阅、节点数据库、路由或 guiNConfig.json。

回退需先正常退出扩展核心，移开本次新增的两个文件，再由客户端加载原 sing-box.exe。
不要在核心运行时覆盖文件。真实客户端选择/升级/退出和真实 AnyTLS 节点仍待验收。
