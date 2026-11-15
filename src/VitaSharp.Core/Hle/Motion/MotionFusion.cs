using System.Numerics;

namespace VitaSharp.Core.Hle.Motion;

/// <summary>
/// The Vita's motion processing (libSceMotion) for a stream of accelerometer + gyroscope samples, after Vita3K's
/// MotionInput (vita3k/motion): a Mahony filter (PI feedback of the accelerometer's gravity on the integrated gyro),
/// gyro deadband and slow bias estimation while still, and the basic orientation with the angle threshold.
/// <para>Frames: the device frame is the Vita's (x right, y top, z out of the screen). <see cref="Orientation"/> maps
/// device to world, where the world has y up and z towards the player, so the identity is the Vita held upright in
/// front of you and a Vita lying screen up is -90 degrees about x. That is where Vita3K's filter settles too (its
/// internal quaternion is remapped on output by get_orientation). Gravity reads (0, -1, 0) in the world.</para>
/// </summary>
public sealed class MotionFusion
{
    const float TwoPi = MathF.PI * 2;
    /// <summary>Proportional gain (Vita3K kp) and integral gain per second (Vita3K ki 0.005 per 60 Hz update).</summary>
    public const float Kp = 0.3f, Ki = 0.3f;
    /// <summary>Gyro deadband magnitude: Vita3K's 0.007 revolutions/s.</summary>
    public const float GyroDeadband = 0.007f * TwoPi;
    /// <summary>Clamp of the reported values (Vita3K GyroMaxValue 5 rev/s, AccelMaxValue 7 G).</summary>
    public const float GyroMax = 5f * TwoPi, AccelMax = 7f;
    const float StillGyro = 0.1f * TwoPi;
    static readonly Vector3 WorldGravity = new(0, -1, 0);

    /// <summary>Device-to-world rotation.</summary>
    public Quaternion Orientation { get; private set; } = Quaternion.Identity;
    /// <summary>Last acceleration in G (clamped).</summary>
    public Vector3 Acceleration { get; private set; } = new(0, 0, -1);
    /// <summary>Last angular velocity in rad/s after bias correction and deadband.</summary>
    public Vector3 AngularVelocity { get; private set; }
    /// <summary>Axis (components -1, 0, 1) of the device pointing away from gravity: lying screen up = (0, 0, 1).</summary>
    public Vector3 BasicOrientation { get; private set; } = new(0, 0, 1);

    public bool TiltCorrection { get; set; } = true;
    public bool Deadband { get; set; } = true;
    public bool GyroBiasCorrection { get; set; } = true;
    /// <summary>
    /// The three basic-orientation thresholds of driver_us 3.74, in degrees, as the library stores them (20 each at
    /// start; sceMotionSetAngleThreshold sets all three): <see cref="AngleThresholdPlane"/> is how far from the screen
    /// plane gravity may be and still give an in-plane axis once an in-plane axis is set (the routine is given 90
    /// minus it), <see cref="AngleThresholdCone"/> the cone about the screen normal that gives +Z / -Z, and
    /// <see cref="AngleThreshold"/> the hysteresis between X and Y (the one sceMotionGetAngleThreshold answers).
    /// </summary>
    public float AngleThresholdPlane { get; set; } = 20f;

    public float AngleThresholdCone { get; set; } = 20f;

    public float AngleThreshold { get; set; } = 20f;

    Vector3 _bias, _integral, _error;
    bool _hasGyro, _initialised;

    /// <summary>Estimated gyro bias (rad/s), subtracted while <see cref="GyroBiasCorrection"/> is on.</summary>
    public Vector3 GyroBias => _bias;

    /// <summary>Feeds one sample; <paramref name="dt"/> is the time since the previous one in seconds (ignored when
    /// not in (0, 0.1], like Vita3K, so gaps do not spin the orientation).</summary>
    public void Update(Vector3 accel, Vector3 gyro, float dt)
    {
        accel = Vector3.Clamp(accel, new Vector3(-AccelMax), new Vector3(AccelMax));
        var w = gyro;
        if (GyroBiasCorrection) w -= _bias;
        if (Deadband && w.LengthSquared() < GyroDeadband * GyroDeadband) w = Vector3.Zero;
        w = Vector3.Clamp(w, new Vector3(-GyroMax), new Vector3(GyroMax));
        Acceleration = accel;
        AngularVelocity = w;

        float alen = accel.Length();
        bool still = w.Length() < StillGyro && alen > 0.9f && alen < 1.1f;
        if (still) _bias = _bias * 0.9999f + gyro * 0.0001f; // drift estimate while the device is not moving
        if (gyro != Vector3.Zero) _hasGyro = true;

        if (!_initialised)
        {
            // start from the attitude gravity gives (Vita3K starts from its reset pose and converges over seconds)
            if (alen > 0.5f) { Orientation = FromGravity(accel); _initialised = true; }
            UpdateBasicOrientation();
            return;
        }

        if (dt > 0 && dt <= 0.1f)
        {
            var rate = _hasGyro ? w : Vector3.Zero;
            if (TiltCorrection && alen >= 0.75f && alen <= 1.25f)
            {
                // error = measured x estimated direction of gravity (device frame); its feedback turns the estimate
                // towards the measurement about the axis perpendicular to both
                var expected = Vector3.Transform(WorldGravity, Quaternion.Conjugate(Orientation));
                _error = Vector3.Cross(accel / alen, expected);
                if (_error.Length() >= 0.05f) _integral += _error * dt; else _integral = Vector3.Zero; // no windup once calibrated
                if (_integral.Length() > 1f) _integral = Vector3.Normalize(_integral);
                // without a gyro the accelerometer alone drives the attitude (Vita3K's stronger gains)
                float kp = _hasGyro ? Kp : 35f * Kp, ki = _hasGyro ? Ki : 10f * Ki;
                rate += kp * _error + ki * _integral;
            }
            float speed = rate.Length();
            if (speed > 0)
                Orientation = Quaternion.Normalize(Orientation * Quaternion.CreateFromAxisAngle(rate / speed, speed * dt));
        }
        UpdateBasicOrientation();
    }

    /// <summary>sceMotionReset: the current attitude becomes the origin: tilt from gravity, heading zero.</summary>
    public void Reset()
    {
        Orientation = Acceleration.Length() > 0.5f ? FromGravity(Acceleration) : Quaternion.Identity;
        _integral = Vector3.Zero;
        _error = Vector3.Zero;
    }

    /// <summary>sceMotionRotateYaw: turns the world reference about its vertical axis.</summary>
    public void RotateYaw(float radians)
        => Orientation = Quaternion.Normalize(Quaternion.CreateFromAxisAngle(Vector3.UnitY, radians) * Orientation);

    /// <summary>Device-to-world rotation that takes the measured gravity (device frame) to the world's (0, -1, 0)
    /// along the shortest arc, i.e. no heading change.</summary>
    public static Quaternion FromGravity(Vector3 accel)
    {
        var d = Vector3.Normalize(accel);
        float c = Vector3.Dot(d, WorldGravity);
        if (c < -0.99999f) return Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI); // upside down: turn about the screen normal
        var axis = Vector3.Cross(d, WorldGravity);
        return Quaternion.Normalize(new Quaternion(axis, 1 + c));
    }

    /// <summary>
    /// The device axis pointing up, updated with each accelerometer sample by the library's routine
    /// (<see cref="BasicOrientationStep"/>) with its three thresholds.
    /// </summary>
    private void UpdateBasicOrientation()
    {
        // no sample yet (a real sensor never reads exactly zero): keep the last one
        if (Acceleration == Vector3.Zero)
        {
            return;
        }

        BasicOrientation = BasicOrientationStep(90f - AngleThresholdPlane, AngleThresholdCone, AngleThreshold,
            Acceleration, BasicOrientation);
    }

    private static float AcosDegrees(float value) => (float)(MathF.Acos(value) * 180f / Math.PI);

    /// <summary>
    /// The basic orientation of one accelerometer sample with hysteresis (driver_us 3.74, also exported as
    /// SceMotion NID_A408BC69). <paramref name="planeBand"/>, <paramref name="cone"/> and <paramref name="switchBand"/>
    /// are degrees. A sample more than 0.08 g over 1 g keeps <paramref name="previous"/>; an acceleration within the
    /// cone of +Z / -Z gives (0, 0, -1) / (0, 0, 1); otherwise the dominant of X / Y in the screen plane, keeping the
    /// previous axis inside the bands.
    /// </summary>
    public static Vector3 BasicOrientationStep(float planeBand, float cone, float switchBand, Vector3 acceleration, Vector3 previous)
    {
        float length = acceleration.Length();
        if (length != 0 && !(length - 1f < 0.08f))
        {
            return previous;
        }

        Vector3 unit = acceleration / length;
        float angleZ = AcosDegrees(unit.Z);
        if (angleZ <= cone)
        {
            return new Vector3(0, 0, -1);
        }

        if (angleZ >= 180f - cone)
        {
            return new Vector3(0, 0, 1);
        }

        float fromPlane = MathF.Abs(angleZ - 90f);
        float x = previous.X;
        float y = previous.Y;
        bool fresh;
        if (90f - planeBand < fromPlane)
        {
            if (previous.Z != 0)
            {
                return previous;
            }

            fresh = x == 0 && y == 0;
        }
        else if (previous.Z == 0)
        {
            fresh = x == 0 && y == 0;
        }
        else
        {
            x = 0;
            y = 0;
            fresh = true;
        }

        // the in-plane direction (Z dropped)
        Vector2 inPlane = Vector2.Normalize(new Vector2(acceleration.X, acceleration.Y));
        float angleX = AcosDegrees(inPlane.X);
        float angleY = AcosDegrees(inPlane.Y);
        float fromY = MathF.Abs(angleX - 90f);
        if (!fresh && !(switchBand >= fromY) && 90f - switchBand > fromY)
        {
            return new Vector3(x, y, 0);
        }

        if (fromY > 45f)
        {
            return angleX < 90f ? new Vector3(-1, 0, 0) : new Vector3(1, 0, 0);
        }

        return angleY < 90f ? new Vector3(0, -1, 0) : new Vector3(0, 1, 0);
    }

    /// <summary>Column-major 4x4 rotation matrix (SceFMatrix4 rows x, y, z, w = the device axes in world space),
    /// the layout Vita3K's Quaternion::ToMatrix writes.</summary>
    public static void ToMatrix(Quaternion q, Span<float> m)
    {
        float x2 = q.X * q.X, y2 = q.Y * q.Y, z2 = q.Z * q.Z;
        float xy = q.X * q.Y, wz = q.W * q.Z, xz = q.X * q.Z, wy = q.W * q.Y, yz = q.Y * q.Z, wx = q.W * q.X;
        m[0] = 1 - 2 * (y2 + z2); m[1] = 2 * (xy + wz); m[2] = 2 * (xz - wy); m[3] = 0;
        m[4] = 2 * (xy - wz); m[5] = 1 - 2 * (x2 + z2); m[6] = 2 * (yz + wx); m[7] = 0;
        m[8] = 2 * (xz + wy); m[9] = 2 * (yz - wx); m[10] = 1 - 2 * (x2 + y2); m[11] = 0;
        m[12] = 0; m[13] = 0; m[14] = 0; m[15] = 1;
    }
}
