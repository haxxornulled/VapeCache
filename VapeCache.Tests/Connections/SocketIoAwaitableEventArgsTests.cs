using System.Net.Sockets;
using VapeCache.Infrastructure.Connections;

namespace VapeCache.Tests.Connections;

public sealed class SocketIoAwaitableEventArgsTests
{
    [Fact]
    public async Task BeginForTests_completes_with_result()
    {
        var args = new SocketIoAwaitableEventArgs();

        var pending = args.BeginForTests();
        args.CompleteForTests(7);
        var bytes = await pending;

        Assert.Equal(7, bytes);
    }

    [Fact]
    public async Task WaitAsync_with_buffer_list_completes()
    {
        var args = new SocketIoAwaitableEventArgs();
        args.ResetForOperation();
        args.SetBuffer(new[] { new ArraySegment<byte>(Array.Empty<byte>()) }, 1);

        var pending = args.WaitAsync();
        args.CompleteForTests(1);
        var bytes = await pending;

        Assert.Equal(1, bytes);
        args.ReturnBufferList();
    }

    [Fact]
    public async Task CompleteForTests_with_error_surfaces_socket_exception()
    {
        var args = new SocketIoAwaitableEventArgs();
        var pending = args.BeginForTests();
        args.CompleteForTests(0, SocketError.ConnectionReset);

        await Assert.ThrowsAsync<SocketException>(async () => await pending);
    }

    [Fact]
    public void SetBufferList_throws_for_invalid_count()
    {
        var args = new SocketIoAwaitableEventArgs();

        Assert.Throws<ArgumentOutOfRangeException>(() => args.SetBuffer(Array.Empty<ArraySegment<byte>>(), 0));
    }
}
