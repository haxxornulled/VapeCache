namespace VapeCache.Infrastructure.Connections;

internal enum RedisResponseMode : byte
{
    Default = 0,
    BulkStringArrayCountAllowNulls = 1,
    ZRangeWithScoresCount = 2,
    BulkStringDiscard = 3,
    FtSearchCount = 4,
    TimeSeriesRangeCount = 5
}
