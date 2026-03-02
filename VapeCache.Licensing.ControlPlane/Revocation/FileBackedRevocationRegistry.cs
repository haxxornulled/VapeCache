using System.Text.Json;
using Microsoft.Extensions.Options;

namespace VapeCache.Licensing.ControlPlane.Revocation;

/// <summary>
/// File-backed revocation registry with in-memory hot state and atomic persistence.
/// </summary>
public sealed class FileBackedRevocationRegistry : IRevocationRegistry
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly Lock _gate = new();
    private readonly ILogger<FileBackedRevocationRegistry> _logger;
    private readonly string _stateFilePath;

    private readonly Dictionary<string, RevocationRecord> _revokedLicenses = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RevocationRecord> _organizationKillSwitches = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RevocationRecord> _revokedKeyIds = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Creates a new registry and loads persisted revocation state if present.
    /// </summary>
    public FileBackedRevocationRegistry(
        IOptionsMonitor<RevocationControlPlaneOptions> optionsMonitor,
        ILogger<FileBackedRevocationRegistry> logger)
    {
        ArgumentNullException.ThrowIfNull(optionsMonitor);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var options = optionsMonitor.CurrentValue;
        _stateFilePath = ResolveStatePath(options.PersistencePath);
        EnsureStateDirectoryExists(_stateFilePath);
        LoadStateUnsafe();
    }

    /// <inheritdoc />
    public RevocationDecision Evaluate(string licenseId, string? organizationId, string? keyId)
    {
        var normalizedLicenseId = NormalizeIdentity(licenseId, nameof(licenseId));
        var normalizedOrganizationId = NormalizeIdentityOptional(organizationId);
        var normalizedKeyId = NormalizeIdentityOptional(keyId);

        lock (_gate)
        {
            if (_revokedLicenses.TryGetValue(normalizedLicenseId, out var licenseRecord))
            {
                return new RevocationDecision(
                    Revoked: true,
                    Reason: licenseRecord.Reason,
                    Source: "license",
                    UpdatedAtUtc: licenseRecord.UpdatedAtUtc);
            }

            if (normalizedOrganizationId is not null &&
                _organizationKillSwitches.TryGetValue(normalizedOrganizationId, out var organizationRecord))
            {
                return new RevocationDecision(
                    Revoked: true,
                    Reason: organizationRecord.Reason,
                    Source: "organization",
                    UpdatedAtUtc: organizationRecord.UpdatedAtUtc);
            }

            if (normalizedKeyId is not null &&
                _revokedKeyIds.TryGetValue(normalizedKeyId, out var keyRecord))
            {
                return new RevocationDecision(
                    Revoked: true,
                    Reason: keyRecord.Reason,
                    Source: "key-id",
                    UpdatedAtUtc: keyRecord.UpdatedAtUtc);
            }

            return new RevocationDecision(
                Revoked: false,
                Reason: "active",
                Source: "none",
                UpdatedAtUtc: null);
        }
    }

    /// <inheritdoc />
    public RevocationMutationResult RevokeLicense(string licenseId, string reason, string actor)
        => Upsert(_revokedLicenses, "license", licenseId, reason, actor, isRevoked: true);

    /// <inheritdoc />
    public RevocationMutationResult ActivateLicense(string licenseId, string reason, string actor)
        => Remove(_revokedLicenses, "license", licenseId, reason, actor, isRevoked: false);

    /// <inheritdoc />
    public RevocationMutationResult EnableOrganizationKillSwitch(string organizationId, string reason, string actor)
        => Upsert(_organizationKillSwitches, "organization", organizationId, reason, actor, isRevoked: true);

    /// <inheritdoc />
    public RevocationMutationResult DisableOrganizationKillSwitch(string organizationId, string reason, string actor)
        => Remove(_organizationKillSwitches, "organization", organizationId, reason, actor, isRevoked: false);

    /// <inheritdoc />
    public RevocationMutationResult RevokeKeyId(string keyId, string reason, string actor)
        => Upsert(_revokedKeyIds, "key-id", keyId, reason, actor, isRevoked: true);

    /// <inheritdoc />
    public RevocationMutationResult ActivateKeyId(string keyId, string reason, string actor)
        => Remove(_revokedKeyIds, "key-id", keyId, reason, actor, isRevoked: false);

    /// <inheritdoc />
    public RevocationSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            return new RevocationSnapshot(
                RevokedLicenses: CopyRecords(_revokedLicenses),
                OrganizationKillSwitches: CopyRecords(_organizationKillSwitches),
                RevokedKeyIds: CopyRecords(_revokedKeyIds));
        }
    }

    private RevocationMutationResult Upsert(
        Dictionary<string, RevocationRecord> map,
        string scope,
        string identity,
        string reason,
        string actor,
        bool isRevoked)
    {
        var normalizedIdentity = NormalizeIdentity(identity, nameof(identity));
        var normalizedReason = NormalizeReason(reason);
        var normalizedActor = NormalizeActor(actor);
        var utcNow = DateTimeOffset.UtcNow;

        lock (_gate)
        {
            var changed = !map.TryGetValue(normalizedIdentity, out var existing) ||
                          !string.Equals(existing.Reason, normalizedReason, StringComparison.Ordinal) ||
                          !string.Equals(existing.Actor, normalizedActor, StringComparison.Ordinal);
            var hadExisting = existing is not null;
            var nextRecord = new RevocationRecord(
                Identity: normalizedIdentity,
                Reason: normalizedReason,
                Actor: normalizedActor,
                UpdatedAtUtc: utcNow);

            map[normalizedIdentity] = nextRecord;
            try
            {
                SaveStateUnsafe();
            }
            catch
            {
                if (hadExisting)
                    map[normalizedIdentity] = existing!;
                else
                    map.Remove(normalizedIdentity);
                throw;
            }

            return new RevocationMutationResult(
                Scope: scope,
                Identity: normalizedIdentity,
                IsRevoked: isRevoked,
                Changed: changed,
                Reason: normalizedReason,
                Actor: normalizedActor,
                UpdatedAtUtc: utcNow);
        }
    }

    private RevocationMutationResult Remove(
        Dictionary<string, RevocationRecord> map,
        string scope,
        string identity,
        string reason,
        string actor,
        bool isRevoked)
    {
        var normalizedIdentity = NormalizeIdentity(identity, nameof(identity));
        var normalizedReason = NormalizeReason(reason);
        var normalizedActor = NormalizeActor(actor);
        var utcNow = DateTimeOffset.UtcNow;

        lock (_gate)
        {
            var changed = map.Remove(normalizedIdentity, out var removedRecord);
            if (changed)
            {
                try
                {
                    SaveStateUnsafe();
                }
                catch
                {
                    map[normalizedIdentity] = removedRecord!;
                    throw;
                }
            }

            return new RevocationMutationResult(
                Scope: scope,
                Identity: normalizedIdentity,
                IsRevoked: isRevoked,
                Changed: changed,
                Reason: normalizedReason,
                Actor: normalizedActor,
                UpdatedAtUtc: utcNow);
        }
    }

    private void LoadStateUnsafe()
    {
        if (!File.Exists(_stateFilePath))
        {
            _logger.LogInformation("Revocation state file not found. Starting fresh. Path={Path}", _stateFilePath);
            return;
        }

        try
        {
            var json = File.ReadAllText(_stateFilePath);
            var payload = JsonSerializer.Deserialize<RevocationStateFilePayload>(json, JsonOptions);
            if (payload is null)
            {
                _logger.LogWarning("Revocation state file is empty. Path={Path}", _stateFilePath);
                return;
            }

            RestoreMap(_revokedLicenses, payload.RevokedLicenses);
            RestoreMap(_organizationKillSwitches, payload.OrganizationKillSwitches);
            RestoreMap(_revokedKeyIds, payload.RevokedKeyIds);

            _logger.LogInformation(
                "Loaded revocation state. Licenses={Licenses} Organizations={Organizations} KeyIds={KeyIds} Path={Path}",
                _revokedLicenses.Count,
                _organizationKillSwitches.Count,
                _revokedKeyIds.Count,
                _stateFilePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read revocation state file. Startup aborted. Path={Path}", _stateFilePath);
            throw new InvalidOperationException("Failed to load revocation state file.", ex);
        }
    }

    private void SaveStateUnsafe()
    {
        var payload = new RevocationStateFilePayload
        {
            SchemaVersion = 1,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            RevokedLicenses = _revokedLicenses.Values.OrderBy(static x => x.Identity, StringComparer.OrdinalIgnoreCase).ToArray(),
            OrganizationKillSwitches = _organizationKillSwitches.Values.OrderBy(static x => x.Identity, StringComparer.OrdinalIgnoreCase).ToArray(),
            RevokedKeyIds = _revokedKeyIds.Values.OrderBy(static x => x.Identity, StringComparer.OrdinalIgnoreCase).ToArray()
        };

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var tempPath = $"{_stateFilePath}.tmp";
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, _stateFilePath, overwrite: true);
    }

    private static void RestoreMap(Dictionary<string, RevocationRecord> destination, IReadOnlyList<RevocationRecord> source)
    {
        destination.Clear();
        foreach (var item in source)
        {
            if (string.IsNullOrWhiteSpace(item.Identity))
                continue;

            var normalizedIdentity = item.Identity.Trim();
            destination[normalizedIdentity] = new RevocationRecord(
                Identity: normalizedIdentity,
                Reason: NormalizeReason(item.Reason),
                Actor: NormalizeActor(item.Actor),
                UpdatedAtUtc: item.UpdatedAtUtc == default ? DateTimeOffset.UtcNow : item.UpdatedAtUtc);
        }
    }

    private static IReadOnlyList<RevocationRecord> CopyRecords(Dictionary<string, RevocationRecord> source)
        => source.Values
            .OrderBy(static x => x.Identity, StringComparer.OrdinalIgnoreCase)
            .Select(static x => x with { })
            .ToArray();

    private static string NormalizeIdentity(string value, string paramName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, paramName);
        return value.Trim();
    }

    private static string? NormalizeIdentityOptional(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        return value.Trim();
    }

    private static string NormalizeReason(string reason)
        => string.IsNullOrWhiteSpace(reason) ? "unspecified" : reason.Trim();

    private static string NormalizeActor(string actor)
        => string.IsNullOrWhiteSpace(actor) ? "unknown" : actor.Trim();

    private static string ResolveStatePath(string configuredPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configuredPath);
        return Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, configuredPath));
    }

    private static void EnsureStateDirectoryExists(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
    }

    private sealed class RevocationStateFilePayload
    {
        public int SchemaVersion { get; set; } = 1;
        public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
        public IReadOnlyList<RevocationRecord> RevokedLicenses { get; set; } = Array.Empty<RevocationRecord>();
        public IReadOnlyList<RevocationRecord> OrganizationKillSwitches { get; set; } = Array.Empty<RevocationRecord>();
        public IReadOnlyList<RevocationRecord> RevokedKeyIds { get; set; } = Array.Empty<RevocationRecord>();
    }
}
