using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

internal static class SaveCorpusAnalyzer
{
    private sealed class Diff
    {
        public long Different;
        public int First = -1;
        public int Last = -1;
        public int LongestEqualRun;
    }

    private static int Main(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine(
                "Usage: SaveCorpusAnalyzer <hideout> <current> <encounter> [encounter ...]");
            return 2;
        }

        byte[] hideout = File.ReadAllBytes(args[0]);
        byte[] current = File.ReadAllBytes(args[1]);
        List<byte[]> encounters = args.Skip(2).Select(File.ReadAllBytes).ToList();
        List<byte[]> all = new List<byte[]> { hideout, current };
        all.AddRange(encounters);
        if (all.Any(bytes => bytes.Length != hideout.Length))
        {
            Console.Error.WriteLine("All corpus files must have the same length.");
            return 3;
        }

        Console.WriteLine("length={0:N0} encounters={1}", hideout.Length, encounters.Count);
        Console.WriteLine(
            "entropy hideout={0:F6} current={1:F6} encounter-first={2:F6} encounter-last={3:F6}",
            Entropy(hideout),
            Entropy(current),
            Entropy(encounters.First()),
            Entropy(encounters.Last()));
        Console.WriteLine("header hideout={0}", Hex(hideout, 32));
        Console.WriteLine("header current={0}", Hex(current, 32));
        PrintDiff("hideout-current", hideout, current);
        PrintDiff("hideout-encounter-first", hideout, encounters.First());
        PrintDiff("hideout-encounter-last", hideout, encounters.Last());

        List<double> consecutive = new List<double>();
        for (int index = 1; index < encounters.Count; index++)
        {
            Diff diff = Compare(encounters[index - 1], encounters[index]);
            consecutive.Add(Percent(diff.Different, hideout.Length));
        }

        Console.WriteLine(
            "encounter-consecutive-diff min={0:F4}% avg={1:F4}% max={2:F4}%",
            consecutive.Min(),
            consecutive.Average(),
            consecutive.Max());

        long stableAcrossCorpus = 0;
        long hideoutCurrentEqual = 0;
        long candidateBytes = 0;
        List<Tuple<int, int>> candidateRuns = new List<Tuple<int, int>>();
        int runStart = -1;
        for (int offset = 0; offset < hideout.Length; offset++)
        {
            byte value = hideout[offset];
            bool allEqual = all.All(bytes => bytes[offset] == value);
            if (allEqual)
            {
                stableAcrossCorpus++;
            }

            bool currentMatches = current[offset] == value;
            if (currentMatches)
            {
                hideoutCurrentEqual++;
            }

            bool candidate = currentMatches
                && encounters.All(bytes => bytes[offset] != value);
            if (candidate)
            {
                candidateBytes++;
                if (runStart < 0)
                {
                    runStart = offset;
                }
            }
            else if (runStart >= 0)
            {
                candidateRuns.Add(Tuple.Create(runStart, offset - runStart));
                runStart = -1;
            }
        }

        if (runStart >= 0)
        {
            candidateRuns.Add(Tuple.Create(runStart, hideout.Length - runStart));
        }

        Console.WriteLine(
            "stable-all={0:N0} ({1:F4}%) hideout-current-equal={2:N0} ({3:F4}%)",
            stableAcrossCorpus,
            Percent(stableAcrossCorpus, hideout.Length),
            hideoutCurrentEqual,
            Percent(hideoutCurrentEqual, hideout.Length));
        Console.WriteLine(
            "candidate-bytes={0:N0} candidate-runs>=2={1}",
            candidateBytes,
            candidateRuns.Count(run => run.Item2 >= 2));
        foreach (Tuple<int, int> run in candidateRuns
            .Where(item => item.Item2 >= 2)
            .OrderByDescending(item => item.Item2)
            .ThenBy(item => item.Item1)
            .Take(20))
        {
            Console.WriteLine(
                "candidate-run offset=0x{0:X8} length={1} bytes={2}",
                run.Item1,
                run.Item2,
                Hex(hideout, run.Item1, Math.Min(16, run.Item2)));
        }

        return 0;
    }

    private static void PrintDiff(string label, byte[] first, byte[] second)
    {
        Diff diff = Compare(first, second);
        Console.WriteLine(
            "{0} different={1:N0} ({2:F4}%) first=0x{3:X8} last=0x{4:X8} longest-equal={5:N0}",
            label,
            diff.Different,
            Percent(diff.Different, first.Length),
            diff.First,
            diff.Last,
            diff.LongestEqualRun);
    }

    private static Diff Compare(byte[] first, byte[] second)
    {
        Diff result = new Diff();
        int equalRun = 0;
        for (int index = 0; index < first.Length; index++)
        {
            if (first[index] == second[index])
            {
                equalRun++;
                if (equalRun > result.LongestEqualRun)
                {
                    result.LongestEqualRun = equalRun;
                }
                continue;
            }

            equalRun = 0;
            result.Different++;
            if (result.First < 0)
            {
                result.First = index;
            }
            result.Last = index;
        }
        return result;
    }

    private static double Entropy(byte[] bytes)
    {
        long[] counts = new long[256];
        foreach (byte value in bytes)
        {
            counts[value]++;
        }

        double entropy = 0;
        foreach (long count in counts)
        {
            if (count == 0)
            {
                continue;
            }
            double probability = (double)count / bytes.Length;
            entropy -= probability * (Math.Log(probability) / Math.Log(2));
        }
        return entropy;
    }

    private static double Percent(long count, int length)
    {
        return length == 0 ? 0 : count * 100.0 / length;
    }

    private static string Hex(byte[] bytes, int count)
    {
        return Hex(bytes, 0, Math.Min(count, bytes.Length));
    }

    private static string Hex(byte[] bytes, int offset, int count)
    {
        return string.Join(
            " ",
            bytes.Skip(offset).Take(count).Select(value => value.ToString("X2")));
    }
}
