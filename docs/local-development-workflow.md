# 本地迭代与空间管理

## 唯一工作目录

- Git 仓库：`customs-clearance-console`
- 源码：`src\CustomsClearanceConsole`
- 界面设计：`docs\design-handoff`、`docs\design-history`、`docs\design-sources`
- 当前本地运行版：`artifacts\local-current`
- 最新正式压缩包：`artifacts\releases\v1.6.0`

从 v1.6.0 起，源码、界面设计、当前运行版和正式压缩包都集中在同一个 Git 仓库目录内。`artifacts` 是本机生成物，不提交到 Git；历史源码由 Git 提交和标签保存，历史二进制由 GitHub Release 保存。

开发机只需要安装 Git、VS Code 和 .NET 8 SDK，不需要安装完整 Visual Studio。当前工作区另保留一份不提交到 Git 的 `.devtools\dotnet-sdk` 作为本地构建备用；程序依赖的 OCR 模型、Tesseract、PDFium 和原生 DLL 已保存在仓库的本机忽略目录及 `artifacts\local-current` 中。

## 日常修改

1. 只修改源码目录。
2. 运行 `scripts\sync-local.ps1`。
3. 脚本编译后仅覆盖 `artifacts\local-current\app` 中发生变化的程序文件。
4. 自动运行 UI 契约回归测试。
5. 直接从 `artifacts\local-current\关单核验台.exe` 启动测试。

日常迭代不创建 ZIP，不复制 OCR 模型、Tesseract、.NET Runtime 或许可证目录。

## 正式发布

只有明确要求“发布”或“生成安装包”时才执行完整打包和全新解压测试。正式包保留一个 ZIP；历史代码由 Git 提交和标签保存，历史二进制由 GitHub Release 保存。

## 本地保留策略

- 仓库内只保留一个 `local-current` 和最新正式 ZIP。
- 旧版本通过 Git 标签和 GitHub Release 回退，不在本机重复解压。
- 发布验证完成后删除一次性解压测试目录。
- 不再使用带版本号的 `clean-unpack-*` 和中间 `final-package-*` 目录作为开发入口。
- OCR 样本、网页缓存、截图和临时日志均放在 `artifacts` 或系统临时目录，完成验证后清除。

## 版本管理

每次完成一个可验证的功能点后提交一次 Git；准备发布时更新版本号、发布说明和测试记录，再创建版本标签。不要通过复制整个文件夹保存历史版本。
