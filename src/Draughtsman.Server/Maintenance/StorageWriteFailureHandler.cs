using Draughtsman.Server.Hardening;
using Draughtsman.Storage;
using Microsoft.AspNetCore.Diagnostics;

namespace Draughtsman.Server.Maintenance;

/// <summary>
/// A database that refuses writes (read-only, full, a role that may not write) used to surface as an unexplained 500 on whichever request wrote first,
/// which for a restored or copied data folder was a correct password at sign-in while a wrong one was a plain 401. This answers it with a 503 problem
/// whose detail says the instance cannot save, and writes the specifics (what the database said, the account, the folder, the fix) to the log, where only
/// an administrator reads them: the response goes to whoever typed the password, so it names neither the folder nor the exception.
/// Every other exception is left to the framework.
/// </summary>
public sealed class StorageWriteFailureHandler(DataFolder dataFolder, ILogger<StorageWriteFailureHandler> logger) : IExceptionHandler
{
    public const string Title = "This instance cannot save changes";

    public const string Detail = "Your request was understood, but this instance cannot save: its database could not be written to, so nothing was changed. Ask the administrator to check the server log and docs/troubleshooting.md (the data folder's permissions and free disk space are the usual causes).";

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        if (!StorageFaults.IsCannotWrite(exception))
        {
            return false;
        }

        logger.LogError(
            "{Method} {Path} failed because the database cannot be written to ({Problem}). Check that the account running the server ({Account}) owns the data folder {Folder} and everything in it "
            + "(for example chown -R <uid> <folder>), and that the disk is neither full nor read-only; see docs/troubleshooting.md.",
            httpContext.Request.Method,
            httpContext.Request.Path.Value,
            exception.GetBaseException().Message,
            DataFolderWriteProbe.CurrentAccount(),
            dataFolder.Path);
        httpContext.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: Title, detail: Detail).ExecuteAsync(httpContext);
        return true;
    }
}
