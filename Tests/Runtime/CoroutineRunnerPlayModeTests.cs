using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Yvsim.CoroutineRunner.Tests
{
    /// <summary>
    /// 协程行为的 PlayMode 测试。协程只能在播放模式中启动，因此这些用例无法作为 EditMode 测试运行。
    /// </summary>
    public sealed class CoroutineRunnerPlayModeTests
    {
        private const int FrameBudget = 600;

        private string temporaryDirectory;

        [SetUp]
        public void SetUp()
        {
            CoroutineRunner.StopAllScene();
            CoroutineRunner.StopAllPersistent();

            temporaryDirectory = Path.Combine(
                Application.temporaryCachePath,
                "YvsimCoroutineRunnerTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporaryDirectory);
        }

        [TearDown]
        public void TearDown()
        {
            CoroutineRunner.StopAllScene();
            CoroutineRunner.StopAllPersistent();
            Time.timeScale = 1f;

            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, true);
            }
        }

        // ---------- 基本生命周期 ----------

        [UnityTest]
        public IEnumerator RunScene_RunsRoutineToCompletion()
        {
            bool finished = false;

            Coroutine coroutine = CoroutineRunner.RunScene(CountingRoutine(3, () => finished = true));

            Assert.IsNotNull(coroutine);
            yield return WaitUntil(() => finished, "协程应当执行完成");
        }

        [UnityTest]
        public IEnumerator Stop_PreventsRemainingInvocations()
        {
            int count = 0;
            Coroutine coroutine = CoroutineRunner.RepeatScene(0f, () => count++);

            yield return null;
            yield return null;

            CoroutineRunner.Stop(coroutine);
            int countAtStop = count;

            yield return null;
            yield return null;
            yield return null;

            Assert.AreEqual(countAtStop, count, "停止后不应再有调用");
        }

        [UnityTest]
        public IEnumerator StopAllScene_DoesNotAffectPersistentCoroutines()
        {
            int sceneCount = 0;
            int persistentCount = 0;

            CoroutineRunner.ListenerScene(() => { sceneCount++; return true; });
            CoroutineRunner.ListenerPersistent(() => { persistentCount++; return true; });

            yield return null;
            yield return null;

            CoroutineRunner.StopAllScene();
            int sceneAtStop = sceneCount;
            int persistentAtStop = persistentCount;

            yield return null;
            yield return null;

            Assert.AreEqual(sceneAtStop, sceneCount, "场景协程应已全部停止");
            Assert.Greater(persistentCount, persistentAtStop, "持久协程不应受影响");
        }

        [UnityTest]
        public IEnumerator StopAllPersistent_DoesNotAffectSceneCoroutines()
        {
            int sceneCount = 0;
            int persistentCount = 0;

            CoroutineRunner.ListenerScene(() => { sceneCount++; return true; });
            CoroutineRunner.ListenerPersistent(() => { persistentCount++; return true; });

            yield return null;
            yield return null;

            CoroutineRunner.StopAllPersistent();
            int sceneAtStop = sceneCount;
            int persistentAtStop = persistentCount;

            yield return null;
            yield return null;

            Assert.AreEqual(persistentAtStop, persistentCount, "持久协程应已全部停止");
            Assert.Greater(sceneCount, sceneAtStop, "场景协程不应受影响");
        }

        // ---------- 重复调用 ----------

        [UnityTest]
        public IEnumerator RepeatScene_InvokesExactlyRepeatCountTimes()
        {
            int count = 0;
            CoroutineRunner.RepeatScene(0f, () => count++, repeatCount: 3);

            yield return WaitUntil(() => count >= 3, "重复调用应达到指定次数");

            yield return null;
            yield return null;
            yield return null;

            Assert.AreEqual(3, count, "重复调用不应超过指定次数");
        }

        [UnityTest]
        public IEnumerator RepeatScene_WithInvokeImmediately_CountsTheImmediateCall()
        {
            int count = 0;
            CoroutineRunner.RepeatScene(0f, () => count++, repeatCount: 2, invokeImmediately: true);

            Assert.AreEqual(1, count, "invokeImmediately 应在启动时同步调用一次");

            yield return WaitUntil(() => count >= 2, "剩余的重复调用应继续执行");

            yield return null;
            yield return null;

            Assert.AreEqual(2, count, "立即调用应计入 repeatCount");
        }

        [UnityTest]
        public IEnumerator RepeatScene_WithZeroRepeatCount_NeverInvokes()
        {
            int count = 0;
            CoroutineRunner.RepeatScene(0f, () => count++, repeatCount: 0, invokeImmediately: true);

            yield return null;
            yield return null;
            yield return null;

            Assert.AreEqual(0, count, "repeatCount 为 0 时不应有任何调用");
        }

        /// <summary>
        /// 回归测试：忽略时间缩放的分支改为手动计时后，interval 为 0 时必须仍然逐帧让出，
        /// 否则会在同一帧内空转并卡死编辑器。
        /// </summary>
        [UnityTest]
        public IEnumerator RepeatScene_WithZeroIntervalIgnoringTimeScale_YieldsEachIteration()
        {
            int count = 0;
            int frames = 0;

            CoroutineRunner.RepeatScene(0f, () => count++, repeatCount: 3, ignoreTimeScale: true);

            while (count < 3 && frames < FrameBudget)
            {
                frames++;
                yield return null;
            }

            Assert.AreEqual(3, count, "重复调用应正常完成而不是卡在同一帧内");
            Assert.GreaterOrEqual(frames, 3, "每次重复至少应让出一帧");
        }

        [UnityTest]
        public IEnumerator RepeatScene_IgnoringTimeScale_StillRunsWhileTimeScaleIsZero()
        {
            Time.timeScale = 0f;

            int ignored = 0;
            int scaled = 0;

            CoroutineRunner.RepeatScene(0.02f, () => ignored++, repeatCount: 2, ignoreTimeScale: true);
            CoroutineRunner.RepeatScene(0.02f, () => scaled++, repeatCount: 2, ignoreTimeScale: false);

            yield return WaitUntil(() => ignored >= 2, "忽略时间缩放的重复调用应继续执行");

            Assert.AreEqual(0, scaled, "受时间缩放影响的重复调用在 timeScale 为 0 时不应执行");
        }

        // ---------- 延迟、等待、监听 ----------

        [UnityTest]
        public IEnumerator DelayScene_InvokesAfterTheGivenDelay()
        {
            bool invoked = false;
            float startTime = Time.time;

            CoroutineRunner.DelayScene(0.1f, () => invoked = true);

            Assert.IsFalse(invoked, "延迟调用不应同步执行");

            yield return WaitUntil(() => invoked, "延迟结束后应当调用");

            Assert.GreaterOrEqual(Time.time - startTime, 0.09f, "调用不应早于指定延迟");
        }

        [UnityTest]
        public IEnumerator WaitUntilScene_InvokesOnlyAfterConditionBecomesTrue()
        {
            bool ready = false;
            bool invoked = false;

            CoroutineRunner.WaitUntilScene(() => ready, () => invoked = true);

            yield return null;
            yield return null;
            Assert.IsFalse(invoked, "条件未成立时不应调用");

            ready = true;
            yield return WaitUntil(() => invoked, "条件成立后应当调用");
        }

        [UnityTest]
        public IEnumerator ListenerScene_StopsWhenListenerReturnsFalse()
        {
            int count = 0;
            CoroutineRunner.ListenerScene(() =>
            {
                count++;
                return count < 3;
            });

            yield return WaitUntil(() => count >= 3, "监听应逐帧执行");

            yield return null;
            yield return null;

            Assert.AreEqual(3, count, "监听方法返回 false 后应停止");
        }

        // ---------- 场景归属 ----------

        [UnityTest]
        public IEnumerator On_BindsCoroutineToOwnerScene_AndStopsWhenThatSceneUnloads()
        {
            Scene extraScene = SceneManager.CreateScene("CoroutineRunnerTests_Extra");
            GameObject owner = new GameObject("Owner");
            SceneManager.MoveGameObjectToScene(owner, extraScene);

            int ownerTicks = 0;
            int activeTicks = 0;

            CoroutineRunner.On(owner).Listener(() => { ownerTicks++; return true; });
            CoroutineRunner.ListenerScene(() => { activeTicks++; return true; });

            yield return null;
            yield return null;

            Assert.Greater(ownerTicks, 0, "绑定到归属场景的协程应当运行");

            yield return SceneManager.UnloadSceneAsync(extraScene);
            yield return null;

            int ownerAfterUnload = ownerTicks;
            int activeAfterUnload = activeTicks;

            yield return null;
            yield return null;
            yield return null;

            Assert.AreEqual(ownerAfterUnload, ownerTicks, "归属场景卸载后，协程应当终止");
            Assert.Greater(activeTicks, activeAfterUnload, "活动场景的协程不应受影响");
        }

        [UnityTest]
        public IEnumerator RunPersistent_SurvivesSceneUnload()
        {
            Scene extraScene = SceneManager.CreateScene("CoroutineRunnerTests_Persistent");

            int ticks = 0;
            CoroutineRunner.ListenerPersistent(() => { ticks++; return true; });

            yield return null;

            yield return SceneManager.UnloadSceneAsync(extraScene);
            yield return null;

            int afterUnload = ticks;

            yield return null;
            yield return null;

            Assert.Greater(ticks, afterUnload, "持久协程应当在场景卸载后继续运行");
        }

        [Test]
        public void In_ExposesTheGivenScene()
        {
            Scene active = SceneManager.GetActiveScene();

            CoroutineRunner.Scope scope = CoroutineRunner.In(active);

            Assert.AreEqual(active, scope.TargetScene);
        }

        // ---------- 异常与跟踪表 ----------

        /// <summary>
        /// 回归测试：协程自身抛出异常时，内部跟踪表不应遗留条目。
        /// </summary>
        [UnityTest]
        public IEnumerator ThrowingRoutine_DoesNotLeakTrackingEntry()
        {
            yield return null;
            int baseline = TrackedCoroutineCount();

            LogAssert.Expect(LogType.Exception, new Regex("CoroutineRunnerTestsExpectedFailure"));

            CoroutineRunner.RunPersistent(ThrowingRoutine());

            yield return WaitUntil(
                () => TrackedCoroutineCount() == baseline,
                "协程抛出异常后，跟踪表应恢复到原有条目数");
        }

        [UnityTest]
        public IEnumerator CompletedRoutine_DoesNotLeakTrackingEntry()
        {
            yield return null;
            int baseline = TrackedCoroutineCount();

            bool finished = false;
            CoroutineRunner.RunScene(CountingRoutine(2, () => finished = true));

            yield return WaitUntil(() => finished, "协程应当执行完成");
            yield return null;

            Assert.AreEqual(baseline, TrackedCoroutineCount(), "协程完成后不应遗留跟踪条目");
        }

        // ---------- 数据读取 ----------

        [UnityTest]
        public IEnumerator LoadTextScene_ReadsLocalFilePath()
        {
            string path = Path.Combine(temporaryDirectory, "data.json");
            string expected = "{\"value\":42}";
            File.WriteAllText(path, expected);

            string result = null;
            string error = null;

            CoroutineRunner.LoadTextScene(path, text => result = text, message => error = message);

            yield return WaitUntil(() => result != null || error != null, "读取应当返回结果");

            Assert.IsNull(error, "读取本地文件不应出错：" + error);
            Assert.AreEqual(expected, result);
        }

        [UnityTest]
        public IEnumerator LoadBytesScene_ReadsLocalFilePath()
        {
            string path = Path.Combine(temporaryDirectory, "data.bin");
            byte[] expected = new byte[] { 1, 2, 3, 4, 5 };
            File.WriteAllBytes(path, expected);

            byte[] result = null;
            string error = null;

            CoroutineRunner.LoadBytesScene(path, bytes => result = bytes, message => error = message);

            yield return WaitUntil(() => result != null || error != null, "读取应当返回结果");

            Assert.IsNull(error, "读取本地文件不应出错：" + error);
            CollectionAssert.AreEqual(expected, result);
        }

        [UnityTest]
        public IEnumerator LoadTextScene_ReportsErrorForMissingFile()
        {
            string path = Path.Combine(temporaryDirectory, "missing.json");

            string result = null;
            string error = null;

            CoroutineRunner.LoadTextScene(path, text => result = text, message => error = message);

            yield return WaitUntil(() => result != null || error != null, "读取应当返回结果");

            Assert.IsNull(result, "文件不存在时不应回调成功");
            Assert.IsNotNull(error, "文件不存在时应当回调错误");
        }

        // ---------- 辅助 ----------

        private static IEnumerator CountingRoutine(int frames, Action onFinished)
        {
            for (int i = 0; i < frames; i++)
            {
                yield return null;
            }

            onFinished();
        }

        private static IEnumerator ThrowingRoutine()
        {
            yield return null;
            throw new InvalidOperationException("CoroutineRunnerTestsExpectedFailure");
        }

        private static IEnumerator WaitUntil(Func<bool> condition, string message)
        {
            // Batch Mode 可能在不足 0.1 秒内跑完数百帧；异步完成超时必须按真实时间计算。
            const float timeoutSeconds = 5f;
            float deadline = Time.realtimeSinceStartup + timeoutSeconds;
            while (Time.realtimeSinceStartup < deadline)
            {
                if (condition())
                {
                    yield break;
                }

                yield return null;
            }

            Assert.That(condition(), Is.True, message + "（等待 " + timeoutSeconds + " 秒后仍未满足）");
        }

        private static int TrackedCoroutineCount()
        {
            FieldInfo field = typeof(CoroutineRunner).GetField(
                "RunningCoroutines",
                BindingFlags.NonPublic | BindingFlags.Static);

            Assert.IsNotNull(field, "未找到内部跟踪表 RunningCoroutines，测试需要同步更新");

            return ((ICollection)field.GetValue(null)).Count;
        }
    }
}
