namespace SRPC;

public class SrpcException : Exception
{
    public SrpcException(string message) : base(message) { }
    public SrpcException(string message, Exception innerException) : base(message, innerException) { }
}

public sealed class ConnectionException : SrpcException
{
    public ConnectionException(string message) : base(message) { }
    public ConnectionException(string message, Exception innerException) : base(message, innerException) { }
}

public sealed class ProtocolException : SrpcException
{
    public ProtocolException(string message) : base(message) { }
    public ProtocolException(string message, Exception innerException) : base(message, innerException) { }
}

public sealed class CommandException : SrpcException
{
    public int StatusCode { get; }

    public CommandException(int statusCode, string message)
        : base($"XBDM command failed with status {statusCode}: {message}") => StatusCode = statusCode;
}

public sealed class RpcException : SrpcException
{
    public RpcException(string message) : base(message) { }
    public RpcException(string message, Exception innerException) : base(message, innerException) { }
}

public sealed class SrpcTimeoutException : SrpcException
{
    public SrpcTimeoutException(string message) : base(message) { }
    public SrpcTimeoutException(string message, Exception innerException) : base(message, innerException) { }
}
