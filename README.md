# Coroutine Runner

Coroutine Runner 是一个 Unity 运行时协程工具包。调用方无需继承、持有或查找 `MonoBehaviour`，即可通过静态入口启动和管理协程。

包内分别提供场景级与跨场景持久协程：场景协程在所属场景卸载时自动终止，持久协程则可在场景切换后继续运行。

## 功能

### 协程生命周期

- 在当前活动场景中启动协程。
- 启动可跨场景继续运行的持久协程。
- 将协程绑定到指定对象所在的场景或指定场景。
- 主动停止指定协程。
- 分别停止全部场景协程或全部持久协程。
- 按需自动创建内部宿主，不需要预先配置场景对象。

### 常用协程操作

- 延迟指定时间后调用方法。
- 在下一帧、帧末或下一次固定更新时调用方法。
- 等待条件满足后调用方法。
- 每帧执行监听，返回 `false` 时自动结束。
- 按指定间隔和次数重复调用方法。
- 延迟与重复调用支持忽略 `Time.timeScale`。

### 数据读取

- 使用 `UnityWebRequest` 读取文本或字节数据。
- 支持 HTTP、HTTPS、file 和 Android `jar:file` 地址。
- 支持 StreamingAssets 地址与普通本地文件路径。
- 可选择让读取过程随场景卸载终止或跨场景继续。

## 安装

当前源码版本为 `1.0.0`。准备本包源码，使用本地 UPM 安装：

1. 在 `Window > Package Manager > + > Add package from disk...` 选择本包根目录的 `package.json`。
2. 等待 Unity 解析清单中声明的 Unity 依赖并完成编译。

也可以将完整包目录放入项目的 `Packages/com.yvsim.coroutine-runner`，作为 Embedded Package 使用。

本包提供本地源码、Git 和 OpenUPM 安装方式；固定版本使用 `1.0.0`，具体步骤见下文。

## 使用方法

所有类型位于 `Yvsim.CoroutineRunner` 命名空间，使用前需要添加 `using Yvsim.CoroutineRunner;`。

该命名空间与类名相同，因此在其他 `Yvsim.*` 命名空间内部直接书写 `CoroutineRunner` 会解析到命名空间而非类，此时需要写完整名称 `Yvsim.CoroutineRunner.CoroutineRunner`，或使用别名 `using Runner = Yvsim.CoroutineRunner.CoroutineRunner;`。

所有启动入口只能在播放模式中调用。内部宿主会在第一次使用时自动创建，并在 Hierarchy 中隐藏。

### 运行协程

场景协程归属于启动时的活动场景，场景卸载时自动终止：

```csharp
using System.Collections;
using UnityEngine;
using Yvsim.CoroutineRunner;

public class CoroutineExample : MonoBehaviour
{
    private void Start()
    {
        CoroutineRunner.RunScene(ExampleRoutine());
    }

    private IEnumerator ExampleRoutine()
    {
        yield return new WaitForSeconds(1f);
        Debug.Log("场景协程执行完成");
    }
}
```

需要在场景切换后继续运行时，使用：

```csharp
Coroutine coroutine = CoroutineRunner.RunPersistent(ExampleRoutine());
```

### 指定协程归属的场景

`RunScene`、`DelayScene` 等入口绑定的是**当前活动场景**，不是调用方所在的场景。单场景工程中两者一致，但在 additive 加载多个场景时会有区别：

- 调用方所在场景卸载了，协程仍在运行（宿主在活动场景里）；
- 活动场景卸载了，协程被终止，即使调用方所在场景还在。

需要让协程严格随调用方所在场景一起卸载时，用 `On` 指定归属对象：

```csharp
public class LevelLogic : MonoBehaviour
{
    private void Start()
    {
        // 归属于本对象所在场景，该场景卸载时终止
        CoroutineRunner.On(this).Delay(1f, Save);
        CoroutineRunner.On(this).Repeat(1f, Tick, repeatCount: 3);
    }
}
```

也可以直接指定场景：

```csharp
Scene target = SceneManager.GetSceneByName("Level_01");
CoroutineRunner.In(target).WaitUntil(() => isReady, Begin);
```

`On` 和 `In` 返回 `CoroutineRunner.Scope`，其上提供与 `Scene` 入口一一对应的方法，行为完全一致，区别只在于协程归属的场景：

| `Scope` 方法 | 对应的场景入口 |
| --- | --- |
| `Run(routine)` | `RunScene` |
| `Delay(seconds, action, ignoreTimeScale)` | `DelayScene` |
| `NextFrame(action)` | `NextFrameScene` |
| `EndOfFrame(action)` | `EndOfFrameScene` |
| `NextFixedUpdate(action)` | `NextFixedUpdateScene` |
| `WaitUntil(predicate, action)` | `WaitUntilScene` |
| `Listener(listener)` | `ListenerScene` |
| `Repeat(interval, action, repeatCount, invokeImmediately, ignoreTimeScale)` | `RepeatScene` |
| `LoadBytes(urlOrPath, onSuccess, onError)` | `LoadBytesScene` |
| `LoadText(urlOrPath, onSuccess, onError)` | `LoadTextScene` |

```csharp
CoroutineRunner.Scope scope = CoroutineRunner.On(this);

scope.Run(CustomRoutine());
scope.NextFrame(Refresh);
scope.EndOfFrame(Capture);
scope.NextFixedUpdate(ApplyForce);
scope.Listener(() =>
{
    UpdateProgress();
    return !isFinished;
});
scope.LoadText(configPath, OnConfigLoaded, OnConfigFailed);
```

返回值和异常也与对应的 `Scene` 入口一致：参数非法时抛异常，退出播放模式时返回 `null`。区别是场景无效时的判断对象——`Scope` 检查的是自己绑定的场景，而不是当前活动场景。

`In` 创建时不校验传入的场景，场景无效或尚未加载会在实际启动协程时抛出 `InvalidOperationException`。

`Scope.TargetScene` 可以读回当前绑定的场景：

```csharp
Debug.Log(CoroutineRunner.On(this).TargetScene.name);
```

归属对象位于 DontDestroyOnLoad 场景时，该场景无法承载场景宿主，这类对象应改用 `Persistent` 入口。

### 延迟和帧时机调用

```csharp
CoroutineRunner.DelayScene(1f, () => Debug.Log("延迟一秒"));
CoroutineRunner.DelayPersistent(
    1f,
    () => Debug.Log("忽略时间缩放"),
    ignoreTimeScale: true);

CoroutineRunner.NextFrameScene(() => Debug.Log("下一帧"));
CoroutineRunner.EndOfFrameScene(() => Debug.Log("本帧结束"));
CoroutineRunner.NextFixedUpdateScene(() => Debug.Log("下一次固定更新"));
```

以上功能均提供 `Scene` 和 `Persistent` 两种入口。

### 条件等待和逐帧监听

```csharp
CoroutineRunner.WaitUntilScene(
    () => isReady,
    () => Debug.Log("条件已满足"));

CoroutineRunner.ListenerScene(() =>
{
    UpdateProgress();
    return !isFinished;
});
```

`ListenerScene` 和 `ListenerPersistent` 每帧调用一次监听方法；监听方法返回 `false` 时结束协程。

### 重复调用

```csharp
Coroutine coroutine = CoroutineRunner.RepeatScene(
    interval: 1f,
    action: () => Debug.Log("执行一次"),
    repeatCount: 3,
    invokeImmediately: true,
    ignoreTimeScale: false);
```

`repeatCount` 为 `-1` 时持续调用，为 `0` 时不调用。返回的 `Coroutine` 可用于主动停止。

### 停止协程

```csharp
CoroutineRunner.Stop(coroutine);
CoroutineRunner.StopAllScene();
CoroutineRunner.StopAllPersistent();
```

`StopAllScene` 不影响持久协程，`StopAllPersistent` 也不影响场景协程。

### 读取文本和字节数据

```csharp
using System.IO;
using UnityEngine;
using Yvsim.CoroutineRunner;

string path = Path.Combine(Application.streamingAssetsPath, "Config/data.json");

CoroutineRunner.LoadTextScene(
    path,
    text => Debug.Log(text),
    error => Debug.LogError(error));

CoroutineRunner.LoadBytesPersistent(
    "https://example.com/data.bin",
    bytes => Debug.Log($"读取到 {bytes.Length} 字节"),
    error => Debug.LogError(error));
```

未传入错误回调时，读取错误会通过 `Debug.LogError` 输出。

## 失败与返回值

启动入口有两种不同的失败方式，区分在于是不是调用方的错误：

| 情况 | 行为 |
| --- | --- |
| 参数为 `null` | 抛出 `ArgumentNullException` |
| 时间为负数、`NaN`、无穷大，或 `repeatCount` 小于 `-1` | 抛出 `ArgumentOutOfRangeException` |
| URL 或路径为空 | 抛出 `ArgumentException` |
| 不在播放模式中调用 | 抛出 `InvalidOperationException` |
| 场景入口遇到无效的活动场景 | 抛出 `InvalidOperationException` |
| 正在退出播放模式 | 不抛异常，返回 `null` |

前五种都是调用方的错误，且均在协程启动前同步抛出（包括 `LoadTextScene` 等入口对路径的校验），不会延迟到协程内部。

最后一种不是调用方的错误，所以返回 `null` 而不抛异常。需要区分时可以先判断：

```csharp
if (!CoroutineRunner.IsQuitting)
{
    CoroutineRunner.DelayScene(1f, Save);
}
```

协程自身抛出的异常会通过 `Debug.LogException` 输出，该协程随之终止，内部跟踪记录会被正确清理。
但协程内部通过 `yield return` 嵌套的子协程由 Unity 调度，其异常仍由 Unity 自行处理。

## 场景与持久入口

| 类型 | 生命周期 | 适用场景 |
| --- | --- | --- |
| `Scene` | 所属场景卸载时终止 | 场景界面、关卡逻辑、场景资源读取 |
| `Persistent` | 场景切换后继续运行 | 全局计时、跨场景流程、持久数据读取 |
| `On` / `In` | 指定的场景卸载时终止 | additive 多场景，需要严格绑定调用方所在场景 |

## 主要 API

| API | 说明 |
| --- | --- |
| `RunScene` / `RunPersistent` | 启动自定义协程 |
| `DelayScene` / `DelayPersistent` | 延迟调用 |
| `NextFrameScene` / `NextFramePersistent` | 下一帧调用 |
| `EndOfFrameScene` / `EndOfFramePersistent` | 帧末调用 |
| `NextFixedUpdateScene` / `NextFixedUpdatePersistent` | 下一次固定更新调用 |
| `WaitUntilScene` / `WaitUntilPersistent` | 条件满足后调用 |
| `ListenerScene` / `ListenerPersistent` | 逐帧监听 |
| `RepeatScene` / `RepeatPersistent` | 重复调用 |
| `LoadTextScene` / `LoadTextPersistent` | 读取文本 |
| `LoadBytesScene` / `LoadBytesPersistent` | 读取字节数据 |
| `On` / `In` | 创建绑定到指定对象所在场景或指定场景的协程入口（`Scope`）|
| `Scope.TargetScene` | 读回该入口绑定的场景 |
| `IsQuitting` | 是否正在退出播放模式 |
| `Stop` | 停止指定协程 |
| `StopAllScene` / `StopAllPersistent` | 按生命周期批量停止协程 |

## WebGL 与平台说明

- WebGL 读取远程地址时，目标服务器必须允许 CORS。
- Android StreamingAssets 地址可使用 Unity 提供的 `jar:file` 地址。
- 普通本地路径会自动转换为 file 地址。
- 网络权限、HTTPS 限制和平台安全策略仍由目标平台决定。

## 测试

包内包含两组测试：

- `Tests/Runtime`：PlayMode 测试，覆盖协程生命周期、停止隔离、重复调用、时间缩放、场景归属与卸载、异常处理和数据读取。
- `Tests/Editor`：EditMode 测试，覆盖参数校验和非播放模式下的守卫。

协程只能在播放模式中启动，因此行为类用例必须作为 PlayMode 测试运行。

在其他工程中运行这些测试时，需要在 `Packages/manifest.json` 的 `testables` 中加入本包：

```json
"testables": [
  "com.yvsim.coroutine-runner"
]
```

## 环境要求

- Unity 2021.3 或更高版本。
- Unity UnityWebRequest 模块。

## 许可证

本包使用 MIT License，详情参见 [LICENSE.md](LICENSE.md)。

## 依赖说明

本包不依赖 Inspector Groups 或其他 Yvsim 包。

## 可导入示例

在 Package Manager 的 Samples 中导入 **Coroutine Controls**。演示绑定对象所在场景的延迟、五次重复、条件等待与独立句柄取消。

在新场景的空物体上添加 `CoroutineControlsSample` 后进入 Play Mode。示例操作面板需要 Unity 内置 IMGUI 模块，不需要 Groups；具体步骤和预期结果见 [示例说明](Samples~/Coroutine%20Controls/README.md)。

## 从 GitHub 安装

仓库：[com.yvsim.coroutine-runner](https://github.com/liuyan7669/com.yvsim.coroutine-runner)。仓库名与 UPM 包名一致。

在 Unity Package Manager 选择 Add package from git URL，输入：

```text
https://github.com/liuyan7669/com.yvsim.coroutine-runner.git#main
```

此地址跟随 main 分支。需要固定 1.0.0 源码时，将 `#main` 改为 `#v1.0.0`；Unity 官方依赖仍按 package.json 解析。

## 从 OpenUPM 安装

在 Unity 的 Project Settings > Package Manager 中添加 Scoped Registry：

- Name：OpenUPM
- URL：`https://package.openupm.com`
- Scope：`com.yvsim`

然后在 Package Manager 中通过 Add package by name 输入 `com.yvsim.coroutine-runner`，版本填 `1.0.0`。也可从 My Registries 中选择本包。声明的 Yvsim 依赖由 OpenUPM 解析，Unity 官方依赖仍由 Unity 注册表提供。

如果已安装 OpenUPM CLI，可在工程根目录执行：

```sh
openupm add com.yvsim.coroutine-runner@1.0.0
```

[OpenUPM 包页面](https://openupm.com/packages/com.yvsim.coroutine-runner/)
