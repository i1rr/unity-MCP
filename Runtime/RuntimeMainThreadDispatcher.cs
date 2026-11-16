using System;
using I1rr.UnityMcp.Shared;
using UnityEngine;
using UnityEngine.Scripting;

namespace I1rr.UnityMcp.Runtime
{
    /// <summary>
    /// MonoBehaviour wrapper around <see cref="MainThreadDispatcher"/> providing the Update drain loop.
    /// Singleton with DontDestroyOnLoad.
    /// </summary>
    [Preserve]
    public sealed class RuntimeMainThreadDispatcher : MonoBehaviour
    {
        private static RuntimeMainThreadDispatcher _instance;
        private static readonly MainThreadDispatcherImpl _dispatcher = new MainThreadDispatcherImpl();
        private static readonly FrameTimeRingBuffer _frameTimeBuffer = new FrameTimeRingBuffer();

        [SerializeField] private int _maxRequestsPerFrame = 3;

        private Action<McpRequest> _handler;

        public static RuntimeMainThreadDispatcher Instance => _instance;

        /// <summary>
        /// The underlying dispatcher used to enqueue requests from background threads.
        /// </summary>
        public static MainThreadDispatcherImpl Dispatcher => _dispatcher;

        /// <summary>
        /// Persistent ring buffer recording per-frame timing data.
        /// </summary>
        public static FrameTimeRingBuffer FrameTimeBuffer => _frameTimeBuffer;

        /// <summary>
        /// Maximum number of requests to process per frame.
        /// </summary>
        public int MaxRequestsPerFrame
        {
            get => _maxRequestsPerFrame;
            set => _maxRequestsPerFrame = Mathf.Max(1, value);
        }

        /// <summary>
        /// Sets the handler invoked for each dequeued request on the main thread.
        /// </summary>
        public void SetHandler(Action<McpRequest> handler)
        {
            _handler = handler;
        }

        [Preserve]
        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }
            _instance = this;
            DontDestroyOnLoad(gameObject);
            if (Application.isEditor) Sessions.SessionManager.Initialize();
        }

        [Preserve]
        private void Update()
        {
            _frameTimeBuffer.Record(Time.unscaledDeltaTime, Time.frameCount, Time.realtimeSinceStartup);
            if (Application.isEditor) Sessions.SessionManager.Instance?.Update();

            if (_handler == null) return;
            _dispatcher.DrainPublic(_maxRequestsPerFrame, _handler);
        }

        private void OnDestroy()
        {
            if (_instance == this)
            {
                if (Application.isEditor) Sessions.SessionManager.Shutdown();
                _instance = null;
            }
        }

        /// <summary>
        /// Concrete implementation exposing the protected Drain method.
        /// </summary>
        [Preserve]
        public sealed class MainThreadDispatcherImpl : MainThreadDispatcher
        {
            public int DrainPublic(int max, Action<McpRequest> handler)
            {
                return Drain(max, handler);
            }
        }
    }
}
