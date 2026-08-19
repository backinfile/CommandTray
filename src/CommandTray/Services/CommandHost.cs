using System.Diagnostics;
using System.Text;

namespace CommandTray.Services;

internal static class CommandHost
{
    public const string ChildPidPrefix = "COMMANDTRAY_CHILD_PID:";

    public static int Run(string gateName, string encodedCommand)
    {
        try
        {
            var command = Encoding.UTF8.GetString(Convert.FromBase64String(encodedCommand));
            using var gate = EventWaitHandle.OpenExisting(gateName);
            if (!gate.WaitOne(TimeSpan.FromSeconds(30)))
            {
                Console.Error.WriteLine("CommandTray host timed out waiting for its ownership gate.");
                return 124;
            }

            var start = new ProcessStartInfo
            {
                FileName = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe",
                Arguments = ProcessSupervisor.BuildCmdArguments(command),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var process = new Process { StartInfo = start };
            if (!process.Start()) return 125;
            Console.Error.WriteLine($"{ChildPidPrefix}{process.Id}");
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) Console.Out.WriteLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) Console.Error.WriteLine(e.Data); };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            process.WaitForExit();
            process.WaitForExit(); // flush asynchronous output handlers
            return process.ExitCode;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 126;
        }
    }
}
