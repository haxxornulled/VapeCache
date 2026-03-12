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

    [Fact]
    public void SetBufferList_with_offset_uses_requested_slice()
    {
        var args = new SocketIoAwaitableEventArgs();
        var buffers = new[]
        {
            new ArraySegment<byte>(new byte[] { 1, 2 }, 0, 2),
            new ArraySegment<byte>(new byte[] { 3, 4, 5 }, 1, 2),
            new ArraySegment<byte>(new byte[] { 6, 7, 8, 9 }, 2, 2)
        };

        args.SetBufferList(buffers, offset: 1, count: 2);
        var selected = args.BufferList!.ToArray();

        Assert.Equal(buffers[1], selected[0]);
        Assert.Equal(buffers[2], selected[1]);

        args.ReturnBufferList();
    }

    [Fact]
    public void SetBufferList_throws_for_invalid_offset_and_count_window()
    {
        var args = new SocketIoAwaitableEventArgs();
        var buffers = new[]
        {
            new ArraySegment<byte>(new byte[] { 1 }),
            new ArraySegment<byte>(new byte[] { 2 }),
            new ArraySegment<byte>(new byte[] { 3 })
        };

        Assert.Throws<ArgumentOutOfRangeException>(() => args.SetBufferList(buffers, offset: -1, count: 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => args.SetBufferList(buffers, offset: 2, count: 2));
    }
}
