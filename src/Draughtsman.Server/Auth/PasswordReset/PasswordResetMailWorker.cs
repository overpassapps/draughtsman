using Microsoft.Extensions.Hosting;

namespace Draughtsman.Server.Auth.PasswordReset;

/// <summary>Drains <see cref="PasswordResetMailQueue"/>: one request at a time, each in its own scope. A failure is logged (without the address) and the next request is taken; mail never stops the server.</summary>
public sealed class PasswordResetMailWorker(PasswordResetMailQueue queue, IServiceScopeFactory scopes, ILogger<PasswordResetMailWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (ResetMailRequest request in queue.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    using IServiceScope scope = scopes.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<PasswordResetMailer>().HandleAsync(request, stoppingToken);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    logger.LogWarning(error, "Handling a password-reset request failed");
                }
                finally
                {
                    queue.Completed();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }
}
