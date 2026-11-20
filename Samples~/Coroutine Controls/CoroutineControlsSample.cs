using System.Collections.Generic;
using UnityEngine;
using Runner = Yvsim.CoroutineRunner.CoroutineRunner;

namespace Yvsim.CoroutineRunner.Samples
{
    /// <summary>通过独立协程句柄演示延迟、重复、条件等待及取消，不停止其他调用方的协程。</summary>
    public sealed class CoroutineControlsSample : MonoBehaviour
    {
        [Tooltip("延迟任务等待的真实时间，单位为秒；示例忽略 Time.timeScale。")]
        [SerializeField, Min(0.05f)] private float delaySeconds = 1f;
        [Tooltip("重复任务的真实时间间隔，单位为秒；示例重复执行五次。")]
        [SerializeField, Min(0.05f)] private float repeatInterval = 0.5f;

        private readonly List<Coroutine> handles = new List<Coroutine>();
        private readonly Queue<string> messages = new Queue<string>();
        private bool gateOpen;
        private int generation;

        public int RepeatCount { get; private set; }
        public bool DelayCompleted { get; private set; }
        public bool GateCompleted { get; private set; }

        private void Start() { BeginDemo(); }

        /// <summary>将任务归属到示例对象所在场景，避免 additive 场景下误用活动场景。</summary>
        public void BeginDemo()
        {
            CancelTasks();
            messages.Clear();
            RepeatCount = 0;
            DelayCompleted = false;
            GateCompleted = false;
            gateOpen = false;
            int run = generation;
            var scope = Runner.On(this);
            AddMessage("Started: delay, five repeats, and a gated task.");
            handles.Add(scope.Delay(Mathf.Max(0.05f, delaySeconds), () =>
            {
                if (run != generation) return;
                DelayCompleted = true;
                AddMessage("Delayed task completed.");
            }, ignoreTimeScale: true));
            handles.Add(scope.Repeat(Mathf.Max(0.05f, repeatInterval), () =>
            {
                if (run != generation) return;
                RepeatCount++;
                AddMessage("Repeat " + RepeatCount + "/5");
            }, repeatCount: 5, invokeImmediately: false, ignoreTimeScale: true));
            handles.Add(scope.WaitUntil(() => gateOpen, () =>
            {
                if (run != generation) return;
                GateCompleted = true;
                AddMessage("Gate opened: waiting task completed.");
            }));
        }

        public void OpenGate() { gateOpen = true; }

        public void CancelTasks()
        {
            generation++;
            foreach (Coroutine handle in handles) if (handle != null) Runner.Stop(handle);
            handles.Clear();
            AddMessage("Stopped only this sample's task handles.");
        }

        private void AddMessage(string message)
        {
            if (messages.Count >= 10) messages.Dequeue();
            messages.Enqueue(message);
            Debug.Log("Coroutine Controls: " + message, this);
        }

        private void OnGUI()
        {
            GUILayout.BeginArea(new Rect(16, 16, Mathf.Min(580, Screen.width - 32), Screen.height - 32), GUI.skin.box);
            GUILayout.Label("Coroutine Runner - scoped tasks and cancellation");
            GUILayout.Label($"Delay: {DelayCompleted} | Repeats: {RepeatCount}/5 | Gate: {GateCompleted}");
            if (GUILayout.Button("Restart all sample tasks")) BeginDemo();
            if (GUILayout.Button("Open gate")) OpenGate();
            if (GUILayout.Button("Cancel sample tasks")) CancelTasks();
            foreach (string message in messages) GUILayout.Label(message);
            GUILayout.EndArea();
        }

        private void OnDisable() { CancelTasks(); }
    }
}
