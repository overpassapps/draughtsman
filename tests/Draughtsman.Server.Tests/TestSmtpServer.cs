using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Draughtsman.Server.Tests;

/// <summary>A message the test server accepted: the envelope and the raw DATA, and whether it arrived over TLS.</summary>
public sealed record ReceivedMail(string From, IReadOnlyList<string> To, string Data, bool OverTls)
{
    private static string DecodeQuotedPrintable(string text)
    {
        string joined = text.Replace("=\n", string.Empty, StringComparison.Ordinal);
        var bytes = new List<byte>();
        for (int i = 0; i < joined.Length; i++)
        {
            if (joined[i] == '=' && i + 2 < joined.Length + 0 && Uri.IsHexDigit(joined[i + 1]) && Uri.IsHexDigit(joined[i + 2]))
            {
                bytes.Add(Convert.ToByte(joined.Substring(i + 1, 2), 16));
                i += 2;
            }
            else
            {
                bytes.AddRange(Encoding.UTF8.GetBytes(joined[i].ToString()));
            }
        }

        return Encoding.UTF8.GetString([.. bytes]);
    }

    /// <summary>The message text after the headers, decoded from base64 when the client encoded it (the framework does for UTF-8 bodies).</summary>
    public string Body
    {
        get
        {
            int blank = Data.IndexOf("\n\n", StringComparison.Ordinal);
            string body = blank < 0 ? string.Empty : Data[(blank + 2)..].Trim();
            if (Data.Contains("Content-Transfer-Encoding: base64", StringComparison.OrdinalIgnoreCase))
            {
                return Encoding.UTF8.GetString(Convert.FromBase64String(body.Replace("\n", string.Empty, StringComparison.Ordinal)));
            }

            return Data.Contains("Content-Transfer-Encoding: quoted-printable", StringComparison.OrdinalIgnoreCase) ? DecodeQuotedPrintable(body) : body;
        }
    }
}

/// <summary>
/// A small in-process SMTP server for the sender tests: EHLO, optional STARTTLS (a self-signed certificate made for the run), AUTH
/// LOGIN against one password, MAIL, RCPT, DATA and QUIT, recording what a client does and in what order. Not a mail server: just
/// enough of the protocol for the framework's <c>SmtpClient</c> to talk to, with switches for each way the real thing can fail.
/// </summary>
public sealed class TestSmtpServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly List<string> _log = [];
    private readonly List<ReceivedMail> _mail = [];
    private Task? _accepting;

    public TestSmtpServer(bool offerStartTls = false, string? password = null, bool silent = false)
    {
        OfferStartTls = offerStartTls;
        Password = password;
        Silent = silent;
        if (offerStartTls)
        {
            using RSA key = RSA.Create(2048);
            var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using X509Certificate2 ephemeral = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
            Certificate = X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pfx), null);
        }
    }

    public bool OfferStartTls { get; }

    public string? Password { get; }

    /// <summary>Accept the connection and say nothing: for the timeout.</summary>
    public bool Silent { get; }

    /// <summary>Refuse every RCPT with this reply (for example <c>550 5.1.1 No such user</c>).</summary>
    public string? RecipientReply { get; set; }

    /// <summary>Put the password the client sent into the 535 text, the way a careless server might.</summary>
    public bool EchoPasswordInRejection { get; set; }

    public X509Certificate2? Certificate { get; }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public IReadOnlyList<ReceivedMail> Mail
    {
        get
        {
            lock (_mail)
            {
                return [.. _mail];
            }
        }
    }

    /// <summary>Every command line received, each prefixed with <c>tls:</c> or <c>clear:</c> by the channel it came over.</summary>
    public IReadOnlyList<string> Log
    {
        get
        {
            lock (_log)
            {
                return [.. _log];
            }
        }
    }

    public bool SawAuthInTheClear => Log.Any(line => line.StartsWith("clear:AUTH", StringComparison.OrdinalIgnoreCase) || line.StartsWith("clear:PASSWORD:", StringComparison.Ordinal));

    public bool SawAuthOverTls => Log.Any(line => line.StartsWith("tls:AUTH", StringComparison.OrdinalIgnoreCase));

    public void Start()
    {
        _listener.Start();
        _accepting = Task.Run(AcceptLoop);
    }

    private async Task AcceptLoop()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client = await _listener.AcceptTcpClientAsync(_stop.Token);
                _ = Task.Run(() => Serve(client));
            }
        }
        catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException or SocketException)
        {
            // Stopped.
        }
    }

    private async Task Serve(TcpClient client)
    {
        using (client)
        {
            try
            {
                if (Silent)
                {
                    await Task.Delay(Timeout.Infinite, _stop.Token);
                }

                Stream stream = client.GetStream();
                bool tls = false;
                await Send(stream, "220 test.example ESMTP ready");
                string? from = null;
                string? supplied = null;
                bool authenticated = false;
                var to = new List<string>();
                while (true)
                {
                    string? line = await ReadLine(stream);
                    if (line is null)
                    {
                        return;
                    }

                    Record(tls, line.StartsWith("AUTH", StringComparison.OrdinalIgnoreCase) ? "AUTH" + (line.Length > 4 ? line[4..] : string.Empty) : line);
                    string verb = line.Split(' ')[0].ToUpperInvariant();
                    switch (verb)
                    {
                        case "EHLO":
                        case "HELO":
                            var capabilities = new List<string> { "test.example" };
                            if (OfferStartTls && !tls)
                            {
                                capabilities.Add("STARTTLS");
                            }

                            if (Password is not null)
                            {
                                capabilities.Add("AUTH LOGIN");
                            }

                            capabilities.Add("8BITMIME");
                            for (int i = 0; i < capabilities.Count; i++)
                            {
                                await Send(stream, (i == capabilities.Count - 1 ? "250 " : "250-") + capabilities[i]);
                            }

                            break;
                        case "STARTTLS":
                            if (!OfferStartTls)
                            {
                                await Send(stream, "502 5.5.1 STARTTLS not available");
                                break;
                            }

                            await Send(stream, "220 2.0.0 Ready to start TLS");
                            var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
                            await ssl.AuthenticateAsServerAsync(Certificate!);
                            stream = ssl;
                            tls = true;
                            break;
                        case "AUTH":
                            (authenticated, supplied) = await Authenticate(stream, tls, line);
                            break;
                        case "MAIL":
                            if (Password is not null && !authenticated)
                            {
                                // What a real relay does: it lets the sign-in fail quietly and refuses the first command that needs one.
                                await Send(stream, EchoPasswordInRejection ? $"530 5.7.0 Authentication required (you tried the password {supplied})" : "530 5.7.0 Authentication required");
                                break;
                            }

                            from = line[(line.IndexOf(':', StringComparison.Ordinal) + 1)..].Trim().Trim('<', '>');
                            to.Clear();
                            await Send(stream, "250 2.1.0 OK");
                            break;
                        case "RCPT":
                            if (RecipientReply is not null)
                            {
                                await Send(stream, RecipientReply);
                                break;
                            }

                            to.Add(line[(line.IndexOf(':', StringComparison.Ordinal) + 1)..].Trim().Trim('<', '>'));
                            await Send(stream, "250 2.1.5 OK");
                            break;
                        case "DATA":
                            await Send(stream, "354 Go ahead");
                            var data = new StringBuilder();
                            while (await ReadLine(stream) is { } dataLine && dataLine != ".")
                            {
                                data.Append(dataLine).Append('\n');
                            }

                            lock (_mail)
                            {
                                _mail.Add(new ReceivedMail(from ?? string.Empty, [.. to], data.ToString(), tls));
                            }

                            await Send(stream, "250 2.0.0 Queued");
                            break;
                        case "RSET":
                        case "NOOP":
                            await Send(stream, "250 OK");
                            break;
                        case "QUIT":
                            await Send(stream, "221 2.0.0 Bye");
                            return;
                        default:
                            await Send(stream, "502 5.5.2 Command not recognised");
                            break;
                    }
                }
            }
            catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException or System.Security.Authentication.AuthenticationException)
            {
                // The client hung up or refused our certificate: part of what some tests are about.
            }
        }
    }

    private async Task<(bool Ok, string Supplied)> Authenticate(Stream stream, bool tls, string line)
    {
        string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string? user = parts.Length > 2 ? parts[2] : null;
        if (user is null)
        {
            await Send(stream, "334 VXNlcm5hbWU6");
            user = await ReadLine(stream);
            Record(tls, "USER:" + user);
        }

        await Send(stream, "334 UGFzc3dvcmQ6");
        string? encoded = await ReadLine(stream);
        string supplied = encoded is null ? string.Empty : Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
        Record(tls, "PASSWORD:" + new string('*', supplied.Length));
        if (supplied == Password)
        {
            await Send(stream, "235 2.7.0 Authentication succeeded");
            return (true, supplied);
        }

        await Send(stream, "535 5.7.8 Authentication credentials invalid");
        return (false, supplied);
    }

    private void Record(bool tls, string line)
    {
        lock (_log)
        {
            _log.Add((tls ? "tls:" : "clear:") + line);
        }
    }

    private static async Task Send(Stream stream, string line)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(line + "\r\n");
        await stream.WriteAsync(bytes);
        await stream.FlushAsync();
    }

    private static async Task<string?> ReadLine(Stream stream)
    {
        var bytes = new List<byte>();
        byte[] one = new byte[1];
        while (true)
        {
            int read = await stream.ReadAsync(one);
            if (read == 0)
            {
                return bytes.Count == 0 ? null : Encoding.UTF8.GetString([.. bytes]);
            }

            if (one[0] == '\n')
            {
                return Encoding.UTF8.GetString([.. bytes]).TrimEnd('\r');
            }

            bytes.Add(one[0]);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        if (_accepting is not null)
        {
            await _accepting;
        }

        Certificate?.Dispose();
        _stop.Dispose();
    }
}
