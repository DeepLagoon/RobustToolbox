using System;
using System.Buffers;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.ObjectPool;
using Robust.Shared.GameObjects;
using Robust.Shared.Maths;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Dynamics;
using Robust.Shared.Physics.Dynamics.Contacts;
using Robust.Shared.Physics.Dynamics.Joints;
using Robust.Shared.Threading;
using Robust.Shared.Utility;

namespace Robust.Shared.Physics.Systems;

/*
 * These comments scabbed directly from Box2D and the licence applies to them.
 */

// MIT License

// Copyright (c) 2019 Erin Catto

// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:

// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.

// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.

/*
Position Correction Notes
=========================
I tried the several algorithms for position correction of the 2D revolute joint.
I looked at these systems:
- simple pendulum (1m diameter sphere on massless 5m stick) with initial angular velocity of 100 rad/s.
- suspension bridge with 30 1m long planks of length 1m.
- multi-link chain with 30 1m long links.
Here are the algorithms:
Baumgarte - A fraction of the position error is added to the velocity error. There is no
separate position solver.
Pseudo Velocities - After the velocity solver and position integration,
the position error, Jacobian, and effective mass are recomputed. Then
the velocity constraints are solved with pseudo velocities and a fraction
of the position error is added to the pseudo velocity error. The pseudo
velocities are initialized to zero and there is no warm-starting. After
the position solver, the pseudo velocities are added to the positions.
This is also called the First Order World method or the Position LCP method.
Modified Nonlinear Gauss-Seidel (NGS) - Like Pseudo Velocities except the
position error is re-computed for each constraint and the positions are updated
after the constraint is solved. The radius vectors (aka Jacobians) are
re-computed too (otherwise the algorithm has horrible instability). The pseudo
velocity states are not needed because they are effectively zero at the beginning
of each iteration. Since we have the current position error, we allow the
iterations to terminate early if the error becomes smaller than b2_linearSlop.
Full NGS or just NGS - Like Modified NGS except the effective mass are re-computed
each time a constraint is solved.
Here are the results:
Baumgarte - this is the cheapest algorithm but it has some stability problems,
especially with the bridge. The chain links separate easily close to the root
and they jitter as they struggle to pull together. This is one of the most common
methods in the field. The big drawback is that the position correction artificially
affects the momentum, thus leading to instabilities and false bounce. I used a
bias factor of 0.2. A larger bias factor makes the bridge less stable, a smaller
factor makes joints and contacts more spongy.
Pseudo Velocities - the is more stable than the Baumgarte method. The bridge is
stable. However, joints still separate with large angular velocities. Drag the
simple pendulum in a circle quickly and the joint will separate. The chain separates
easily and does not recover. I used a bias factor of 0.2. A larger value lead to
the bridge collapsing when a heavy cube drops on it.
Modified NGS - this algorithm is better in some ways than Baumgarte and Pseudo
Velocities, but in other ways it is worse. The bridge and chain are much more
stable, but the simple pendulum goes unstable at high angular velocities.
Full NGS - stable in all tests. The joints display good stiffness. The bridge
still sags, but this is better than infinite forces.
Recommendations
Pseudo Velocities are not really worthwhile because the bridge and chain cannot
recover from joint separation. In other cases the benefit over Baumgarte is small.
Modified NGS is not a robust method for the revolute joint due to the violent
instability seen in the simple pendulum. Perhaps it is viable with other constraint
types, especially scalar constraints where the effective mass is a scalar.
This leaves Baumgarte and Full NGS. Baumgarte has small, but manageable instabilities
and is very fast. I don't think we can escape Baumgarte, especially in highly
demanding cases where high constraint fidelity is not needed.
Full NGS is robust and easy on the eyes. I recommend this as an option for
higher fidelity simulation and certainly for suspension bridges and long chains.
Full NGS might be a good choice for ragdolls, especially motorized ragdolls where
joint separation can be problematic. The number of NGS iterations can be reduced
for better performance without harming robustness much.
Each joint in a can be handled differently in the position solver. So I recommend
a system where the user can select the algorithm on a per joint basis. I would
probably default to the slower Full NGS and let the user select the faster
Baumgarte method in performance critical scenarios.
*/

/*
Cache Performance
The Box2D solvers are dominated by cache misses. Data structures are designed
to increase the number of cache hits. Much of misses are due to random access
to body data. The constraint structures are iterated over linearly, which leads
to few cache misses.
The bodies are not accessed during iteration. Instead read only data, such as
the mass values are stored with the constraints. The mutable data are the constraint
impulses and the bodies velocities/positions. The impulses are held inside the
constraint structures. The body velocities/positions are held in compact, temporary
arrays to increase the number of cache hits. Linear and angular velocity are
stored in a single array since multiple arrays lead to multiple misses.
*/

public abstract partial class SharedPhysicsSystem
{
    /*
     * Handles island generation and constraints solver code.
     */
    private const int InitialIslandCapacity = 256;
    // Thousands of separate items can touch static walls without joining the same island.
    private const int MaxPooledIslands = 8192;
    private const int CompactIslandBodyLimit = 4;

    // Contact-only islands share this empty list. BuildIslands rents a writable list on the first joint.
    private static readonly List<(Joint Original, Joint Joint)> EmptyIslandJoints = [];

    private readonly ObjectPool<List<Entity<PhysicsComponent, TransformComponent>>> _islandBodyPool =
        new DefaultObjectPool<List<Entity<PhysicsComponent, TransformComponent>>>(new ListPolicy<Entity<PhysicsComponent, TransformComponent>>(), MaxPooledIslands);

    private readonly ObjectPool<List<Contact>> _islandContactPool =
        new DefaultObjectPool<List<Contact>>(new ListPolicy<Contact>(), MaxPooledIslands);

    /// <summary>
    /// Due to joint relays we need to track the dummy joint and the original joint.
    /// </summary>
    private readonly ObjectPool<List<(Joint Original, Joint Joint)>> _islandJointPool =
        new DefaultObjectPool<List<(Joint Original, Joint Joint)>>(new ListPolicy<(Joint Original, Joint Joint)>(), MaxPooledIslands);

    internal record struct IslandData(
        int Index,
        bool LoneIsland,
        List<Entity<PhysicsComponent, TransformComponent>> Bodies,
        List<Contact> Contacts,
        List<(Joint Original, Joint Joint)> Joints)
    {
        /// <summary>
        /// MapUid for the island.
        /// </summary>
        public EntityUid MapUid;

        /// <summary>
        /// Island index to be used for bodies to identify which island they're in.
        /// </summary>
        public readonly int Index = Index;

        /// <summary>
        /// Are we the special island that has all contact-less bodies in it?
        /// This is treated separately for sleep purposes.
        /// </summary>
        public readonly bool LoneIsland = LoneIsland;

        /// <summary>
        /// Offset in the data arrays
        /// </summary>
        public int Offset = 0;

        public readonly List<Entity<PhysicsComponent, TransformComponent>> Bodies = Bodies;
        public readonly List<Contact> Contacts = Contacts;
        public readonly List<(Joint Original, Joint Joint)> Joints = Joints;

        // With only a few bodies and no joints, the contact solver can read their indices from Bodies.
        public readonly bool UsesBodyIndex = !LoneIsland && (Bodies.Count > CompactIslandBodyLimit || Joints.Count != 0);
        public bool PositionSolved = false;
        public List<(Joint Joint, float Error)>? BrokenJoints;
    }

    // Caching for island generation.
    private readonly List<Entity<PhysicsComponent, TransformComponent>> _islandBodies = new(64);
    private readonly Stack<Entity<PhysicsComponent, TransformComponent>> _bodyStack = new(64);
    private readonly List<Entity<PhysicsComponent, TransformComponent>> _awakeBodyList = new(256);
    private readonly List<IslandData> _islands = new(InitialIslandCapacity);
    private readonly SolveIslandsJob _solveIslandsJob = new();
    private readonly List<(Joint Original, Joint Joint)> _islandJoints = new(InitialIslandCapacity);

    // Config
    private bool _warmStarting;
    private float _maxLinearCorrection;
    private float _maxAngularCorrection;
    private int _velocityIterations;
    private int _positionIterations;
    private float _maxLinearVelocity;
    private float _maxAngularVelocity;
    private float _maxTranslationPerTick;
    private float _maxRotationPerTick;
    private int _tickRate;
    private bool _sleepAllowed;
    protected float AngularToleranceSqr;
    protected float LinearToleranceSqr;
    protected float TimeToSleep;
    private float _velocityThreshold;
    private float _baumgarte;

    #region Setup

    private void InitializeIsland()
    {
        Subs.CVar(_cfg, CVars.NetTickrate, SetTickRate, true);
        Subs.CVar(_cfg, CVars.WarmStarting, SetWarmStarting, true);
        Subs.CVar(_cfg, CVars.MaxLinearCorrection, SetMaxLinearCorrection, true);
        Subs.CVar(_cfg, CVars.MaxAngularCorrection, SetMaxAngularCorrection, true);
        Subs.CVar(_cfg, CVars.VelocityIterations, SetVelocityIterations, true);
        Subs.CVar(_cfg, CVars.PositionIterations, SetPositionIterations, true);
        Subs.CVar(_cfg, CVars.MaxLinVelocity, SetMaxLinearVelocity, true);
        Subs.CVar(_cfg, CVars.MaxAngVelocity, SetMaxAngularVelocity, true);
        Subs.CVar(_cfg, CVars.SleepAllowed, SetSleepAllowed, true);
        Subs.CVar(_cfg, CVars.AngularSleepTolerance, SetAngularToleranceSqr, true);
        Subs.CVar(_cfg, CVars.LinearSleepTolerance, SetLinearToleranceSqr, true);
        Subs.CVar(_cfg, CVars.TimeToSleep, SetTimeToSleep, true);
        Subs.CVar(_cfg, CVars.VelocityThreshold, SetVelocityThreshold, true);
        Subs.CVar(_cfg, CVars.Baumgarte, SetBaumgarte, true);
    }

    private void SetWarmStarting(bool value) => _warmStarting = value;
    private void SetMaxLinearCorrection(float value) => _maxLinearCorrection = value;
    private void SetMaxAngularCorrection(float value) => _maxAngularCorrection = value;
    private void SetVelocityIterations(int value) => _velocityIterations = value;
    private void SetPositionIterations(int value) => _positionIterations = value;
    private void SetMaxLinearVelocity(float value)
    {
        _maxLinearVelocity = value;
        UpdateMaxTranslation();
    }

    private void SetMaxAngularVelocity(float value)
    {
        _maxAngularVelocity = value;
        UpdateMaxRotation();
    }

    private void SetTickRate(int value)
    {
        _tickRate = value;
        UpdateMaxTranslation();
        UpdateMaxRotation();
    }

    private void SetSleepAllowed(bool value) => _sleepAllowed = value;
    private void SetAngularToleranceSqr(float value) => AngularToleranceSqr = value;
    private void SetLinearToleranceSqr(float value) => LinearToleranceSqr = value;
    private void SetTimeToSleep(float value) => TimeToSleep = value;
    private void SetVelocityThreshold(float value) => _velocityThreshold = value;
    private void SetBaumgarte(float value) => _baumgarte = value;

    private void UpdateMaxTranslation()
    {
        _maxTranslationPerTick = _maxLinearVelocity / _tickRate;
    }

    private void UpdateMaxRotation()
    {
        _maxRotationPerTick = (MathF.Tau * _maxAngularVelocity) / _tickRate;
    }

    #endregion

    /// <summary>
    ///     Where the magic happens.
    /// </summary>
    public void Step(float frameTime, bool prediction)
        => Step(frameTime, prediction, default, clearForces: true);

    private void Step(float frameTime, bool prediction, SubstepScope scope, bool clearForces)
    {
        var invDt = frameTime > 0.0f ? 1.0f / frameTime : 0.0f;
        var dtRatio = _invDt0 * frameTime;

        // Integrate velocities, solve velocity constraints, and do integration.
        Solve(frameTime, dtRatio, invDt, prediction, scope);

        // TODO: SolveTOI

        // Box2d recommends clearing (if you are) during fixed updates rather than variable if you are using it
        if (_autoClearForces && clearForces)
            ClearForces(scope);

        _invDt0 = invDt;
    }

    private void ClearForces(SubstepScope scope)
    {
        foreach (var ent in AwakeBodies)
        {
            if (!scope.Includes(ent.Owner))
                continue;

            var uid = ent.Owner;
            var body = ent.Comp1;

            if (body.Force != Vector2.Zero)
            {
                body.Force = Vector2.Zero;
                DirtyField(uid, body, nameof(PhysicsComponent.Force));
            }

            if (body.Torque != 0f)
            {
                body.Torque = 0f;
                DirtyField(uid, body, nameof(PhysicsComponent.Torque));
            }
        }
    }

    private void Solve(float frameTime, float dtRatio, float invDt, bool prediction, SubstepScope scope)
    {
        _islands.Clear();
        using (_prof.Group("Build Islands"))
        {
            BuildIslands(prediction, scope);
        }

        using (_prof.Group("Solve Islands"))
        {
            SolveIslands(frameTime, dtRatio, invDt, prediction);
        }

        foreach (var island in _islands)
        {
            ReturnIsland(island);
        }

        Cleanup(frameTime);
    }

    private void BuildIslands(bool prediction, SubstepScope scope)
    {
        // Snapshot seeds since island traversal may wake more bodies.
        if (scope is { Bodies: { } local, Complement: false })
        {
            foreach (var uid in local)
            {
                if (PhysicsQuery.TryComp(uid, out var body) && body.Awake &&
                    XformQuery.TryComp(uid, out var xform))
                    _awakeBodyList.Add((uid, body, xform));
            }
        }
        else
        {
            foreach (var body in AwakeBodies)
            {
                if (scope.Includes(body.Owner))
                    _awakeBodyList.Add(body);
            }
        }

        _bodyStack.EnsureCapacity(_awakeBodyList.Count);
        _islandBodies.EnsureCapacity(_awakeBodyList.Count);

        var islandIndex = 0;
        var loneIsland = new IslandData(
            islandIndex++,
            true,
            _islandBodyPool.Get(),
            _islandContactPool.Get(),
            EmptyIslandJoints);

        // Build the relevant islands / graphs for all bodies.
        foreach (var ent in _awakeBodyList)
        {
            var xform = ent.Comp2;
            var seed = ent.Comp1;

            // I tried not running prediction for non-contacted entities but unfortunately it looked like shit
            // when contact broke so if you want to try that then GOOD LUCK.
            if (seed.Island) continue;

            var seedUid = ent.Owner;
            var mapUid = xform.MapUid;

            // TODO: Handle this on client.
            if (mapUid == null)
            {
                continue;
            }

            if (!EntityManager.MetaQuery.TryGetComponent(seedUid, out var metadata))
            {
                Log.Error($"Found deleted entity {ToPrettyString(seedUid)} on map!");
                RemoveSleepBody(ent);
                continue;
            }

            if ((metadata.EntityPaused && !seed.IgnorePaused) ||
                (prediction && !seed.Predict) ||
                !seed.CanCollide ||
                seed.BodyType == BodyType.Static)
            {
                continue;
            }

            // Free bodies share the unconstrained island. There is no graph to traverse
            // and no constraint will look up an island-local body index for them.
            if (seed.ContactCount == 0 && !JointQuery.HasComp(seedUid) && !RelayTargetQuery.HasComp(seedUid))
            {
                seed.Island = true;
                _islandBodies.Add(ent);
                SetAwake(ent, true, updateSleepTime: false);
                loneIsland.MapUid = mapUid.Value;
                loneIsland.Bodies.Add(ent);
                continue;
            }

            // Start of a new island
            var bodies = _islandBodyPool.Get();
            var contacts = _islandContactPool.Get();
            List<(Joint Original, Joint Joint)>? joints = null;
            _bodyStack.Push(ent);

            seed.Island = true;

            while (_bodyStack.TryPop(out var bodyEnt))
            {
                var bodyUid = bodyEnt.Owner;
                var body = bodyEnt.Comp1;

                bodies.Add(bodyEnt);

                // Static bodies don't propagate islands
                if (body.BodyType == BodyType.Static) continue;

                _islandBodies.Add(bodyEnt);

                // As static bodies can never be awake (unlike Farseer) we'll set this after the check.
                SetAwake(bodyEnt, true, updateSleepTime: false);

                var node = body.Contacts.First;

                while (node != null)
                {
                    var contact = node.Value;
                    node = node.Next;

                    // Has this contact already been added to an island / is it pre-init?
                    if ((contact.Flags & (ContactFlags.Island | ContactFlags.PreInit)) != 0x0) continue;

                    // Is this contact solid and touching?
                    if (!contact.Enabled || !contact.IsTouching) continue;

                    // Skip sensors.
                    if (contact.FixtureA?.Hard != true || contact.FixtureB?.Hard != true) continue;

                    var other = contact.OtherBody(bodyUid);
                    var otherEnt = contact.OtherEnt(bodyUid);
                    if (!scope.IncludesConstraintBody(otherEnt, other))
                        continue;

                    contacts.Add(contact);
                    contact.Flags |= ContactFlags.Island;

                    // Was the other body already added to this island?
                    if (other.Island) continue;

                    var otherXform = contact.OtherTransform(bodyUid);
                    // TODO: Store this transform on the component directly.
                    _bodyStack.Push(new Entity<PhysicsComponent, TransformComponent>(otherEnt, other, otherXform));
                    other.Island = true;
                }

                // Handle joints
                _islandJoints.Clear();
                if (RelayTargetQuery.TryGetComponent(bodyUid, out var relayComp))
                {
                    foreach (var relay in relayComp.Relayed)
                    {
                        if (!JointQuery.TryGetComponent(relay, out var jointComp))
                            continue;

                        foreach (var joint in jointComp.GetJoints.Values)
                        {
                            if (joint.IslandFlag)
                                continue;

                            var uidA = joint.BodyAUid;
                            var uidB = joint.BodyBUid;
                            DebugTools.AssertNotEqual(uidA, uidB);

                            if (JointQuery.TryGetComponent(uidA, out var jointCompA) &&
                                jointCompA.Relay != null)
                            {
                                DebugTools.AssertNotEqual(uidB, jointCompA.Relay.Value);
                                uidA = jointCompA.Relay.Value;
                            }

                            if (JointQuery.TryGetComponent(uidB, out var jointCompB) &&
                                jointCompB.Relay != null)
                            {
                                DebugTools.AssertNotEqual(uidA, jointCompB.Relay.Value);
                                uidB = jointCompB.Relay.Value;
                            }

                            var copy = joint.Clone(uidA, uidB);
                            _islandJoints.Add((joint, copy));
                            joint.IslandFlag = true;
                        }
                    }
                }

                if (JointQuery.TryGetComponent(bodyUid, out var jointComponent) &&
                    jointComponent.Relay == null)
                {
                    foreach (var joint in jointComponent.Joints.Values)
                    {
                        if (joint.IslandFlag)
                            continue;

                        var uidA = joint.BodyAUid;
                        var uidB = joint.BodyBUid;

                        if (JointQuery.TryGetComponent(uidA, out var jointCompA) &&
                            jointCompA.Relay != null)
                        {
                            uidA = jointCompA.Relay.Value;
                        }

                        if (JointQuery.TryGetComponent(uidB, out var jointCompB) &&
                            jointCompB.Relay != null)
                        {
                            uidB = jointCompB.Relay.Value;
                        }

                        var copy = joint.Clone(uidA, uidB);
                        _islandJoints.Add((joint, copy));
                        joint.IslandFlag = true;
                    }
                }

                foreach (var (original, joint) in _islandJoints)
                {
                    // TODO: Same here store physicscomp + transform on the joint, the savings are worth it.
                    if (!PhysicsQuery.TryComp(joint.BodyAUid, out var bodyA) ||
                        !PhysicsQuery.TryComp(joint.BodyBUid, out var bodyB) ||
                        !bodyA.CanCollide ||
                        !bodyB.CanCollide ||
                        !scope.IncludesConstraintBody(joint.BodyAUid, bodyA) ||
                        !scope.IncludesConstraintBody(joint.BodyBUid, bodyB))
                    {
                        original.IslandFlag = false;
                        continue;
                    }

                    (joints ??= _islandJointPool.Get()).Add((original, joint));

                    if (!bodyA.Island)
                    {
                        _bodyStack.Push(new Entity<PhysicsComponent, TransformComponent>(joint.BodyAUid, bodyA, XformQuery.GetComponent(joint.BodyAUid)));
                        bodyA.Island = true;
                    }

                    if (!bodyB.Island)
                    {
                        _bodyStack.Push(new Entity<PhysicsComponent, TransformComponent>(joint.BodyBUid, bodyB, XformQuery.GetComponent(joint.BodyBUid)));
                        bodyB.Island = true;
                    }
                }
            }

            // Bodies not touching anything, hence we can just add it to the lone island.
            var loneBody = contacts.Count == 0 && joints == null;
            if (loneBody)
            {
                DebugTools.Assert(bodies.Count == 1 && bodies[0].Comp1.BodyType != BodyType.Static);
                loneIsland.MapUid = mapUid.Value;
                loneIsland.Bodies.Add(bodies[0]);

                _islandBodyPool.Return(bodies);
                _islandContactPool.Return(contacts);
                continue;
            }

            var data = new IslandData(islandIndex++, false, bodies, contacts, joints ?? EmptyIslandJoints)
            {
                MapUid = mapUid.Value
            };
            _islands.Add(data);

            // Allow static bodies to be re-used in other islands
            for (var i = 0; i < bodies.Count; i++)
            {
                var body = bodies[i].Comp1;

                // Static bodies can participate in other islands
                if (body.BodyType == BodyType.Static)
                {
                    body.Island = false;
                }

                if (data.UsesBodyIndex)
                    body.IslandIndex[data.Index] = i;
            }
        }

        // If we didn't use lone island just return it.
        if (loneIsland.Bodies.Count > 0)
        {
            _islands.Add(loneIsland);
        }
        else
        {
            ReturnIsland(loneIsland);
        }
    }

    private void ReturnIsland(in IslandData island)
    {
        if (island.UsesBodyIndex)
        {
            foreach (var bodyEnt in island.Bodies)
            {
                var body = bodyEnt.Comp1;
                DebugTools.Assert(body.IslandIndex.ContainsKey(island.Index));
                body.IslandIndex.Remove(island.Index);
            }
        }

        _islandBodyPool.Return(island.Bodies);
        _islandContactPool.Return(island.Contacts);

        if (island.Joints.Count == 0)
            return;

        foreach (var (original, joint) in island.Joints)
        {
            // Do we need to copy data back to the original?
            if (original != joint)
            {
                joint.CopyTo(original);
            }

            original.IslandFlag = false;
        }

        _islandJointPool.Return(island.Joints);
    }

    protected virtual void Cleanup(float frameTime)
    {
        foreach (var bodyEnt in _islandBodies)
        {
            var body = bodyEnt.Comp1;

            if (!body.Island || body.Deleted)
            {
                continue;
            }

            body.Island = false;
            DebugTools.Assert(body.BodyType != BodyType.Static);

            // So Box2D would update broadphase here buutttt we'll just wait until MoveEvent queue is used.
        }

        _islandBodies.Clear();
        _bodyStack.Clear();
        _awakeBodyList.Clear();
    }

    private void SolveIslands(float frameTime, float dtRatio, float invDt, bool prediction)
    {
        var data = new SolverData(
            frameTime,
            dtRatio,
            invDt,
            _warmStarting,
            _maxLinearCorrection,
            _maxAngularCorrection,
            _velocityIterations,
            _positionIterations,
            _maxLinearVelocity,
            _maxAngularVelocity,
            _maxTranslationPerTick,
            _maxRotationPerTick,
            _sleepAllowed,
            AngularToleranceSqr,
            LinearToleranceSqr,
            TimeToSleep,
            _velocityThreshold,
            _baumgarte
        );

        var totalBodies = 0;
        var islands = CollectionsMarshal.AsSpan(_islands);
        for (var i = 0; i < _islands.Count; i++)
        {
            ref var island = ref islands[i];
            island.Offset = totalBodies;
            UpdateLerpData(island.Bodies);

#if DEBUG
            RaiseLocalEvent(new IslandSolveMessage(island.Bodies));
#endif

            totalBodies += island.Bodies.Count;
        }

        // Preparation runs in the existing solver jobs, before ECS move handlers can
        // change the world. Small solves keep the existing inline fixture update.
        var prepareMoves = totalBodies >= 256 && _physicsNet.IsServer && _cfg.GetCVar(CVars.PrepareFixtureMoves);
        _solveIslandsJob.EnsureCapacity(totalBodies, prepareMoves);
        var solvedPositions = _solveIslandsJob.SolvedPositions;
        var solvedAngles = _solveIslandsJob.SolvedAngles;
        var linearVelocities = _solveIslandsJob.LinearVelocities;
        var angularVelocities = _solveIslandsJob.AngularVelocities;
        var sleepStatus = _solveIslandsJob.SleepStatus;
        var preparedMoves = prepareMoves ? _solveIslandsJob.PreparedMoves : null;
        _solveIslandsJob.PrepareMoves = prepareMoves;
        // Discard sleep flags from the previous solve.
        Array.Clear(sleepStatus, 0, totalBodies);

        _solveIslandsJob.System = this;
        _solveIslandsJob.Data = data;
        _solveIslandsJob.Prediction = prediction;
        // Cargo against static geometry creates many small independent islands.
        // Queue batches of those islands so each substep does not schedule a job per item.
        _solveIslandsJob.BatchSize = Math.Clamp(islands.Length / (Environment.ProcessorCount * 4), 1, 32);

        _prof.WriteValue("Physics islands", islands.Length);
        using (_prof.Group("Island jobs"))
            _parallel.ProcessNow(_solveIslandsJob, islands.Length);

        // Apply updates and deliver ECS events in island and body order.
        if (preparedMoves != null)
            _lookup.BeginPreparedFixtureMoves(preparedMoves);
        try
        {
            using var group = _prof.Group("Island body updates");
            for (var i = 0; i < islands.Length; i++)
            {
                ref readonly var island = ref islands[i];

                UpdateBodies(in island, solvedPositions, solvedAngles, linearVelocities, angularVelocities, preparedMoves, invDt);
                SleepBodies(in island, sleepStatus);
            }
        }
        finally
        {
            if (preparedMoves != null)
                _prof.WriteValue("Prepared fixture moves", _lookup.EndPreparedFixtureMoves());
        }
    }

    // Kept on the worker's stack and discarded before body writes or contact events.
    private const int PhysicsPoseCacheSize = 32;

    private readonly record struct CachedPhysicsPose<T>(EntityUid Entity, T Value) where T : unmanaged;

    private readonly struct IslandSolverBuffers(int bodyCount, int contactCount) : IDisposable
    {
        public readonly Vector2[] Positions = ArrayPool<Vector2>.Shared.Rent(bodyCount);
        public readonly float[] Angles = ArrayPool<float>.Shared.Rent(bodyCount);
        public readonly ContactVelocityConstraint[] VelocityConstraints = ArrayPool<ContactVelocityConstraint>.Shared.Rent(contactCount);
        public readonly ContactPositionConstraint[] PositionConstraints = ArrayPool<ContactPositionConstraint>.Shared.Rent(contactCount);

        public void Dispose()
        {
            ArrayPool<Vector2>.Shared.Return(Positions);
            ArrayPool<float>.Shared.Return(Angles);
            ArrayPool<ContactVelocityConstraint>.Shared.Return(VelocityConstraints);
            ArrayPool<ContactPositionConstraint>.Shared.Return(PositionConstraints);
        }
    }

    private sealed class SolveIslandsJob : IParallelBulkRobustJob
    {
        public SharedPhysicsSystem System = default!;
        public SolverData Data;
        public bool Prediction;
        public Vector2[] SolvedPositions = Array.Empty<Vector2>();
        public float[] SolvedAngles = Array.Empty<float>();
        public Vector2[] LinearVelocities = Array.Empty<Vector2>();
        public float[] AngularVelocities = Array.Empty<float>();
        public bool[] SleepStatus = Array.Empty<bool>();
        public bool PrepareMoves;
        public EntityLookupSystem.PreparedFixtureMove[] PreparedMoves = Array.Empty<EntityLookupSystem.PreparedFixtureMove>();

        public int BatchSize { get; set; } = 1;
        public int MinimumBatchParallel => 1;

        public void EnsureCapacity(int bodyCount, bool prepareMoves)
        {
            if (SolvedPositions.Length < bodyCount)
            {
                var capacity = Math.Max(bodyCount, Math.Max(16, SolvedPositions.Length * 2));
                SolvedPositions = new Vector2[capacity];
                SolvedAngles = new float[capacity];
                LinearVelocities = new Vector2[capacity];
                AngularVelocities = new float[capacity];
                SleepStatus = new bool[capacity];
            }

            if (prepareMoves && PreparedMoves.Length < bodyCount)
                PreparedMoves = new EntityLookupSystem.PreparedFixtureMove[SolvedPositions.Length];
        }

        public void ExecuteRange(int startIndex, int endIndex)
        {
            Span<CachedPhysicsPose<(Vector2 Position, float Angle)>> staticPoses =
                stackalloc CachedPhysicsPose<(Vector2, float)>[PhysicsPoseCacheSize];
            staticPoses.Clear();
            CachedPhysicsPose<(Angle Rotation, Matrix3x2 Inverse)> parentPose = default;

            var islands = CollectionsMarshal.AsSpan(System._islands)[startIndex..endIndex];
            var maxBodies = 0;
            var maxContacts = 0;
            foreach (ref readonly var island in islands)
            {
                maxBodies = Math.Max(maxBodies, island.Bodies.Count);
                maxContacts = Math.Max(maxContacts, island.Contacts.Count);
            }

            // Every island in this job uses the same scratch arrays. Only the solver results are kept per island.
            using var buffers = new IslandSolverBuffers(maxBodies, maxContacts);
            foreach (ref var island in islands)
                ExecuteIsland(ref island, in buffers, staticPoses, ref parentPose);
        }

        private void ExecuteIsland(ref IslandData island, in IslandSolverBuffers buffers,
            Span<CachedPhysicsPose<(Vector2 Position, float Angle)>> staticPoses,
            ref CachedPhysicsPose<(Angle Rotation, Matrix3x2 Inverse)> parentPose)
        {
            var data = Data;
            if (System._physicsNet.IsServer && !island.LoneIsland)
            {
                // Bodies that used different step sizes cannot share a warm-start ratio
                // on the first solve after their islands merge.
                var previousInvDt = island.Bodies[0].Comp1.PreviousInvDt;
                foreach (var (_, body, _) in island.Bodies)
                {
                    if (body.BodyType != BodyType.Static && body.PreviousInvDt != previousInvDt)
                    {
                        previousInvDt = 0;
                        break;
                    }
                }

                data = data with { DtRatio = previousInvDt * data.FrameTime, WarmStarting = data.WarmStarting && previousInvDt > 0 };
            }
            System.SolveIsland(ref island, in data, Prediction, SolvedPositions, SolvedAngles,
                LinearVelocities, AngularVelocities, SleepStatus, PrepareMoves ? PreparedMoves : null, staticPoses, ref parentPose, in buffers);
        }
    }

    /// <summary>
    /// If this is the first time a body has been updated this tick update its position for lerping.
    /// Due to substepping we have to check it every time.
    /// </summary>
    protected virtual void UpdateLerpData(List<Entity<PhysicsComponent, TransformComponent>> bodies)
    {

    }

    /// <summary>
    ///     Go through all the bodies in this island and solve.
    /// </summary>
    private void SolveIsland(
        ref IslandData island,
        in SolverData data,
        bool prediction,
        Vector2[] solvedPositions,
        float[] solvedAngles,
        Vector2[] linearVelocities,
        float[] angularVelocities,
        bool[] sleepStatus,
        EntityLookupSystem.PreparedFixtureMove[]? preparedMoves,
        Span<CachedPhysicsPose<(Vector2 Position, float Angle)>> staticPoses,
        ref CachedPhysicsPose<(Angle Rotation, Matrix3x2 Inverse)> parentPose,
        in IslandSolverBuffers buffers)
    {
        var bodyCount = island.Bodies.Count;
        var positions = buffers.Positions;
        var angles = buffers.Angles;
        var offset = island.Offset;
        var gravity = Gravity;


        for (var i = 0; i < island.Bodies.Count; i++)
        {
            var bodyEnt = island.Bodies[i];
            var body = bodyEnt.Comp1;
            // The same static support can appear in multiple independent islands.
            var (position, angle) = body.BodyType == BodyType.Static
                ? GetStaticSolverPose(bodyEnt, staticPoses)
                : GetSolverPose(bodyEnt);

            var linearVelocity = body.LinearVelocity;
            var angularVelocity = body.AngularVelocity;

            // if the body cannot move, nothing to do here
            if (body.BodyType == BodyType.Dynamic)
            {
                if (body.IgnoreGravity)
                {
                    linearVelocity += body.Force * data.FrameTime * body.InvMass;
                }
                else
                {
                    linearVelocity += (gravity + body.Force * body.InvMass) * data.FrameTime;
                }

                angularVelocity += body.InvI * body.Torque * data.FrameTime;

                linearVelocity *= Math.Clamp(1.0f - data.FrameTime * body.LinearDamping, 0.0f, 1.0f);
                angularVelocity *= Math.Clamp(1.0f - data.FrameTime * body.AngularDamping, 0.0f, 1.0f);
            }

            positions[i] = position;
            angles[i] = angle;
            linearVelocities[i + offset] = linearVelocity;
            angularVelocities[i + offset] = angularVelocity;
        }

        var velocityConstraints = buffers.VelocityConstraints;
        var positionConstraints = buffers.PositionConstraints;

        // Pass the data into the solver
        ResetSolver(in data, in island, velocityConstraints, positionConstraints);

        InitializeVelocityConstraints(in data, in island, velocityConstraints, positionConstraints, positions, angles, linearVelocities, angularVelocities);

        if (data.WarmStarting)
        {
            WarmStart(in data, in island, velocityConstraints, linearVelocities, angularVelocities);
        }

        var jointCount = island.Joints.Count;

        if (jointCount > 0)
        {
            for (var i = 0; i < island.Joints.Count; i++)
            {
                var joint = island.Joints[i].Joint;
                if (!joint.Enabled) continue;

                var bodyA = PhysicsQuery.GetComponent(joint.BodyAUid);
                var bodyB = PhysicsQuery.GetComponent(joint.BodyBUid);
                joint.InitVelocityConstraints(in data, in island, bodyA, bodyB, positions, angles, linearVelocities, angularVelocities);
            }
        }

        // Velocity solver
        for (var i = 0; i < data.VelocityIterations; i++)
        {
            for (var j = 0; j < jointCount; ++j)
            {
                var joint = island.Joints[j].Joint;

                if (!joint.Enabled)
                    continue;

                joint.SolveVelocityConstraints(in data, in island, linearVelocities, angularVelocities);

                var error = joint.Validate(data.InvDt);

                if (error > 0.0f)
                    (island.BrokenJoints ??= new()).Add((island.Joints[j].Original, error));
            }

            SolveVelocityConstraints(in island, velocityConstraints, linearVelocities, angularVelocities);
        }

        // Store for warm starting.
        StoreImpulses(in island, velocityConstraints);

        var maxVel = data.MaxTranslation / data.FrameTime;
        var maxVelSq = maxVel * maxVel;
        var maxAngVel = data.MaxRotation / data.FrameTime;
        var maxAngVelSq = maxAngVel * maxAngVel;

        // Integrate positions
        for (var i = 0; i < bodyCount; i++)
        {
            var linearVelocity = linearVelocities[offset + i];
            var angularVelocity = angularVelocities[offset + i];

            var velSqr = linearVelocity.LengthSquared();
            if (velSqr > maxVelSq)
            {
                linearVelocity *= maxVel / MathF.Sqrt(velSqr);
                linearVelocities[offset + i] = linearVelocity;
            }

            if (angularVelocity * angularVelocity > maxAngVelSq)
            {
                angularVelocity *= maxAngVel / MathF.Abs(angularVelocity);
                angularVelocities[offset + i] = angularVelocity;
            }

            // Integrate
            positions[i] += linearVelocity * data.FrameTime;
            angles[i] += angularVelocity * data.FrameTime;
        }

        island.PositionSolved = false;

        for (var i = 0; i < data.PositionIterations; i++)
        {
            var contactsOkay = SolvePositionConstraints(in data, in island, positionConstraints, positions, angles);
            var jointsOkay = true;

            for (var j = 0; j < island.Joints.Count; ++j)
            {
                var joint = island.Joints[j].Joint;

                if (!joint.Enabled)
                    continue;

                var jointOkay = joint.SolvePositionConstraints(in data, positions, angles);

                jointsOkay = jointsOkay && jointOkay;
            }

            if (contactsOkay && jointsOkay)
            {
                island.PositionSolved = true;
                break;
            }
        }

        // Transform the solved positions back into local terms
        // This means we can run the entire solver in parallel and not have to worry about stale world positions later
        // E.g. if a parent had its position updated then our worldposition is invalid
        // We can safely do this in parallel.

        // Solve positions now and store for later; we can't write this safely in parallel.
        var bodies = island.Bodies;

        FinalisePositions(0, bodyCount, offset, bodies, positions, angles, solvedPositions, solvedAngles, preparedMoves, ref parentPose);

        // Check sleep status for all of the bodies
        // Writing sleep timer is safe but updating awake or not is not safe.

        // We have a special island for no-contact no-joint bodies and just run this custom sleeping behaviour
        // for it while still keeping the benefits of a big island.
        if (island.LoneIsland)
        {
            if (!prediction && data.SleepAllowed)
            {
                for (var i = 0; i < bodyCount; i++)
                {
                    var body = island.Bodies[i].Comp1;

                    if (body.BodyType == BodyType.Static) continue;

                    if (!body.SleepingAllowed ||
                        body.AngularVelocity * body.AngularVelocity > data.AngTolSqr ||
                        Vector2.Dot(body.LinearVelocity, body.LinearVelocity) > data.LinTolSqr)
                    {
                        SetSleepTime(body, 0f);
                    }
                    else
                    {
                        SetSleepTime(body, body.SleepTime + data.FrameTime);
                    }

                    if (body.SleepTime >= data.TimeToSleep && island.PositionSolved)
                    {
                        sleepStatus[offset + i] = true;
                    }
                }
            }
        }
        else
        {
            // Sleep bodies if needed. Prediction won't accumulate sleep-time for bodies.
            if (!prediction && data.SleepAllowed)
            {
                var minSleepTime = float.MaxValue;

                for (var i = 0; i < bodyCount; i++)
                {
                    var body = island.Bodies[i].Comp1;

                    if (body.BodyType == BodyType.Static) continue;

                    if (!body.SleepingAllowed ||
                        body.AngularVelocity * body.AngularVelocity > data.AngTolSqr ||
                        Vector2.Dot(body.LinearVelocity, body.LinearVelocity) > data.LinTolSqr)
                    {
                        SetSleepTime(body, 0f);
                        minSleepTime = 0.0f;
                    }
                    else
                    {
                        SetSleepTime(body, body.SleepTime + data.FrameTime);
                        minSleepTime = MathF.Min(minSleepTime, body.SleepTime);
                    }
                }

                if (minSleepTime >= data.TimeToSleep && island.PositionSolved)
                {
                    for (var i = 0; i < island.Bodies.Count; i++)
                    {
                        sleepStatus[offset + i] = true;
                    }
                }
            }
        }
    }

    private (Vector2 Position, float Angle) GetSolverPose(Entity<PhysicsComponent, TransformComponent> entity)
    {
        var (position, rotation) = _transform.GetWorldPositionRotation(entity.Comp2);
        var transform = new Transform(position, rotation);
        return (Physics.Transform.Mul(transform, entity.Comp1.LocalCenter), transform.Quaternion2D.Angle);
    }

    private (Vector2 Position, float Angle) GetStaticSolverPose(Entity<PhysicsComponent, TransformComponent> entity,
        Span<CachedPhysicsPose<(Vector2 Position, float Angle)>> poses)
    {
        var uid = entity.Owner;
        ref var cached = ref poses[uid.Id & (PhysicsPoseCacheSize - 1)];
        if (cached.Entity != uid)
            cached = new(uid, GetSolverPose(entity));
        return cached.Value;
    }

    private void FinalisePositions(int start, int end, int offset, List<Entity<PhysicsComponent, TransformComponent>> bodies,
        Vector2[] positions, float[] angles, Vector2[] solvedPositions, float[] solvedAngles,
        EntityLookupSystem.PreparedFixtureMove[]? preparedMoves,
        ref CachedPhysicsPose<(Angle Rotation, Matrix3x2 Inverse)> parentPose)
    {
        for (var i = start; i < end; i++)
        {
            var ent = bodies[i];
            var body = ent.Comp1;
            if (preparedMoves != null)
                preparedMoves[offset + i] = default;

            if (body.BodyType == BodyType.Static)
                continue;

            var xform = ent.Comp2;
            var parent = xform.ParentUid;
            if (parentPose.Entity != parent || parent == EntityUid.Invalid)
            {
                if (!XformQuery.TryComp(parent, out var transform))
                    continue;

                var (_, rotation, inverse) = _transform.GetWorldPositionRotationInvMatrix(transform);
                parentPose = new(parent, (rotation, inverse));
            }

            var (parentRot, parentInvMatrix) = parentPose.Value;
            var worldRot = (float) (parentRot + xform._localRotation);

            var angle = angles[i];

            var q = new Quaternion2D(angle);
            var adjustedPosition = positions[i] - Physics.Transform.Mul(q, body.LocalCenter);

            var solvedPosition = Vector2.Transform(adjustedPosition, parentInvMatrix);
            solvedPositions[offset + i] = solvedPosition - xform.LocalPosition;
            solvedAngles[offset + i] = angle - worldRot;
            if (preparedMoves != null)
                preparedMoves[offset + i] = _lookup.PrepareFixtureMove(ent, solvedPositions[offset + i], solvedAngles[offset + i]);
        }
    }

    /// <summary>
    /// Updates the positions, rotations, and velocities of all of the solved bodies.
    /// Run sequentially to avoid threading issues.
    /// </summary>
    private void UpdateBodies(
        in IslandData island,
        Vector2[] positions,
        float[] angles,
        Vector2[] linearVelocities,
        float[] angularVelocities,
        EntityLookupSystem.PreparedFixtureMove[]? preparedMoves,
        float invDt)
    {
        if (island.BrokenJoints is { } brokenJoints)
        {
            foreach (var (joint, error) in brokenJoints)
            {
                var ev = new JointBreakEvent(joint, MathF.Sqrt(error));
                RaiseLocalEvent(joint.BodyAUid, ref ev);
                RaiseLocalEvent(joint.BodyBUid, ref ev);
                RaiseLocalEvent(ref ev);
                joint.Dirty();
            }
        }

        var offset = island.Offset;

        for (var i = 0; i < island.Bodies.Count; i++)
        {
            var bodyEnt = island.Bodies[i];
            var body = bodyEnt.Comp1;

            // So technically we don't /need/ to skip static bodies here but it saves us having to check for deferred updates so we'll do it anyway.
            // Plus calcing worldpos can be costly so we skip that too which is nice.
            if (body.BodyType == BodyType.Static) continue;

            body.PreviousInvDt = invDt;
            var uid = bodyEnt.Owner;
            var position = positions[offset + i];
            var angle = angles[offset + i];
            var xform = bodyEnt.Comp2;

            var linVelocity = linearVelocities[offset + i];
            var angVelocity = angularVelocities[offset + i];
            var validLinear = !float.IsNaN(linVelocity.X) && !float.IsNaN(linVelocity.Y);
            var validAngular = !float.IsNaN(angVelocity);
            var physicsDirtied = false;

            if (body.Awake && body.CanCollide)
            {
                // BuildIslands already wakes these bodies. Apply both solver outputs before
                // raising move events, without resolving the same component twice.
                physicsDirtied = validLinear && ApplyLinearVelocity(body, linVelocity);
                physicsDirtied |= validAngular && ApplyAngularVelocity(body, angVelocity);
            }
            else
            {
                // An earlier body's move event may have changed this body's state.
                physicsDirtied = validLinear && SetLinearVelocity(uid, linVelocity, false, body: body);
                physicsDirtied |= validAngular && SetAngularVelocity(uid, angVelocity, false, body: body);
            }

            // Temporary NaN guards until PVS is fixed.
            // May reparent object and change body's velocity.
            if (!float.IsNaN(position.X) && !float.IsNaN(position.Y))
            {
                var newPosition = xform.LocalPosition + position;
                var newRotation = xform.LocalRotation + angle;
                var index = offset + i;
                if (preparedMoves != null && preparedMoves[index].Owner == uid)
                    _lookup.ApplyPreparedFixtureMove(index, uid, newPosition, newRotation, xform);
                else
                    _transform.SetLocalPositionRotation(uid, newPosition, newRotation, xform);
            }

            if (physicsDirtied)
            {
                Dirty(uid, body);
            }
        }
    }

    private void SleepBodies(in IslandData island, bool[] sleepStatus)
    {
        var offset = island.Offset;

        for (var i = 0; i < island.Bodies.Count; i++)
        {
            var sleep = sleepStatus[offset + i];

            if (!sleep)
                continue;

            var body = island.Bodies[i];

            SetAwake(body, false);
        }
    }
}
