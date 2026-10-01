namespace FsCopilot.Hoppie;

using System.ComponentModel;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

public sealed class HoppieTransparentCaptureManager : IDisposable
{
    public const string TargetHost = "www.hoppie.nl";
    public const string AliasTargetHost = "hoppie.nl";
    public const string UpstreamBypassHost = "94.142.246.179";
    public const int HttpPort = 80;
    public const int HttpsPort = 443;
    public static IReadOnlyList<string> TargetHosts { get; } = [TargetHost, AliasTargetHost];
    public static string TargetHostsDisplay => string.Join(" / ", TargetHosts);

    private const string HostsMarkerStart = "# FS Copilot Hoppie Capture BEGIN";
    private const string HostsMarkerEnd = "# FS Copilot Hoppie Capture END";
    private const string CertificateFriendlyName = "FS Copilot Hoppie Capture v2";
    private const string CertificateSubject = "CN=www.hoppie.nl";

    private readonly BehaviorSubject<HoppieCaptureState> _changes;

    public HoppieTransparentCaptureManager()
    {
        _changes = new(GetState());
    }

    public HoppieCaptureState Current => _changes.Value;

    public IObservable<HoppieCaptureState> Changes => _changes.DistinctUntilChanged();

    public static bool IsAdministrator => IsCurrentProcessElevated();

    public void Dispose() => _changes.Dispose();

    public HoppieCaptureActionResult Install()
    {
        try
        {
            EnsureTrustedCertificate();
        }
        catch (Exception e)
        {
            Log.Error(e, "[Hoppie] Failed to provision local HTTPS certificate");
            Refresh();
            return new(false, "Failed to prepare the local HTTPS certificate.");
        }

        var helperResult = RunHostsHelper("install");
        Refresh();
        return helperResult;
    }

    public HoppieCaptureActionResult Remove()
    {
        var helperResult = RunHostsHelper("remove");
        Refresh();
        return helperResult;
    }

    public void Refresh() => _changes.OnNext(GetState());

    public X509Certificate2 EnsureTrustedCertificate()
    {
        var certificate = FindServerCertificate();
        if (certificate == null)
            certificate = CreateAndStoreServerCertificate();

        TrustCertificate(certificate);
        Refresh();
        return certificate;
    }

    private HoppieCaptureState GetState()
    {
        var hostsInstalled = IsHostsOverrideInstalled();
        var certificate = FindServerCertificate();
        var certificateReady = certificate != null;
        var certificateTrusted = certificate != null && IsCertificateTrusted(certificate);

        var message = !hostsInstalled
            ? "Capture not installed."
            : !certificateReady
                ? "Capture installed, certificate missing."
                : !certificateTrusted
                    ? "Capture installed, certificate not trusted."
                    : "Capture active.";

        return new(hostsInstalled, certificateReady, certificateTrusted, message);
    }

    private static HoppieCaptureActionResult RunHostsHelper(string operation)
    {
        try
        {
            if (IsCurrentProcessElevated())
            {
                ApplyHostsOverride(operation.Equals("install", StringComparison.OrdinalIgnoreCase));
                return new(true, operation.Equals("install", StringComparison.OrdinalIgnoreCase)
                    ? $"Installed transparent capture redirect for {TargetHostsDisplay}."
                    : $"Removed transparent capture redirect for {TargetHostsDisplay}.");
            }

            var exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
                return new(false, "FS Copilot could not locate its own executable to request elevation.");

            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = $"--hoppie-hosts {operation}",
                UseShellExecute = true,
                Verb = "runas"
            };

            using var process = Process.Start(psi);
            if (process == null)
                return new(false, "Windows did not start the elevated helper process.");

            process.WaitForExit();
            if (process.ExitCode != 0)
                return new(false, "The elevated helper did not complete successfully.");

            return new(true, operation.Equals("install", StringComparison.OrdinalIgnoreCase)
                ? $"Installed transparent capture redirect for {TargetHostsDisplay}."
                : $"Removed transparent capture redirect for {TargetHostsDisplay}.");
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223)
        {
            return new(false, "The Windows elevation prompt was cancelled.");
        }
        catch (Exception e)
        {
            Log.Error(e, "[Hoppie] Failed to run the elevated hosts helper");
            return new(false, "Windows refused the transparent capture install helper.");
        }
    }

    public static bool TryRunAdminCommand(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args.Length < 2 || !string.Equals(args[0], "--hoppie-hosts", StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            switch (args[1].Trim().ToLowerInvariant())
            {
                case "install":
                    ApplyHostsOverride(true);
                    return true;
                case "remove":
                    ApplyHostsOverride(false);
                    return true;
                default:
                    exitCode = 2;
                    return true;
            }
        }
        catch (Exception e)
        {
            Log.Error(e, "[Hoppie] Elevated hosts helper failed");
            exitCode = 1;
            return true;
        }
    }

    private static void ApplyHostsOverride(bool install)
    {
        var path = HostsFilePath;
        var existing = File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        var cleaned = RemoveManagedBlock(existing).TrimEnd();
        var builder = new StringBuilder(cleaned);

        if (install)
        {
            if (builder.Length > 0)
                builder.AppendLine().AppendLine();

            builder.AppendLine(HostsMarkerStart);
            foreach (var host in TargetHosts)
            {
                builder.AppendLine($"127.0.0.1 {host}");
                builder.AppendLine($"::1 {host}");
            }

            builder.AppendLine(HostsMarkerEnd);
        }
        else if (builder.Length > 0)
        {
            builder.AppendLine();
        }

        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(false));
    }

    private static bool IsCurrentProcessElevated()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var principal = new System.Security.Principal.WindowsPrincipal(identity);
        return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    private static bool IsHostsOverrideInstalled()
    {
        try
        {
            if (!File.Exists(HostsFilePath))
                return false;

            var content = File.ReadAllText(HostsFilePath);
            return content.Contains(HostsMarkerStart, StringComparison.Ordinal)
                   && content.Contains(HostsMarkerEnd, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static string RemoveManagedBlock(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return string.Empty;

        var start = content.IndexOf(HostsMarkerStart, StringComparison.Ordinal);
        if (start < 0)
            return content;

        var end = content.IndexOf(HostsMarkerEnd, start, StringComparison.Ordinal);
        if (end < 0)
            return content[..start];

        end += HostsMarkerEnd.Length;
        while (end < content.Length && (content[end] == '\r' || content[end] == '\n'))
            end++;

        return content.Remove(start, end - start);
    }

    private static X509Certificate2? FindServerCertificate()
    {
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly);

        return store.Certificates
            .OfType<X509Certificate2>()
            .Where(cert => cert.HasPrivateKey
                           && cert.NotAfter > DateTime.Now.AddDays(30)
                           && string.Equals(cert.FriendlyName, CertificateFriendlyName, StringComparison.Ordinal))
            .OrderByDescending(cert => cert.NotAfter)
            .Select(cert => X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pkcs12), string.Empty))
            .FirstOrDefault();
    }

    private static bool IsCertificateTrusted(X509Certificate2 certificate)
    {
        using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly);

        return store.Certificates
            .OfType<X509Certificate2>()
            .Any(cert => string.Equals(cert.Thumbprint, certificate.Thumbprint, StringComparison.OrdinalIgnoreCase));
    }

    private static X509Certificate2 CreateAndStoreServerCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(CertificateSubject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));

        var usages = new OidCollection
        {
            new("1.3.6.1.5.5.7.3.1")
        };
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(usages, false));

        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(TargetHost);
        san.AddDnsName(AliasTargetHost);
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        san.AddIpAddress(IPAddress.IPv6Loopback);
        request.CertificateExtensions.Add(san.Build());

        var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(2));
        generated.FriendlyName = CertificateFriendlyName;

        var persisted = X509CertificateLoader.LoadPkcs12(
            generated.Export(X509ContentType.Pkcs12),
            string.Empty,
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);
        persisted.FriendlyName = CertificateFriendlyName;

        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        store.Add(persisted);

        return X509CertificateLoader.LoadPkcs12(persisted.Export(X509ContentType.Pkcs12), string.Empty);
    }

    private static void TrustCertificate(X509Certificate2 certificate)
    {
        using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);

        if (store.Certificates
            .OfType<X509Certificate2>()
            .Any(cert => string.Equals(cert.Thumbprint, certificate.Thumbprint, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        store.Add(X509CertificateLoader.LoadCertificate(certificate.Export(X509ContentType.Cert)));
    }

    private static string HostsFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        "drivers",
        "etc",
        "hosts");

    public sealed record HoppieCaptureState(
        bool HostsOverrideInstalled,
        bool CertificateReady,
        bool CertificateTrusted,
        string Message);

    public sealed record HoppieCaptureActionResult(bool Succeeded, string Message);
}
