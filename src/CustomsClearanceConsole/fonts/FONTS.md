# 随包字体与许可

程序启动时 `AppFonts.Initialize()` 从 `fonts/` 目录加载以下文件；缺失时回退到系统字体，不影响启动。正式便携包必须包含全部字体与许可。

| 文件 | 字体 | 来源（官方） | 许可 |
|---|---|---|---|
| `NotoSansSC-Variable.ttf` | Noto Sans SC（字重轴 100–900，界面按 Regular / Bold 使用） | https://github.com/google/fonts/tree/main/ofl/notosanssc | SIL Open Font License 1.1（`NotoSansSC-OFL.txt`） |
| `JetBrainsMono-Regular.ttf` | JetBrains Mono Regular | https://github.com/JetBrains/JetBrainsMono/tree/master/fonts/ttf | SIL Open Font License 1.1（`JetBrainsMono-OFL.txt`） |
| `JetBrainsMono-Medium.ttf` | JetBrains Mono Medium | 同上 | 同上 |
| `JetBrainsMono-Bold.ttf` | JetBrains Mono Bold | 同上 | 同上 |

说明：

- Noto Sans SC 官方仓库当前仅提供可变字体 `NotoSansSC[wght].ttf`（静态实例由第三方工具生成，不属于官方发布物）。因此随包使用官方可变字体文件并重命名为 `NotoSansSC-Variable.ttf`；`AppFonts` 会优先使用可用字重，无法取得 Medium / Bold 时回退到 Regular 并保留字体族，不阻断界面。
- 许可原文随包放在同目录，便携包内路径为 `app/fonts/`。
- `AboutAndLicenses.txt` 同步列出字体许可信息。
