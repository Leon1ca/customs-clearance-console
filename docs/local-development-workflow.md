# 本地迭代与空间管理

## 固定目录

- 源码：`work\github\customs-clearance-console`
- 当前本地运行版：`work\local-current`
- .NET SDK：`work\dotnet-sdk`
- NuGet 缓存：`work\nuget-packages`

`local-current` 是指向当前完整运行目录的 NTFS 目录联接，不复制文件，因此本身几乎不占空间。

## 日常修改

1. 只修改源码目录。
2. 运行 `scripts\sync-local.ps1`。
3. 脚本编译后仅覆盖 `local-current\app` 中发生变化的程序文件。
4. 自动运行 UI 契约回归测试。
5. 直接从 `local-current\关单核验台.exe` 启动测试。

日常迭代不创建 ZIP，不复制 OCR 模型、Tesseract、.NET Runtime 或许可证目录。

## 正式发布

只有明确要求“发布”或“生成安装包”时才执行完整打包和全新解压测试。正式包保留一个 ZIP；历史代码由 Git 提交和标签保存，历史二进制由 GitHub Release 保存。

## 本地保留策略

- 保留源码、SDK、NuGet 缓存、`local-current` 和最新正式 ZIP。
- 可选保留上一个稳定 ZIP，用于紧急回退。
- 发布验证完成后删除一次性解压测试目录。
- 不再使用带版本号的 `clean-unpack-*` 和中间 `final-package-*` 目录作为开发入口。

## 现有空间

2026-08-25 统计：`work` 约占 24.79 GB。仅删除旧的 `final-package-*` 和 `clean-unpack-*`，同时保留当前稳定修正版，即可保守释放约 18.18 GB。
