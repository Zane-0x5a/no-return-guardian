using System;
using System.Diagnostics;
using System.Linq;
using NoReturnGuardian.Core;

internal static class ObserveWarmCredits
{
    private static int Main()
    {
        var processes = Process.GetProcessesByName("tlou-ii")
            .Concat(Process.GetProcessesByName("tlou-ii-l")).ToArray();
        if (processes.Length != 1)
        {
            Console.Error.WriteLine("Expected exactly one game process.");
            return 2;
        }
        var process = processes[0];
        var result = new WindowsWarmRestoreCreditsProbe().IsT2RCreditsOpen(
            process.MainModule.FileName, new[] { process.Id });
        Console.WriteLine("{0:o} pid={1} success={2} open={3} code={4} message={5}",
            DateTime.UtcNow, process.Id, result.Success, result.Value, result.Code, result.Message);
        return result.Success && result.Value ? 0 : 1;
    }
}
