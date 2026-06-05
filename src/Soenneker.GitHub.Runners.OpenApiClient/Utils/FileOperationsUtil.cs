using Microsoft.Extensions.Logging;
using Soenneker.Extensions.String;
using Soenneker.Extensions.ValueTask;
using Soenneker.Kiota.Util.Abstract;
using Soenneker.Git.Util.Abstract;
using Soenneker.GitHub.Runners.OpenApiClient.Utils.Abstract;
using Soenneker.Utils.Dotnet.Abstract;
using Soenneker.Utils.Environment;
using Soenneker.Utils.Directory.Abstract;
using Soenneker.Utils.File.Abstract;
using Soenneker.Utils.File.Download.Abstract;
using System.Collections.Generic;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Soenneker.OpenApi.Fixer;
using Soenneker.OpenApi.Fixer.Abstract;

namespace Soenneker.GitHub.Runners.OpenApiClient.Utils;

///<inheritdoc cref="IFileOperationsUtil"/>
public sealed class FileOperationsUtil : IFileOperationsUtil
{
    private readonly ILogger<FileOperationsUtil> _logger;
    private readonly IGitUtil _gitUtil;
    private readonly IDotnetUtil _dotnetUtil;
    private readonly IKiotaUtil _kiotaUtil;
    private readonly IFileDownloadUtil _fileDownloadUtil;
    private readonly IFileUtil _fileUtil;
    private readonly IOpenApiFixer _openApiFixer;
    private readonly IDirectoryUtil _directoryUtil;

    public FileOperationsUtil(ILogger<FileOperationsUtil> logger, IGitUtil gitUtil, IDotnetUtil dotnetUtil, IFileDownloadUtil fileDownloadUtil,
        IFileUtil fileUtil, IDirectoryUtil directoryUtil, IKiotaUtil kiotaUtil, IOpenApiFixer openApiFixer)
    {
        _logger = logger;
        _gitUtil = gitUtil;
        _dotnetUtil = dotnetUtil;
        _kiotaUtil = kiotaUtil;
        _fileDownloadUtil = fileDownloadUtil;
        _fileUtil = fileUtil;
        _openApiFixer = openApiFixer;
        _directoryUtil = directoryUtil;
    }

    public async ValueTask Process(CancellationToken cancellationToken = default)
    {
        string gitDirectory = await _gitUtil.CloneToTempDirectory($"https://github.com/soenneker/{Constants.Library.ToLowerInvariantFast()}",
            cancellationToken: cancellationToken);

        string targetFilePath = Path.Combine(gitDirectory, "api.github.com.json");

        await _fileUtil.DeleteIfExists(targetFilePath, cancellationToken: cancellationToken);

        string? filePath =
            await _fileDownloadUtil.Download(
                "https://raw.githubusercontent.com/github/rest-api-description/refs/heads/main/descriptions/api.github.com/api.github.com.json", targetFilePath,
                fileExtension: ".json", cancellationToken: cancellationToken);

        string fixedFilePath = Path.Combine(gitDirectory, "fixed.json");

        await _fileUtil.DeleteIfExists(fixedFilePath, cancellationToken: cancellationToken);

        await _openApiFixer.Fix(filePath, fixedFilePath, new OpenApiFixerOptions { Int32IdTransform = true }, cancellationToken);

        string srcDirectory = Path.Combine(gitDirectory, "src", Constants.Library);

        await DeleteAllExceptCsproj(srcDirectory, cancellationToken);

        await _kiotaUtil.EnsureInstalled(cancellationToken);

        await _kiotaUtil.Generate(fixedFilePath, "GitHubOpenApiClient", Constants.Library, gitDirectory, cancellationToken);

        await Restore(gitDirectory, cancellationToken);

        await BuildAndPush(gitDirectory, cancellationToken);
    }

    /// <summary>
    /// Deletes all except csproj.
    /// </summary>
    /// <param name="directoryPath">The directory path.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public async ValueTask DeleteAllExceptCsproj(string directoryPath, CancellationToken cancellationToken = default)
    {
        if (!(await _directoryUtil.Exists(directoryPath, cancellationToken)))
        {
            _logger.LogWarning("Directory does not exist: {DirectoryPath}", directoryPath);
            return;
        }

        try
        {
            // Delete all files except .csproj
            List<string> files = await _directoryUtil.GetFilesByExtension(directoryPath, "", true, cancellationToken);
            foreach (string file in files)
            {
                if (!file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        await _fileUtil.Delete(file, ignoreMissing: true, log: true, cancellationToken);
                        _logger.LogInformation("Deleted file: {FilePath}", file);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to delete file: {FilePath}", file);
                    }
                }
            }

            // Delete all empty subdirectories
            List<string> dirs = await _directoryUtil.GetAllDirectoriesRecursively(directoryPath, cancellationToken);
            foreach (string dir in dirs.OrderByDescending(d => d.Length)) // Sort by depth to delete from deepest first
            {
                try
                {
                    List<string> dirFiles = await _directoryUtil.GetFilesByExtension(dir, "", false, cancellationToken);
                    List<string> subDirs = await _directoryUtil.GetAllDirectories(dir, cancellationToken);
                    if (dirFiles.Count == 0 && subDirs.Count == 0)
                    {
                        await _directoryUtil.Delete(dir, cancellationToken);
                        _logger.LogInformation("Deleted empty directory: {DirectoryPath}", dir);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to delete directory: {DirectoryPath}", dir);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An error occurred while cleaning the directory: {DirectoryPath}", directoryPath);
        }
    }

    private async ValueTask Restore(string gitDirectory, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting Restore...");

        string projFilePath = Path.Combine(gitDirectory, "src", Constants.Library, $"{Constants.Library}.csproj");

        await _dotnetUtil.Restore(projFilePath, cancellationToken: cancellationToken);

        _logger.LogInformation("Ending Restore");
    }

    private async ValueTask BuildAndPush(string gitDirectory, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting BuildAndPush...");

        string projFilePath = Path.Combine(gitDirectory, "src", Constants.Library, $"{Constants.Library}.csproj");

        bool successful = await _dotnetUtil.Build(projFilePath, true, "Release", false, cancellationToken: cancellationToken);

        if (!successful)
        {
            _logger.LogError("Build was not successful, exiting...");
            return;
        }

        string gitHubToken = EnvironmentUtil.GetVariableStrict("GH__TOKEN");
        string name = EnvironmentUtil.GetVariableStrict("GIT__NAME");
        string email = EnvironmentUtil.GetVariableStrict("GIT__EMAIL");

        await _gitUtil.CommitAndPush(gitDirectory, "Automated update", gitHubToken, name, email, cancellationToken);
    }
}