# 应用图标

原创图形由深蓝圆角底与白色上传、青绿色下载箭头组成，表示 Monitor 的双向流量统计。外缘透明；不使用 v2rayN 客户端的图标。

- 唯一矢量源：`web/public/icon.svg`。
- Windows：`web/public/icon.ico` 包含 16、20、24、32、40、48、64、128、256 px 的 32-bit RGBA 图像。Host 的 `ApplicationIcon` 与托盘嵌入资源共用此文件，不依赖工作目录或额外的散落文件。
- 托盘按启动时系统小图标尺寸选择 ICO 图层，退出时释放资源。运行中更改缩放比例的效果仍须 Windows 实机检查；如图标模糊可重启 Monitor。
- HTML：favicon 与侧栏标识使用同源 SVG，ICO 为浏览器兼容选项；另保留 `icon-256.png`。资源由 Vite 复制到 dist，随现有 wwwroot 发布，不依赖网络资源，不改变 CSP 或会话权限。
- 当前应用主界面在默认浏览器中打开，没有独立 WinForms 主窗口。浏览器标签页显示 favicon；Windows 文件和快捷方式使用可执行文件图标。

## 重新生成

正常构建直接使用仓库内的资产，不需要图形工具。修改矢量源后，在开发环境安装 Inkscape 与 Pillow，运行：

```sh
python scripts/generate_icons.py
npm test --prefix web
```

提交 SVG、ICO 和 PNG，保持三者同步。生成器逐尺寸渲染，避免先生成小尺寸再放大。资源结构与生产输出由 `web/tests/icons.test.mjs` 检查。

## Windows 目视验收

在新构建的预览包中检查 EXE/快捷方式、托盘及浏览器标签图标；分别检查浅/深色任务栏和 100%、125%、150%、200% 缩放。检查退出后托盘项消失、再次启动图标正常。Explorer 可能缓存旧 EXE 图标。Linux 构建、发布和资源解码不能代替 Windows 目视验收。
