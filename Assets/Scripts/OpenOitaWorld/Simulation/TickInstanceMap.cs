using System;
using System.Collections.Generic;
using OpenOita.Contracts;

namespace OpenOita.Simulation
{
    internal sealed class TickInstanceMap : ITickInstanceMap
    {
        private sealed class IdentityStorage
        {
            internal readonly Dictionary<CellInstanceHandle, CellKey> Keys;
            internal readonly Dictionary<CellKey, CellInstanceHandle> Instances;
            internal int References = 1;
            internal IdentityStorage() { Keys = new(); Instances = new(); }
            internal IdentityStorage(IdentityStorage source)
            { Keys = new(source.Keys); Instances = new(source.Instances); }
        }
        private sealed class WetStorage
        {
            internal readonly HashSet<CellInstanceHandle> Items;
            internal int References = 1;
            internal WetStorage() { Items = new(); }
            internal WetStorage(WetStorage source) { Items = new(source.Items); }
        }
        private readonly ulong _generation;
        private readonly ulong _tick;
        private IdentityStorage _identity;
        private WetStorage _wet;
        private ulong _sequence;
        private bool _closed;
        internal bool IsClosed => _closed;
        internal int IdentityCopies { get; private set; }

        internal TickInstanceMap(ulong generation, ulong tick) : this(generation, tick, new IdentityStorage(), new WetStorage()) { }
        private TickInstanceMap(ulong generation, ulong tick, IdentityStorage identity, WetStorage wet)
        { _generation = generation; _tick = tick; _identity = identity; _wet = wet; }

        public bool TryResolve(in CellInstanceHandle instance, out CellKey key)
        {
            key = default;
            return !_closed && instance.Generation == _generation && instance.WorkingTick == _tick &&
                _identity.Keys.TryGetValue(instance, out key);
        }
        public bool TryGetInstance(in CellKey key, out CellInstanceHandle instance)
        {
            instance = default;
            return !_closed && key.Generation == _generation && _identity.Instances.TryGetValue(key, out instance);
        }
        public bool IsWet(in CellInstanceHandle instance) => TryResolve(instance, out _) && _wet.Items.Contains(instance);
        public void MarkWet(in CellInstanceHandle instance)
        {
            Require(instance);
            if (_wet.Items.Contains(instance)) return;
            MakeWetWritable(); _wet.Items.Add(instance);
        }
        public void Move(in CellInstanceHandle instance, in CellKey target)
        {
            CellKey source = Require(instance); ValidateKey(target);
            if (_identity.Instances.TryGetValue(target, out CellInstanceHandle occupant) && !occupant.Equals(instance))
                throw new InvalidOperationException("目标已属于另一个材料实例。");
            if (source.Equals(target)) return;
            MakeIdentityWritable();
            _identity.Instances.Remove(source); _identity.Instances[target] = instance; _identity.Keys[instance] = target;
        }
        public void Invalidate(in CellInstanceHandle instance)
        {
            CellKey source = Require(instance);
            MakeIdentityWritable();
            if (_wet.Items.Contains(instance)) { MakeWetWritable(); _wet.Items.Remove(instance); }
            _identity.Keys.Remove(instance); _identity.Instances.Remove(source);
        }
        public CellInstanceHandle Create(in CellKey key)
        {
            ValidateKey(key);
            if (_identity.Instances.ContainsKey(key)) throw new InvalidOperationException("材料实例必须唯一。");
            if (_sequence == ulong.MaxValue) throw new OverflowException("Tick 实例序号已耗尽。");
            MakeIdentityWritable();
            var instance = new CellInstanceHandle(_generation, _tick, ++_sequence);
            _identity.Keys.Add(instance, key); _identity.Instances.Add(key, instance);
            return instance;
        }

        // 纯状态/位姿候选只借用身份数据；移动、替换、删除时才分离身份表。
        internal TickInstanceMap Clone()
        {
            if (_closed) throw new InvalidOperationException("Tick 租约已失效。");
            if (_identity.References == int.MaxValue || _wet.References == int.MaxValue)
                throw new InvalidOperationException("实例表共享引用数超限。");
            var copy = new TickInstanceMap(_generation, _tick, _identity, _wet) { _sequence = _sequence };
            _identity.References++; _wet.References++;
            return copy;
        }
        internal void Close()
        {
            if (_closed) return;
            _closed = true;
            _identity.References--; _wet.References--;
            _identity = null; _wet = null;
        }
        internal void Adopt(TickInstanceMap candidate)
        {
            if (candidate == null || ReferenceEquals(this, candidate) || _closed || candidate._closed ||
                candidate._generation != _generation || candidate._tick != _tick)
                throw new InvalidOperationException("不能移交其他 Tick 的实例映射。");
            _identity.References--; _wet.References--;
            _identity = candidate._identity; _wet = candidate._wet; _sequence = candidate._sequence;
            // 转移候选拥有权；关闭候选不能清空采用后的当前Tick实例表。
            candidate._closed = true; candidate._identity = null; candidate._wet = null;
        }
        private void MakeIdentityWritable()
        {
            if (_identity.References == 1) return;
            var copy = new IdentityStorage(_identity);
            _identity.References--; _identity = copy; IdentityCopies++;
        }
        private void MakeWetWritable()
        {
            if (_wet.References == 1) return;
            var copy = new WetStorage(_wet);
            _wet.References--; _wet = copy;
        }
        private CellKey Require(in CellInstanceHandle instance)
        {
            if (!TryResolve(instance, out CellKey key)) throw new InvalidOperationException("材料实例租约失效。");
            return key;
        }
        private void ValidateKey(in CellKey key)
        {
            if (_closed || key.Generation != _generation) throw new InvalidOperationException("材料实例代次已失效。");
        }
    }
}
