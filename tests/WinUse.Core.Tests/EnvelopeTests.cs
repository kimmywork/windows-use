using System.Text.Json;
using WinUse.Core.Errors;
using WinUse.Core.Protocol;

namespace WinUse.Core.Tests;

/// <summary>协议信封往返（09 M0.5 G-A；06 §2）。</summary>
public class EnvelopeTests
{
    [Fact]
    public void Request_round_trips()
    {
        var json = """
        {"jsonrpc":"2.0","id":7,"method":"display.list","params":{},"meta":{"client":"x"}}
        """;
        var request = JsonSerializer.Deserialize<RpcRequest>(json)!;

        Assert.Equal("2.0", request.JsonRpc);
        Assert.Equal(7, request.Id!.Value.GetInt32());
        Assert.Equal("display.list", request.Method);
        Assert.NotNull(request.Params);
        Assert.Equal("x", request.Meta!.Client);
    }

    [Fact]
    public void Notification_has_null_id()
    {
        var request = JsonSerializer.Deserialize<RpcRequest>(
            """{"jsonrpc":"2.0","method":"window.list"}""")!;
        Assert.Null(request.Id);
    }

    [Fact]
    public void Ok_and_error_are_exclusive_in_shape()
    {
        var ok = RpcResponse.Ok(JsonSerializer.SerializeToElement(1), new { a = 1 });
        var fail = RpcResponse.Fail(JsonSerializer.SerializeToElement(1),
            ErrorCatalog.Rpc("TIMEOUT"));

        Assert.Null(ok.Error);
        Assert.Null(fail.Result);
    }
}

/// <summary>错误码断言（09 M0.5 G-A；06 §9）。</summary>
public class ErrorCatalogTests
{
    [Theory]
    [InlineData("ELEMENT_NOT_FOUND", -32000)]
    [InlineData("CAPTURE_PROTECTED", -32011)]
    [InlineData("RESERVED_KEY", -32025)]
    [InlineData("NOT_IMPLEMENTED", -32033)]
    public void Business_codes_match(string type, int expectedCode)
    {
        var error = ErrorCatalog.Rpc(type);
        Assert.Equal(expectedCode, error.Code);
        Assert.Equal(type, error.Type);
    }

    [Fact]
    public void Standard_codes_match()
    {
        Assert.Equal(-32700, ErrorCatalog.ParseError);
        Assert.Equal(-32601, ErrorCatalog.MethodNotFound);
        Assert.Equal(-32603, ErrorCatalog.InternalError);
    }

    [Fact]
    public void Unknown_type_throws_not_silent()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ErrorCatalog.Rpc("NOPE"));
    }
}
