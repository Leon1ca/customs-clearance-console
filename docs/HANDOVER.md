# 关单核验台 移交文档

更新：2026-09-28。本文说明当前代码状态、如何构建、测试和发布，以及尚未解决的问题。首次接手请从这里读起；更早的过程记录见 [iterations/ui-v2/HANDOFF-NEXT-AI.md](iterations/ui-v2/HANDOFF-NEXT-AI.md)。

## 1. 当前状态

| 项 | 状态 |
| --- | --- |
| 最新版本 | **v1.5.3**（预发布），`main` 提交 `0e1f47d`，标签 `v1.5.3` |
| 下载 | GitHub Release `v1.5.3`：`CustomsClearanceConsole-Windows-x64-v1.5.3.zip` 与 `SHA256SUMS.txt` |
| 界面 | 真机验收通过（用户 2026-09-28 确认“界面已经没什么问题”） |
| 功能与识别 | **仍有问题**：真机反馈未全部解决，详见第 7 节 |
| 自动测试 | 核心回归 92 项、浏览器 E2E 33 项、Windows CI 全部门禁，在 v1.5.3 上全部通过 |

各版本的变化见 `docs/release-notes-v1.5.0.md` 至 `docs/release-notes-v1.5.3.md`。

## 2. 仓库结构

```
src/CustomsClearanceConsole/   主程序（.NET 8 WinForms，程序集名“关单核验台”）
src/Launcher/                  根目录启动器 关单核验台.exe（只负责调用 runtime/dotnet.exe app/关单核验台.dll）
tests/CoreRegression/          跨平台核心回归（直接编译生产解析/导出/扫描代码，Linux 可跑）
tests/CoreRegression/Fixtures/ 真实关单 OCR 版式（已脱敏）
tests/BrowserE2E.Portable/     跨平台浏览器长截图 E2E（运行生产 BrowserValidation，见其 README）
tests/BrowserRegression/       早期静态网页回归页（保留，不在 CI 中）
scripts/                       打包、依赖准备、字体、显示缩放等脚本
.github/workflows/windows-ui-v2.yml  唯一的 CI / 发布工作流
docs/                          设计、架构、OCR 流程、各版本更新说明
```

### 主程序模块

| 文件 | 职责 |
| --- | --- |
| `Program.cs` | 入口；无参数启动界面，带参数运行各种自检命令（见第 4 节） |
| `MainForm.cs` / `MainForm.Layout.cs` | 主窗口：批次、表格绘制、列宽拖动、分页、标题栏、窗口消息（拉伸 / 最大化 / 移动） |
| `DpiLayout.cs` | 高 DPI 模型：所有窗口按 96 DPI 逻辑像素搭建，句柄创建后按实际 DPI 缩放一次，`WM_DPICHANGED` 时再缩放。`DpiDialog` 是所有弹窗的基类 |
| `PopupFrame.cs` | 弹窗、菜单、下拉的圆角外形和边框：用同一个 GDI 区域同时做窗口形状和 `FrameRgn` 描边，四边粗细一致，无投影 |
| `UiControls.cs` / `UiV2Controls.cs` / `DesignMenu.cs` / `CopyContextMenu.cs` / `ModalDialogs.cs` / `DirectorySettingsForm.cs` / `DetailForm.cs` | 控件、菜单和弹窗 |
| `Theme.cs` / `DesignTokens.cs` / `AppFonts.cs` / `UiIcons.cs` | 颜色、尺寸、随包字体、图标 |
| `ScanPlan.cs` / `BatchScanner*.cs` | 扫描清单、后台识别调度、重复分组、合计。支持的扩展名在 `BatchScanner.SupportedExtensions` |
| `DocumentExtractor.cs` | PDF 文字层读取（PDFium）；无文字层的页面渲染后做 OCR；页面方向校正 |
| `OcrImages.cs` | 图片解码（GDI+，失败时用 OpenCV，如 WEBP）、多页 TIFF、缩放到长边 2800 |
| `RapidOcrEngine.cs` + `RapidOcrSource/` | PP-OCRv5（RapidOCR C# 移植 + ONNX Runtime + Emgu CV）。模型在 `ocr-models/` |
| `DeclarationParser.cs` | 从带坐标的文字块提取报关单号、境外收货人、合同协议号、出境关别、目的国、逐项总价；OCR 页面的一致性规则 `ApplyOcrRules` |
| `CountryNames.cs` | ISO 三字母国别代码 ↔ 中文名，含别名 |
| `DeclarationReconciler.cs` | v1.5.2 及以前双引擎的逐字段比对。v1.5.3 起不再产生第二份结果，这部分代码实际不再执行，可以删除（需同步删改核心回归中相应用例） |
| `ExcelListExporter.cs` / `MarkdownListExporter.cs` / `ExportValidation.cs` | 导出与导出自检 |
| `BrowserValidation.cs` + `BrowserScripts/*.js` | 打开 Edge/Chrome（CDP）、自动填单号、网页卡片、长截图 |
| `TargetUrlPolicy.cs` | 哪些网页地址允许截图（官网按 `#/publicInquiryDetail` 路由） |
| `StateStore.cs` / `FileCleanupService.cs` / `AppLog*.cs` / `AppPaths.cs` | 历史保存、清理、日志、路径 |
| `SelfTest.cs` / `OcrSelfTest.cs` / `BrowserCaptureE2E.cs` | 自检、UI 快照、OCR 端到端、浏览器 E2E 场景（随程序编译，由命令行开关调用） |

## 3. 开发环境与构建

### Windows（完整运行）

需要 .NET 8 SDK 和 Git。OCR 原生组件（Emgu CV / ONNX Runtime 的原生 DLL）、PP-OCRv5 模型和 PDFium 不在 Git 中，需要从一个已发布的便携包准备：

```powershell
# 解压任意发布包（CI 使用 v1.4.0，需要其中的 app 与 tools 目录）
.\scripts\bootstrap-from-release.ps1 -PackageRoot <解压目录>
.\scripts\prepare-fonts.ps1            # 需要 python + fonttools==4.60.2
dotnet build src\CustomsClearanceConsole\CustomsClearanceConsole.csproj -c Release
dotnet src\CustomsClearanceConsole\bin\Release\net8.0-windows\关单核验台.dll
```

`bootstrap-from-release.ps1` 会把 DLL 放进 `src/CustomsClearanceConsole/dual-ocr-bin`，模型放进 `ocr-models`，PDFium 放进 `tools`。旧包里的 Tesseract 会被跳过。日常迭代流程见 [local-development-workflow.md](local-development-workflow.md)。

### Linux / macOS（只能编译和跑跨平台测试）

```bash
# 编译检查（需要 dual-ocr-bin 里的托管 DLL；原生组件缺失不影响编译）
dotnet build src/CustomsClearanceConsole/CustomsClearanceConsole.csproj -c Release -p:EnableWindowsTargeting=true
# 核心回归（92 项）
dotnet run -c Release --project tests/CoreRegression
# 浏览器 E2E（需要任意 Chromium，用法见 tests/BrowserE2E.Portable/README.md）
dotnet build -c Release tests/BrowserE2E.Portable -o /tmp/e2ebin
CUSTOMS_CONSOLE_E2E_BROWSER=/path/to/chrome CUSTOMS_CONSOLE_DATA=/tmp/ccc dotnet /tmp/e2ebin/BrowserE2E.Portable.dll /tmp/out [场景方法名 次数]
```

WinForms 界面、OCR 引擎和 PDFium 只能在 Windows 上运行。

## 4. 命令行自检开关

对 `关单核验台.dll`（或根目录 exe）传参数：

| 开关 | 用途 |
| --- | --- |
| `--ocr-self-test <目录>` | 按真实关单版式合成一张图，另存为 PNG/JPG/BMP/WEBP、两页 TIFF、旋转 90/180/270°、无文字层 PDF，全部用 PP-OCRv5 实际识别并核对字段。结果写入 `<目录>/report.txt` |
| `--ocr-debug <文件>` | 输出某个文件每页的 OCR 文字块（文字、坐标、置信度）JSON。**排查识别问题的首选工具** |
| `--self-test <目录>` | 识别整个目录并输出 JSON（字段、合计、状态、提示） |
| `--regression-test <目录>` | 旧的真实样本回归（断言写在 `SelfTest.RunKnownRegressionAsync`，样本不在仓库） |
| `--ui-contract-self-test` | 界面契约检查 |
| `--ui-state-snapshot <目录>` / `--ui-snapshot` / `--ui-dialog-snapshot` | 界面各状态截图与布局断言 |
| `--export-samples <目录>` | 导出 Excel/Markdown 并校验 |
| `--env-report <目录>` | 环境、DPI、字体报告 |
| `--browser-e2e <目录>` | 受控页面上的长截图 E2E |

## 5. CI 与发布

工作流：`.github/workflows/windows-ui-v2.yml`。可手动触发（workflow_dispatch），推送到 `codex/ui-v2-iteration` 时也会自动运行。

- **核心回归**：`windows-latest` 上运行 `tests/CoreRegression`。
- **主验证作业**：
  - 从 v1.4.0 发布包准备依赖（会校验 SHA256）；
  - 编译；
  - UI 契约检查；
  - OCR 端到端；
  - 环境与字体报告；
  - 导出校验；
  - 100% 下的界面快照；
  - 浏览器 E2E；
  - 便携打包、ZIP 内容校验、干净解压冒烟测试；
  - 150% 显示缩放下的界面快照（用 `scripts/set-display-scale.ps1` 把 runner 切到 150%）。

  各步骤都设为 `continue-on-error`，最后由 “Enforce validation gate” 统一判定，任一步失败整体即失败。
- **发布**：只能在 `main` 上手动触发，并填 `release_tag=vX.Y.Z`，勾选 `prerelease` 即为预发布。只有全部门禁通过才会发布；发布的就是本次门禁测过的 ZIP，不重新编译。标签必须与项目版本号一致。
- **查看 CI 证据**：
  - Artifact 名为 `ui-v2-validation-evidence`。
  - 若在非 main 分支触发时勾选 `evidence_ref`，界面截图、`browser-e2e.json`、`ocr/report.txt` 还会推送到隐藏引用 `refs/ci-evidence/latest`，可这样获取：

    ```bash
    git fetch --depth 1 origin refs/ci-evidence/latest && git checkout FETCH_HEAD
    ```

### 发布新版本的步骤

1. 修改版本号，共 4 处：
   - `src/CustomsClearanceConsole/CustomsClearanceConsole.csproj`（Version / FileVersion / AssemblyVersion）
   - `src/Launcher/关单核验台启动器.csproj`
   - `src/Launcher/AssemblyInfo.cs`
   - `src/CustomsClearanceConsole/使用说明.txt`（两处）
2. 新增 `docs/release-notes-vX.Y.Z.md`（打包步骤要求此文件存在），并更新 README 顶部的版本说明。
3. 在分支上手动触发 CI，确认全部通过。
4. 合并到 `main`。
5. 在 `main` 上触发工作流，填写 `release_tag`。

## 6. 关键设计与踩过的坑

- **DPI**：不要在构造函数里设置 `AutoScaleMode`，否则只有空窗体被放大、子控件不缩放。一律按逻辑像素搭建，交给 `DpiLayout` / `DpiDialog` 缩放。弹窗尺寸请用 `LogicalClientSize`，因为句柄创建期间 `ClientSize` 读出来偏小。
- **表格绘制**：`PaintRecordCell` 每格都要重置 `SmoothingMode` / `PixelOffsetMode`，否则抗锯齿设置会从圆角标签带到下一格，真实屏幕上出现灰色接缝（`DrawToBitmap` 截图里看不出来，CI 有专门的真实屏幕接缝检查）。
- **窗口拉伸与移动**：`WS_EX_COMPOSITED` 只在 `WM_SIZING` 到 `WM_EXITSIZEMOVE` 之间开启；如果移动窗口时也开启，拖动标题栏会卡顿。
- **弹窗边框**：不要用 `CS_DROPSHADOW`（投影偏在右下，使右边和下边看起来更粗）。也不要在 GDI+ Region 里画抗锯齿边框，边缘会被裁得不均匀。统一用 `PopupFrame`。
- **CDP 与官网**：
  - `Frame.url` 不含 `#` 片段，片段在 `urlFragment` 字段里；页面内路由切换通过 `Page.navigatedWithinDocument` 通知。这两点都已处理，否则官网截图会被判为“当前页面不是核验目标页”。
  - 官网的查询表单在同站的 `swapp.singlewindow.cn` iframe 中，页面加载后才生成。卡片只安装在顶层页面（`widget.js` 中有 `window.top === window` 判断）。
- **截图流程**（`BrowserValidation.CaptureCoreAsync`）：
  - 顺序为：确认结果 → 两次稳定性校验 → 准备页面 → 展开框架 → 读取尺寸 → （需要时）放大视口 → 分段截图 → 跨进程框架复核 → 最终校验并原子保存。
  - 整体 60 秒上限（`CaptureDeadline`），每一步由 `CaptureStep` 写入 `app.log`。
  - 卡片端有 90 秒看门狗。
- **OCR 页面方向**：方向分类器能把倒置的文字行读对，所以倒置页面按文字内容看“也像关单”，只是坐标是反的。因此判断方向时，先比较竖长文字框的比例和倒置行的比例，再比较关单得分。
- **隐私**：仓库是公开的，测试数据只能用脱敏内容。新增真实样本时，先替换公司名、号码和金额，只保留版式坐标（参考 `tests/CoreRegression/Fixtures/declaration-5166-rapidocr.json`）。

## 7. 未解决的问题与建议

### 功能：真实网站长截图

- CI 只在受控仿真页面上验证，**真实单一窗口站点上的长截图从未在自动化中跑通过**。v1.5.2 的真机反馈是一直停在“截取中”，这个问题在 CI 的仿真页面上（包括同站跨源 iframe、`#` 路由）都**无法复现**。
- v1.5.3 做的是兜底，没有定位根因：60 秒超时、卡片看门狗、浏览器反节流参数、逐步日志。
- 下一步：在真实网站上复现一次，然后看 `%LocalAppData%\关单核验台\app.log` 中的 `截图步骤：…（xx ms）` 和 `截图超时：…停在“…”`，就能知道卡在哪一步。可能的原因（均未证实）：
  - 真实页面的某个 iframe 上下文不响应 `Runtime.evaluate`；
  - 窗口被遮挡导致 `captureScreenshot` 等不到新画面；
  - 页面尺寸超大时单段截图耗时过长。

### 识别

- **出境关别**：
  - 已知关区代码表 `CustomsShortByCode` 只有 6 个代码，其余依赖框内文字或表头括号中的关区名。
  - 若真实单据的这两处都读不出，结果会是空的。可以补一张完整的关区代码表。
- **规则复核阈值**：还没有用一批真实单据验证。
  - 数量 × 单价 = 总价的容差是 `0.011 + 数量×0.00005 + 总价×0.0001`，数量候选取自该行数量列中所有带单位的数字。
  - 如出现大量误报“需关注”，先用 `--ocr-debug` 查看该行的文字块。
- **页面方向**：只处理 0/90/180/270°，没有做小角度纠偏。严重倾斜的扫描件可能读错。
- **速度**：每页约 4–5 秒（CI runner）；横放页面最多要识别 3 次。如仍嫌慢：
  - 可把 `OcrImages.TargetLongEdge` 从 2800 降低，但需要评估对准确率的影响；
  - 也可以按文件并行识别，注意 `RapidOcrEngine` 内部有一个全局锁。
- **仍保留的旧逻辑**：
  - `DeclarationParser` 里仍有按固定比例区域读取的兜底（`ReadRegion`）；
  - `DocumentExtractor` 中文字层少于 30 个文字块的 PDF 页面会整页 OCR。
- **历史记录**：v1.5.2 及以前保存的记录仍显示“双引擎”结论。

### 待办清理

1. **隐私**：`main` 上的提交 `0cd738f`（v1.5.2）的 `tests/CoreRegression/Program.cs` 含一张真实关单的公司名、报关单号和合同号。当前代码已脱敏，但历史中仍可查到。如需彻底清除，需要改写 `main` 历史（例如用 `git filter-repo`）并强制推送，同时重做已有的标签和 Release。
2. **隐藏引用**：`refs/ci-evidence/latest` 仅供开发查看 CI 证据，不再需要时执行 `git push origin --delete refs/ci-evidence/latest`。
3. **分支**：`codex/ui-v2-iteration`、`claude/customs-console-review-optimize-t4epeg` 的内容都已合入 `main`，可以删除。注意删除 `codex/ui-v2-iteration` 后，工作流中针对它的 push 触发条件也就不会再生效。
4. **死代码**：`DeclarationReconciler.cs` 中双引擎比对部分，以及 `DocumentText.VerificationPages` / `SecondaryOcr*` 字段（见第 2 节）。
5. **旧文档**：[local-development-workflow.md](local-development-workflow.md) 和 `docs/iterations/` 下的记录写于双引擎时期，涉及 Tesseract 的描述已过时。
