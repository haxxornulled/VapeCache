using System;
using System.Collections.Generic;
using System.Net;
using LanguageExt.Common;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Application.Common.Extensions;

namespace ResultDemo.Examples;

[ApiController]
[Route("api/users")]
public sealed class UsersController : ControllerBase
{
    private readonly IUserService _service;
    private readonly ILogger<UsersController> _logger;

    public UsersController(IUserService service, ILogger<UsersController> logger)
    {
        _service = service;
        _logger = logger;
    }

    [HttpPost]
    public IActionResult Create([FromBody] CreateUserRequest request)
    {
        try
        {
            var result = _service
                .CreateUser(request)
                .LogFailureMessage(_logger, messageTemplate: "CreateUser failed for {Email}", request.Email);

            return result.Match<IActionResult>(
                created => CreatedAtAction(nameof(GetById), new { id = created.Id }, created),
                ex =>
                {
                    _logger.LogWarning("User creation rejected for {Email}: {Reason}", request.Email, ex.Message);
                    return Conflict(new ProblemDetails
                    {
                        Title = "User already exists or request invalid.",
                        Detail = ex.Message,
                        Status = (int)HttpStatusCode.Conflict
                    });
                });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled error during CreateUser for {Email}", request.Email);
            return StatusCode((int)HttpStatusCode.InternalServerError, new ProblemDetails
            {
                Title = "Unexpected error.",
                Detail = "An unexpected error occurred.",
                Status = (int)HttpStatusCode.InternalServerError
            });
        }
    }

    [HttpGet("{id:guid}")]
    public IActionResult GetById(Guid id) => Ok(new { Id = id });
}

public interface IUserService
{
    Result<UserDto> CreateUser(CreateUserRequest request);
    Result<UserDto> GetById(Guid id);
}

public sealed record CreateUserRequest(string Email, string DisplayName);
public sealed record UserDto(Guid Id, string Email, string DisplayName);

public sealed class InMemoryUserService : IUserService
{
    private readonly InMemoryUserStore _store;
    private readonly ILogger<InMemoryUserService> _logger;

    public InMemoryUserService(InMemoryUserStore store, ILogger<InMemoryUserService> logger)
    {
        _store = store;
        _logger = logger;
    }

    public Result<UserDto> CreateUser(CreateUserRequest request)
    {
        var result = _store.CreateUser(request);

        return result.Match(
            created =>
            {
                _logger.LogInformation("Created user {Email}", created.Email);
                return new Result<UserDto>(created);
            },
            ex => new Result<UserDto>(ex));
    }

    public Result<UserDto> GetById(Guid id) => _store.GetById(id);
}

public sealed class InMemoryUserStore
{
    private readonly Dictionary<Guid, UserDto> _byId = new();
    private readonly Dictionary<string, UserDto> _byEmail = new(StringComparer.OrdinalIgnoreCase);

    public Result<UserDto> CreateUser(CreateUserRequest request)
    {
        if (_byEmail.ContainsKey(request.Email))
        {
            return new Result<UserDto>(new InvalidOperationException("User already exists."));
        }

        var user = new UserDto(Guid.NewGuid(), request.Email, request.DisplayName);
        _byEmail[user.Email] = user;
        _byId[user.Id] = user;
        return new Result<UserDto>(user);
    }

    public Result<UserDto> GetById(Guid id)
    {
        if (_byId.TryGetValue(id, out var user))
        {
            return new Result<UserDto>(user);
        }

        return new Result<UserDto>(new KeyNotFoundException("User not found."));
    }
}
