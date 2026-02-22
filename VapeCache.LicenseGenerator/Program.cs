using VapeCache.Licensing;

namespace VapeCache.LicenseGenerator;

/// <summary>
/// Internal utility for generating VapeCache license keys.
/// NOT FOR DISTRIBUTION - For VapeCache team use only.
/// </summary>
internal class Program
{
    private static readonly string LicenseSecretKey = LicenseValidationOptions.ResolveValidationSecret();

    static void Main(string[] args)
    {
        Console.WriteLine("=== VapeCache License Key Generator ===");
        Console.WriteLine("FOR INTERNAL USE ONLY - DO NOT DISTRIBUTE\n");

        if (LicenseSecretKey == LicenseValidationOptions.DefaultValidationSecret)
        {
            Console.WriteLine(
                $"WARNING: Using default validation secret. Set {LicenseValidationOptions.ValidationSecretEnvironmentVariable} to rotate it.\n");
        }

        if (args.Length > 0 && args[0] == "--validate")
        {
            ValidateLicenseKey();
            return;
        }

        GenerateLicenseKey();
    }

    private static void GenerateLicenseKey()
    {
        Console.WriteLine("=== VapeCache Enterprise License Generator ===");
        Console.WriteLine("Application-based licensing: $499/month per organization");
        Console.WriteLine("Unlimited deployments, unlimited Redis topology\n");

        Console.Write("Enter Organization ID (e.g., acme, contoso): ");
        var organizationId = Console.ReadLine()?.Trim();
        if (string.IsNullOrWhiteSpace(organizationId))
        {
            Console.WriteLine("Organization ID cannot be empty. Exiting.");
            return;
        }

        Console.Write("Enter expiration date (YYYY-MM-DD) or leave blank for 1 year: ");
        var expiryInput = Console.ReadLine()?.Trim();
        DateTimeOffset expiresAt;

        if (string.IsNullOrWhiteSpace(expiryInput))
        {
            expiresAt = DateTimeOffset.UtcNow.AddYears(1);
        }
        else
        {
            if (!DateTimeOffset.TryParse(expiryInput, out expiresAt))
            {
                Console.WriteLine("Invalid date format. Exiting.");
                return;
            }
        }

        var validator = new LicenseValidator(LicenseSecretKey);
        var licenseKey = validator.GenerateLicenseKey(organizationId, expiresAt);

        Console.WriteLine();
        Console.WriteLine("=== ENTERPRISE LICENSE KEY GENERATED ===");
        Console.WriteLine($"Tier:         Enterprise");
        Console.WriteLine($"Organization: {organizationId}");
        Console.WriteLine($"Expires:      {expiresAt:yyyy-MM-dd}");
        Console.WriteLine($"Deployments:  Unlimited");
        Console.WriteLine($"Redis Topology: Any (standalone/sentinel/cluster)");
        Console.WriteLine();
        Console.WriteLine($"License Key:");
        Console.WriteLine(licenseKey);
        Console.WriteLine();
        Console.WriteLine("=== USAGE ===");
        Console.WriteLine("Add to appsettings.json or set environment variable:");
        Console.WriteLine($"  VAPECACHE_LICENSE_KEY={licenseKey}");
        Console.WriteLine();
        Console.WriteLine("For Persistence:");
        Console.WriteLine($"  services.AddVapeCachePersistence(\"{licenseKey}\");");
        Console.WriteLine();
        Console.WriteLine("For Reconciliation:");
        Console.WriteLine($"  services.AddVapeCacheReconciliation(\"{licenseKey}\");");
        Console.Out.Flush();
    }

    private static void ValidateLicenseKey()
    {
        Console.Write("Enter license key to validate: ");
        var licenseKey = Console.ReadLine()?.Trim();

        if (string.IsNullOrWhiteSpace(licenseKey))
        {
            Console.WriteLine("License key cannot be empty. Exiting.");
            return;
        }

        var validator = new LicenseValidator(LicenseSecretKey);
        var result = validator.Validate(licenseKey);

        Console.WriteLine("\n=== VALIDATION RESULT ===");
        Console.WriteLine($"Valid:      {result.IsValid}");

        if (result.IsValid)
        {
            Console.WriteLine($"Tier:       {result.Tier}");
            Console.WriteLine($"Customer:   {result.CustomerId}");
            Console.WriteLine($"Expires:    {result.ExpiresAt:yyyy-MM-dd}");
            Console.WriteLine($"Instances:  {(result.MaxInstances == 999 ? "Unlimited" : result.MaxInstances.ToString())}");
            Console.WriteLine($"Expired:    {result.IsExpired}");
        }
        else
        {
            Console.WriteLine($"Error:      {result.ErrorMessage}");
        }
    }
}
