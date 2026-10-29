namespace BeatBench;

public readonly record struct Score(int Detected, int Labeled, int Hits)
{
    public double Precision => Detected == 0 ? 0 : (double)Hits / Detected;
    public double Recall => Labeled == 0 ? 0 : (double)Hits / Labeled;
    public double F1 => Precision + Recall == 0 ? 0 : 2 * Precision * Recall / (Precision + Recall);
}

public static class Scoring
{
    /// <summary>
    /// Matches each detected onset to at most one labeled beat within the tolerance (greedy, in time order).
    /// Both lists must be sorted ascending, in seconds.
    /// </summary>
    public static Score Match(IReadOnlyList<double> detected, IReadOnlyList<double> labels, double toleranceSeconds)
    {
        int hits = 0, j = 0;
        foreach (double d in detected)
        {
            while (j < labels.Count && labels[j] < d - toleranceSeconds) j++;
            if (j < labels.Count && Math.Abs(labels[j] - d) <= toleranceSeconds)
            {
                hits++;
                j++;
            }
        }
        return new Score(detected.Count, labels.Count, hits);
    }

    /// <summary>
    /// Reads beat labels: one time in seconds per line (Audacity label exports work too: the first column is used).
    /// Blank lines and lines starting with # are ignored.
    /// </summary>
    public static List<double> ReadLabels(string path)
    {
        var times = new List<double>();
        foreach (string raw in File.ReadLines(path))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            string first = line.Split(['\t', ' ', ','], StringSplitOptions.RemoveEmptyEntries)[0];
            times.Add(double.Parse(first, System.Globalization.CultureInfo.InvariantCulture));
        }
        times.Sort();
        return times;
    }
}
