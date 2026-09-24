# a9cf172 截图步骤停滞：短时只读排查

范围：固定 `a9cf172fef08b939396db2961accbfd5847f55b5` 的 CaptureAllStates、桌面模式、渲染/事件与CLI异常路径。未本机运行产品/测试。run `35961148492` 的“停在快照步骤”来自主路由状态信息；尚无该步骤运行中堆栈或窗口证据，不能断言具体根因。本文不改变 FINAL-ACCEPTANCE 的源码结论。

## 确定存在的阻塞机制

`Program.cs:13–14` 在判断命令行模式前，对所有模式启用 CatchException，并把 Application.ThreadException 交给 AppLog.ShowUnexpected。`AppLog.Desktop.cs:5–8` 先写日志，再同步 MessageBox.Show，必须有人关闭才返回。因此 `--ui-state-snapshot` 内 Show、重绘、窗口事件或 Application.DoEvents 期间只要有UI事件异常，就能在无人值守机器上一直等待弹窗。当前工作流该步骤直到25分钟超时（windows-ui-v2.yml:143–147），没有CLI模式的立即失败保护。这条机制确定；本次是否已经触发仍待AppLog/截图证实。

建议先修CLI异常处理：在测试/截图命令模式，记录异常到stderr及随产物收集的日志后非零退出，禁止显示MessageBox；正常桌面启动仍沿用交互报错。不要捕获异常后当成功继续。

## 没有找到确定的代码无限循环

- CaptureAllStates 的16个基础状态、2个processing状态、2个菜单及6个对话框都是固定数量（SelfTest.cs:678–719）。
- EnumDisplaySettings 循环按mode递增，API返回false即退出（978–988）；未看到把mode重置或永不变的条件。不能仅因 `for(;;)` 外形判无限循环。
- ChangeDisplaySettings 同步调用位于开始生成所有状态之前（639–640,995）。如果**本轮**已产出后续状态PNG，则它已返回；旧轮“图片很快”不能据此排除本轮环境调用停滞。
- Application.DoEvents 是最需要定位的消息泵边界，出现于新建窗口、状态配置、RenderForm、屏幕捕获前及菜单开关（655,777,786,809,836,1069）。循环本身有限，不代表其中的事件或原生调用一定返回。
- 本轮新增 CopyFromScreen 在显示窗口后调用（1064–1074）；这是另一个待定位的原生阻塞点。源码不足以判定它就是根因。

## 最小定位动作（交 DeepSeek 云端处理）

1. 在桌面模式切换前后、每个 `state-size` 开始/结束、Show前后、DoEvents前后、DrawToBitmap前后、CopyFromScreen前后、菜单/对话框前后输出并flush时间戳。只需阶段日志，先确认停在哪个调用。
2. 为快照子进程设置短于整个步骤的超时；超时前收集已有PNG文件名/时间、AppLog文件、当前桌面截图及进程状态，再非零结束并上传证据。现有工作流只上传 artifacts/validation，AppLog默认位置未必被包含，应显式复制其路径输出的日志。
3. 如日志显示 ThreadException，使用第一条异常堆栈定位实际控件；如停在桌面调用/CopyFromScreen，则针对云端显示会话处理。没有阶段/堆栈证据前，不建议修改产品绘制逻辑来猜测修复。

优先级判断：**先消除确定存在的CLI模态弹窗阻塞并增加阶段日志**，比延长25分钟等待更能获得下一轮可用证据。当前没有证据支持认定某个绘制循环无限运行。
