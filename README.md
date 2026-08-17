# 📝 MD2Blog — Markdown 一键发布 WordPress 草稿（支持特色图）

把 Markdown 文件**拖进窗口**，自动发布为 WordPress 博客草稿；还带一个**草稿特色图管理器**——列出所有草稿、预览/更换/清除特色图，全程不用打开浏览器后台。

![C#](https://img.shields.io/badge/language-C%23-blue) ![Platform](https://img.shields.io/badge/platform-Windows-lightgrey) ![License](https://img.shields.io/badge/license-MIT-green)

## ✨ 功能特性

| 功能 | 说明 |
|---|---|
| 🖱️ 拖拽发布 | 把 `.md` 文件拖到窗口上，自动转换为 HTML 并发布为 WordPress 草稿 |
| 🖼️ 特色图 | 同时拖入 `.png/.jpg` 等图片，自动上传并设为文章特色图 |
| 📋 批量发布 | 一次拖入多篇，第 N 篇自动配第 N 张图 |
| 🎛️ 草稿管理 | 列出博客全部草稿（ID/标题/特色图状态） |
| 👁️ 特色图预览 | 点选草稿即显示当前特色图原图 |
| 🔄 更换/清除 | 一键更换特色图（重新上传）或清除特色图 |
| 🔒 密码加密 | 配置用 Windows DPAPI 加密保存，仅本机当前用户可解密，不明文落盘 |
| 📦 免安装 | 单个 EXE（约 43 KB），依赖系统自带 .NET Framework 4.x |

## 🚀 快速开始

### 方式一：直接运行（推荐）

1. 下载 `MD2Blog.exe`（见 Releases）；
2. 双击运行，**首次启动会弹出配置窗口**，填写：
   - **站点 URL**：你的 WordPress 地址（如 `https://example.com`）
   - **用户名**：WordPress 登录用户名
   - **密码**：WordPress 登录密码
   - **发布状态**：`draft`（草稿，默认）或 `publish`（直接发布）
3. 保存后即进入主窗口，把 `.md` 文件拖进去即可。

### 方式二：自行编译

```bat
csc /nologo /target:winexe /out:MD2Blog.exe ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Security.dll ^
  MD2Blog.cs
```

（`csc.exe` 位于 `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\`）

## 🖱️ 使用方法

### 发布文章

1. 双击 `MD2Blog.exe` 打开主窗口；
2. 把 `.md` 文件拖到窗口上（可一次拖多篇）；
3. 同时拖入图片（`.png/.jpg/.jpeg/.gif/.webp/.bmp`）则自动设为该篇特色图；
4. 自动完成：Markdown → HTML → 创建草稿 → 上传图片 → 设置特色图 → 弹出结果（含文章 ID 和后台编辑链接）。

> Markdown 首行 `# 标题` 会作为文章标题；没有则用文件名。

### 管理草稿特色图

1. 主窗口点 **「📝 管理草稿特色图」**；
2. 左侧列表显示全部草稿（列表可滚动）；
3. 点选一篇 → 右侧显示当前特色图预览和附件信息；
4. **「更换特色图...」** 选新图自动替换；**「清除特色图」** 移除；
5. 点 **「← 返回主窗口」**（在窗口底部）继续拖入新文章。

### 命令行模式

```bat
MD2Blog.exe 文章.md 封面.png
MD2Blog.exe 文章.md 封面.png -quiet   :: 不弹窗，结果写入 publish-result.txt
```

## 🔐 安全说明

- 登录密码通过 **Windows DPAPI** 加密后存入 `blog-config.json`，只有**本机当前用户**能解密，换账号/换机器无法读取；
- 配置文件请勿外传；建议定期修改博客密码；
- 发布走 WordPress **XML-RPC** 接口（`wp.newPost` / `wp.uploadFile` / `wp.editPost`）。若你的站点禁用了 `xmlrpc.php`（常见于安全插件），发布会失败，需在插件中放行。

## 🛠️ 技术实现

- **C# / WinForms**（.NET Framework 4.x），单文件源码 `MD2Blog.cs`；
- 发布链路：`wp.newPost` 创建草稿 → `wp.uploadFile` 上传图片 → `wp.editPost` 的 `post_thumbnail` 字段设置特色图（注：`wp.setPostThumbnail` 不是 WordPress 核心方法，多数站点没有，故用 `editPost` 方式实现）；
- 草稿列表：`wp.getPosts` 拉取 ID → `wp.getPost` 逐篇取标题与特色图，**异步加载**，界面不卡顿；
- 内置 Markdown → HTML 转换器（支持标题/表格/代码块/列表/引用/行内样式）；
- 界面启用 DPI 感知 + `AutoScaleMode.Dpi`，高分屏下不模糊、不错位。

## 📁 文件结构

```
MD2Blog.cs           全部源码（单文件）
MD2Blog.exe          编译产物（不含，见 Releases）
使用说明.txt         中文使用文档
```

## 📄 许可证

[MIT](LICENSE) © 2026 7sevenayu
