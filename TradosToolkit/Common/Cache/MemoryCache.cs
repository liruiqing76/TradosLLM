using System;
using System.Collections.Generic;
using System.Threading;

namespace TradosToolkit.Common
{
    /// <summary>
    /// 进程内线程安全缓存：可选过期时间与容量上限，超限按最近最少使用（LRU）淘汰。
    /// 用于缓存"算一次很贵、短时间内不变"的东西（语种清单、领域树、目录扫描结果等）。
    /// <para>
    /// 约定：<see cref="GetOrAdd(TKey, Func{TKey, TValue})"/> 的工厂回调在同一键上只会真正执行一次
    /// （并发请求会等待首个结果，靠 <see cref="Lazy{T}"/> 的 ExecutionAndPublication 保证）；
    /// 工厂抛异常时不写入缓存，下次访问会重新计算。
    /// </para>
    /// </summary>
    public class MemoryCache<TKey, TValue>
    {
        private sealed class Entry
        {
            public Lazy<TValue> Lazy;
            public DateTime ExpiresUtc; // DateTime.MaxValue = 永不过期
            public long Stamp;          // 最近访问序号，用于 LRU 淘汰
        }

        private readonly object _gate = new object();
        private readonly Dictionary<TKey, Entry> _map;
        private readonly int _capacity;
        private readonly TimeSpan _defaultTtl;
        private long _clock;
        private long _hits;
        private long _misses;

        /// <param name="capacity">容量上限，超出按 LRU 淘汰（至少 1）。</param>
        /// <param name="defaultTtl">默认存活时长；null 或 &lt;= 0 表示永不过期。</param>
        /// <param name="comparer">键比较器，默认 <see cref="EqualityComparer{T}.Default"/>。</param>
        public MemoryCache(int capacity = 512, TimeSpan? defaultTtl = null, IEqualityComparer<TKey> comparer = null)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException("capacity", "容量必须大于 0");
            _capacity = capacity;
            _defaultTtl = defaultTtl ?? TimeSpan.Zero;
            _map = new Dictionary<TKey, Entry>(comparer ?? EqualityComparer<TKey>.Default);
        }

        public int Count { get { lock (_gate) { return _map.Count; } } }
        public long Hits { get { lock (_gate) { return _hits; } } }
        public long Misses { get { lock (_gate) { return _misses; } } }

        /// <summary>命中则返回缓存值；未命中用工厂计算并写入（按默认 TTL）。</summary>
        public TValue GetOrAdd(TKey key, Func<TKey, TValue> factory)
        {
            return GetOrAdd(key, factory, null);
        }

        /// <summary>命中则返回缓存值；未命中用工厂计算并写入（ttl 覆盖默认值）。</summary>
        public TValue GetOrAdd(TKey key, Func<TKey, TValue> factory, TimeSpan? ttl)
        {
            if (factory == null) throw new ArgumentNullException("factory");

            Lazy<TValue> lazy;
            lock (_gate)
            {
                Entry e;
                if (_map.TryGetValue(key, out e) && e.ExpiresUtc > DateTime.UtcNow)
                {
                    e.Stamp = ++_clock;
                    lazy = e.Lazy;
                    _hits++;
                }
                else
                {
                    // 过期或缺失：换新的 Lazy（旧项被覆盖，等价于失效）
                    lazy = new Lazy<TValue>(() => factory(key), LazyThreadSafetyMode.ExecutionAndPublication);
                    _map[key] = new Entry { Lazy = lazy, ExpiresUtc = ExpiresAt(ttl), Stamp = ++_clock };
                    Trim();
                    _misses++;
                }
            }

            try
            {
                return lazy.Value;
            }
            catch
            {
                // 工厂失败：把这条脏数据摘掉，避免下次仍拿到缓存的异常
                lock (_gate)
                {
                    Entry e;
                    if (_map.TryGetValue(key, out e) && ReferenceEquals(e.Lazy, lazy))
                        _map.Remove(key);
                }
                throw;
            }
        }

        /// <summary>直接写入/覆盖一条缓存。</summary>
        public void Set(TKey key, TValue value, TimeSpan? ttl = null)
        {
            lock (_gate)
            {
                _map[key] = new Entry
                {
                    Lazy = new Lazy<TValue>(() => value),
                    ExpiresUtc = ExpiresAt(ttl),
                    Stamp = ++_clock,
                };
                Trim();
            }
        }

        /// <summary>尝试取值；过期视为未命中。</summary>
        public bool TryGet(TKey key, out TValue value)
        {
            lock (_gate)
            {
                Entry e;
                if (_map.TryGetValue(key, out e) && e.ExpiresUtc > DateTime.UtcNow)
                {
                    e.Stamp = ++_clock;
                    value = e.Lazy.Value;
                    return true;
                }
                value = default(TValue);
                return false;
            }
        }

        public bool Contains(TKey key)
        {
            lock (_gate)
            {
                Entry e;
                return _map.TryGetValue(key, out e) && e.ExpiresUtc > DateTime.UtcNow;
            }
        }

        public bool Remove(TKey key)
        {
            lock (_gate)
            {
                return _map.Remove(key);
            }
        }

        public void Clear()
        {
            lock (_gate)
            {
                _map.Clear();
            }
        }

        private DateTime ExpiresAt(TimeSpan? ttl)
        {
            var span = ttl ?? _defaultTtl;
            return span <= TimeSpan.Zero ? DateTime.MaxValue : DateTime.UtcNow.Add(span);
        }

        // 调用方已持锁：先清过期，再按最旧访问序号淘汰到容量内
        private void Trim()
        {
            if (_map.Count <= _capacity) return;

            var now = DateTime.UtcNow;
            var expired = new List<TKey>();
            foreach (var kv in _map)
                if (kv.Value.ExpiresUtc <= now) expired.Add(kv.Key);
            foreach (var k in expired) _map.Remove(k);

            while (_map.Count > _capacity)
            {
                var oldest = default(TKey);
                var min = long.MaxValue;
                var found = false;
                foreach (var kv in _map)
                {
                    if (kv.Value.Stamp < min) { min = kv.Value.Stamp; oldest = kv.Key; found = true; }
                }
                if (!found) break;
                _map.Remove(oldest);
            }
        }
    }

    /// <summary>
    /// 单值惰性缓存：整进程只计算一次，之后直接返回；线程安全。
    /// 适合"全进程就一份、初始化昂贵"的数据（如语种清单）。需要重算时调用 <see cref="Reset"/>。
    /// </summary>
    public sealed class LazyValue<T>
    {
        private readonly Func<T> _factory;
        private readonly object _gate = new object();
        private T _value;
        private bool _created;

        public LazyValue(Func<T> factory)
        {
            if (factory == null) throw new ArgumentNullException("factory");
            _factory = factory;
        }

        /// <summary>是否已计算过。</summary>
        public bool IsCreated { get { lock (_gate) { return _created; } } }

        /// <summary>取值；首次访问触发工厂计算，之后返回缓存结果。</summary>
        public T Value
        {
            get
            {
                lock (_gate)
                {
                    if (!_created)
                    {
                        _value = _factory();
                        _created = true;
                    }
                    return _value;
                }
            }
        }

        /// <summary>丢弃已缓存的值，下次访问重新计算。</summary>
        public void Reset()
        {
            lock (_gate)
            {
                _value = default(T);
                _created = false;
            }
        }
    }
}
