using System.Text.Json.Serialization;

namespace VapeCache.Console.GroceryStore;

[JsonSerializable(typeof(Product))]
[JsonSerializable(typeof(CartItem))]
[JsonSerializable(typeof(CartItem[]))]
[JsonSerializable(typeof(UserSession))]
internal sealed partial class GroceryStoreJsonContext : JsonSerializerContext
{
}
