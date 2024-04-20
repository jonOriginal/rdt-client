using RdtClient.Service.Helpers;
using Serilog;

namespace RdtClient.Service.Services.Downloaders;

public class SymlinkDownloader(String uri, String destinationPath, String path) : IDownloader
{
    public event EventHandler<DownloadCompleteEventArgs>? DownloadComplete;
    public event EventHandler<DownloadProgressEventArgs>? DownloadProgress;

    private readonly CancellationTokenSource _cancellationToken = new();

    private readonly ILogger _logger = Log.ForContext<SymlinkDownloader>();

    private const Int32 MaxRetries = 10;
    
    private readonly List<String> _archiveExtensions =
    [
        ".zip",
        ".rar",
        ".tar"
    ];

    private async Task<String?> SearchForFile(ICollection<String> potentialFilePaths, String fileName)
    {   
        FileInfo? info = null;
        for (var retryCount = 0; retryCount < MaxRetries; retryCount++)
        {
            DownloadProgress?.Invoke(this,
                                     new DownloadProgressEventArgs
                                     {
                                         BytesDone = retryCount,
                                         BytesTotal = 10,
                                         Speed = 1
                                     });

            _logger.Debug($"Searching for {fileName} (attempt #{retryCount})...");

            foreach (var potentialFilePath in potentialFilePaths)
            {
                var potentialFilePathWithFileName = Path.Combine(potentialFilePath, fileName);

                _logger.Debug($"Searching {potentialFilePathWithFileName}...");

                if (File.Exists(potentialFilePathWithFileName))
                {
                    info = new FileInfo(potentialFilePathWithFileName);
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
    private async Task<String?> SearchForUnarchived(String SearchPath)
    {
        for (var retryCount = 0; retryCount < MaxRetries; retryCount++)
        {
            DownloadProgress?.Invoke(this,
                                     new DownloadProgressEventArgs
                                     {
                                         BytesDone = retryCount,
                                         BytesTotal = 10,
                                         Speed = 1
                                     });

            _logger.Debug($"Searching for unarchived (attempt #{retryCount})...");

            if (Directory.Exists(SearchPath))
            {
                var info = new DirectoryInfo(SearchPath);
                return info.FullName;
            }
            else
            {
                await Task.Delay(1000 * retryCount);
            }
        }
        
        return null;
    }

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
            
            if (_archiveExtensions.Any(m => fileExtension == m))
            {
                file = await SearchForUnarchived(searchPath);
            }
            else
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

                potentialFilePaths.Add(Path.Combine(rcloneMountPath, fileName));
                potentialFilePaths.Add(Path.Combine(rcloneMountPath, fileNameWithoutExtension));
            
                _logger.Debug($"Potential file paths: {String.Join(", ", potentialFilePaths)}");
                
                file = await SearchForFile(potentialFilePaths, fileName);
            }

            if (file == null)
            {
                _logger.Debug($"Unable to find file in rclone mount. Folders available in {rcloneMountPath}: ");
                try
                {
                    var allFolders = FileHelper.GetDirectoryContents(rcloneMountPath);
                    _logger.Debug(allFolders);
                }
                catch(Exception ex)
                {
                    _logger.Error(ex.Message);
                }
                
                throw new Exception("Could not find file from rclone mount!");
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
            DownloadComplete?.Invoke(this, new DownloadCompleteEventArgs
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

    private Boolean TryCreateSymbolicLink(String sourcePath, String symlinkPath)
    {
        try
        {
            File.CreateSymbolicLink(symlinkPath, sourcePath);

            if (File.Exists(symlinkPath) || Directory.Exists(symlinkPath)) // Double-check that the link was created
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
}
