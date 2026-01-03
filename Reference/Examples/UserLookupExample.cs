using System;
using LanguageExt.Common;
using Microsoft.Extensions.Logging;
using Application.Common.Extensions;

namespace ResultDemo.Examples;

public sealed class UserLookupService
{
    private readonly ILogger<UserLookupService> _logger;
    private readonly IUserRepository2 _repo;

    public UserLookupService(ILogger<UserLookupService> logger, IUserRepository2 repo)
    {
        _logger = logger;
        _repo = repo;
    }

    public UserDto? TryLookup(Guid id)
    {
        try
        {
            var res = _repo.TryFind(id);

            if (res.TryGetValue(out var user))
                return user;

            res.LogFailureMessage(_logger, messageTemplate: "TryFind failed for {UserId}", id);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled error in TryLookup for {UserId}", id);
            return null;
        }
    }
}

public interface IUserRepository2
{
    Result<UserDto> TryFind(Guid id);
}

public sealed class InMemoryUserRepository2 : IUserRepository2
{
    private readonly InMemoryUserStore _store;

    public InMemoryUserRepository2(InMemoryUserStore store) => _store = store;

    public Result<UserDto> TryFind(Guid id)
    {
        return _store.GetById(id);
    }
}
