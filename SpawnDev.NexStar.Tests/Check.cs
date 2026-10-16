namespace SpawnDev.NexStar.Tests;

/// <summary>Tiny assertion helpers: a failed check throws, which the runner reports with the message.</summary>
internal static class Check
{
    public static void That(bool condition, string what)
    {
        if (!condition) throw new Exception("FAILED: " + what);
    }

    public static void Equal<T>(T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"FAILED: {what}: expected {expected}, got {actual}");
    }

    public static void Bytes(byte[] expected, byte[]? actual, string what)
    {
        if (actual == null) throw new Exception($"FAILED: {what}: expected {Hex(expected)}, got null");
        if (!expected.AsSpan().SequenceEqual(actual))
            throw new Exception($"FAILED: {what}: expected {Hex(expected)}, got {Hex(actual)}");
    }

    public static void Near(double expected, double actual, double tolerance, string what)
    {
        if (!(Math.Abs(expected - actual) <= tolerance))
            throw new Exception($"FAILED: {what}: expected {expected} +/- {tolerance}, got {actual}");
    }

    public static string Hex(byte[] bytes) => string.Join(" ", bytes.Select(b => b.ToString("X2")));
}
