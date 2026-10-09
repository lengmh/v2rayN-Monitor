# 本地 HTML 控制台

Vanilla TypeScript + Vite。生产资源随宿主分发，不依赖 CDN、第三方字体、前端框架或图表库。没有前端运行时依赖；锁定依赖仅用于构建和测试。

## 构建与测试

需要 Node.js 24。

```sh
npm ci
npm test
npm run build
npx playwright install --with-deps chromium
npm run test:ui
```

`npm test` 构建生产资源，再运行字节/时间范围/分桶单测与 JSDOM 生产 bundle 测试。`test:ui` 使用生产 Vite 预览、合成本机 API 和严格 CSP，检查真实浏览器中的流程、动态柱状图、文本转义和移动端溢出。默认使用 Playwright 安装的 Chromium；可用 `CHROMIUM_PATH` 显式指定兼容的本机浏览器。

`npm run dev` 仅为前端开发服务器。完整运行请启动仓库的宿主或 DemoHost，并通过它签发的本机链接进入；Vite 开发服务器自身不提供采集 API。

## 已实现边界

- 主要页面：本机总览、只读采集状态、目录与时区设置；服务商 CSV 使用独立页面及账本。
- 日/周/月/年/自定义日期，独立小时/日/周/月/年分桶。周一开始，范围使用所选 IANA 时区的半开 UTC 边界；DST 重复小时保留 offset。
- 原始整数 bytes 全程使用 BigInt，GB/GiB 仅影响显示；浮点数仅用于有界图表百分比。
- 空白分桶与真实零记录分别表示；未分时恢复量明确排除于分时合计，且提示跨日期不能重复相加。过密图表提示换粗分桶，不静默截断记录。
- 启动 fragment 立即从地址栏移除，仅交换一次 HttpOnly cookie。没有令牌存储。所有 API 请求携带 x-monitor-request，外来字符串仅写入 textContent。
- 5 秒刷新、12 秒 API 超时、取消较早请求、会话失效停止刷新、CSV 下载及有确认步骤的独立导入。

## 验证分层

字节/范围及 DOM 自动化可以在无浏览器环境中运行，不能代替真实 Chromium 的布局与 CSP 验证。真实浏览器测试留给有受支持运行时的 CI；Windows 托盘、v2rayN/Xray 实机采集仍需单独验收。演示与测试使用合成数据。
