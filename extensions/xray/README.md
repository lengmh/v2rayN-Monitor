# Xray 内部 HTTP 探测计量实验扩展

固定上游 `XTLS/Xray-core` 提交 `5ca6f4b7d4dc20a881d4330e498892697627ec0c`，使用 Go 1.26.8，版本输出明确包含 `monitor-test.1`。原安装核心没有可核验的 VCS 构建信息；本扩展不宣称与其源码或功能完全等价，不自动替换、重载或修改配置。

`probe-stats.patch` 在普通 Observatory 与 Burst Observatory 的成功返回连接上接入 `probe-stats.go`。每条连接保留自己的强制出站 tag，按实际 `Write` / `Read` 返回的字节累计上传／下载，包括读写报错前的非零返回值。复用原请求、TLS、超时、调度和重定向行为，不读取组的当前选择，不主动增加探测。

## 口径与接口

`/debug/vars` 新增 `monitor_probes`，含 `version: 1`、`kind: "observatory"`、`members` 和 `totals`。各计数为非负整数 `uplink` / `downlink`：

- `members` 按真实出站处理器目录提供；未发生探测时有明确零计数。
- `totals.business` 只计已知独立代理；`excluded` 为 direct / block；依赖出站或协议未核实的请求计入 `unknown`。
- 标准 `stats` 不混入新计数，既有出站累计保持原语义。原出站量与探测流字节的计量层不同，可能重叠，不能相加或直接相减。

该口径包含目标 TLS 与 HTTP 交换，不包含返回连接前的消耗、代理外层、TCP/IP 头及重传。Burst 的额外直连连通性检查、DNS、规则集更新及经普通代理入口进行的下载测速不在当前范围。计数不等同服务商计费。

Monitor 复用显式 v3 的四个 `probe_urltest_*` 历史维度保存 HTTP 延迟探测；这些名称作为既有存储标识保留，页面说明同时覆盖 URLTest 与 Observatory。业务与探测、成员与总量分别查询；首接不回填，未知不补零，不需要新增 Schema 或迁移。只有精确核验的扩展二进制才能启用 Xray 探测读取，普通 Xray 的出站采集保持可用。

## 构建与验证

使用现有 Go / Git / PowerShell 7，输出必须是全新过程目录；原生探测回归通过参数明确提供，测试及产物保留在过程目录。依赖清单不变，不执行 `go get` 或 `go mod tidy`。

```powershell
./build-ci.ps1 -OutputRoot '<新的过程目录>' -ProbeRegressionSource '<原生回归源文件>' -SourceArchive '<已核验的固定源码压缩包>'
```

脚本核对源码摘要、补丁、工具链、原生回归、依赖清单及实验版本，保存 EXE、SHA256、许可证、原生源码／回归与 provenance。默认不安装、不发布、不修改用户数据。

本地回环验证与远端 CI、实际客户端接入分别记录。真实接入前需备份原核心、核对实际配置能力并提前提醒；切换到实验核心会中断既有连接，不能用本地构建或合成流量验证替代实机验收。
