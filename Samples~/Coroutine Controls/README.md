# Coroutine Controls 示例

## 导入与启动

1. 在 Package Manager 的 Coroutine Runner 页面导入 **Coroutine Controls**。
2. 确保已安装 Unity 内置 **IMGUI** 模块（`com.unity.modules.imgui`，用于示例的运行时操作面板）。无需 Inspector Groups。
3. 新建场景，在空物体上添加 `CoroutineControlsSample`，进入 Play Mode。

示例自动启动三个任务：默认 1 秒后完成延迟任务，每 0.5 秒重复一次共五次，以及等待手动打开条件门的任务。结果显示在 Game 面板和 Console。

## 操作与预期结果

- **Restart all sample tasks**：仅停止上一轮示例的任务，再启动新一轮；计数重置。
- **Open gate**：使 `WaitUntil` 的条件成立，等待任务只完成一次。
- **Cancel sample tasks**：通过各自的 `Coroutine` 句柄调用 `Stop`，计数停止增长，不调用全局 StopAll。
- 禁用组件或卸载其所在场景后，任务停止。重新启用组件后可点击 Restart 开始新一轮。

代码使用 `Runner.On(this)` 绑定本组件所在场景，在 additive 场景中也不依赖当前活动场景。延迟和重复明确忽略 `Time.timeScale`，不修改项目时间缩放。持久任务应另用 `Persistent` API；此例演示随场景结束的任务。

Inspector 中的两个时间参数单位为秒，已有中文悬停说明。示例只依赖本包和 Unity 官方模块，不需要其他业务包。
