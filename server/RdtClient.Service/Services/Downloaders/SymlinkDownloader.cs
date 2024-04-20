using RdtClient.Service.Helpers;
using Serilog;

namespace RdtClient.Service.Services.Downloaders;

public class SymlinkDownloader(String uri, String destinationPath, String path) : IDownloader
{
    private const Int32 MaxRetries = 10;

    private readonly CancellationTokenSource _cancellationToken = new();

    private readonly ILogger _logger = Log.ForContext<SymlinkDownloader>();
    public event EventHandler<DownloadCompleteEventArgs>? DownloadComplete;
    public event EventHandler<DownloadProgressEventArgs>? DownloadProgress;

    public async Task<String> Download()
    {
        _logger.Debug($"Starting symlink resolving of {uri}, writing to path: {path}");

        try
        {
            var filePath = new FileInfo(path);
            var rcloneMountPath = Settings.Get.DownloadClient.RcloneMountPath.TrimEnd(['\\', '/']);
            var fileName = filePath.Name;

            var fileExtension = filePath.Extension;
            var fileNameWithoutExtension = fileName.Replace(fileExtension, "");

            var pathWithoutFileName = path.Replace(fileName, "").TrimEnd(['\\', '/']);
            var searchPath = Path.Combine(rcloneMountPath, pathWithoutFileName);

            _logger.Debug($"Extension: {fileExtension}");
            _logger.Debug($"Filename: {fileName}");
            _logger.Debug($"Filename without extension: {fileNameWithoutExtension}");
            _logger.Debug($"Search path: {searchPath}");
            _logger.Debug($"Rclone mount path: {rcloneMountPath}");

            DownloadProgress?.Invoke(this,
                                     new DownloadProgressEventArgs
                                     {
                                         BytesDone = 0,
                                         BytesTotal = 0,
                                         Speed = 0
                                     });

            String? file = null;
            
            var potentialFiles = new List<String>();
            potentialFiles.Add(Path.Combine(rcloneMountPath, fileNameWithoutExtension, fileName));
            potentialFiles.Add(Path.Combine(rcloneMountPath, fileName, fileName));
            potentialFiles.Add(Path.Combine(rcloneMountPath, fileNameWithoutExtension));
            potentialFiles.Add(Path.Combine(rcloneMountPath, fileName));

            _logger.Debug($"Potential file paths: {String.Join(", ", potentialFiles)}");

            file = await SearchForFile(potentialFiles);

            if (file == null)
            {
                _logger.Debug($"Unable to find file in rclone mount. Folders available in {searchPath}: ");
                DebugListFolders(searchPath);

                throw new Exception($"Could not find file {fileName} from rclone mount!");
            }

            _logger.Debug($"Found {file}");

            var result = TryCreateSymbolicLink(file, destinationPath);

            if (!result)
            {
                throw new Exception("Could not find file from rclone mount!");
            }

            DownloadComplete?.Invoke(this, new DownloadCompleteEventArgs());

            return file;
        }
        catch (Exception ex)
        {
            DownloadComplete?.Invoke(this,
                                     new DownloadCompleteEventArgs
                                     {
                                         Error = ex.Message
                                     });

            throw;
        }
    }

    public Task Cancel()
    {
        _cancellationToken.Cancel(false);

        return Task.CompletedTask;
    }

    public Task Pause()
    {
        return Task.CompletedTask;
    }

    public Task Resume()
    {
        return Task.CompletedTask;
    }

    private async Task<String?> SearchForFile(ICollection<String> potentialFilePaths)
    {
        FileSystemInfo? info = null;

        for (var retryCount = 0; retryCount < MaxRetries; retryCount++)
        {
            DownloadProgress?.Invoke(this,
                                     new DownloadProgressEventArgs
                                     {
                                         BytesDone = retryCount,
                                         BytesTotal = 10,
                                         Speed = 1
                                     });

            var fileName = Path.GetFileName(path);
            _logger.Debug($"Searching for {fileName} (attempt #{retryCount})...");

            foreach (var potentialFilePath in potentialFilePaths)
            {

                _logger.Debug($"Searching filem{potentialFilePath}...");

                if (File.Exists(potentialFilePath))
                {
                    info = new FileInfo(potentialFilePath);

                    break;
                }
            }
            
            foreach (var potentialFilePath in potentialFilePaths)
            {

                _logger.Debug($"Searching directory {potentialFilePath}...");

                if (Directory.Exists(potentialFilePath))
                {
                    info = new DirectoryInfo(potentialFilePath);

                    break;
                }
            }

            if (info == null)
            {
                await Task.Delay(1000 * retryCount);
            }
            else
            {
                break;
            }
        }
        return info?.FullName;
    }

    private void DebugListFolders(String parentDir)
    {
        try
        {
            var allFolders = FileHelper.GetDirectoryContents(parentDir);
            _logger.Debug(allFolders);
        }
        catch (Exception ex)
        {
            _logger.Error(ex.Message);
        }
    }

    private ICollection<String> GetAdditionalFilePaths(String searchPath, String rcloneMountPath)
    {
        var potentialFilePaths = new List<String>();

        var directoryInfo = new DirectoryInfo(searchPath);

        while (directoryInfo.Parent != null)
        {
            potentialFilePaths.Add(directoryInfo.FullName);
            directoryInfo = directoryInfo.Parent;

            if (directoryInfo.FullName.TrimEnd(['\\', '/']) == rcloneMountPath)
            {
                break;
            }
        }

        return potentialFilePaths;
    }

    private Boolean TryCreateSymLinkDirectory(String sourcePath, String symlinkPath)
    {
        try
        {
            var files = Directory.GetFiles(sourcePath, "*", SearchOption.AllDirectories);
            
            foreach (var file in files)
            {
                File.CreateSymbolicLink(symlinkPath, file);
                
                if (!File.Exists(symlinkPath))
                {
                    _logger.Error($"Failed to create symbolic link from {file} to {symlinkPath}");
                    return false;
                }
            }
            
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error($"Error creating symbolic link from {sourcePath} to {symlinkPath}: {ex.Message}");

            return false;
        }
    }
    
    private Boolean TryCreateSymLinkFile(String sourcePath, String symlinkPath)
    {
        try
        {
            File.CreateSymbolicLink(symlinkPath, sourcePath);

            if (File.Exists(symlinkPath))
            {
                _logger.Information($"Created symbolic link from {sourcePath} to {symlinkPath}");

                return true;
            }

            _logger.Error($"Failed to create symbolic link from {sourcePath} to {symlinkPath}");

            return false;
        }
        catch (Exception ex)
        {
            _logger.Error($"Error creating symbolic link from {sourcePath} to {symlinkPath}: {ex.Message}");

            return false;
        }
    }

    private Boolean TryCreateSymbolicLink(String sourcePath, String symlinkPath)
    {
        if (File.Exists(sourcePath))
        {
            return TryCreateSymLinkFile(sourcePath, symlinkPath);
        }

        if (Directory.Exists(sourcePath))
        {
            return TryCreateSymLinkDirectory(sourcePath, symlinkPath);
        }

        _logger.Error($"Source path {sourcePath} does not exist");

        return false;
    }
}
