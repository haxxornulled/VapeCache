using VapeCache.Licensing;

namespace VapeCache.LicenseGenerator;

/// <summary>
/// Internal utility for generating VapeCache license keys.
/// NOT FOR DISTRIBUTION - For VapeCache team use only.
/// </summary>
internal class Program
{
    // IMPORTANT: This secret key MUST match the key used in RedisReconciliationExtensions
    private const string LicenseSecretKey = "VapeCache-HMAC-Secret-2026-Production";

    static void Main(string[] args)
    {
        Console.WriteLine("=== VapeCache License Key Generator ===");
        Console.WriteLine("FOR INTERNAL USE ONLY - DO NOT DISTRIBUTE\n");

        if (args.Length > 0 && args[0] == "--validate")
        {
            ValidateLicenseKey();
            return;
        }

        GenerateLicenseKey();
    }

    private static void GenerateLicenseKey()
    {
        Console.WriteLine("Select License Tier:");
        Console.WriteLine("  1. Pro ($29/month, max 3 instances)");
        Console.WriteLine("  2. Enterprise ($299/month, unlimited instances)");
        Console.Write("\nEnter tier (1 or 2): ");
        var tierInput = Console.ReadLine();

        LicenseTier tier;
        if (tierInput == "1")
            tier = LicenseTier.Pro;
        else if (tierInput == "2")
            tier = LicenseTier.Enterprise;
        else
        {
            Console.WriteLine("Invalid tier selection. Exiting.");
            return;
        }

        Console.Write("Enter Customer ID (e.g., CUST12345): ");
        var customerId = Console.ReadLine()?.Trim();
        if (string.IsNullOrWhiteSpace(customerId))
        {
            Console.WriteLine("Customer ID cannot be empty. Exiting.");
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
        var licenseKey = validator.GenerateLicenseKey(tier, customerId, expiresAt);

        Console.WriteLine("\n=== LICENSE KEY GENERATED ===");
        Console.WriteLine($"Tier:       {tier}");
        Console.WriteLine($"Customer:   {customerId}");
        Console.WriteLine($"Expires:    {expiresAt:yyyy-MM-dd}");
        Console.WriteLine($"Instances:  {(tier == LicenseTier.Pro ? "3" : "Unlimited")}");
        Console.WriteLine($"\nLicense Key:\n{licenseKey}");
        Console.WriteLine("\n=== USAGE ===");
        Console.WriteLine("Add to appsettings.json or set environment variable:");
        Console.WriteLine($"  VAPECACHE_LICENSE_KEY={licenseKey}");
        Console.WriteLine("\nOr pass directly to AddVapeCacheRedisReconciliation:");
        Console.WriteLine($"  services.AddVapeCacheRedisReconciliation(\"{licenseKey}\");");
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
