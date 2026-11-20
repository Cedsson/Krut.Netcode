using System;
using System.Numerics;
using Krut.Netcode;
using Xunit;

namespace Krut.Netcode.Tests;

public class NetworkSlerpAndBlendTests
{
    private struct MixedVehicleState
    {
        public Vector3 Position;
        public Quaternion Rotation;
        public int Gear;
        public int Flags;
        public bool Grounded;
    }

    private static float AngularDistanceRadians(Quaternion a, Quaternion b)
    {
        float dot = Math.Clamp(MathF.Abs(Quaternion.Dot(NetworkStateBlend.Normalized(a), NetworkStateBlend.Normalized(b))), 0f, 1f);
        return 2f * MathF.Acos(dot);
    }

    private static void AssertUnitFinite(Quaternion q)
    {
        Assert.True(float.IsFinite(q.X) && float.IsFinite(q.Y) && float.IsFinite(q.Z) && float.IsFinite(q.W), $"Non-finite quaternion {q}");
        Assert.InRange(q.Length(), 0.999f, 1.001f);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(0.25f)]
    [InlineData(0.5f)]
    [InlineData(0.75f)]
    [InlineData(1f)]
    public void SlerpRotation_OppositeSignSameRotation_RemainsUnitAndEquivalent(float t)
    {
        Quaternion q = Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(0.9f, -0.4f, 1.1f));
        Quaternion result = NetworkStateBlend.SlerpRotation(q, -q, t);

        AssertUnitFinite(result);
        Assert.True(AngularDistanceRadians(result, q) < 1e-3f, $"Expected identical rotation at t={t}, got angle {AngularDistanceRadians(result, q)}");
    }

    [Fact]
    public void SlerpRotation_IdenticalAndNearlyIdentical_WorkForBothSigns()
    {
        Quaternion baseRot = Quaternion.Normalize(Quaternion.CreateFromAxisAngle(new Vector3(1f, 2f, -1f), 0.7f));
        Quaternion nearRot = Quaternion.Normalize(baseRot * Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.5f * (MathF.PI / 180f)));

        Quaternion exact = NetworkStateBlend.SlerpRotation(baseRot, baseRot, 0.5f);
        AssertUnitFinite(exact);
        Assert.True(AngularDistanceRadians(exact, baseRot) < 1e-4f);

        Quaternion nearPlus = NetworkStateBlend.SlerpRotation(baseRot, nearRot, 0.5f);
        Quaternion nearMinus = NetworkStateBlend.SlerpRotation(baseRot, -nearRot, 0.5f);
        AssertUnitFinite(nearPlus);
        AssertUnitFinite(nearMinus);
        Assert.True(AngularDistanceRadians(nearPlus, nearMinus) < 1e-4f);
        Assert.True(AngularDistanceRadians(baseRot, nearPlus) <= AngularDistanceRadians(baseRot, nearRot) + 1e-4f);
    }

    [Fact]
    public void SlerpRotation_ZeroTo180And179ToMinus179_TakesShortestArc()
    {
        Quaternion q0 = Quaternion.Identity;
        Quaternion q180 = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI);
        Quaternion mid180 = NetworkStateBlend.SlerpRotation(q0, q180, 0.5f);
        AssertUnitFinite(mid180);
        Assert.Equal(MathF.PI / 2f, AngularDistanceRadians(q0, mid180), 3);

        // +179 deg vs -179 deg around Y: shortest path is 2 degrees across 180, not 358 degrees through 0.
        Quaternion pos179 = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 179f * (MathF.PI / 180f));
        Quaternion neg179 = Quaternion.CreateFromAxisAngle(Vector3.UnitY, -179f * (MathF.PI / 180f));
        Quaternion midWrap = NetworkStateBlend.SlerpRotation(pos179, neg179, 0.5f);
        AssertUnitFinite(midWrap);

        float distFrom180 = AngularDistanceRadians(midWrap, q180);
        Assert.True(distFrom180 < 0.02f, $"Expected midpoint near 180 deg, got {distFrom180} rad away");
    }

    [Fact]
    public void SlerpRotation_NegativeDotNonZeroAngle_InterpolatesHalfAngle()
    {
        Quaternion a = Quaternion.Identity;
        Quaternion b = -Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 3f); // 60 deg, negated -> dot < 0
        Assert.True(Quaternion.Dot(a, b) < 0f);

        Quaternion mid = NetworkStateBlend.SlerpRotation(a, b, 0.5f);
        AssertUnitFinite(mid);
        Assert.Equal(MathF.PI / 6f, AngularDistanceRadians(a, mid), 3);
        Assert.Equal(MathF.PI / 6f, AngularDistanceRadians(b, mid), 3);
    }

    [Fact]
    public void SlerpRotation_RandomNormalizedPairs_AreConsistentAcrossSignsAndMonotonic()
    {
        var rng = new Random(1337);
        for (int i = 0; i < 200; i++)
        {
            Quaternion a = Quaternion.Normalize(new Quaternion(
                (float)(rng.NextDouble() * 2 - 1),
                (float)(rng.NextDouble() * 2 - 1),
                (float)(rng.NextDouble() * 2 - 1),
                (float)(rng.NextDouble() * 2 - 1)));
            Quaternion b = Quaternion.Normalize(new Quaternion(
                (float)(rng.NextDouble() * 2 - 1),
                (float)(rng.NextDouble() * 2 - 1),
                (float)(rng.NextDouble() * 2 - 1),
                (float)(rng.NextDouble() * 2 - 1)));

            float totalAngle = AngularDistanceRadians(a, b);
            Quaternion r25Plus = NetworkStateBlend.SlerpRotation(a, b, 0.25f);
            Quaternion r25Minus = NetworkStateBlend.SlerpRotation(a, -b, 0.25f);
            Quaternion r50Plus = NetworkStateBlend.SlerpRotation(a, b, 0.50f);
            Quaternion r75Plus = NetworkStateBlend.SlerpRotation(a, b, 0.75f);

            AssertUnitFinite(r25Plus);
            AssertUnitFinite(r25Minus);
            AssertUnitFinite(r50Plus);
            AssertUnitFinite(r75Plus);
            Assert.True(AngularDistanceRadians(r25Plus, r25Minus) < 1e-3f);

            float d25 = AngularDistanceRadians(a, r25Plus);
            float d50 = AngularDistanceRadians(a, r50Plus);
            float d75 = AngularDistanceRadians(a, r75Plus);
            Assert.True(d25 <= d50 + 1e-3f && d50 <= d75 + 1e-3f && d75 <= totalAngle + 1e-3f);
        }
    }

    [Fact]
    public void SlerpRotation_AndNormalized_HandleZeroAndNonFiniteInputs()
    {
        Quaternion valid = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.8f);
        Quaternion zero = new(0f, 0f, 0f, 0f);
        Quaternion nanQ = new(float.NaN, 0f, 0f, 1f);
        Quaternion infQ = new(float.PositiveInfinity, 0f, 0f, 1f);

        Assert.Equal(Quaternion.Identity, NetworkStateBlend.Normalized(zero));
        Assert.Equal(Quaternion.Identity, NetworkStateBlend.Normalized(nanQ));
        Assert.Equal(Quaternion.Identity, NetworkStateBlend.Normalized(infQ));

        AssertUnitFinite(NetworkStateBlend.SlerpRotation(zero, valid, 0.5f));
        AssertUnitFinite(NetworkStateBlend.SlerpRotation(valid, zero, 0.5f));
        AssertUnitFinite(NetworkStateBlend.SlerpRotation(nanQ, valid, 0.5f));
        AssertUnitFinite(NetworkStateBlend.SlerpRotation(valid, infQ, 0.5f));
        AssertUnitFinite(NetworkStateBlend.SlerpRotation(valid, valid, float.NaN));
        AssertUnitFinite(NetworkStateBlend.SlerpRotation(valid, valid, float.PositiveInfinity));
        Assert.Equal(Quaternion.Identity, NetworkStateBlend.SlerpRotation(zero, zero, 0.5f));
    }

    [Fact]
    public void CustomTypedDelegates_PreserveDiscreteFieldsWhileBlendingContinuousFields()
    {
        var interpolator = new NetworkSnapshotInterpolator<MixedVehicleState>(delayTicks: 2)
        {
            Lerp = static (a, b, t) => new MixedVehicleState
            {
                Position = Vector3.Lerp(a.Position, b.Position, t),
                Rotation = NetworkStateBlend.SlerpRotation(a.Rotation, b.Rotation, t),
                Gear = t >= 0.5f ? b.Gear : a.Gear,
                Flags = t >= 0.5f ? b.Flags : a.Flags,
                Grounded = t >= 0.5f ? b.Grounded : a.Grounded,
            },
        };

        // -1 and 0x7F800001 would turn into NaN bit patterns if ever passed through float blending.
        var s10 = new MixedVehicleState
        {
            Position = new Vector3(0f, 0f, 0f),
            Rotation = Quaternion.Identity,
            Gear = -1,
            Flags = unchecked((int)0x80000001),
            Grounded = true,
        };
        var s20 = new MixedVehicleState
        {
            Position = new Vector3(100f, 0f, 0f),
            Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f),
            Gear = 4,
            Flags = 0x00FF00FF,
            Grounded = false,
        };

        interpolator.Push(10, s10);
        interpolator.Push(20, s20);

        Assert.True(interpolator.TrySampleTime(12.5, out MixedVehicleState early));
        Assert.Equal(25f, early.Position.X, 3);
        AssertUnitFinite(early.Rotation);
        Assert.Equal(-1, early.Gear);
        Assert.Equal(unchecked((int)0x80000001), early.Flags);
        Assert.True(early.Grounded);

        Assert.True(interpolator.TrySampleTime(17.5, out MixedVehicleState late));
        Assert.Equal(75f, late.Position.X, 3);
        AssertUnitFinite(late.Rotation);
        Assert.Equal(4, late.Gear);
        Assert.Equal(0x00FF00FF, late.Flags);
        Assert.False(late.Grounded);

        var reconciler = new NetworkReconciler<MixedVehicleState, float>(VisualSmoothingSettings.Default)
        {
            DistanceMetric = static (a, b) => Vector3.Distance(a.Position, b.Position),
            RenderOffset = static (state, from, to, share) => new MixedVehicleState
            {
                Position = state.Position + ((from.Position - to.Position) * share),
                Rotation = state.Rotation,
                Gear = state.Gear,
                Flags = state.Flags,
                Grounded = state.Grounded,
            },
            Step = static (s, _, dx) =>
            {
                s.Position.X += dx;
                return s;
            },
        };

        reconciler.Reset(0, s10);
        reconciler.Predict(1, 0.2f);
        reconciler.SetAuthoritativeState(2, new MixedVehicleState
        {
            Position = new Vector3(0.4f, 0f, 0f),
            Rotation = s10.Rotation,
            Gear = -1,
            Flags = unchecked((int)0x80000001),
            Grounded = true,
        });
        reconciler.Reconcile(2, 1, static (auth, _, _, _) => auth);
        reconciler.UpdateRenderState(0.025f);

        Assert.Equal(-1, reconciler.SmoothedState.Gear);
        Assert.Equal(unchecked((int)0x80000001), reconciler.SmoothedState.Flags);
        Assert.True(reconciler.SmoothedState.Grounded);
        Assert.InRange(reconciler.SmoothedState.Position.X, 0.2f, 0.4f);
    }
}
