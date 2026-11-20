using System;
using System.Collections;
using NUnit.Framework;
using UnityEngine;

namespace Yvsim.CoroutineRunner.Tests
{
    /// <summary>
    /// 参数校验与播放模式守卫的测试。
    /// <para>
    /// 这些校验全部发生在协程启动之前，因此不依赖播放模式，可以作为 EditMode 测试运行。
    /// 测试本身也用于固定这个顺序：参数错误必须先于 <c>EnsurePlaying</c> 抛出。
    /// </para>
    /// </summary>
    public sealed class CoroutineRunnerValidationTests
    {
        private static IEnumerator EmptyRoutine()
        {
            yield break;
        }

        [Test]
        public void RunScene_ThrowsInvalidOperationException_WhenNotPlaying()
        {
            Assert.Throws<InvalidOperationException>(() => CoroutineRunner.RunScene(EmptyRoutine()));
        }

        [Test]
        public void RunPersistent_ThrowsInvalidOperationException_WhenNotPlaying()
        {
            Assert.Throws<InvalidOperationException>(() => CoroutineRunner.RunPersistent(EmptyRoutine()));
        }

        [Test]
        public void RunScene_ThrowsArgumentNullException_BeforeCheckingPlayMode()
        {
            Assert.Throws<ArgumentNullException>(() => CoroutineRunner.RunScene(null));
        }

        [Test]
        public void DelayScene_ThrowsArgumentNullException_OnNullAction()
        {
            Assert.Throws<ArgumentNullException>(() => CoroutineRunner.DelayScene(1f, null));
        }

        [Test]
        public void WaitUntilScene_ThrowsArgumentNullException_OnNullPredicate()
        {
            Assert.Throws<ArgumentNullException>(() => CoroutineRunner.WaitUntilScene(null, () => { }));
        }

        [Test]
        public void ListenerScene_ThrowsArgumentNullException_OnNullListener()
        {
            Assert.Throws<ArgumentNullException>(() => CoroutineRunner.ListenerScene(null));
        }

        [TestCase(-1f)]
        [TestCase(float.NaN)]
        [TestCase(float.PositiveInfinity)]
        public void DelayScene_ThrowsArgumentOutOfRangeException_OnInvalidSeconds(float seconds)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => CoroutineRunner.DelayScene(seconds, () => { }));
        }

        [Test]
        public void RepeatScene_ThrowsArgumentOutOfRangeException_OnRepeatCountBelowMinusOne()
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => CoroutineRunner.RepeatScene(1f, () => { }, repeatCount: -2));
        }

        [TestCase("")]
        [TestCase("   ")]
        [TestCase(null)]
        public void LoadTextScene_ThrowsArgumentException_OnEmptyPath(string urlOrPath)
        {
            Assert.Throws<ArgumentException>(() => CoroutineRunner.LoadTextScene(urlOrPath, text => { }));
        }

        [Test]
        public void LoadBytesScene_ThrowsArgumentNullException_OnNullSuccessCallback()
        {
            Assert.Throws<ArgumentNullException>(
                () => CoroutineRunner.LoadBytesScene("http://example.com", null));
        }

        [Test]
        public void On_ThrowsArgumentNullException_OnNullOwner()
        {
            Assert.Throws<ArgumentNullException>(() => CoroutineRunner.On((Component)null));
            Assert.Throws<ArgumentNullException>(() => CoroutineRunner.On((GameObject)null));
        }

        [Test]
        public void On_ThrowsArgumentNullException_OnDestroyedOwner()
        {
            GameObject owner = new GameObject("DestroyedOwner");
            UnityEngine.Object.DestroyImmediate(owner);

            Assert.Throws<ArgumentNullException>(() => CoroutineRunner.On(owner));
        }

        [Test]
        public void IsQuitting_IsFalse_OutsideOfShutdown()
        {
            Assert.IsFalse(CoroutineRunner.IsQuitting);
        }

        [Test]
        public void Stop_DoesNothing_OnNullCoroutine()
        {
            Assert.DoesNotThrow(() => CoroutineRunner.Stop(null));
        }
    }
}
