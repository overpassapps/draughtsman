using Draughtsman.Server.Ai;
using Draughtsman.Server.Configuration;
using Draughtsman.Server.Email;
using Draughtsman.Server.KeyRing;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Draughtsman.Server.Tests;

/// <summary>
/// The startup of the Data Protection part of the host without the rest of it: the key ring setup (the pinned application name, the legacy
/// reader, the record of where the program ran from), the two settings stores and the startup re-protection, wired as <c>Program.cs</c> wires
/// them, over a data folder and a content root the test chooses. A host needs the content root to be a real folder; this does not, so a
/// test can stand in for a program installed anywhere.
/// </summary>
internal sealed class StoredSecretsRig : IDisposable
{
    private readonly ServiceProvider _services;

    private StoredSecretsRig(ServiceProvider services, KeyRingStatus status, KeyRingTestSupport.CapturingLogger log, IReadOnlyList<StoredSecretState> secrets)
    {
        _services = services;
        Status = status;
        Log = log;
        Secrets = secrets;
    }

    public KeyRingStatus Status { get; }

    public KeyRingTestSupport.CapturingLogger Log { get; }

    /// <summary>What the startup re-protection reported for each secret that is stored.</summary>
    public IReadOnlyList<StoredSecretState> Secrets { get; }

    public AiSettingsStore Ai => _services.GetRequiredService<AiSettingsStore>();

    public EmailSettingsStore Email => _services.GetRequiredService<EmailSettingsStore>();

    public IDataProtectionProvider Protection => _services.GetRequiredService<IDataProtectionProvider>();

    public StoredSecretState Secret(string name)
    {
        return Assert.Single(Secrets, s => s.Name == name);
    }

    public static StoredSecretsRig Start(
        string dataFolder,
        string contentRoot,
        KeyRingConfig? config = null,
        Func<string, string?>? environment = null,
        KeyRingPlatform platform = KeyRingPlatform.Other,
        Func<string, IKeyEncryptionKeySource>? keychain = null,
        Func<string?>? dpapiProbe = null,
        TimeProvider? clock = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var log = new KeyRingTestSupport.CapturingLogger();
        KeyRingStatus status = KeyRingSetup.Configure(services, dataFolder, config ?? new KeyRingConfig { Protection = KeyRingConfig.ProtectionNone }, contentRoot, log, environment ?? (_ => null), platform, keychain, dpapiProbe);
        services.AddSingleton(new DataFolder(dataFolder));
        services.AddSingleton<AiSettingsStore>();
        services.AddSingleton<EmailSettingsStore>();
        services.AddSingleton(sp => new StoredSecretsMigration(sp.GetRequiredService<DataFolder>(), sp.GetRequiredService<AiSettingsStore>(), sp.GetRequiredService<EmailSettingsStore>(), sp.GetRequiredService<KeyRingStatus>(), clock));
        ServiceProvider provider = services.BuildServiceProvider();
        IReadOnlyList<StoredSecretState> secrets = provider.GetRequiredService<StoredSecretsMigration>().Run(log);
        return new StoredSecretsRig(provider, status, log, secrets);
    }

    public void Dispose()
    {
        _services.Dispose();
    }
}
