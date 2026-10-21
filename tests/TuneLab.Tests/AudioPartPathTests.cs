using System;
using System.IO;
using TuneLab.Data;
using Xunit;

namespace TuneLab.Tests;

// [EP-MIXER-18] 音频部件在工程文件里的路径格式。
//
// 这里钉住的是"保存后再读回来必须指向同一个文件"，以及两个已经踩过的坑：
//   · 工程位于盘符根目录时，旧编码会存出 `..Insts\…`（`..` 直接贴目录名）；
//   · 只做字符串前缀比较会把 `C:\Music2\x.wav` 当成在 `C:\Music` 里。
public class AudioPartPathTests
{
    [Fact]
    public void APathInsideTheProjectFolderRoundTrips()
    {
        string projectDir = Path.Combine("Y:", "Projects", "Song");
        string file = Path.Combine(projectDir, "audio", "vox.wav");

        string stored = ExperimentalPatchAudioPartPath.ToStored(file, projectDir);

        Assert.Equal(".." + Path.DirectorySeparatorChar + Path.Combine("audio", "vox.wav"), stored);
        Assert.Equal(file, ExperimentalPatchAudioPartPath.FromStored(stored, projectDir));
    }

    [Fact]
    public void AProjectAtADriveRootStillRoundTrips()
    {
        // 这是用户报的那个：工程就在盘符根目录，GetDirectoryName 返回 "Y:\"。
        string projectDir = "Y:" + Path.DirectorySeparatorChar;
        string file = Path.Combine(projectDir, "Insts", "Midi", "x.m4a");

        string stored = ExperimentalPatchAudioPartPath.ToStored(file, projectDir);

        Assert.Equal(".." + Path.DirectorySeparatorChar + Path.Combine("Insts", "Midi", "x.m4a"), stored);
        Assert.Equal(file, ExperimentalPatchAudioPartPath.FromStored(stored, projectDir));
    }

    [Fact]
    public void LegacyFilesWithTheMissingSeparatorStillResolve()
    {
        // 老版本存坏的形态：`..Insts\Midi\x.m4a`（`..` 后面没有分隔符）。
        // 已经存在用户磁盘上的工程不能要求重存，所以读取端必须也认。
        string legacy = ".." + Path.Combine("Insts", "Midi", "x.m4a");

        string resolved = ExperimentalPatchAudioPartPath.FromStored(legacy, "Y:" + Path.DirectorySeparatorChar);

        Assert.Equal(Path.Combine("Y:", "Insts", "Midi", "x.m4a"), resolved);
    }

    [Fact]
    public void APathOutsideTheProjectFolderStaysAbsolute()
    {
        string projectDir = Path.Combine("C:", "Music");
        string file = Path.Combine("D:", "Samples", "kick.wav");

        Assert.Equal(file, ExperimentalPatchAudioPartPath.ToStored(file, projectDir));
    }

    [Fact]
    public void ASiblingFolderWithTheSamePrefixIsNotMistakenForAChild()
    {
        // `C:\Music2\x.wav` 不在 `C:\Music` 里，只做 StartsWith 会误判。
        string projectDir = Path.Combine("C:", "Music");
        string file = Path.Combine("C:", "Music2", "x.wav");

        Assert.Equal(file, ExperimentalPatchAudioPartPath.ToStored(file, projectDir));
    }
}
