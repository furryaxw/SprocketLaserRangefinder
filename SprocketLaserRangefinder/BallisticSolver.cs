using UnityEngine;

namespace SprocketLaserRangefinder
{
    internal readonly struct BallisticModel
    {
        private const float AirDensityHalf = 0.6f;
        private const float SpeedOfSound = 343.0f;

        public BallisticModel(
            float diameterMeters,
            float massKilograms,
            AnimationCurve sizeCurve,
            AnimationCurve machCurve)
        {
            DiameterMeters = diameterMeters;
            MassKilograms = massKilograms;
            SizeCurve = sizeCurve;
            MachCurve = machCurve;

            float radius = diameterMeters * 0.5f;
            DragConstant = sizeCurve.Evaluate(diameterMeters * 1000.0f) *
                           AirDensityHalf * Mathf.PI * radius * radius;
        }

        public float DiameterMeters { get; }
        public float MassKilograms { get; }
        public AnimationCurve SizeCurve { get; }
        public AnimationCurve MachCurve { get; }
        public float DragConstant { get; }

        public Vector3 Acceleration(Vector3 velocity)
        {
            float speed = velocity.magnitude;
            if (speed <= 1e-6f)
                return Physics.gravity;

            float machCoefficient = MachCurve.Evaluate(speed / SpeedOfSound);
            float dragForce = machCoefficient * DragConstant * speed * speed;
            return Physics.gravity -
                   velocity.normalized * (dragForce / MassKilograms);
        }
    }

    internal readonly struct BallisticSolution
    {
        public BallisticSolution(
            Vector3 direction,
            float timeOfFlight,
            float missDistance,
            float elevationCorrectionDegrees)
        {
            Direction = direction;
            TimeOfFlight = timeOfFlight;
            MissDistance = missDistance;
            ElevationCorrectionDegrees = elevationCorrectionDegrees;
        }

        public Vector3 Direction { get; }
        public float TimeOfFlight { get; }
        public float MissDistance { get; }
        public float ElevationCorrectionDegrees { get; }
    }

    internal static class BallisticSolver
    {
        private const int SolverIterations = 10;
        private const float DerivativeStepRadians = 0.001f;
        private const float MaximumCorrectionStepRadians = 0.08f;
        private const float MaximumFlightTimeSeconds = 30.0f;
        private const float AcceptableMissMeters = 1.0f;

        public static bool TrySolveLowArc(
            BallisticModel model,
            Ray opticalRay,
            float slantRange,
            Vector3 muzzlePosition,
            float muzzleVelocity,
            Vector3 inheritedVelocity,
            float fixedDeltaTime,
            out BallisticSolution solution)
        {
            solution = default;
            if (!IsModelUsable(model, muzzleVelocity, fixedDeltaTime) ||
                !float.IsFinite(slantRange) ||
                slantRange < 1.0f ||
                opticalRay.direction.sqrMagnitude <= 1e-8f)
            {
                return false;
            }

            Vector3 opticalDirection = opticalRay.direction.normalized;
            Vector3 targetPosition = opticalRay.origin +
                                     opticalDirection * slantRange;
            Vector3 displacement = targetPosition - muzzlePosition;
            Vector3 horizontalDisplacement = new(
                displacement.x,
                0.0f,
                displacement.z);
            float horizontalRange = horizontalDisplacement.magnitude;
            if (horizontalRange < 1.0f)
                return false;

            Vector3 horizontalForward = horizontalDisplacement / horizontalRange;
            Vector3 lateralRight = Vector3.Cross(
                Vector3.up,
                horizontalForward).normalized;
            float lineOfSightPitch = Mathf.Atan2(
                displacement.y,
                horizontalRange);
            float pitch = lineOfSightPitch;
            float yaw = 0.0f;

            TrajectoryPlaneSample best = default;
            float bestMiss = float.PositiveInfinity;
            float bestPitch = pitch;
            float bestYaw = yaw;

            for (int iteration = 0; iteration < SolverIterations; iteration++)
            {
                if (!TrySampleTargetPlane(
                        model,
                        muzzlePosition,
                        muzzleVelocity,
                        inheritedVelocity,
                        horizontalForward,
                        lateralRight,
                        horizontalRange,
                        pitch,
                        yaw,
                        fixedDeltaTime,
                        out TrajectoryPlaneSample current))
                {
                    return false;
                }

                float verticalError = current.Position.y - targetPosition.y;
                float lateralError = Vector3.Dot(
                    current.Position - targetPosition,
                    lateralRight);
                float currentMiss = Mathf.Sqrt(
                    verticalError * verticalError +
                    lateralError * lateralError);
                if (currentMiss < bestMiss)
                {
                    bestMiss = currentMiss;
                    best = current;
                    bestPitch = pitch;
                    bestYaw = yaw;
                }

                if (currentMiss <= 0.05f)
                    break;

                if (!TrySampleTargetPlane(
                        model,
                        muzzlePosition,
                        muzzleVelocity,
                        inheritedVelocity,
                        horizontalForward,
                        lateralRight,
                        horizontalRange,
                        pitch + DerivativeStepRadians,
                        yaw,
                        fixedDeltaTime,
                        out TrajectoryPlaneSample pitchSample) ||
                    !TrySampleTargetPlane(
                        model,
                        muzzlePosition,
                        muzzleVelocity,
                        inheritedVelocity,
                        horizontalForward,
                        lateralRight,
                        horizontalRange,
                        pitch,
                        yaw + DerivativeStepRadians,
                        fixedDeltaTime,
                        out TrajectoryPlaneSample yawSample))
                {
                    break;
                }

                float pitchVerticalDerivative =
                    (pitchSample.Position.y - current.Position.y) /
                    DerivativeStepRadians;
                float pitchLateralDerivative =
                    Vector3.Dot(
                        pitchSample.Position - current.Position,
                        lateralRight) /
                    DerivativeStepRadians;
                float yawVerticalDerivative =
                    (yawSample.Position.y - current.Position.y) /
                    DerivativeStepRadians;
                float yawLateralDerivative =
                    Vector3.Dot(
                        yawSample.Position - current.Position,
                        lateralRight) /
                    DerivativeStepRadians;

                float determinant =
                    pitchVerticalDerivative * yawLateralDerivative -
                    yawVerticalDerivative * pitchLateralDerivative;
                if (Mathf.Abs(determinant) < 1e-5f)
                    break;

                float pitchCorrection =
                    (-verticalError * yawLateralDerivative +
                     yawVerticalDerivative * lateralError) /
                    determinant;
                float yawCorrection =
                    (-pitchVerticalDerivative * lateralError +
                     pitchLateralDerivative * verticalError) /
                    determinant;

                pitch += Mathf.Clamp(
                    pitchCorrection,
                    -MaximumCorrectionStepRadians,
                    MaximumCorrectionStepRadians);
                yaw += Mathf.Clamp(
                    yawCorrection,
                    -MaximumCorrectionStepRadians,
                    MaximumCorrectionStepRadians);
                pitch = Mathf.Clamp(
                    pitch,
                    -15.0f * Mathf.Deg2Rad,
                    80.0f * Mathf.Deg2Rad);
                yaw = Mathf.Clamp(
                    yaw,
                    -30.0f * Mathf.Deg2Rad,
                    30.0f * Mathf.Deg2Rad);
            }

            if (!float.IsFinite(bestMiss) || bestMiss > AcceptableMissMeters)
                return false;

            Vector3 direction = BuildDirection(
                horizontalForward,
                bestPitch,
                bestYaw);
            solution = new BallisticSolution(
                direction,
                best.Time,
                bestMiss,
                (bestPitch - lineOfSightPitch) * Mathf.Rad2Deg);
            return true;
        }

        private static bool TrySampleTargetPlane(
            BallisticModel model,
            Vector3 muzzlePosition,
            float muzzleVelocity,
            Vector3 inheritedVelocity,
            Vector3 horizontalForward,
            Vector3 lateralRight,
            float horizontalRange,
            float pitch,
            float yaw,
            float fixedDeltaTime,
            out TrajectoryPlaneSample sample)
        {
            sample = default;
            Vector3 direction = BuildDirection(
                horizontalForward,
                pitch,
                yaw);
            Vector3 velocity = inheritedVelocity + direction * muzzleVelocity;
            Vector3 position = muzzlePosition;
            float previousDownrange = 0.0f;
            float time = 0.0f;
            int maxSteps = Mathf.CeilToInt(
                MaximumFlightTimeSeconds / fixedDeltaTime);

            for (int step = 0; step < maxSteps; step++)
            {
                Vector3 previousPosition = position;
                float previousTime = time;
                velocity += model.Acceleration(velocity) * fixedDeltaTime;
                position += velocity * fixedDeltaTime;
                time += fixedDeltaTime;

                float downrange = Vector3.Dot(
                    position - muzzlePosition,
                    horizontalForward);
                if (previousDownrange < horizontalRange &&
                    downrange >= horizontalRange)
                {
                    float denominator = downrange - previousDownrange;
                    float fraction = denominator > 1e-6f
                        ? (horizontalRange - previousDownrange) / denominator
                        : 1.0f;
                    sample = new TrajectoryPlaneSample(
                        Vector3.Lerp(previousPosition, position, fraction),
                        Mathf.Lerp(previousTime, time, fraction));
                    return true;
                }

                if (downrange + 1.0f < previousDownrange)
                    return false;
                previousDownrange = downrange;
            }

            return false;
        }

        private static Vector3 BuildDirection(
            Vector3 horizontalForward,
            float pitch,
            float yaw)
        {
            Vector3 yawedForward = Quaternion.AngleAxis(
                yaw * Mathf.Rad2Deg,
                Vector3.up) * horizontalForward;
            return (
                yawedForward * Mathf.Cos(pitch) +
                Vector3.up * Mathf.Sin(pitch)).normalized;
        }

        private static bool IsModelUsable(
            BallisticModel model,
            float speed,
            float fixedDeltaTime)
        {
            return model.SizeCurve != null &&
                   model.MachCurve != null &&
                   model.DiameterMeters > 0.0f &&
                   model.MassKilograms > 0.0f &&
                   speed > 0.0f &&
                   fixedDeltaTime > 0.0f;
        }

        private readonly struct TrajectoryPlaneSample
        {
            public TrajectoryPlaneSample(Vector3 position, float time)
            {
                Position = position;
                Time = time;
            }

            public Vector3 Position { get; }
            public float Time { get; }
        }
    }
}
