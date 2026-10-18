using Draughtsman.Server.Configuration;

namespace Draughtsman.Server.Email;

public static class EmailRegistration
{
    /// <summary>
    /// Registers the live email settings (draughtsman.yaml layered with what an administrator saved at Admin, Email) and the one sender every
    /// feature uses. The sender looks at the current settings on each send, so a save applies with no restart.
    /// </summary>
    public static IServiceCollection AddEmail(this IServiceCollection services, DraughtsmanConfig config, string dataFolder)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(config);

        services.AddSingleton<EmailSettingsStore>();
        services.AddSingleton(provider => new EmailSettingsService(
            config,
            dataFolder,
            provider.GetRequiredService<EmailSettingsStore>(),
            (provider.GetService<ILoggerFactory>() ?? Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance).CreateLogger("Draughtsman.Server.Email.Settings")));
        services.AddSingleton<IEmailSender>(provider => new LiveEmailSender(
            provider.GetRequiredService<EmailSettingsService>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ILoggerFactory>()));
        return services;
    }
}

/// <summary>
/// The sender every feature is given. Chooses the real one (SMTP, or the development file one) from <see cref="EmailSettingsService.Current"/> on
/// each send, so settings saved a moment ago are used by the next message and nothing is captured at startup.
/// </summary>
public sealed class LiveEmailSender(EmailSettingsService settings, TimeProvider clock, ILoggerFactory loggers) : IEmailSender
{
    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        return For(settings.Current, clock, loggers).SendAsync(message, cancellationToken);
    }

    internal static IEmailSender For(EmailSettings current, TimeProvider clock, ILoggerFactory loggers)
    {
        if (!current.Configured)
        {
            return new UnconfiguredEmailSender(current);
        }

        return current.Provider == EmailConfig.ProviderSmtp
            ? new SmtpEmailSender(current, loggers.CreateLogger<SmtpEmailSender>())
            : new FileEmailSender(current, clock, loggers.CreateLogger<FileEmailSender>());
    }
}
