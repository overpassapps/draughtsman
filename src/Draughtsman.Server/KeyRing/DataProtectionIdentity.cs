namespace Draughtsman.Server.KeyRing;

/// <summary>
/// What Data Protection isolates this application's payloads by. ASP.NET Core defaults the application discriminator to the
/// host's content root, so every payload an earlier build wrote (the stored AI key, the saved SMTP password) was bound to the
/// folder the program ran from and became unreadable the moment the program moved. A constant makes a payload depend on the
/// key ring alone, which travels with the data folder.
/// <para>
/// Never change this value: every stored secret written under it would become unreadable, exactly as a move used to make them.
/// A secret written by a build that predates it is bound to a path instead and is read through <see cref="LegacySecretReader"/>,
/// then re-protected under this name (<see cref="StoredSecretsMigration"/>).
/// </para>
/// </summary>
internal static class DataProtectionIdentity
{
    public const string ApplicationName = "Draughtsman";
}
