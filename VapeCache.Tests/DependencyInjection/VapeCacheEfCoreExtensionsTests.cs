using System.Collections;
using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using VapeCache.Extensions.EntityFrameworkCore;

#pragma warning disable CS8764
#pragma warning disable CS8765

namespace VapeCache.Tests.DependencyInjection;

public sealed class VapeCacheEfCoreExtensionsTests
{
    [Fact]
    public void AddVapeCacheEntityFrameworkCore_registers_contracts_and_interceptors()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddVapeCacheEntityFrameworkCore();

        using var provider = services.BuildServiceProvider(validateScopes: true);
        Assert.NotNull(provider.GetService<IEfCoreQueryCacheKeyBuilder>());

        var interceptors = provider.GetServices<IInterceptor>().ToArray();
        Assert.Contains(interceptors, interceptor => interceptor is VapeCacheEfCoreCommandInterceptor);
        Assert.Contains(interceptors, interceptor => interceptor is VapeCacheEfCoreSaveChangesInterceptor);
    }

    [Fact]
    public void AddVapeCacheEntityFrameworkCore_respects_custom_observer_registration()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IEfCoreSecondLevelCacheObserver, TestObserver>();
        services.AddVapeCacheEntityFrameworkCore();

        using var provider = services.BuildServiceProvider(validateScopes: true);
        var observers = provider.GetServices<IEfCoreSecondLevelCacheObserver>().ToArray();
        Assert.Single(observers);
        Assert.IsType<TestObserver>(observers[0]);
    }

    [Fact]
    public void EfCore_options_default_to_zero_observer_overhead()
    {
        var options = new EfCoreSecondLevelCacheOptions();
        Assert.False(options.EnableObserverCallbacks);
    }

    [Fact]
    public void Sha256_builder_is_deterministic_and_parameter_sensitive()
    {
        var builder = new Sha256EfCoreQueryCacheKeyBuilder();

        var command = new TestDbCommand
        {
            CommandType = CommandType.Text,
            CommandText = "SELECT * FROM products WHERE id = @id AND tenant = @tenant"
        };
        command.Parameters.Add(new TestDbParameter("@id", DbType.Int32, 42));
        command.Parameters.Add(new TestDbParameter("@tenant", DbType.String, "dfw"));

        var key1 = builder.BuildQueryCacheKey("Microsoft.EntityFrameworkCore.SqlServer", command);
        var key2 = builder.BuildQueryCacheKey("Microsoft.EntityFrameworkCore.SqlServer", command);
        Assert.Equal(key1, key2);
        Assert.StartsWith("ef:q:v1:", key1, StringComparison.Ordinal);

        ((TestDbParameter)command.Parameters[0]).Value = 43;
        var key3 = builder.BuildQueryCacheKey("Microsoft.EntityFrameworkCore.SqlServer", command);
        Assert.NotEqual(key1, key3);
    }

    private sealed class TestDbCommand : DbCommand
    {
        private readonly TestDbParameterCollection _parameters = new();

        public override string? CommandText { get; set; } = string.Empty;
        public override int CommandTimeout { get; set; }
        public override CommandType CommandType { get; set; } = CommandType.Text;
        public override UpdateRowSource UpdatedRowSource { get; set; } = UpdateRowSource.None;
        protected override DbConnection? DbConnection { get; set; }
        protected override DbParameterCollection DbParameterCollection => _parameters;
        protected override DbTransaction? DbTransaction { get; set; }
        public override bool DesignTimeVisible { get; set; }

        public override void Cancel()
        {
        }

        public override int ExecuteNonQuery() => throw new NotSupportedException();

        public override object? ExecuteScalar() => throw new NotSupportedException();

        public override void Prepare()
        {
        }

        protected override DbParameter CreateDbParameter() => new TestDbParameter();

        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => throw new NotSupportedException();
    }

    private sealed class TestDbParameterCollection : DbParameterCollection
    {
        private readonly List<DbParameter> _items = [];

        public override int Count => _items.Count;
        public override object SyncRoot => ((ICollection)_items).SyncRoot;

        public override int Add(object value)
        {
            ArgumentNullException.ThrowIfNull(value);
            _items.Add((DbParameter)value);
            return _items.Count - 1;
        }

        public override void AddRange(Array values)
        {
            ArgumentNullException.ThrowIfNull(values);
            foreach (var value in values)
            {
                if (value is not DbParameter parameter)
                    throw new ArgumentException("Value must be DbParameter.", nameof(values));
                _items.Add(parameter);
            }
        }

        public override void Clear() => _items.Clear();

        public override bool Contains(object value) => _items.Contains((DbParameter)value);

        public override bool Contains(string value) => IndexOf(value) >= 0;

        public override void CopyTo(Array array, int index) => ((ICollection)_items).CopyTo(array, index);

        public override IEnumerator GetEnumerator() => _items.GetEnumerator();

        protected override DbParameter GetParameter(int index) => _items[index];

        protected override DbParameter GetParameter(string parameterName)
        {
            var index = IndexOf(parameterName);
            if (index < 0)
                throw new IndexOutOfRangeException(parameterName);
            return _items[index];
        }

        public override int IndexOf(object value) => _items.IndexOf((DbParameter)value);

        public override int IndexOf(string parameterName)
        {
            for (var i = 0; i < _items.Count; i++)
            {
                if (string.Equals(_items[i].ParameterName, parameterName, StringComparison.Ordinal))
                    return i;
            }

            return -1;
        }

        public override void Insert(int index, object value) => _items.Insert(index, (DbParameter)value);

        public override void Remove(object value) => _items.Remove((DbParameter)value);

        public override void RemoveAt(int index) => _items.RemoveAt(index);

        public override void RemoveAt(string parameterName)
        {
            var index = IndexOf(parameterName);
            if (index >= 0)
                _items.RemoveAt(index);
        }

        protected override void SetParameter(int index, DbParameter value) => _items[index] = value;

        protected override void SetParameter(string parameterName, DbParameter value)
        {
            var index = IndexOf(parameterName);
            if (index < 0)
                _items.Add(value);
            else
                _items[index] = value;
        }
    }

    private sealed class TestDbParameter : DbParameter
    {
        public TestDbParameter()
        {
        }

        public TestDbParameter(string parameterName, DbType dbType, object? value)
        {
            ParameterName = parameterName;
            DbType = dbType;
            Value = value;
        }

        public override DbType DbType { get; set; }
        public override ParameterDirection Direction { get; set; } = ParameterDirection.Input;
        public override bool IsNullable { get; set; }
        public override string? ParameterName { get; set; }
        public override string? SourceColumn { get; set; }
        public override object? Value { get; set; }
        public override bool SourceColumnNullMapping { get; set; }
        public override int Size { get; set; }

        public override void ResetDbType()
        {
        }
    }

    private sealed class TestObserver : IEfCoreSecondLevelCacheObserver
    {
    }
}

#pragma warning restore CS8765
#pragma warning restore CS8764
