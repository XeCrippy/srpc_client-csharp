using System.Collections.ObjectModel;

namespace SRPC;

public enum Protocol { Automatic, NativeSrpc, Jrpc2 }
public enum ReturnType : uint { Void = 0, Int32 = 1, String = 2, Float32 = 3, Byte = 4, Int32Array = 5, FloatArray = 6, ByteArray = 7, UInt64 = 8 }
public enum Endian { Big, Little }
public enum TemperatureSensor : byte { Cpu = 0, Gpu = 1, Memory = 2, Motherboard = 3 }
public enum LedColor : byte { Off = 0, Red = 8, Green = 128, Orange = 136 }
public enum SignInState : uint { NotSignedIn = 0, SignedInLocally = 1, SignedInToXboxLive = 2, GuestAccountLocally = 3, GuestAccountXboxLive = 4 }

public enum NotificationType : uint
{
    FriendOnline = 0, GameInvite = 1, FriendRequest = 2, Generic = 3, MultiPending = 4,
    PersonalMessage = 5, SignedOut = 6, SignedIn = 7, SignedInLive = 8, SignedInNeedPass = 9,
    ChatRequest = 10, ConnectionLost = 11, DownloadComplete = 12, SongPlaying = 13,
    PreferredReview = 14, AvoidReview = 15, Complaint = 16, ChatCallback = 17, RemovedMu = 18,
    RemovedGamepad = 19, ChatJoin = 20, ChatLeave = 21, GameInviteSent = 22,
    CancelPersistent = 23, ChatCallbackSent = 24, MultiFriendOnline = 25, OneFriendOnline = 26,
    Achievement = 27, HybridDisc = 28, Mailbox = 29, VideoChatInvite = 30,
    DownloadCompletedReadyToPlay = 31, CannotDownload = 32, DownloadStopped = 33,
    ConsoleMessage = 34, GameMessage = 35, DeviceFull = 36, ChatMessage = 38,
    MultiAchievements = 39, Nudge = 40, MessengerConnectionLost = 41,
    MessengerSignInFailed = 43, MessengerConversationMissed = 44, FamilyTimerRemaining = 45,
    ConnectionLostReconnect = 46, ExcessivePlayTime = 47, PartyJoinRequest = 49,
    PartyInviteSent = 50, PartyGameInviteSent = 51, PartyKicked = 52, PartyDisconnected = 53,
    PartyCannotConnect = 56, PartySomeoneJoined = 57, PartySomeoneLeft = 58,
    GamerPictureUnlocked = 59, AvatarAwardUnlocked = 60, PartyJoined = 61, RemovedUsb = 62,
    PlayerMuted = 63, PlayerUnmuted = 64, ChatMessage2 = 65, KinectConnected = 66,
    KinectBreak = 67, Ethernet = 68, KinectPlayerRecognized = 69,
    ConsoleShuttingDownSoonAlert = 70, ProfileSignedInElsewhere = 71, LastSignInElsewhere = 73,
    KinectDeviceUnsupported = 74, WirelessDeviceTurnOff = 75, Updating = 76,
    SmartglassAvailable = 77
}

public sealed record DirectoryEntry(string Name, ulong Size = 0, ulong? CreatedFileTime = null, ulong? ChangedFileTime = null, bool IsDirectory = false);
public sealed record ModuleInfo(string Name, uint Base, uint Size, uint Checksum = 0, uint Timestamp = 0, bool IsDll = false) { public ulong End => (ulong)Base + Size; }
public sealed record MemoryRegion(uint Base, uint Size, uint Protection = 0) { public ulong End => (ulong)Base + Size; }
public sealed record ExecutablePoolInfo(uint Used, uint Free);
public enum ExecutablePoolReset { ConfirmLiveAllocationsMayBeOverwritten }
public sealed record Response(int StatusCode, string Message) { public bool IsSuccess => StatusCode is >= 200 and < 300; }

public sealed class ClientOptions
{
    public Protocol Protocol { get; set; } = Protocol.Automatic;
    public ushort Port { get; set; } = 730;
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan IoTimeout { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan RpcTimeout { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(50);

    internal ClientOptions Copy() => (ClientOptions)MemberwiseClone();
}

public sealed class CallOptions
{
    public bool SystemThread { get; set; } = true;
    public bool VirtualMachine { get; set; }
    public uint ArraySize { get; set; }

    internal CallOptions Copy() => (CallOptions)MemberwiseClone();
}

public readonly struct RpcArgument
{
    internal object Value { get; }
    private RpcArgument(object value) => Value = value;
    public static implicit operator RpcArgument(bool value) => new(value);
    public static implicit operator RpcArgument(int value) => new(value);
    public static implicit operator RpcArgument(uint value) => new(value);
    public static implicit operator RpcArgument(long value) => new(value);
    public static implicit operator RpcArgument(ulong value) => new(value);
    public static implicit operator RpcArgument(float value) => new(value);
    public static implicit operator RpcArgument(string? value) => new(value ?? string.Empty);
    public static implicit operator RpcArgument(byte[] value) => new(value ?? throw new ArgumentNullException(nameof(value)));
}

public sealed class RpcValue
{
    public object? Value { get; }
    internal RpcValue(object? value) => Value = value;
    public T As<T>() => Value is T typed ? typed : throw new InvalidCastException($"RPC value is {Value?.GetType().Name ?? "void"}, not {typeof(T).Name}.");
}

public enum ExistingFilePolicy { Fail, Overwrite, Skip }
public enum TransferPhase { Scanning, Transferring, FileComplete, FileSkipped }
public enum TransferControl { Continue, Cancel }

public sealed record TransferProgress(
    TransferPhase Phase,
    string LocalPath,
    string RemotePath,
    ulong FileBytesTransferred,
    ulong FileBytesTotal,
    ulong OverallBytesTransferred,
    ulong? OverallBytesTotal,
    int FileIndex,
    int? FileCount);

public sealed class FileTransferOptions
{
    public ExistingFilePolicy ExistingFile { get; set; } = ExistingFilePolicy.Fail;
    public int ChunkSize { get; set; } = 64 * 1024;
    public ulong MaximumFileSize { get; set; } = 512UL * 1024 * 1024;
    public bool RemovePartialFile { get; set; } = true;
    public Func<TransferProgress, TransferControl>? Progress { get; set; }
    internal FileTransferOptions Copy() => (FileTransferOptions)MemberwiseClone();
}

public sealed class DirectoryTransferOptions
{
    public FileTransferOptions Files { get; set; } = new();
    public bool CreateDestinationRoot { get; set; } = true;
    public int MaximumDepth { get; set; } = 64;
    public int MaximumEntries { get; set; } = 100_000;
    public ulong? MaximumTotalSize { get; set; }
}

public sealed record TransferResult(bool Cancelled = false, int FilesCompleted = 0, int FilesSkipped = 0, ulong BytesTransferred = 0);
