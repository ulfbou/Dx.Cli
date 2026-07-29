using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Dx.Cli.Commands;
using Dx.Cli.Infrastructure;
using Dx.Core;
using FluentAssertions;
using Xunit;

namespace Dx.Cli.Tests;

[CollectionDefinition("PrototypeGateSerial", DisableParallelization = true)]
public class PrototypeGateSerialCollection { }

[Collection("PrototypeGateSerial")]
public class PrototypeGateTests
{
    private static int FeatureUnavailableCode => DxException.ExitCode(DxError.FeatureUnavailable);
    private static string ExpectedMessage(string id) => $"error: command '{id}' is unavailable in the prototype release";

    private static int CountOccurrences(string hay, string needle)
    {
        if (string.IsNullOrEmpty(hay) || string.IsNullOrEmpty(needle)) return 0;
        int count = 0, idx = 0;
        while ((idx = hay.IndexOf(needle, idx, StringComparison.Ordinal)) != -1)
        {
            count++; idx += needle.Length;
        }
        return count;
    }

    private async Task<(int rc, string stderr)> RunHandlerCaptureAsync<TCmd, TSettings>(TCmd cmd, TSettings settings, string id)
        where TCmd : class
    {
        var tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tmp);
        var cwd = Directory.GetCurrentDirectory();
        var oldErr = Console.Error;
        try
        {
            Directory.SetCurrentDirectory(tmp);
            var sw = new StringWriter();
            Console.SetError(sw);

            dynamic dcmd = cmd;
            dynamic dsettings = settings;
            Task<int> t = dcmd.ExecuteAsync(null!, dsettings);
            var rc = await t;
            var stderr = sw.ToString();
            return (rc, stderr);
        }
        finally
        {
            Console.SetError(oldErr);
            Directory.SetCurrentDirectory(cwd);
            try { Directory.Delete(tmp, true); } catch { }
        }
    }

    [Fact]
    public async Task Handler_Run_IsBlocked()
    {
        var cmd = new RunCommand();
        var settings = new RunSettings { Command = "echo hi" };
        var (rc, stderr) = await RunHandlerCaptureAsync(cmd, settings, "run");
        rc.Should().Be(FeatureUnavailableCode);
        CountOccurrences(stderr, ExpectedMessage("run")).Should().Be(1);
    }

    [Fact]
    public async Task Handler_Eval_IsBlocked()
    {
        var cmd = new EvalCommand();
        var settings = new EvalSettings { SnapA = "T0000", SnapB = "T0001", Command = "echo x" };
        var (rc, stderr) = await RunHandlerCaptureAsync(cmd, settings, "eval");
        rc.Should().Be(FeatureUnavailableCode);
        CountOccurrences(stderr, ExpectedMessage("eval")).Should().Be(1);
    }

    [Fact]
    public async Task Handler_SessionNew_IsBlocked()
    {
        var cmd = new SessionNewCommand();
        var settings = new SessionNewSettings { Id = "s1" };
        var (rc, stderr) = await RunHandlerCaptureAsync(cmd, settings, "session:new");
        rc.Should().Be(FeatureUnavailableCode);
        CountOccurrences(stderr, ExpectedMessage("session:new")).Should().Be(1);
    }

    [Fact]
    public async Task Handler_SessionClose_IsBlocked()
    {
        var cmd = new SessionCloseCommand();
        var settings = new SessionCloseSettings { Id = "s1" };
        var (rc, stderr) = await RunHandlerCaptureAsync(cmd, settings, "session:close");
        rc.Should().Be(FeatureUnavailableCode);
        CountOccurrences(stderr, ExpectedMessage("session:close")).Should().Be(1);
    }

    [Fact]
    public async Task Handler_ConfigGet_IsBlocked()
    {
        var cmd = new ConfigGetCommand();
        var settings = new ConfigGetSettings { Key = "conflict.on_base_mismatch" };
        var (rc, stderr) = await RunHandlerCaptureAsync(cmd, settings, "config");
        rc.Should().Be(FeatureUnavailableCode);
        CountOccurrences(stderr, ExpectedMessage("config")).Should().Be(1);
    }

    [Fact]
    public async Task Handler_ConfigSet_IsBlocked()
    {
        var cmd = new ConfigSetCommand();
        var settings = new ConfigSetSettings { Key = "conflict.on_base_mismatch", Value = "warn" };
        var (rc, stderr) = await RunHandlerCaptureAsync(cmd, settings, "config");
        rc.Should().Be(FeatureUnavailableCode);
        CountOccurrences(stderr, ExpectedMessage("config")).Should().Be(1);
    }

    [Fact]
    public async Task Handler_ConfigUnset_IsBlocked()
    {
        var cmd = new ConfigUnsetCommand();
        var settings = new ConfigUnsetSettings { Key = "conflict.on_base_mismatch" };
        var (rc, stderr) = await RunHandlerCaptureAsync(cmd, settings, "config");
        rc.Should().Be(FeatureUnavailableCode);
        CountOccurrences(stderr, ExpectedMessage("config")).Should().Be(1);
    }

    [Fact]
    public async Task Handler_ConfigList_IsBlocked()
    {
        var cmd = new ConfigListCommand();
        var settings = new ConfigListSettings();
        var (rc, stderr) = await RunHandlerCaptureAsync(cmd, settings, "config");
        rc.Should().Be(FeatureUnavailableCode);
        CountOccurrences(stderr, ExpectedMessage("config")).Should().Be(1);
    }

    [Fact]
    public async Task Handler_ConfigShowEffective_IsBlocked()
    {
        var cmd = new ConfigShowEffectiveCommand();
        var settings = new ConfigShowEffectiveSettings();
        var (rc, stderr) = await RunHandlerCaptureAsync(cmd, settings, "config");
        rc.Should().Be(FeatureUnavailableCode);
        CountOccurrences(stderr, ExpectedMessage("config")).Should().Be(1);
    }

    [Fact]
    public void Policy_Allows_RequiredCommands()
    {
        // Allowed set — gate should return 0
        var allowed = new[] {
            "init","apply","pack","snap:list","snap:show","snap:diff","snap:checkout",
            "session:list","session:show","log","doctor"
        };
        foreach (var id in allowed)
        {
            PrototypeGate.Enforce(id).Should().Be(0, because: $"{id} must be allowed in prototype");
        }
    }

    // -------------------------
    // Real CLI process tests
    // -------------------------

    private string FindDxCliDll()
    {
        // Walk upwards from test assembly dir to repo root and check standard build output
        var dir = AppContext.BaseDirectory;
        var cur = new DirectoryInfo(dir);
        while (cur != null)
        {
            var candidate = Path.Combine(cur.FullName, "src", "Dx.Cli", "bin", "Debug", "net10.0", "dxs.dll");
            if (File.Exists(candidate)) return candidate;
            cur = cur.Parent;
        }
        return string.Empty;
    }

    private (int rc, string stdout, string stderr) RunProcess(string dllPath, string args, int timeoutMs = 10000)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"""{dllPath}"" {args}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var p = Process.Start(psi)!;
        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeoutMs))
        {
            try { p.Kill(); } catch { }
            throw new Exception("CLI process timed out");
        }

        Task.WaitAll(outTask, errTask);
        return (p.ExitCode, outTask.Result, errTask.Result);
    }

    [Fact]
    public void Cli_Run_IsBlocked_Process()
    {
        var dll = FindDxCliDll();
        if (string.IsNullOrEmpty(dll)) Assert.True(false, "dxs.dll not found in src/Dx.Cli/bin/Debug/net10.0. Run a build first.");
        var (rc, stdout, stderr) = RunProcess(dll, "run echo hello");
        rc.Should().Be(FeatureUnavailableCode);
        CountOccurrences(stderr, ExpectedMessage("run")).Should().Be(1);
        stdout.Should().BeNullOrEmpty();
        Directory.GetFiles(Directory.GetCurrentDirectory(), ".dx", SearchOption.TopDirectoryOnly).Should().BeEmpty();
    }

    [Fact]
    public void Cli_Eval_IsBlocked_Process()
    {
        var dll = FindDxCliDll();
        if (string.IsNullOrEmpty(dll)) Assert.True(false, "dxs.dll not found in src/Dx.Cli/bin/Debug/net10.0. Run a build first.");
        var (rc, stdout, stderr) = RunProcess(dll, "eval T0000 T0001 echo x");
        rc.Should().Be(FeatureUnavailableCode);
        CountOccurrences(stderr, ExpectedMessage("eval")).Should().Be(1);
        stdout.Should().BeNullOrEmpty();
        Directory.GetFiles(Directory.GetCurrentDirectory(), ".dx", SearchOption.TopDirectoryOnly).Should().BeEmpty();
    }

    [Fact]
    public void Cli_SessionNew_IsBlocked_Process()
    {
        var dll = FindDxCliDll();
        if (string.IsNullOrEmpty(dll)) Assert.True(false, "dxs.dll not found in src/Dx.Cli/bin/Debug/net10.0. Run a build first.");
        var (rc, stdout, stderr) = RunProcess(dll, "session new my-session");
        rc.Should().Be(FeatureUnavailableCode);
        CountOccurrences(stderr, ExpectedMessage("session:new")).Should().Be(1);
        stdout.Should().BeNullOrEmpty();
        Directory.GetFiles(Directory.GetCurrentDirectory(), ".dx", SearchOption.TopDirectoryOnly).Should().BeEmpty();
    }

    [Fact]
    public void Cli_SessionClose_IsBlocked_Process()
    {
        var dll = FindDxCliDll();
        if (string.IsNullOrEmpty(dll)) Assert.True(false, "dxs.dll not found in src/Dx.Cli/bin/Debug/net10.0. Run a build first.");
        var (rc, stdout, stderr) = RunProcess(dll, "session close my-session");
        rc.Should().Be(FeatureUnavailableCode);
        CountOccurrences(stderr, ExpectedMessage("session:close")).Should().Be(1);
        stdout.Should().BeNullOrEmpty();
        Directory.GetFiles(Directory.GetCurrentDirectory(), ".dx", SearchOption.TopDirectoryOnly).Should().BeEmpty();
    }

    [Fact]
    public void Cli_ConfigGet_IsBlocked_Process()
    {
        var dll = FindDxCliDll();
        if (string.IsNullOrEmpty(dll)) Assert.True(false, "dxs.dll not found in src/Dx.Cli/bin/Debug/net10.0. Run a build first.");
        var (rc, stdout, stderr) = RunProcess(dll, "config get conflict.on_base_mismatch");
        rc.Should().Be(FeatureUnavailableCode);
        CountOccurrences(stderr, ExpectedMessage("config")).Should().Be(1);
        stdout.Should().BeNullOrEmpty();
        Directory.GetFiles(Directory.GetCurrentDirectory(), ".dx", SearchOption.TopDirectoryOnly).Should().BeEmpty();
    }
}