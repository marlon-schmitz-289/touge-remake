using System.Numerics;

namespace Touge.Tests;

public class CourseEnvTests
{
    /// <summary>
    ///     Driving along a road whose env sets switch A → B (a 2-point blip) → C → D (a run just long enough): the blip
    ///     is merged away, every set's share in what the cars reflect changes by less than 2 % per 10 cm (the game's hard
    ///     switch: 100 %), and away from the borders it is the plain set.
    /// </summary>
    [Fact]
    public void EnvAt_FadesAlongTheRoad()
    {
        int[] a = [1, 1, 1, 1], b = [2, 2, 2, 2], c = [3, 3, 3, 3], d = [4, 4, 4, 4];
        var road = Enumerable.Range(0, 100).Select(i => new Vector3(0, 0, 2 * i)).ToArray();
        var env = CourseLoader.Debounce([.. Enumerable.Range(0, 100).Select(i => i < 50 ? a : i < 52 ? b : i < 60 ? c : d)]);
        Assert.DoesNotContain(b, env);
        var course = new CourseLoader.Course(null!, null!, [], road, env, [], null, null, null);
        int[][] sets = [a, b, c, d];
        float[] Shares(float z)
        {
            var (x, y, mix) = course.EnvAt(new Vector3(0.3f, 0, z));
            return [.. sets.Select(s => (s == x ? 1 - mix : 0) + (s == y ? mix : 0))];
        }
        var last = Shares(0);
        var maxStep = 0f;
        for (var z = 0.1f; z < 198; z += 0.1f)
        {
            var now = Shares(z);
            maxStep = MathF.Max(maxStep, now.Zip(last, (p, q) => MathF.Abs(p - q)).Max());
            last = now;
        }
        Assert.True(maxStep < 0.02f, $"max step {maxStep}");
        Assert.Equal([1f, 0, 0, 0], Shares(20));
        Assert.Equal([0, 0, 0, 1f], Shares(180));
    }
}
