using System;
using LanguageExt.Common;
using Application.Common.Extensions;

namespace ResultDemo.Examples;

public interface IUserQueryService
{
    Result<UserDto> GetById(Guid id);
    Result<ValueTask<UserDto>> GetByIdAsync(Guid id);
}

public sealed class InMemoryUserQueryService : IUserQueryService
{
    private readonly InMemoryUserStore _store;

    public InMemoryUserQueryService(InMemoryUserStore store) => _store = store;

    public Result<UserDto> GetById(Guid id) => _store.GetById(id);

    public Result<ValueTask<UserDto>> GetByIdAsync(Guid id)
    {
        var result = _store.GetById(id);
        return result.Match(
            user => new Result<ValueTask<UserDto>>(FetchAsync(user)),
            ex => new Result<ValueTask<UserDto>>(ex));
    }

    private static async ValueTask<UserDto> FetchAsync(UserDto user)
    {
        await Task.Delay(10);
        return user;
    }
}
