namespace DeadlockAdvisor.Vision;

/// <summary>The few numpy routines detection's numbers depend on, computed the way numpy does.</summary>
public static class NumpyMath
{
    /// <summary>np.linspace(start, stop, num): i × step + start, with the last value exactly stop.</summary>
    public static double[] Linspace(double start, double stop, int num)
    {
        var values = new double[num];
        if (num == 0)
            return values;
        var step = num > 1 ? (stop - start) / (num - 1) : 0.0;
        for (var i = 0; i < num; i++)
            values[i] = i * step + start;
        if (num > 1)
            values[^1] = stop;
        return values;
    }

    /// <summary>np.geomspace(start, stop, num): powers of ten spaced evenly between the logs, with exact endpoints.</summary>
    public static double[] Geomspace(double start, double stop, int num)
    {
        var values = Linspace(Math.Log10(start), Math.Log10(stop), num).Select(exponent => Math.Pow(10.0, exponent)).ToArray();
        if (num > 0)
            values[0] = start;
        if (num > 1)
            values[^1] = stop;
        return values;
    }

    /// <summary>The mean of float32 values as numpy computes it: pairwise summation in float32.</summary>
    public static float Mean(IReadOnlyList<float> values) => values.Count == 0 ? float.NaN : PairwiseSum(values, 0, values.Count) / values.Count;

    private static float PairwiseSum(IReadOnlyList<float> a, int start, int n)
    {
        if (n < 8)
        {
            var total = 0f;
            for (var i = 0; i < n; i++)
                total += a[start + i];
            return total;
        }
        if (n <= 128)
        {
            var r = new float[8];
            for (var j = 0; j < 8; j++)
                r[j] = a[start + j];
            var i = 8;
            for (; i < n - n % 8; i += 8)
            {
                for (var j = 0; j < 8; j++)
                    r[j] += a[start + i + j];
            }
            var result = ((r[0] + r[1]) + (r[2] + r[3])) + ((r[4] + r[5]) + (r[6] + r[7]));
            for (; i < n; i++)
                result += a[start + i];
            return result;
        }
        var half = n / 2;
        half -= half % 8;
        return PairwiseSum(a, start, half) + PairwiseSum(a, start + half, n - half);
    }

    /// <summary>np.median of float32 values: the middle one, or the mean of the middle two.</summary>
    public static float Median(IReadOnlyList<float> values)
    {
        if (values.Count == 0)
            return float.NaN;
        var sorted = values.Order().ToArray();
        var middle = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2f;
    }
}
