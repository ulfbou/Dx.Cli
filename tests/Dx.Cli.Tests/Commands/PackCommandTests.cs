using System.Diagnostics;
using System.Text;
using FluentAssertions;
using Xunit;

namespace Dx.Cli.Tests.Commands;

public class PackCommandTests
{
    [Fact]
    public async Task Pack_ExcludesOutputOutsideSource_AndIsCwdIndependent()
    {
        // Arrange: create temp directories
        var tmpRoot = Path.Combine(Path.GetTempPath(), "dxs-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpRoot);

        var sourceDir = Path.Combine(tmpRoot, "source");
        Directory.CreateDirectory(sourceDir);

        var file1 = Path.Combine(sourceDir, "a.txt");
        var file2 = Path.Combine(sourceDir, "b.txt");
        var file3 = Path.Combine(sourceDir, "sub", "c.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(file3)!);
        await File.WriteAllTextAsync(file1, "one");
        await File.WriteAllTextAsync(file2, "two");
        await File.WriteAllTextAsync(file3, "three");

        var outputFile = Path.GetFullPath(Path.Combine(tmpRoot, "out", "packed.dx"));
        Directory.CreateDirectory(Path.GetDirectoryName(outputFile)!);

        try
        {
            // Act: run pack from inside source with relative output that points outside
            var origCwd = Directory.GetCurrentDirectory();
            try
            {
                Directory.SetCurrentDirectory(sourceDir);

                var cmd = new Dx.Cli.Commands.PackCommand();
                var settings = new Dx.Cli.Commands.PackSettings
                {
                    Path = ".",
                    Out = "../out/packed.dx",
                    NoHeader = true,
                };

                var exit = await cmd.ExecuteAsync(null!, settings);
                exit.Should().Be(0);
            }
            finally
            {
                Directory.SetCurrentDirectory(origCwd);
            }

            // Assert: output file exists and contains three %%FILE blocks
            File.Exists(outputFile).Should().BeTrue();
            var content = await File.ReadAllTextAsync(outputFile, Encoding.UTF8);
            var fileBlocks = content.Split(new[] {"%%FILE"}, StringSplitOptions.RemoveEmptyEntries).Length;
            fileBlocks.Should().Be(3);
            content.Should().NotContain(Path.GetFileName(outputFile));
        }
        finally
        {
            try { Directory.Delete(tmpRoot, true); } catch { }
        }
    }
}
