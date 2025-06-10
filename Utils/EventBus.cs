using System;
using System.Collections.Generic;

namespace AreWeThereYet.Utils
{
    public class EventBus
    {
        private static EventBus _instance;
        public static EventBus Instance => _instance ??= new EventBus();
        
        private readonly Dictionary<Type, List<Delegate>> _subscribers = new();

        public void Subscribe<T>(Action<T> handler)
        {
            var eventType = typeof(T);
            if (!_subscribers.ContainsKey(eventType))
                _subscribers[eventType] = new List<Delegate>();
            _subscribers[eventType].Add(handler);
        }

        public void Publish<T>(T eventData)
        {
            if (_subscribers.TryGetValue(typeof(T), out var handlers))
            {
                foreach (var handler in handlers)
                {
                    try { ((Action<T>)handler)(eventData); }
                    catch { /* Ignore errors */ }
                }
            }
        }
    }

    public class AreaChangeEvent { }
    
    public class RenderEvent
    {
        public ExileCore.Graphics Graphics { get; set; }
        public RenderEvent(ExileCore.Graphics graphics) => Graphics = graphics;
    }
}
