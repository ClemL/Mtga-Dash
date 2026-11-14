using System.Diagnostics;

namespace MtgaDash.Tests;

/// <summary>
/// Runs a script in Node for the tests of the page's own JavaScript. Node is not a project dependency:
/// without it on the PATH these tests report Inconclusive instead of failing.
/// </summary>
internal static class Node
{
    public static string Run(string script)
    {
        var file = Path.Combine(Path.GetTempPath(), $"mtgadash-{Guid.NewGuid():N}.cjs");
        File.WriteAllText(file, script);
        try
        {
            var psi = new ProcessStartInfo("node", $"\"{file}\"") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            Process p;
            try
            {
                p = Process.Start(psi)!;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                Assert.Inconclusive("node is not on the PATH; the page's JavaScript tests were skipped");
                throw;
            }
            using (p)
            {
                var stdout = p.StandardOutput.ReadToEnd();
                var stderr = p.StandardError.ReadToEnd();
                p.WaitForExit();
                if (p.ExitCode != 0) Assert.Fail($"node exited {p.ExitCode}:\n{stderr}\n{stdout}");
                return stdout.Trim();
            }
        }
        finally
        {
            File.Delete(file);
        }
    }
}
