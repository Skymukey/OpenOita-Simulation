using System;
using System.Collections.Generic;
using OpenOita.Contracts;
using UnityEngine;
using UnityEngine.SceneManagement;
using OpenOita.Spatial;

namespace OpenOita.PhysicsAdapter
{
    public sealed class WorldPhysicsAdapter : IPhysicsMutationPreparer, IPhysicsStepper
    {
        private readonly Scene _scene;
        private readonly PhysicsScene2D _physics;
        private readonly WorldConfig _config;
        private readonly ulong _generation;
        private readonly PhysicsMaterial2D _surface;
        private readonly List<GameObject> _boundary = new();
        private List<GameObject> _objects = new();
        private SortedDictionary<ulong, Rigidbody2D> _bodies = new();
        private PreparedPhysics _pending;
        private bool _disposed;
        private long _serial;
        private SpatialLease _lease;
        private Func<bool> _valid;
        private int _contactCells;
        private StepView _lastStep;
        public IReadOnlyList<BodyGeometryPlan> Geometry { get; private set; } = Array.Empty<BodyGeometryPlan>();
        public int BoundaryShapes => _boundary.Count;
        public int OwnedObjects => _objects.Count + _boundary.Count + (_pending?.Objects.Count ?? 0);
        public long ReservedCpuBytes => 8192L + OwnedObjects * 1024L + _config.Limits.MaxDynamicBodies * 256L + _config.Limits.MaxTotalShapes * 512L + _contactCells * 8192L;
        internal PhysicsScene2D PhysicsScene => _physics;
        public bool FreezeBodyRotation { get; }

        public WorldPhysicsAdapter(WorldConfig config, Vector2 origin, ulong generation, bool freezeBodyRotation = false)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _generation = generation;
            FreezeBodyRotation = freezeBodyRotation;
            _scene = SceneManager.CreateScene("OpenOitaPhysics-" + Guid.NewGuid().ToString("N"), new CreateSceneParameters(LocalPhysicsMode.Physics2D));
            _physics = _scene.GetPhysicsScene2D();
            _surface = new PhysicsMaterial2D("OpenOitaSurface") { friction = 0, bounciness = 0 };
            try
            {
                float s = config.CellSize, width = config.Width * s, height = config.Height * s;
                Boundary(origin + new Vector2(-s / 2, height / 2), new Vector2(s, height + 2 * s));
                Boundary(origin + new Vector2(width + s / 2, height / 2), new Vector2(s, height + 2 * s));
                Boundary(origin + new Vector2(width / 2, -s / 2), new Vector2(width, s));
                Boundary(origin + new Vector2(width / 2, height + s / 2), new Vector2(width, s));
            }
            catch { Dispose(); throw; }
        }

        private GameObject CreateObject(string name)
        {
            var go = new GameObject(name);
            go.SetActive(false);
            SceneManager.MoveGameObjectToScene(go, _scene);
            return go;
        }
        private void Boundary(Vector2 center, Vector2 size)
        {
            GameObject go = CreateObject("Boundary"); _boundary.Add(go);
            go.transform.position = center;
            BoxCollider2D box = go.AddComponent<BoxCollider2D>(); box.size = size; box.sharedMaterial = _surface;
            go.SetActive(true);
        }

        public PreparationResult<IPreparedPhysicsMutation> Prepare(IReadOnlyList<BodyGeometryPlan> geometry,
            ReadOnlySpan<BodyIdMapping> mappings, in ResourceBudget worldBudget, in TransactionContext context, IFailureInjector failures)
        {
            if (_disposed || _pending != null || geometry == null || context.PublishedVersion.Generation != _generation)
                return new PreparationResult<IPreparedPhysicsMutation>(Error(WorldErrorCode.NotReady, "准备租约无效。"));
            var prepared = new PreparedPhysics(this, context);
            try
            {
                int staticShapes = 0, dynamicShapes = 0;
                foreach (BodyGeometryPlan item in geometry)
                {
                    bool dynamic = item.Owner.OwnerKind == OwnerKind.Body;
                    if (dynamic && FreezeBodyRotation && item.Motion.AngularVelocityRadians != 0)
                        return new PreparationResult<IPreparedPhysicsMutation>(Error(WorldErrorCode.InvalidArgument, "禁转世界的几何计划不能带非零角速度。"));
                    if (!ContractDefaults.IsFinite(item.Pose.Position) || !ContractDefaults.IsFinite(item.Pose.AngleRadians) ||
                        !ContractDefaults.IsFinite(item.LocalCenterOfMass) || !ContractDefaults.IsFinite(item.Mass) || item.Mass <= 0 ||
                        !ContractDefaults.IsFinite(item.Inertia) || item.Inertia <= 0 || !ContractDefaults.IsFinite(item.BoundingRadius) || item.BoundingRadius <= 0 ||
                        !ContractDefaults.IsFinite(item.Motion.LinearVelocity) || !ContractDefaults.IsFinite(item.Motion.AngularVelocityRadians))
                        return new PreparationResult<IPreparedPhysicsMutation>(Error(WorldErrorCode.InvalidArgument, "几何计划含无效质量、惯量、位姿或运动。"));
                    if (item.GeometryVersion == 0 || item.Rectangles.Count == 0 || (dynamic && item.Rectangles.Count > _config.Limits.MaxShapesPerBody))
                        return new PreparationResult<IPreparedPhysicsMutation>(Error(WorldErrorCode.CapacityExceeded, "几何版本或单体形状预算无效。"));
                    GameObject go = CreateObject(dynamic ? "Body-" + item.Owner.BodyId : "FixedMaterial");
                    prepared.Objects.Add(go);
                    go.transform.SetPositionAndRotation(item.Pose.Position, Quaternion.Euler(0, 0, item.Pose.AngleRadians * Mathf.Rad2Deg));
                    Rigidbody2D rb = null;
                    if (dynamic)
                    {
                        rb = go.AddComponent<Rigidbody2D>(); rb.simulated = false;
                        rb.useAutoMass = false; rb.gravityScale = 0; rb.linearDamping = 0; rb.angularDamping = 0;
                        rb.sleepMode = RigidbodySleepMode2D.NeverSleep; rb.collisionDetectionMode = CollisionDetectionMode2D.Continuous;
                        prepared.Bodies.Add(item.Owner.BodyId, rb);
                    }
                    foreach (CellRectangle rectangle in item.Rectangles)
                    {
                        var box = go.AddComponent<BoxCollider2D>();
                        box.size = (Vector2)(rectangle.MaxExclusive - rectangle.Min) * _config.CellSize;
                        box.offset = (Vector2)(rectangle.MaxExclusive + rectangle.Min) * (_config.CellSize / 2);
                        box.sharedMaterial = _surface;
                    }
                    if (dynamic)
                    {
                        dynamicShapes = checked(dynamicShapes + item.Rectangles.Count);
                        rb.mass = item.Mass; rb.centerOfMass = item.LocalCenterOfMass; rb.inertia = item.Inertia;
                        RestoreMotion(rb, item.Motion);
                    }
                    else staticShapes = checked(staticShapes + item.Rectangles.Count);
                    if ((long)staticShapes + dynamicShapes > _config.Limits.MaxTotalShapes || prepared.Bodies.Count > _config.Limits.MaxDynamicBodies)
                        return new PreparationResult<IPreparedPhysicsMutation>(Error(WorldErrorCode.CapacityExceeded, "完整材料形状或体预算超限。"));
                }
                if (staticShapes != worldBudget.StaticShapes || dynamicShapes != worldBudget.DynamicShapes || prepared.Bodies.Count != worldBudget.DynamicBodies)
                    return new PreparationResult<IPreparedPhysicsMutation>(Error(WorldErrorCode.InvalidArgument, "物理与材料完整世界预算不一致。"));
                prepared.Plans = new List<BodyGeometryPlan>(geometry).AsReadOnly();
                prepared.Budget = new ResourceBudget(worldBudget.MaterialCells, prepared.Bodies.Count, staticShapes, dynamicShapes,
                    worldBudget.ChangedPositions, ReservedCpuBytes + prepared.Objects.Count * 1024L + (staticShapes + dynamicShapes) * 512L);
                WorldResult injection = failures?.Check(context, FailurePoint.AfterPhysicsPrepared) ?? WorldResult.Success();
                if (!injection.IsSuccess) return new PreparationResult<IPreparedPhysicsMutation>(injection);
                _pending = prepared;
                return new PreparationResult<IPreparedPhysicsMutation>(WorldResult.Success(), prepared);
            }
            catch (Exception exception) { return new PreparationResult<IPreparedPhysicsMutation>(Error(WorldErrorCode.CapacityExceeded, exception.Message)); }
            finally { if (!ReferenceEquals(_pending, prepared)) prepared.Dispose(); }
        }

        public void Bind(SpatialLease lease, Func<bool> valid) { _lease = lease; _valid = valid; }
        public PhysicsStepResult StepSubstep(IWorkingWorldView world, float substepSeconds, int substepIndex, long availableCpuBytes, IFailureInjector failures)
        {
            _serial++;
            _lastStep?.Invalidate(); _lastStep = null;
            if (_disposed || world == null || world.Generation != _generation || world.WorkingTick == 0 || _pending != null || _valid == null || !_valid() || _lease.Generation != world.Generation ||
                _lease.WorkingTick != world.WorkingTick || _lease.Stage != TickStage.Physics || substepIndex < 0 || !ContractDefaults.IsFinite(substepSeconds) || substepSeconds <= 0)
                return new PhysicsStepResult(Error(WorldErrorCode.NotReady, "子步绑定或步长无效。"));
            _contactCells = world.OccupiedCells.Length;
            if (ReservedCpuBytes > availableCpuBytes)
                return new PhysicsStepResult(Error(WorldErrorCode.CapacityExceeded, "求解前完整物理/接触工作集超过剩余CPU预算。"));
            foreach (Rigidbody2D rb in _bodies.Values) rb.AddForce(new Vector2(0, rb.mass * _config.GravityY), ForceMode2D.Force);
            if (!_physics.Simulate(substepSeconds)) return new PhysicsStepResult(Error(WorldErrorCode.Faulted, "独立物理场景未推进。"));
            var snapshots = new BodySnapshot[_bodies.Count];
            int index = 0;
            foreach (BodySnapshot original in world.Bodies)
            {
                if (!_bodies.TryGetValue(original.BodyId, out Rigidbody2D rb)) return new PhysicsStepResult(Error(WorldErrorCode.Faulted, "求解器与材料目录不一致。"));
                var motion = new BodyMotion(rb.linearVelocity, rb.angularVelocity * Mathf.Deg2Rad);
                WorldResult speed = PhysicsSubstepPlanner.CheckSpeed(_config, motion);
                if (!speed.IsSuccess) return new PhysicsStepResult(speed);
                snapshots[index++] = new BodySnapshot(original.BodyId, new BodyPose(rb.position, rb.rotation * Mathf.Deg2Rad), motion,
                    original.LocalCenterOfMass, original.GeometryVersion);
            }
            var context = new TransactionContext(world is null ? default : new WorldVersion(world.Generation, world.WorkingTick - 1), world.WorkingTick, TickStage.Physics);
            WorldResult injection = failures?.Check(context, FailurePoint.AfterPhysicsSubstep) ?? WorldResult.Success();
            if (!injection.IsSuccess) return new PhysicsStepResult(injection);
            WorldResult contactResult = MaterialContactResolver.Collect(world, snapshots, out CellContact[] contacts);
            if (!contactResult.IsSuccess) return new PhysicsStepResult(contactResult);
            _lastStep = new StepView(this, _serial, snapshots, contacts, _lease);
            return new PhysicsStepResult(WorldResult.Success(), _lastStep);
        }

        internal Rigidbody2D InspectBody(ulong id) => _bodies[id];
        private void RestoreMotion(Rigidbody2D rb, in BodyMotion motion)
        {
            // 锁定求解器的旋转自由度；不量化位置，也不在显示端强行转正。
            rb.constraints = FreezeBodyRotation ? RigidbodyConstraints2D.FreezeRotation : RigidbodyConstraints2D.None;
            rb.linearVelocity = motion.LinearVelocity;
            rb.angularVelocity = FreezeBodyRotation ? 0 : motion.AngularVelocityRadians * Mathf.Rad2Deg;
        }
        internal void Synchronize(BodySnapshot snapshot)
        {
            Rigidbody2D rb = _bodies[snapshot.BodyId];
            rb.position = snapshot.Pose.Position; rb.rotation = snapshot.Pose.AngleRadians * Mathf.Rad2Deg;
            RestoreMotion(rb, snapshot.Motion);
        }
        internal static void Release(GameObject go)
        {
            if (go == null) return;
            go.SetActive(false);
            if (Application.isPlaying) UnityEngine.Object.Destroy(go); else UnityEngine.Object.DestroyImmediate(go);
        }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true; _serial++; _valid = null;
            _lastStep?.Invalidate(); _lastStep = null; _contactCells = 0;
            _pending?.Dispose(); _pending = null;
            foreach (GameObject go in _objects) Release(go);
            foreach (GameObject go in _boundary) Release(go);
            _objects.Clear(); _boundary.Clear(); _bodies.Clear();
            Geometry = Array.Empty<BodyGeometryPlan>();
            if (_surface != null)
            {
                if (Application.isPlaying) UnityEngine.Object.Destroy(_surface); else UnityEngine.Object.DestroyImmediate(_surface);
            }
            if (_scene.IsValid() && _scene.isLoaded)
            {
#if UNITY_EDITOR
                if (!Application.isPlaying) UnityEditor.SceneManagement.EditorSceneManager.CloseScene(_scene, true);
                else
#endif
                    SceneManager.UnloadSceneAsync(_scene);
            }
        }

        private sealed class StepView : IPhysicsStepView
        {
            private readonly WorldPhysicsAdapter _owner;
            private readonly long _serial;
            private BodySnapshot[] _bodies;
            private CellContact[] _contacts;
            public SpatialLease Lease { get; }
            internal StepView(WorldPhysicsAdapter owner, long serial, BodySnapshot[] bodies, CellContact[] contacts, SpatialLease lease)
            { _owner = owner; _serial = serial; _bodies = bodies; _contacts = contacts; Lease = lease; }
            internal void Invalidate() { _bodies = null; _contacts = null; }
            private void Require()
            {
                if (_owner._disposed || _serial != _owner._serial || _owner._valid == null || !_owner._valid()) throw new InvalidOperationException("物理候选租约已失效。");
            }
            public ReadOnlySpan<BodySnapshot> CandidateBodies { get { Require(); return _bodies; } }
            public ReadOnlySpan<CellContact> Contacts { get { Require(); return _contacts; } }
        }

        private sealed class PreparedPhysics : IPreparedPhysicsMutation
        {
            private readonly WorldPhysicsAdapter _owner;
            private readonly TransactionContext _context;
            internal List<GameObject> Objects = new();
            internal SortedDictionary<ulong, Rigidbody2D> Bodies = new();
            internal IReadOnlyList<BodyGeometryPlan> Plans;
            public PreparationState State { get; private set; } = PreparationState.Prepared;
            public ResourceBudget Budget { get; internal set; }
            public IWorkingWorldView CandidateWorld => null;
            public ITickInstanceMap CandidateInstances => null;
            public ReadOnlySpan<CellPositionKey> CandidateWrites => ReadOnlySpan<CellPositionKey>.Empty;
            internal PreparedPhysics(WorldPhysicsAdapter owner, TransactionContext context) { _owner = owner; _context = context; }
            public WorldResult Preflight(in TransactionContext context) => !_owner._disposed && ReferenceEquals(_owner._pending, this) && State == PreparationState.Prepared &&
                context.PublishedVersion.Equals(_context.PublishedVersion) && context.WorkingTick == _context.WorkingTick && context.Stage == _context.Stage && context.Command.Equals(_context.Command)
                ? WorldResult.Success() : Error(WorldErrorCode.Busy, "物理准备对象租约已失效。");
            public WorldResult Apply(in TransactionContext context)
            {
                WorldResult check = Preflight(context); if (!check.IsSuccess) return check;
                // 先立即撤销全部旧碰撞，再启用候选；销毁的延迟不影响下一次求解。
                foreach (GameObject go in _owner._objects) Release(go);
                _owner._objects = Objects; Objects = new List<GameObject>();
                _owner._bodies = Bodies; Bodies = new SortedDictionary<ulong, Rigidbody2D>();
                _owner.Geometry = Plans;
                foreach (GameObject go in _owner._objects) go.SetActive(true);
                foreach (Rigidbody2D rb in _owner._bodies.Values) rb.simulated = true;
                // 激活/创建原生Body会重建质量属性；必须在最终Collider有效后显式恢复。
                foreach (BodyGeometryPlan plan in Plans)
                {
                    if (plan.Owner.OwnerKind != OwnerKind.Body) continue;
                    Rigidbody2D rb = _owner._bodies[plan.Owner.BodyId];
                    rb.useAutoMass = false; rb.mass = plan.Mass; rb.centerOfMass = plan.LocalCenterOfMass; rb.inertia = plan.Inertia;
                    _owner.RestoreMotion(rb, plan.Motion);
                }
                _owner._pending = null; _owner._serial++; State = PreparationState.Applied;
                _owner._lastStep?.Invalidate(); _owner._lastStep = null;
                return WorldResult.Success();
            }
            public void MarkCommitted(WorldVersion version)
            {
                if (State != PreparationState.Applied || version.Generation != _context.PublishedVersion.Generation || version.CommittedTick != _context.WorkingTick)
                    throw new InvalidOperationException("物理参与者只能随成功Tick提交。");
                State = PreparationState.Committed;
            }
            public void Abort()
            {
                if (State == PreparationState.Committed || State == PreparationState.Aborted) return;
                foreach (GameObject go in Objects) Release(go);
                Objects.Clear(); Bodies.Clear();
                if (ReferenceEquals(_owner._pending, this)) _owner._pending = null;
                State = PreparationState.Aborted;
            }
            public void Dispose() { if (State != PreparationState.Committed) Abort(); }
        }
        private static WorldResult Error(WorldErrorCode code, string message) => WorldResult.Failure(code, new WorldDiagnostic("Physics", "scene/body", message));
    }
}
