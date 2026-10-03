using UnityEngine;

/// <summary>Time-based response and spatial sampling, independent of a camera or render cadence.</summary>
public static class GrassInteractionMath
{
    public static float Response(float current, float target, float seconds, float timeConstant)
    {
        if (!Finite(current) || !Finite(target) || !Finite(seconds) || !Finite(timeConstant))
            return 0f;
        return Mathf.Lerp(Mathf.Clamp01(current), Mathf.Clamp01(target),
            1f - Mathf.Exp(-Mathf.Max(0f, seconds) / Mathf.Max(.001f, timeConstant)));
    }

    public static float Recovery(float strength, float age, float timeConstant)
    {
        if (!Finite(strength) || !Finite(age) || !Finite(timeConstant))
            return 0f;
        return Mathf.Clamp01(strength) * Mathf.Exp(-Mathf.Max(0f, age) / Mathf.Max(.001f, timeConstant));
    }

    public static int SegmentSamples(float distance, float radius, int limit = 32)
    {
        if (!Finite(distance) || !Finite(radius) || distance < 0f || radius <= 0f || limit < 1)
            return 0;
        // Never stretch a capped trail across a teleport or an unobserved interval.
        float spacing = radius * .4f;
        if (distance > spacing * limit)
            return 0;
        return Mathf.Clamp(Mathf.CeilToInt(distance / spacing), 1, limit);
    }

    public static float HeightContact(float bottom, float ground, float bladeHeight)
    {
        if (!Finite(bottom) || !Finite(ground) || !Finite(bladeHeight) || bladeHeight <= 0f)
            return 0f;
        return 1f - Mathf.SmoothStep(0f, 1f, Mathf.Max(0f, bottom - ground) / bladeHeight);
    }

    public static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    public static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);
}
