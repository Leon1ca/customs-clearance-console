# 独立子路由 UI/打包预审 R3（待修复）

静态审查，未本机编译/测试。由 DeepSeek 实施修复，并云端验证。

1. 编译：DetailForm.Columns() 返回 IEnumerable<(Rect,Text,Alignment)>，调用却 columns[0] 且当Rectangle；须实体化列表并使用.Rect。UiV2Controls.RecordStatePanel.OnPaint 的S(int)却S(1.5F)。
2. P1 单号/金额/状态复制为空：MainForm.Layout CellFormatting 把 No/Amount/Status 的e.Value设为空，CopySelectedCells读FormattedValue，自绘内容无法复制。保留真实值或从原始模型取完整文本，增加真实选择/复制断言。
3. P1 多币种明细误标币：DetailForm用首币种作为整表总价标题，行仅画金额；页脚也只首币种，无Totals时跨币种求和。MainForm仅画totals[0]，无完整tooltip。逐项标币、各币种汇总，多币种都可见/可查看，不跨币种相加。
4. P1 汇总截币种和错加未确认金额：UiV2Controls超高直接break，MainForm高度max230，无滚动，多个币种不可见；MainForm只取Totals为空的冲突项遗漏部分确认记录，再不同币种金额求和标首币种。逐币种列所有不可靠项，实现溢出滚动/查看完整数据。
5. P1 便携root/tools缺失：publish放app/tools，AppPaths读根tools；workflow未移动/复制，package-release必报缺Tesseract。调整包结构，从干净解压目录通过根启动器烟测，不能只测开发输出。
6. P1 LICENSE和第三方原文未复制，AboutAndLicenses摘要不够；Noto静态400/500/700及JetBrains/OFL实际文件未齐全。workflow要求variable违反PRD。请云端fonttools从官方variable实例化或官方静态包，处理OFL保留名，落实静态字体与原许可。最终ZIP检查每个文件及加载成功，不留给用户再确认。
7. A03未通过：SelfTest菜单直接画左上角不能证明真实弹出定位/点击/DPI；只100%DPI运行，其他只有算式，env report写死96。获取实际DeviceDpi并云端真实缩放或正确DPI上下文验证，无法验证则如实保留，不假称完整通过。
8. 明细长商品名/冲突说明被省略且无全文提示，增加可完整阅读方式。

最终界面需真实WinForms截图对照，不接受参考HTML截图代替；真实文件的关键数字/币种不能为了外观而隐藏/丢失。

执行补充：本机直连 GitHub/raw 下载不稳定，已有授权的回环代理 http://127.0.0.1:7890（READER_DOWNLOAD_PROXY），git已局部使用。curl 可显式 --proxy 使用，勿修改全局设置。更推荐字体在 Actions 下载并实例化，不必等待本机断点续传。云端setup-dotnet安装8.x后仍可被机器10.x SDK抢选，应使用global.json选8.x以保持复现性。轮询已完成作业不要继续长sleep。
