using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;

namespace Yvsim.CoroutineRunner
{
    /// <summary>
    /// Unity 协程统一入口，调用方无需继承、持有或查找 MonoBehaviour。
    /// <para>
    /// 核心入口为 <see cref="RunScene"/> 和 <see cref="RunPersistent"/>：场景协程归属于启动时的活动场景，
    /// 场景卸载时自动终止；持久协程可跨场景继续运行。两类协程均可通过 <see cref="Stop"/> 主动停止，
    /// 也可分别通过 <see cref="StopAllScene"/> 和 <see cref="StopAllPersistent"/> 批量停止。
    /// </para>
    /// <para>
    /// 此类同时提供延迟调用、下一帧、帧末、固定更新、条件等待、逐帧监听、重复调用等常用协程功能，
    /// 并提供从 URL、StreamingAssets 地址或本地路径读取文本和字节数据的便捷入口。
    /// </para>
    /// <para>
    /// 数据读取统一使用 UnityWebRequest，不使用平台专属文件读取 API，支持 Unity 编辑器、PC、WebGL、Android 和 iOS。
    /// 普通本地路径会自动转换为 file 地址，HTTP、HTTPS 及 Android 的 jar:file 地址会直接使用。
    /// WebGL 远程请求仍需目标服务器允许 CORS，其他平台还需满足对应系统的网络权限和安全策略。
    /// </para>
    /// <para>
    /// 所有入口均为静态方法，内部场景宿主和持久宿主会按需自动创建，无需在场景中预先配置对象。
    /// </para>
    /// </summary>
    public static class CoroutineRunner
    {
        private const string SceneHostName = "[CoroutineRunner Scene Host]";
        private const string PersistentHostName = "[CoroutineRunner Persistent Host]";

        private static readonly Dictionary<int, CoroutineRunnerHost> SceneHosts =
            new Dictionary<int, CoroutineRunnerHost>();

        private static readonly Dictionary<Coroutine, CoroutineRunnerHost> RunningCoroutines =
            new Dictionary<Coroutine, CoroutineRunnerHost>();

        private static readonly WaitForEndOfFrame EndOfFrameWait = new WaitForEndOfFrame();
        private static readonly WaitForFixedUpdate FixedUpdateWait = new WaitForFixedUpdate();

        private static CoroutineRunnerHost persistentHost;
        private static bool isQuitting;

        /// <summary>
        /// 是否正在退出播放模式。为 true 时所有启动入口都不再启动新协程，直接返回 <see langword="null"/>。
        /// </summary>
        public static bool IsQuitting
        {
            get { return isQuitting; }
        }

        /// <summary>
        /// 在当前活动场景中启动协程。所属场景卸载时，协程会随场景宿主一同终止。
        /// </summary>
        /// <param name="routine">需要执行的协程。</param>
        /// <returns>已启动的 Unity 协程，可传给 <see cref="Stop"/> 主动停止。退出播放模式的过程中不会启动协程，此时返回 <see langword="null"/>（参见 <see cref="IsQuitting"/>）。</returns>
        /// <exception cref="ArgumentNullException"><paramref name="routine"/> 为 null。</exception>
        /// <exception cref="InvalidOperationException">当前不在播放模式，或当前活动场景无效。</exception>
        public static Coroutine RunScene(IEnumerator routine)
        {
            ValidateRoutine(routine);
            EnsurePlaying();

            if (isQuitting)
            {
                return null;
            }

            var scene = SceneManager.GetActiveScene();
            if (!scene.IsValid() || !scene.isLoaded)
            {
                throw new InvalidOperationException("当前没有可用于启动场景协程的已加载活动场景。");
            }

            return StartTracked(GetOrCreateSceneHost(scene), routine);
        }

        /// <summary>
        /// 启动可跨场景继续执行的持久协程。
        /// </summary>
        /// <param name="routine">需要执行的协程。</param>
        /// <returns>已启动的 Unity 协程，可传给 <see cref="Stop"/> 主动停止。退出播放模式的过程中不会启动协程，此时返回 <see langword="null"/>（参见 <see cref="IsQuitting"/>）。</returns>
        /// <exception cref="ArgumentNullException"><paramref name="routine"/> 为 null。</exception>
        /// <exception cref="InvalidOperationException">当前不在播放模式。</exception>
        public static Coroutine RunPersistent(IEnumerator routine)
        {
            ValidateRoutine(routine);
            EnsurePlaying();

            if (isQuitting)
            {
                return null;
            }

            return StartTracked(GetOrCreatePersistentHost(), routine);
        }

        /// <summary>
        /// 创建绑定到 <paramref name="owner"/> 所在场景的协程入口。
        /// <para>
        /// 与 <see cref="RunScene"/> 等入口的区别在于：那些入口绑定的是当前活动场景，
        /// 在 additive 加载的场景中调用时，协程会挂在活动场景而不是调用方所在的场景上。
        /// 需要让协程严格随调用方所在场景一起卸载时，应使用本入口。
        /// </para>
        /// </summary>
        /// <param name="owner">归属对象，通常直接传入调用方自身（<c>this</c>）。</param>
        /// <returns>绑定到该对象所在场景的协程入口。</returns>
        /// <exception cref="ArgumentNullException"><paramref name="owner"/> 为 null 或已被销毁。</exception>
        /// <remarks>
        /// 归属对象位于 DontDestroyOnLoad 场景时，其所在场景无法承载场景宿主，
        /// 此类对象应改用 <see cref="RunPersistent"/> 等持久入口。
        /// </remarks>
        public static Scope On(Component owner)
        {
            if (owner == null)
            {
                throw new ArgumentNullException(nameof(owner));
            }

            return new Scope(owner.gameObject.scene);
        }

        /// <summary>
        /// 创建绑定到 <paramref name="owner"/> 所在场景的协程入口。
        /// </summary>
        /// <param name="owner">归属对象。</param>
        /// <returns>绑定到该对象所在场景的协程入口。</returns>
        /// <exception cref="ArgumentNullException"><paramref name="owner"/> 为 null 或已被销毁。</exception>
        public static Scope On(GameObject owner)
        {
            if (owner == null)
            {
                throw new ArgumentNullException(nameof(owner));
            }

            return new Scope(owner.scene);
        }

        /// <summary>
        /// 创建绑定到指定场景的协程入口。
        /// </summary>
        /// <param name="scene">目标场景，必须是已加载的有效场景。</param>
        /// <returns>绑定到该场景的协程入口。</returns>
        /// <remarks>
        /// 此处不校验 <paramref name="scene"/>；场景无效或尚未加载时，
        /// 会在实际启动协程时抛出 <see cref="InvalidOperationException"/>。
        /// </remarks>
        public static Scope In(Scene scene)
        {
            return new Scope(scene);
        }

        /// <summary>
        /// 延迟指定时间后，在当前活动场景的生命周期内调用方法。
        /// </summary>
        /// <param name="seconds">延迟秒数，必须大于或等于 0。</param>
        /// <param name="action">延迟结束后调用的方法。</param>
        /// <param name="ignoreTimeScale">是否忽略 <see cref="Time.timeScale"/>。</param>
        /// <returns>已启动的场景协程，可传给 <see cref="Stop"/> 取消调用。退出播放模式的过程中不会启动协程，此时返回 <see langword="null"/>（参见 <see cref="IsQuitting"/>）。</returns>
        /// <exception cref="ArgumentNullException"><paramref name="action"/> 为 null。</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="seconds"/> 为负数、NaN 或无穷大。</exception>
        /// <exception cref="InvalidOperationException">当前不在播放模式，或当前活动场景无效。</exception>
        public static Coroutine DelayScene(float seconds, Action action, bool ignoreTimeScale = false)
        {
            ValidateDuration(seconds, nameof(seconds));
            ValidateAction(action);
            return RunScene(DelayRoutine(seconds, action, ignoreTimeScale));
        }

        /// <summary>
        /// 延迟指定时间后调用方法，场景切换不会中断等待。
        /// </summary>
        /// <param name="seconds">延迟秒数，必须大于或等于 0。</param>
        /// <param name="action">延迟结束后调用的方法。</param>
        /// <param name="ignoreTimeScale">是否忽略 <see cref="Time.timeScale"/>。</param>
        /// <returns>已启动的持久协程，可传给 <see cref="Stop"/> 取消调用。退出播放模式的过程中不会启动协程，此时返回 <see langword="null"/>（参见 <see cref="IsQuitting"/>）。</returns>
        /// <exception cref="ArgumentNullException"><paramref name="action"/> 为 null。</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="seconds"/> 为负数、NaN 或无穷大。</exception>
        /// <exception cref="InvalidOperationException">当前不在播放模式。</exception>
        public static Coroutine DelayPersistent(float seconds, Action action, bool ignoreTimeScale = false)
        {
            ValidateDuration(seconds, nameof(seconds));
            ValidateAction(action);
            return RunPersistent(DelayRoutine(seconds, action, ignoreTimeScale));
        }

        /// <summary>
        /// 在下一帧调用方法，所属场景卸载时自动取消。
        /// </summary>
        /// <param name="action">下一帧调用的方法。</param>
        /// <returns>已启动的场景协程，可传给 <see cref="Stop"/> 取消调用。退出播放模式的过程中不会启动协程，此时返回 <see langword="null"/>（参见 <see cref="IsQuitting"/>）。</returns>
        /// <exception cref="ArgumentNullException"><paramref name="action"/> 为 null。</exception>
        /// <exception cref="InvalidOperationException">当前不在播放模式，或当前活动场景无效。</exception>
        public static Coroutine NextFrameScene(Action action)
        {
            ValidateAction(action);
            return RunScene(NextFrameRoutine(action));
        }

        /// <summary>
        /// 在下一帧调用方法，场景切换不会中断等待。
        /// </summary>
        /// <param name="action">下一帧调用的方法。</param>
        /// <returns>已启动的持久协程，可传给 <see cref="Stop"/> 取消调用。退出播放模式的过程中不会启动协程，此时返回 <see langword="null"/>（参见 <see cref="IsQuitting"/>）。</returns>
        /// <exception cref="ArgumentNullException"><paramref name="action"/> 为 null。</exception>
        /// <exception cref="InvalidOperationException">当前不在播放模式。</exception>
        public static Coroutine NextFramePersistent(Action action)
        {
            ValidateAction(action);
            return RunPersistent(NextFrameRoutine(action));
        }

        /// <summary>
        /// 在当前帧末调用方法，所属场景卸载时自动取消。
        /// </summary>
        /// <param name="action">帧末调用的方法。</param>
        /// <returns>已启动的场景协程，可传给 <see cref="Stop"/> 取消调用。退出播放模式的过程中不会启动协程，此时返回 <see langword="null"/>（参见 <see cref="IsQuitting"/>）。</returns>
        /// <exception cref="ArgumentNullException"><paramref name="action"/> 为 null。</exception>
        /// <exception cref="InvalidOperationException">当前不在播放模式，或当前活动场景无效。</exception>
        public static Coroutine EndOfFrameScene(Action action)
        {
            ValidateAction(action);
            return RunScene(EndOfFrameRoutine(action));
        }

        /// <summary>
        /// 在当前帧末调用方法，场景切换不会中断等待。
        /// </summary>
        /// <param name="action">帧末调用的方法。</param>
        /// <returns>已启动的持久协程，可传给 <see cref="Stop"/> 取消调用。退出播放模式的过程中不会启动协程，此时返回 <see langword="null"/>（参见 <see cref="IsQuitting"/>）。</returns>
        /// <exception cref="ArgumentNullException"><paramref name="action"/> 为 null。</exception>
        /// <exception cref="InvalidOperationException">当前不在播放模式。</exception>
        public static Coroutine EndOfFramePersistent(Action action)
        {
            ValidateAction(action);
            return RunPersistent(EndOfFrameRoutine(action));
        }

        /// <summary>
        /// 在下一次固定更新时调用方法，所属场景卸载时自动取消。
        /// </summary>
        /// <param name="action">固定更新时调用的方法。</param>
        /// <returns>已启动的场景协程，可传给 <see cref="Stop"/> 取消调用。退出播放模式的过程中不会启动协程，此时返回 <see langword="null"/>（参见 <see cref="IsQuitting"/>）。</returns>
        /// <exception cref="ArgumentNullException"><paramref name="action"/> 为 null。</exception>
        /// <exception cref="InvalidOperationException">当前不在播放模式，或当前活动场景无效。</exception>
        public static Coroutine NextFixedUpdateScene(Action action)
        {
            ValidateAction(action);
            return RunScene(NextFixedUpdateRoutine(action));
        }

        /// <summary>
        /// 在下一次固定更新时调用方法，场景切换不会中断等待。
        /// </summary>
        /// <param name="action">固定更新时调用的方法。</param>
        /// <returns>已启动的持久协程，可传给 <see cref="Stop"/> 取消调用。退出播放模式的过程中不会启动协程，此时返回 <see langword="null"/>（参见 <see cref="IsQuitting"/>）。</returns>
        /// <exception cref="ArgumentNullException"><paramref name="action"/> 为 null。</exception>
        /// <exception cref="InvalidOperationException">当前不在播放模式。</exception>
        public static Coroutine NextFixedUpdatePersistent(Action action)
        {
            ValidateAction(action);
            return RunPersistent(NextFixedUpdateRoutine(action));
        }

        /// <summary>
        /// 等待条件成立后调用方法，所属场景卸载时自动取消。
        /// </summary>
        /// <param name="predicate">每帧检查的条件。</param>
        /// <param name="action">条件成立后调用的方法。</param>
        /// <returns>已启动的场景协程，可传给 <see cref="Stop"/> 取消调用。退出播放模式的过程中不会启动协程，此时返回 <see langword="null"/>（参见 <see cref="IsQuitting"/>）。</returns>
        /// <exception cref="ArgumentNullException"><paramref name="predicate"/> 或 <paramref name="action"/> 为 null。</exception>
        /// <exception cref="InvalidOperationException">当前不在播放模式，或当前活动场景无效。</exception>
        public static Coroutine WaitUntilScene(Func<bool> predicate, Action action)
        {
            ValidatePredicate(predicate);
            ValidateAction(action);
            return RunScene(WaitUntilRoutine(predicate, action));
        }

        /// <summary>
        /// 等待条件成立后调用方法，场景切换不会中断等待。
        /// </summary>
        /// <param name="predicate">每帧检查的条件。</param>
        /// <param name="action">条件成立后调用的方法。</param>
        /// <returns>已启动的持久协程，可传给 <see cref="Stop"/> 取消调用。退出播放模式的过程中不会启动协程，此时返回 <see langword="null"/>（参见 <see cref="IsQuitting"/>）。</returns>
        /// <exception cref="ArgumentNullException"><paramref name="predicate"/> 或 <paramref name="action"/> 为 null。</exception>
        /// <exception cref="InvalidOperationException">当前不在播放模式。</exception>
        public static Coroutine WaitUntilPersistent(Func<bool> predicate, Action action)
        {
            ValidatePredicate(predicate);
            ValidateAction(action);
            return RunPersistent(WaitUntilRoutine(predicate, action));
        }

        /// <summary>
        /// 在当前活动场景的生命周期内，每帧调用一次监听方法，形成类似 Update 的持续监听。
        /// 监听方法返回 true 时下一帧继续监听，返回 false 时结束协程。
        /// </summary>
        /// <param name="listener">需要持续调用的监听方法；返回 true 继续监听，返回 false 结束监听。</param>
        /// <returns>已启动的场景协程，可传给 <see cref="Stop"/> 主动停止监听。退出播放模式的过程中不会启动协程，此时返回 <see langword="null"/>（参见 <see cref="IsQuitting"/>）。</returns>
        /// <exception cref="ArgumentNullException"><paramref name="listener"/> 为 null。</exception>
        /// <exception cref="InvalidOperationException">当前不在播放模式，或当前活动场景无效。</exception>
        public static Coroutine ListenerScene(Func<bool> listener)
        {
            ValidateListener(listener);
            return RunScene(ListenerRoutine(listener));
        }

        /// <summary>
        /// 每帧调用一次监听方法，形成可跨场景运行的类似 Update 的持续监听。
        /// 监听方法返回 true 时下一帧继续监听，返回 false 时结束协程。
        /// </summary>
        /// <param name="listener">需要持续调用的监听方法；返回 true 继续监听，返回 false 结束监听。</param>
        /// <returns>已启动的持久协程，可传给 <see cref="Stop"/> 主动停止监听。退出播放模式的过程中不会启动协程，此时返回 <see langword="null"/>（参见 <see cref="IsQuitting"/>）。</returns>
        /// <exception cref="ArgumentNullException"><paramref name="listener"/> 为 null。</exception>
        /// <exception cref="InvalidOperationException">当前不在播放模式。</exception>
        public static Coroutine ListenerPersistent(Func<bool> listener)
        {
            ValidateListener(listener);
            return RunPersistent(ListenerRoutine(listener));
        }

        /// <summary>
        /// 在当前活动场景的生命周期内，按指定间隔重复调用方法。
        /// </summary>
        /// <param name="interval">调用间隔秒数，必须大于或等于 0。</param>
        /// <param name="action">每次调用的方法。</param>
        /// <param name="repeatCount">调用次数；-1 表示持续调用，0 表示不调用。</param>
        /// <param name="invokeImmediately">是否启动后立即调用一次。</param>
        /// <param name="ignoreTimeScale">是否忽略 <see cref="Time.timeScale"/>。</param>
        /// <returns>已启动的场景协程，可传给 <see cref="Stop"/> 停止重复调用。退出播放模式的过程中不会启动协程，此时返回 <see langword="null"/>（参见 <see cref="IsQuitting"/>）。</returns>
        /// <exception cref="ArgumentNullException"><paramref name="action"/> 为 null。</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="interval"/> 为负数、NaN 或无穷大，或 <paramref name="repeatCount"/> 小于 -1。</exception>
        /// <exception cref="InvalidOperationException">当前不在播放模式，或当前活动场景无效。</exception>
        public static Coroutine RepeatScene(
            float interval,
            Action action,
            int repeatCount = -1,
            bool invokeImmediately = false,
            bool ignoreTimeScale = false)
        {
            ValidateDuration(interval, nameof(interval));
            ValidateRepeatCount(repeatCount);
            ValidateAction(action);
            return RunScene(RepeatRoutine(interval, action, repeatCount, invokeImmediately, ignoreTimeScale));
        }

        /// <summary>
        /// 按指定间隔重复调用方法，场景切换不会中断等待。
        /// </summary>
        /// <param name="interval">调用间隔秒数，必须大于或等于 0。</param>
        /// <param name="action">每次调用的方法。</param>
        /// <param name="repeatCount">调用次数；-1 表示持续调用，0 表示不调用。</param>
        /// <param name="invokeImmediately">是否启动后立即调用一次。</param>
        /// <param name="ignoreTimeScale">是否忽略 <see cref="Time.timeScale"/>。</param>
        /// <returns>已启动的持久协程，可传给 <see cref="Stop"/> 停止重复调用。退出播放模式的过程中不会启动协程，此时返回 <see langword="null"/>（参见 <see cref="IsQuitting"/>）。</returns>
        /// <exception cref="ArgumentNullException"><paramref name="action"/> 为 null。</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="interval"/> 为负数、NaN 或无穷大，或 <paramref name="repeatCount"/> 小于 -1。</exception>
        /// <exception cref="InvalidOperationException">当前不在播放模式。</exception>
        public static Coroutine RepeatPersistent(
            float interval,
            Action action,
            int repeatCount = -1,
            bool invokeImmediately = false,
            bool ignoreTimeScale = false)
        {
            ValidateDuration(interval, nameof(interval));
            ValidateRepeatCount(repeatCount);
            ValidateAction(action);
            return RunPersistent(RepeatRoutine(interval, action, repeatCount, invokeImmediately, ignoreTimeScale));
        }

        /// <summary>
        /// 从 URL 或本地路径读取字节数据，读取过程归属于当前活动场景。
        /// </summary>
        /// <param name="urlOrPath">HTTP、HTTPS、file、jar 地址或本地文件路径。</param>
        /// <param name="onSuccess">读取成功时调用，并传入字节数据。</param>
        /// <param name="onError">读取失败时调用，并传入错误信息；为 null 时输出错误日志。</param>
        /// <returns>已启动的场景协程，可传给 <see cref="Stop"/> 取消读取。退出播放模式的过程中不会启动协程，此时返回 <see langword="null"/>（参见 <see cref="IsQuitting"/>）。</returns>
        /// <exception cref="ArgumentNullException"><paramref name="onSuccess"/> 为 null。</exception>
        /// <exception cref="ArgumentException"><paramref name="urlOrPath"/> 为空或只包含空白字符。</exception>
        /// <exception cref="InvalidOperationException">当前不在播放模式，或当前活动场景无效。</exception>
        public static Coroutine LoadBytesScene(
            string urlOrPath,
            Action<byte[]> onSuccess,
            Action<string> onError = null)
        {
            ValidateSuccessCallback(onSuccess);
            return RunScene(LoadBytesRoutine(NormalizeRequestUrl(urlOrPath), onSuccess, onError));
        }

        /// <summary>
        /// 从 URL 或本地路径读取字节数据，场景切换不会中断读取。
        /// </summary>
        /// <param name="urlOrPath">HTTP、HTTPS、file、jar 地址或本地文件路径。</param>
        /// <param name="onSuccess">读取成功时调用，并传入字节数据。</param>
        /// <param name="onError">读取失败时调用，并传入错误信息；为 null 时输出错误日志。</param>
        /// <returns>已启动的持久协程，可传给 <see cref="Stop"/> 取消读取。退出播放模式的过程中不会启动协程，此时返回 <see langword="null"/>（参见 <see cref="IsQuitting"/>）。</returns>
        /// <exception cref="ArgumentNullException"><paramref name="onSuccess"/> 为 null。</exception>
        /// <exception cref="ArgumentException"><paramref name="urlOrPath"/> 为空或只包含空白字符。</exception>
        /// <exception cref="InvalidOperationException">当前不在播放模式。</exception>
        public static Coroutine LoadBytesPersistent(
            string urlOrPath,
            Action<byte[]> onSuccess,
            Action<string> onError = null)
        {
            ValidateSuccessCallback(onSuccess);
            return RunPersistent(LoadBytesRoutine(NormalizeRequestUrl(urlOrPath), onSuccess, onError));
        }

        /// <summary>
        /// 从 URL 或本地路径读取文本，读取过程归属于当前活动场景。
        /// </summary>
        /// <param name="urlOrPath">HTTP、HTTPS、file、jar 地址或本地文件路径。</param>
        /// <param name="onSuccess">读取成功时调用，并传入文本内容。</param>
        /// <param name="onError">读取失败时调用，并传入错误信息；为 null 时输出错误日志。</param>
        /// <returns>已启动的场景协程，可传给 <see cref="Stop"/> 取消读取。退出播放模式的过程中不会启动协程，此时返回 <see langword="null"/>（参见 <see cref="IsQuitting"/>）。</returns>
        /// <exception cref="ArgumentNullException"><paramref name="onSuccess"/> 为 null。</exception>
        /// <exception cref="ArgumentException"><paramref name="urlOrPath"/> 为空或只包含空白字符。</exception>
        /// <exception cref="InvalidOperationException">当前不在播放模式，或当前活动场景无效。</exception>
        public static Coroutine LoadTextScene(
            string urlOrPath,
            Action<string> onSuccess,
            Action<string> onError = null)
        {
            ValidateSuccessCallback(onSuccess);
            return RunScene(LoadTextRoutine(NormalizeRequestUrl(urlOrPath), onSuccess, onError));
        }

        /// <summary>
        /// 从 URL 或本地路径读取文本，场景切换不会中断读取。
        /// </summary>
        /// <param name="urlOrPath">HTTP、HTTPS、file、jar 地址或本地文件路径。</param>
        /// <param name="onSuccess">读取成功时调用，并传入文本内容。</param>
        /// <param name="onError">读取失败时调用，并传入错误信息；为 null 时输出错误日志。</param>
        /// <returns>已启动的持久协程，可传给 <see cref="Stop"/> 取消读取。退出播放模式的过程中不会启动协程，此时返回 <see langword="null"/>（参见 <see cref="IsQuitting"/>）。</returns>
        /// <exception cref="ArgumentNullException"><paramref name="onSuccess"/> 为 null。</exception>
        /// <exception cref="ArgumentException"><paramref name="urlOrPath"/> 为空或只包含空白字符。</exception>
        /// <exception cref="InvalidOperationException">当前不在播放模式。</exception>
        public static Coroutine LoadTextPersistent(
            string urlOrPath,
            Action<string> onSuccess,
            Action<string> onError = null)
        {
            ValidateSuccessCallback(onSuccess);
            return RunPersistent(LoadTextRoutine(NormalizeRequestUrl(urlOrPath), onSuccess, onError));
        }

        /// <summary>
        /// 停止由 <see cref="RunScene"/> 或 <see cref="RunPersistent"/> 启动的指定协程。
        /// 传入 null、已结束协程或宿主已销毁的协程时安全返回。
        /// </summary>
        /// <param name="coroutine">需要停止的协程。</param>
        public static void Stop(Coroutine coroutine)
        {
            if (coroutine == null || !RunningCoroutines.TryGetValue(coroutine, out var host))
            {
                return;
            }

            RunningCoroutines.Remove(coroutine);
            if (host != null)
            {
                host.StopCoroutine(coroutine);
            }
        }

        /// <summary>
        /// 停止当前所有已加载场景中的场景协程，不影响持久协程。
        /// </summary>
        public static void StopAllScene()
        {
            var hosts = CollectHosts(CoroutineRunnerHostLifecycle.Scene);
            RemoveTrackedCoroutines(CoroutineRunnerHostLifecycle.Scene);

            foreach (var host in hosts)
            {
                if (host != null)
                {
                    host.StopAllCoroutines();
                }
            }
        }

        /// <summary>
        /// 停止所有持久协程，不影响场景协程。
        /// </summary>
        public static void StopAllPersistent()
        {
            var hosts = CollectHosts(CoroutineRunnerHostLifecycle.Persistent);
            RemoveTrackedCoroutines(CoroutineRunnerHostLifecycle.Persistent);

            foreach (var host in hosts)
            {
                if (host != null)
                {
                    host.StopAllCoroutines();
                }
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticState()
        {
            Application.quitting -= NotifyApplicationQuitting;
            Application.quitting += NotifyApplicationQuitting;
            SceneHosts.Clear();
            RunningCoroutines.Clear();
            persistentHost = null;
            isQuitting = false;
        }

        private static void ValidateRoutine(IEnumerator routine)
        {
            if (routine == null)
            {
                throw new ArgumentNullException(nameof(routine));
            }
        }

        private static void ValidateAction(Action action)
        {
            if (action == null)
            {
                throw new ArgumentNullException(nameof(action));
            }
        }

        private static void ValidateSuccessCallback<T>(Action<T> onSuccess)
        {
            if (onSuccess == null)
            {
                throw new ArgumentNullException(nameof(onSuccess));
            }
        }

        private static void ValidatePredicate(Func<bool> predicate)
        {
            if (predicate == null)
            {
                throw new ArgumentNullException(nameof(predicate));
            }
        }

        private static void ValidateListener(Func<bool> listener)
        {
            if (listener == null)
            {
                throw new ArgumentNullException(nameof(listener));
            }
        }

        private static void ValidateDuration(float duration, string parameterName)
        {
            if (float.IsNaN(duration) || float.IsInfinity(duration) || duration < 0f)
            {
                throw new ArgumentOutOfRangeException(parameterName, "时间必须是大于或等于 0 的有限数值。");
            }
        }

        private static void ValidateRepeatCount(int repeatCount)
        {
            if (repeatCount < -1)
            {
                throw new ArgumentOutOfRangeException(nameof(repeatCount), "重复次数只能是 -1 或大于等于 0 的数值。");
            }
        }

        private static void EnsurePlaying()
        {
            if (!Application.isPlaying)
            {
                throw new InvalidOperationException("CoroutineRunner 只能在播放模式中启动协程。");
            }
        }

        private static IEnumerator DelayRoutine(float seconds, Action action, bool ignoreTimeScale)
        {
            if (ignoreTimeScale)
            {
                yield return new WaitForSecondsRealtime(seconds);
            }
            else
            {
                yield return new WaitForSeconds(seconds);
            }

            action();
        }

        private static IEnumerator NextFrameRoutine(Action action)
        {
            yield return null;
            action();
        }

        private static IEnumerator EndOfFrameRoutine(Action action)
        {
            yield return EndOfFrameWait;
            action();
        }

        private static IEnumerator NextFixedUpdateRoutine(Action action)
        {
            yield return FixedUpdateWait;
            action();
        }

        private static IEnumerator WaitUntilRoutine(Func<bool> predicate, Action action)
        {
            yield return new WaitUntil(predicate);
            action();
        }

        private static IEnumerator ListenerRoutine(Func<bool> listener)
        {
            while (true)
            {
                if (!listener())
                {
                    yield break;
                }

                yield return null;
            }
        }

        private static IEnumerator RepeatRoutine(
            float interval,
            Action action,
            int repeatCount,
            bool invokeImmediately,
            bool ignoreTimeScale)
        {
            var remainingCount = repeatCount;
            if (remainingCount == 0)
            {
                yield break;
            }

            if (invokeImmediately)
            {
                action();
                if (remainingCount > 0)
                {
                    remainingCount--;
                }
            }

            // interval 在整个循环中不变，等待对象只创建一次，避免每次重复都产生垃圾。
            WaitForSeconds scaledWait = null;

            while (remainingCount != 0)
            {
                if (ignoreTimeScale)
                {
                    // 与 WaitForSecondsRealtime 一致：至少等待一帧，interval 为 0 时也不会在同一帧内空转。
                    var endTime = Time.realtimeSinceStartup + interval;
                    do
                    {
                        yield return null;
                    }
                    while (Time.realtimeSinceStartup < endTime);
                }
                else
                {
                    scaledWait = scaledWait ?? new WaitForSeconds(interval);
                    yield return scaledWait;
                }

                action();
                if (remainingCount > 0)
                {
                    remainingCount--;
                }
            }
        }

        private static IEnumerator LoadBytesRoutine(
            string requestUrl,
            Action<byte[]> onSuccess,
            Action<string> onError)
        {
            using (var request = UnityWebRequest.Get(requestUrl))
            {
                yield return request.SendWebRequest();

                if (request.result != UnityWebRequest.Result.Success)
                {
                    ReportRequestError(request, requestUrl, onError);
                    yield break;
                }

                onSuccess(request.downloadHandler.data);
            }
        }

        private static IEnumerator LoadTextRoutine(
            string requestUrl,
            Action<string> onSuccess,
            Action<string> onError)
        {
            using (var request = UnityWebRequest.Get(requestUrl))
            {
                yield return request.SendWebRequest();

                if (request.result != UnityWebRequest.Result.Success)
                {
                    ReportRequestError(request, requestUrl, onError);
                    yield break;
                }

                onSuccess(request.downloadHandler.text);
            }
        }

        private static string NormalizeRequestUrl(string urlOrPath)
        {
            if (string.IsNullOrWhiteSpace(urlOrPath))
            {
                throw new ArgumentException("URL 或路径不能为空。", nameof(urlOrPath));
            }

            if (Uri.TryCreate(urlOrPath, UriKind.Absolute, out var absoluteUri))
            {
                return absoluteUri.IsFile ? absoluteUri.AbsoluteUri : urlOrPath;
            }

            return new Uri(Path.GetFullPath(urlOrPath)).AbsoluteUri;
        }

        private static void ReportRequestError(
            UnityWebRequest request,
            string requestUrl,
            Action<string> onError)
        {
            var responseCode = request.responseCode > 0 ? $"，状态码：{request.responseCode}" : string.Empty;
            var errorMessage = $"读取数据失败：{requestUrl}{responseCode}，错误：{request.error}";

            if (onError != null)
            {
                onError(errorMessage);
            }
            else
            {
                Debug.LogError(errorMessage);
            }
        }

        private static CoroutineRunnerHost GetOrCreateSceneHost(Scene scene)
        {
            if (SceneHosts.TryGetValue(scene.handle, out var host) && host != null)
            {
                return host;
            }

            host = FindExistingSceneHost(scene);
            if (host == null)
            {
                var hostObject = new GameObject(SceneHostName)
                {
                    hideFlags = HideFlags.HideInHierarchy
                };

                SceneManager.MoveGameObjectToScene(hostObject, scene);
                host = hostObject.AddComponent<CoroutineRunnerHost>();
                host.Configure(CoroutineRunnerHostLifecycle.Scene);
            }

            SceneHosts[scene.handle] = host;
            return host;
        }

        private static CoroutineRunnerHost GetOrCreatePersistentHost()
        {
            if (persistentHost != null)
            {
                return persistentHost;
            }

            persistentHost = FindExistingPersistentHost();
            if (persistentHost == null)
            {
                var hostObject = new GameObject(PersistentHostName)
                {
                    hideFlags = HideFlags.HideInHierarchy
                };

                persistentHost = hostObject.AddComponent<CoroutineRunnerHost>();
                persistentHost.Configure(CoroutineRunnerHostLifecycle.Persistent);
                UnityEngine.Object.DontDestroyOnLoad(hostObject);
            }

            return persistentHost;
        }

        /// <summary>
        /// 在指定场景的根对象中查找已存在的场景宿主。宿主始终是场景根对象，
        /// 因此只需遍历根对象，无需扫描全部已加载对象。
        /// </summary>
        private static CoroutineRunnerHost FindExistingSceneHost(Scene scene)
        {
            CoroutineRunnerHost selectedHost = null;
            var roots = scene.GetRootGameObjects();

            for (int i = 0; i < roots.Length; i++)
            {
                var host = roots[i].GetComponent<CoroutineRunnerHost>();
                if (host == null || host.Lifecycle != CoroutineRunnerHostLifecycle.Scene)
                {
                    continue;
                }

                if (selectedHost == null)
                {
                    selectedHost = host;
                    selectedHost.gameObject.hideFlags = HideFlags.HideInHierarchy;
                    continue;
                }

                host.gameObject.SetActive(false);
                UnityEngine.Object.Destroy(host.gameObject);
            }

            return selectedHost;
        }

        /// <summary>
        /// 查找已存在的持久宿主。持久宿主位于 DontDestroyOnLoad 场景，没有直接的枚举接口，
        /// 只能扫描全部对象；该查找在每次静态状态重置后最多执行一次。
        /// </summary>
        private static CoroutineRunnerHost FindExistingPersistentHost()
        {
            CoroutineRunnerHost selectedHost = null;
            var hosts = Resources.FindObjectsOfTypeAll<CoroutineRunnerHost>();

            for (int i = 0; i < hosts.Length; i++)
            {
                var host = hosts[i];
                if (host == null || host.Lifecycle != CoroutineRunnerHostLifecycle.Persistent)
                {
                    continue;
                }

                // 预制体资源等未实例化的对象所属场景无效，必须排除，避免误删工程资源。
                if (!host.gameObject.scene.IsValid())
                {
                    continue;
                }

                if (selectedHost == null)
                {
                    selectedHost = host;
                    selectedHost.gameObject.hideFlags = HideFlags.HideInHierarchy;
                    continue;
                }

                host.gameObject.SetActive(false);
                UnityEngine.Object.Destroy(host.gameObject);
            }

            return selectedHost;
        }

        /// <summary>
        /// 在指定场景中启动协程，供 <see cref="Scope"/> 使用。
        /// </summary>
        private static Coroutine RunInScene(Scene scene, IEnumerator routine)
        {
            ValidateRoutine(routine);
            EnsurePlaying();

            if (isQuitting)
            {
                return null;
            }

            if (!scene.IsValid() || !scene.isLoaded)
            {
                throw new InvalidOperationException(
                    $"指定的场景无效或尚未加载，无法启动场景协程：{scene.name}。");
            }

            return StartTracked(GetOrCreateSceneHost(scene), routine);
        }

        private static Coroutine StartTracked(CoroutineRunnerHost host, IEnumerator routine)
        {
            var registration = new CoroutineRegistration();
            var coroutine = host.StartCoroutine(RunTracked(routine, registration));
            registration.Coroutine = coroutine;

            if (!registration.Completed && coroutine != null)
            {
                RunningCoroutines[coroutine] = host;
            }

            return coroutine;
        }

        /// <summary>
        /// 包装用户协程并跟踪其生命周期。
        /// <para>
        /// 此处手动驱动 <paramref name="routine"/>，而不是直接 yield return：
        /// 直接 yield return 时内层枚举器由 Unity 调度，它抛出的异常会终止整个协程且不保证执行到外层 finally，
        /// 会在 RunningCoroutines 中遗留条目；持久协程没有场景卸载兜底，这些条目会一直积累。
        /// </para>
        /// <para>
        /// 注意：用户协程内部通过 yield return 嵌套的子协程仍由 Unity 调度，其异常不经过这里。
        /// </para>
        /// </summary>
        private static IEnumerator RunTracked(IEnumerator routine, CoroutineRegistration registration)
        {
            try
            {
                while (true)
                {
                    object current;

                    try
                    {
                        if (!routine.MoveNext())
                        {
                            break;
                        }

                        current = routine.Current;
                    }
                    catch (Exception exception)
                    {
                        Debug.LogException(exception);
                        break;
                    }

                    yield return current;
                }
            }
            finally
            {
                registration.Completed = true;
                if (registration.Coroutine != null)
                {
                    RunningCoroutines.Remove(registration.Coroutine);
                }

                var disposable = routine as IDisposable;
                if (disposable != null)
                {
                    disposable.Dispose();
                }
            }
        }

        private static HashSet<CoroutineRunnerHost> CollectHosts(CoroutineRunnerHostLifecycle lifecycle)
        {
            var hosts = new HashSet<CoroutineRunnerHost>();

            foreach (var pair in RunningCoroutines)
            {
                if (pair.Value != null && pair.Value.Lifecycle == lifecycle)
                {
                    hosts.Add(pair.Value);
                }
            }

            if (lifecycle == CoroutineRunnerHostLifecycle.Scene)
            {
                foreach (var pair in SceneHosts)
                {
                    if (pair.Value != null)
                    {
                        hosts.Add(pair.Value);
                    }
                }
            }
            else if (persistentHost != null)
            {
                hosts.Add(persistentHost);
            }

            return hosts;
        }

        private static void RemoveTrackedCoroutines(CoroutineRunnerHostLifecycle lifecycle)
        {
            List<Coroutine> coroutinesToRemove = null;

            foreach (var pair in RunningCoroutines)
            {
                if (pair.Value == null || pair.Value.Lifecycle == lifecycle)
                {
                    if (coroutinesToRemove == null)
                    {
                        coroutinesToRemove = new List<Coroutine>();
                    }

                    coroutinesToRemove.Add(pair.Key);
                }
            }

            if (coroutinesToRemove == null)
            {
                return;
            }

            for (int i = 0; i < coroutinesToRemove.Count; i++)
            {
                RunningCoroutines.Remove(coroutinesToRemove[i]);
            }
        }

        internal static void NotifyHostDestroyed(CoroutineRunnerHost host)
        {
            if (host == null)
            {
                return;
            }

            if (persistentHost == host)
            {
                persistentHost = null;
            }

            List<int> sceneHandlesToRemove = null;
            foreach (var pair in SceneHosts)
            {
                if (pair.Value != host && pair.Value != null)
                {
                    continue;
                }

                if (sceneHandlesToRemove == null)
                {
                    sceneHandlesToRemove = new List<int>();
                }

                sceneHandlesToRemove.Add(pair.Key);
            }

            if (sceneHandlesToRemove != null)
            {
                for (int i = 0; i < sceneHandlesToRemove.Count; i++)
                {
                    SceneHosts.Remove(sceneHandlesToRemove[i]);
                }
            }

            List<Coroutine> coroutinesToRemove = null;
            foreach (var pair in RunningCoroutines)
            {
                if (pair.Value != host && pair.Value != null)
                {
                    continue;
                }

                if (coroutinesToRemove == null)
                {
                    coroutinesToRemove = new List<Coroutine>();
                }

                coroutinesToRemove.Add(pair.Key);
            }

            if (coroutinesToRemove == null)
            {
                return;
            }

            for (int i = 0; i < coroutinesToRemove.Count; i++)
            {
                RunningCoroutines.Remove(coroutinesToRemove[i]);
            }
        }

        internal static void NotifyApplicationQuitting()
        {
            isQuitting = true;
        }

        /// <summary>
        /// 绑定到某个具体场景的协程入口，由 <see cref="On(Component)"/>、<see cref="On(GameObject)"/>
        /// 和 <see cref="In"/> 创建。
        /// <para>
        /// 各方法与 <see cref="CoroutineRunner"/> 上对应的 <c>Scene</c> 入口行为一致，
        /// 区别只在于协程归属的是本入口绑定的场景，而不是当前活动场景。
        /// </para>
        /// <para>
        /// 所有方法的失败方式与对应的 <c>Scene</c> 入口相同：参数非法或不在播放模式时抛出异常，
        /// 正在退出播放模式时返回 <see langword="null"/>。
        /// 绑定的场景无效或尚未加载时，抛出 <see cref="InvalidOperationException"/>。
        /// </para>
        /// </summary>
        public readonly struct Scope
        {
            private readonly Scene scene;

            internal Scope(Scene scene)
            {
                this.scene = scene;
            }

            /// <summary>
            /// 本入口绑定的场景。
            /// </summary>
            public Scene TargetScene
            {
                get { return scene; }
            }

            /// <summary>
            /// 在绑定的场景中启动协程。场景卸载时，协程会随场景宿主一同终止。
            /// </summary>
            /// <param name="routine">需要执行的协程。</param>
            /// <returns>已启动的场景协程，可传给 <see cref="Stop"/> 主动停止。退出播放模式的过程中不会启动协程，此时返回 <see langword="null"/>（参见 <see cref="IsQuitting"/>）。</returns>
            /// <exception cref="ArgumentNullException"><paramref name="routine"/> 为 null。</exception>
            /// <exception cref="InvalidOperationException">当前不在播放模式，或绑定的场景无效、尚未加载。</exception>
            public Coroutine Run(IEnumerator routine)
            {
                return RunInScene(scene, routine);
            }

            /// <summary>
            /// 延迟指定时间后调用方法。
            /// </summary>
            /// <param name="seconds">延迟秒数，必须大于或等于 0。</param>
            /// <param name="action">延迟结束后调用的方法。</param>
            /// <param name="ignoreTimeScale">是否忽略 <see cref="Time.timeScale"/>。</param>
            /// <returns>已启动的场景协程，可传给 <see cref="Stop"/> 取消调用。退出播放模式的过程中不会启动协程，此时返回 <see langword="null"/>（参见 <see cref="IsQuitting"/>）。</returns>
            /// <exception cref="ArgumentNullException"><paramref name="action"/> 为 null。</exception>
            /// <exception cref="ArgumentOutOfRangeException"><paramref name="seconds"/> 为负数、NaN 或无穷大。</exception>
            /// <exception cref="InvalidOperationException">当前不在播放模式，或绑定的场景无效、尚未加载。</exception>
            public Coroutine Delay(float seconds, Action action, bool ignoreTimeScale = false)
            {
                ValidateDuration(seconds, nameof(seconds));
                ValidateAction(action);
                return RunInScene(scene, DelayRoutine(seconds, action, ignoreTimeScale));
            }

            /// <summary>
            /// 在下一帧调用方法。
            /// </summary>
            /// <param name="action">下一帧调用的方法。</param>
            /// <returns>已启动的场景协程，可传给 <see cref="Stop"/> 取消调用。退出播放模式的过程中不会启动协程，此时返回 <see langword="null"/>（参见 <see cref="IsQuitting"/>）。</returns>
            /// <exception cref="ArgumentNullException"><paramref name="action"/> 为 null。</exception>
            /// <exception cref="InvalidOperationException">当前不在播放模式，或绑定的场景无效、尚未加载。</exception>
            public Coroutine NextFrame(Action action)
            {
                ValidateAction(action);
                return RunInScene(scene, NextFrameRoutine(action));
            }

            /// <summary>
            /// 在本帧结束时调用方法。
            /// </summary>
            /// <param name="action">帧末调用的方法。</param>
            /// <returns>已启动的场景协程，可传给 <see cref="Stop"/> 取消调用。退出播放模式的过程中不会启动协程，此时返回 <see langword="null"/>（参见 <see cref="IsQuitting"/>）。</returns>
            /// <exception cref="ArgumentNullException"><paramref name="action"/> 为 null。</exception>
            /// <exception cref="InvalidOperationException">当前不在播放模式，或绑定的场景无效、尚未加载。</exception>
            public Coroutine EndOfFrame(Action action)
            {
                ValidateAction(action);
                return RunInScene(scene, EndOfFrameRoutine(action));
            }

            /// <summary>
            /// 在下一次固定更新时调用方法。
            /// </summary>
            /// <param name="action">固定更新时调用的方法。</param>
            /// <returns>已启动的场景协程，可传给 <see cref="Stop"/> 取消调用。退出播放模式的过程中不会启动协程，此时返回 <see langword="null"/>（参见 <see cref="IsQuitting"/>）。</returns>
            /// <exception cref="ArgumentNullException"><paramref name="action"/> 为 null。</exception>
            /// <exception cref="InvalidOperationException">当前不在播放模式，或绑定的场景无效、尚未加载。</exception>
            public Coroutine NextFixedUpdate(Action action)
            {
                ValidateAction(action);
                return RunInScene(scene, NextFixedUpdateRoutine(action));
            }

            /// <summary>
            /// 等待条件成立后调用方法。
            /// </summary>
            /// <param name="predicate">每帧检查的条件。</param>
            /// <param name="action">条件成立后调用的方法。</param>
            /// <returns>已启动的场景协程，可传给 <see cref="Stop"/> 取消调用。退出播放模式的过程中不会启动协程，此时返回 <see langword="null"/>（参见 <see cref="IsQuitting"/>）。</returns>
            /// <exception cref="ArgumentNullException"><paramref name="predicate"/> 或 <paramref name="action"/> 为 null。</exception>
            /// <exception cref="InvalidOperationException">当前不在播放模式，或绑定的场景无效、尚未加载。</exception>
            public Coroutine WaitUntil(Func<bool> predicate, Action action)
            {
                ValidatePredicate(predicate);
                ValidateAction(action);
                return RunInScene(scene, WaitUntilRoutine(predicate, action));
            }

            /// <summary>
            /// 每帧调用一次监听方法，直到其返回 false。
            /// </summary>
            /// <param name="listener">需要持续调用的监听方法；返回 true 继续监听，返回 false 结束监听。</param>
            /// <returns>已启动的场景协程，可传给 <see cref="Stop"/> 主动停止监听。退出播放模式的过程中不会启动协程，此时返回 <see langword="null"/>（参见 <see cref="IsQuitting"/>）。</returns>
            /// <exception cref="ArgumentNullException"><paramref name="listener"/> 为 null。</exception>
            /// <exception cref="InvalidOperationException">当前不在播放模式，或绑定的场景无效、尚未加载。</exception>
            public Coroutine Listener(Func<bool> listener)
            {
                ValidateListener(listener);
                return RunInScene(scene, ListenerRoutine(listener));
            }

            /// <summary>
            /// 按指定间隔重复调用方法。
            /// </summary>
            /// <param name="interval">调用间隔秒数，必须大于或等于 0。</param>
            /// <param name="action">每次调用的方法。</param>
            /// <param name="repeatCount">重复次数；-1 表示持续调用，0 表示不调用。</param>
            /// <param name="invokeImmediately">是否在启动时立即调用一次。</param>
            /// <param name="ignoreTimeScale">是否忽略 <see cref="Time.timeScale"/>。</param>
            /// <returns>已启动的场景协程，可传给 <see cref="Stop"/> 停止重复调用。退出播放模式的过程中不会启动协程，此时返回 <see langword="null"/>（参见 <see cref="IsQuitting"/>）。</returns>
            /// <exception cref="ArgumentNullException"><paramref name="action"/> 为 null。</exception>
            /// <exception cref="ArgumentOutOfRangeException"><paramref name="interval"/> 为负数、NaN 或无穷大，或 <paramref name="repeatCount"/> 小于 -1。</exception>
            /// <exception cref="InvalidOperationException">当前不在播放模式，或绑定的场景无效、尚未加载。</exception>
            public Coroutine Repeat(
                float interval,
                Action action,
                int repeatCount = -1,
                bool invokeImmediately = false,
                bool ignoreTimeScale = false)
            {
                ValidateDuration(interval, nameof(interval));
                ValidateRepeatCount(repeatCount);
                ValidateAction(action);
                return RunInScene(scene, RepeatRoutine(interval, action, repeatCount, invokeImmediately, ignoreTimeScale));
            }

            /// <summary>
            /// 读取字节数据。
            /// </summary>
            /// <param name="urlOrPath">URL、StreamingAssets 地址或本地文件路径。</param>
            /// <param name="onSuccess">读取成功后的回调。</param>
            /// <param name="onError">读取失败后的回调；为 null 时通过 <see cref="Debug.LogError(object)"/> 输出。</param>
            /// <returns>已启动的场景协程，可传给 <see cref="Stop"/> 中断读取。退出播放模式的过程中不会启动协程，此时返回 <see langword="null"/>（参见 <see cref="IsQuitting"/>）。</returns>
            /// <exception cref="ArgumentNullException"><paramref name="onSuccess"/> 为 null。</exception>
            /// <exception cref="ArgumentException"><paramref name="urlOrPath"/> 为空或只包含空白字符。</exception>
            /// <exception cref="InvalidOperationException">当前不在播放模式，或绑定的场景无效、尚未加载。</exception>
            public Coroutine LoadBytes(string urlOrPath, Action<byte[]> onSuccess, Action<string> onError = null)
            {
                ValidateSuccessCallback(onSuccess);
                return RunInScene(scene, LoadBytesRoutine(NormalizeRequestUrl(urlOrPath), onSuccess, onError));
            }

            /// <summary>
            /// 读取文本数据。
            /// </summary>
            /// <param name="urlOrPath">URL、StreamingAssets 地址或本地文件路径。</param>
            /// <param name="onSuccess">读取成功后的回调。</param>
            /// <param name="onError">读取失败后的回调；为 null 时通过 <see cref="Debug.LogError(object)"/> 输出。</param>
            /// <returns>已启动的场景协程，可传给 <see cref="Stop"/> 中断读取。退出播放模式的过程中不会启动协程，此时返回 <see langword="null"/>（参见 <see cref="IsQuitting"/>）。</returns>
            /// <exception cref="ArgumentNullException"><paramref name="onSuccess"/> 为 null。</exception>
            /// <exception cref="ArgumentException"><paramref name="urlOrPath"/> 为空或只包含空白字符。</exception>
            /// <exception cref="InvalidOperationException">当前不在播放模式，或绑定的场景无效、尚未加载。</exception>
            public Coroutine LoadText(string urlOrPath, Action<string> onSuccess, Action<string> onError = null)
            {
                ValidateSuccessCallback(onSuccess);
                return RunInScene(scene, LoadTextRoutine(NormalizeRequestUrl(urlOrPath), onSuccess, onError));
            }
        }

        private sealed class CoroutineRegistration
        {
            public Coroutine Coroutine;
            public bool Completed;
        }
    }

    internal enum CoroutineRunnerHostLifecycle
    {
        Scene,
        Persistent
    }

    [DisallowMultipleComponent]
    internal sealed class CoroutineRunnerHost : MonoBehaviour
    {
        [Tooltip("协程宿主的生命周期类型，由 CoroutineRunner 自动配置：Scene 随所属场景销毁，Persistent 跨场景保留。")]
        [SerializeField] private CoroutineRunnerHostLifecycle lifecycle;

        internal CoroutineRunnerHostLifecycle Lifecycle
        {
            get { return lifecycle; }
        }

        internal void Configure(CoroutineRunnerHostLifecycle lifecycle)
        {
            this.lifecycle = lifecycle;
        }

        private void OnApplicationQuit()
        {
            CoroutineRunner.NotifyApplicationQuitting();
        }

        private void OnDestroy()
        {
            CoroutineRunner.NotifyHostDestroyed(this);
        }
    }
}
