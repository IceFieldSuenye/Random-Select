using System;
using System.Collections.Generic;

namespace RandomDrawer.Messaging
{
    /// <summary>
    /// 轻量全局消息总线。
    /// · 类型即"频道"，订阅/发布通过泛型 T 区分
    /// · 订阅者需自行在 Unloaded / Closed 时反订阅（或使用 IDisposable）
    /// · 所有 Publish 都会捕获异常，避免一个订阅者崩溃影响其它订阅者
    /// </summary>
    public static class MessageBus
    {
        private static readonly Dictionary<Type, List<Delegate>> _handlers = new Dictionary<Type, List<Delegate>>();
        private static readonly object _lock = new object();

        public static void Subscribe<T>(Action<T> handler)
        {
            if (handler == null) return;
            lock (_lock)
            {
                if (!_handlers.TryGetValue(typeof(T), out var list))
                {
                    list = new List<Delegate>();
                    _handlers[typeof(T)] = list;
                }
                if (!list.Contains(handler)) list.Add(handler);
            }
        }

        public static void Unsubscribe<T>(Action<T> handler)
        {
            if (handler == null) return;
            lock (_lock)
            {
                if (_handlers.TryGetValue(typeof(T), out var list))
                {
                    list.Remove(handler);
                    if (list.Count == 0) _handlers.Remove(typeof(T));
                }
            }
        }

        public static void Publish<T>(T message)
        {
            Delegate[] snapshot;
            lock (_lock)
            {
                if (!_handlers.TryGetValue(typeof(T), out var list)) return;
                snapshot = list.ToArray();
            }

            foreach (var d in snapshot)
            {
                try { ((Action<T>)d)(message); }
                catch { /* 单个订阅者崩溃不影响其它订阅者 */ }
            }
        }

        public static void Clear()
        {
            lock (_lock) { _handlers.Clear(); }
        }
    }
}