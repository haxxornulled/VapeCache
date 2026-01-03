using System;
using System.Collections.Generic;
using LanguageExt.Common;
using Microsoft.Extensions.Logging;
using Application.Common.Extensions;

namespace ResultDemo.Examples;

public sealed class UserProfileFacade
{
    private readonly IUserRepository _repo;
    private readonly ILogger<UserProfileFacade> _logger;

    public UserProfileFacade(IUserRepository repo, ILogger<UserProfileFacade> logger)
    {
        _repo = repo;
        _logger = logger;
    }

    public UserProfileDto GetProfileOrAnonymous(Guid userId)
    {
        try
        {
            var profile = _repo
                .GetProfile(userId)
                .LogFailureMessage(_logger, messageTemplate: "GetProfile failed for {UserId}", userId)
                .ValueOr(ex => new UserProfileDto(userId, "anonymous", "Anonymous User"));

            return profile;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error building profile for {UserId}", userId);
            throw;
        }
    }
}

public interface IUserRepository
{
    Result<UserProfileDto> GetProfile(Guid userId);
}

public sealed record UserProfileDto(Guid UserId, string Username, string DisplayName);

public sealed class InMemoryProfileRepository : IUserRepository
{
    private readonly Dictionary<Guid, UserProfileDto> _profiles = new();
    public Guid KnownUserId { get; }

    public InMemoryProfileRepository()
    {
        KnownUserId = Guid.NewGuid();
        _profiles[KnownUserId] = new UserProfileDto(KnownUserId, "jdoe", "Jane Doe");
    }

    public Result<UserProfileDto> GetProfile(Guid userId)
    {
        if (_profiles.TryGetValue(userId, out var profile))
        {
            return new Result<UserProfileDto>(profile);
        }

        return new Result<UserProfileDto>(new KeyNotFoundException("Profile not found."));
    }
}
