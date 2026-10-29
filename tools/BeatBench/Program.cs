using System.Globalization;
using BeatBench;
using AmbiWin.Core.Audio;

// BeatBench: score AmbiWin's beat detector against hand-tapped beat labels.
//
//   BeatBench <file.wav | folder> [--sensitivity 0.5] [--tolerance-ms 50] [--min-f1 0.0] [--csv onsets.csv]
//
// For each clip.wav, labels are read from clip.txt beside it (one beat time in seconds per line).
// With --min-f1, the exit code is 1 when the mean F1 is below the threshold (used by CI as a regression gate).

var options = ParseArgs(args);
if (options is null)
{
    Console.Error.WriteLine("usage: BeatBench <file.wav | folder> [--sensitivity 0.5] [--tolerance-ms 50] [--min-f1 0.0] [--csv onsets.csv]");
    return 2;
}

var clips = Directory.Exists(options.Path)
    ? Directory.GetFiles(options.Path, "*.wav").OrderBy(p => p, StringComparer.Ordinal).ToList()
    : [options.Path];

if (clips.Count == 0)
{
    Console.Error.WriteLine($"No .wav files in {options.Path}.");
    return 2;
}

var detectorOptions = new BeatDetectorOptions().WithSensitivity(options.Sensitivity);
Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
    $"Sensitivity {options.Sensitivity:0.00} (k = {detectorOptions.ThresholdK:0.00}), tolerance ±{options.ToleranceMs} ms"));
Console.WriteLine($"{"Clip",-32} {"Onsets",7} {"Beats",6} {"Prec",6} {"Recall",7} {"F1",6}");

var f1Scores = new List<double>();
using var csv = options.CsvPath is null ? null : new StreamWriter(options.CsvPath);
csv?.WriteLine("clip,time_s");

foreach (string clip in clips)
{
    var (mono, sampleRate) = WavReader.ReadMono(clip);
    var detector = new BeatDetector(sampleRate, detectorOptions);
    var onsets = new List<double>();
    detector.Onset += t => onsets.Add(t.TotalSeconds);
    detector.Process(mono);

    foreach (double t in onsets)
        csv?.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{Path.GetFileName(clip)},{t:0.000}"));

    string name = Path.GetFileName(clip);
    if (name.Length > 32) name = name[..29] + "...";
    string labelPath = Path.ChangeExtension(clip, ".txt");

    if (File.Exists(labelPath))
    {
        var score = Scoring.Match(onsets, Scoring.ReadLabels(labelPath), options.ToleranceMs / 1000.0);
        f1Scores.Add(score.F1);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{name,-32} {score.Detected,7} {score.Labeled,6} {score.Precision,6:0.00} {score.Recall,7:0.00} {score.F1,6:0.00}"));
    }
    else
    {
        Console.WriteLine($"{name,-32} {onsets.Count,7} {"-",6} {"-",6} {"-",7} {"-",6}");
    }
}

if (f1Scores.Count == 0)
    return 0;

double mean = f1Scores.Average();
Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Mean F1 over {f1Scores.Count} labeled clip(s): {mean:0.000}"));
if (mean < options.MinF1)
{
    Console.Error.WriteLine(string.Create(CultureInfo.InvariantCulture, $"FAIL: mean F1 {mean:0.000} is below --min-f1 {options.MinF1:0.000}"));
    return 1;
}
return 0;

static Options? ParseArgs(string[] args)
{
    if (args.Length == 0) return null;
    var o = new Options { Path = args[0] };
    for (int i = 1; i < args.Length; i++)
    {
        string flag = args[i];
        if (i + 1 >= args.Length) return null;
        string value = args[++i];
        switch (flag)
        {
            case "--sensitivity": o.Sensitivity = float.Parse(value, CultureInfo.InvariantCulture); break;
            case "--tolerance-ms": o.ToleranceMs = int.Parse(value, CultureInfo.InvariantCulture); break;
            case "--min-f1": o.MinF1 = double.Parse(value, CultureInfo.InvariantCulture); break;
            case "--csv": o.CsvPath = value; break;
            default: return null;
        }
    }
    return o;
}

internal sealed class Options
{
    public required string Path { get; init; }
    public float Sensitivity { get; set; } = 0.5f;
    public int ToleranceMs { get; set; } = 50;
    public double MinF1 { get; set; }
    public string? CsvPath { get; set; }
}
