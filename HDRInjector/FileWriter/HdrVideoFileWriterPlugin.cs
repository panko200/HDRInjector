using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Plugin.FileWriter;
using HDRInjector.Patches;
using YukkuriMovieMaker.Project;
using YukkuriMovieMaker.Plugin;

namespace HDRInjector.FileWriter;

/// <summary>
/// HDR10 / HEVC Main10 video output plugin.
/// Provides the HDR10 export settings UI used by the writer.
/// </summary>
public sealed class HdrVideoFileWriterPlugin : IVideoFileWriterPlugin, IPlugin
{
    public string Name => "HDR10 動画出力 (HEVC Main10 / BT.2020 PQ)";

    public VideoFileWriterOutputPath OutputPathMode => VideoFileWriterOutputPath.File;

    public string GetFileExtention() => ".mp4";

    public bool NeedDownloadResources() => false;

    public Task DownloadResources(ProgressMessage progress, CancellationToken token) => Task.CompletedTask;

    public IVideoFileWriter CreateVideoFileWriter(string path, VideoInfo videoInfo)
    {
        HdrExportRenderTargetPatch.BeginExport();
        try
        {
            return new HdrVideoFileWriter(path, videoInfo, HdrVideoWriterSettings.Current);
        }
        catch
        {
            HdrExportRenderTargetPatch.EndExport();
            throw;
        }
    }

    public UIElement GetVideoConfigView(string projectName, VideoInfo videoInfo, int length)
    {
        return HdrVideoWriterSettings.CreateView();
    }
}
