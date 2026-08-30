using System.Buffers.Binary;
using SRPC.Internal;

namespace SRPC;

public sealed partial class SrpcClient
{
    public byte[] DownloadFile(string remotePath, int maximumSize = 512 * 1024 * 1024)
    {
        if (string.IsNullOrEmpty(remotePath)) throw new ProtocolException("Remote file path cannot be empty.");
        if (maximumSize < 0) throw new ArgumentOutOfRangeException(nameof(maximumSize));
        lock (_sync)
        {
            var response = SendCommandRawLocked("getfile name=" + ProtocolCodec.QuoteArgument(remotePath, "Remote file path"));
            ProtocolCodec.ThrowIfCommandFailed(response); if (response.StatusCode == 202) ReadMultilineLocked();
            if (response.StatusCode != 203) { if (response.StatusCode is not (200 or 201 or 202)) { _connection.Close(); _selectedProtocol = _options.Protocol; } throw new ProtocolException($"getfile returned status {response.StatusCode} instead of 203."); }
            var size = BinaryPrimitives.ReadUInt32LittleEndian(_connection.ReadExact(4));
            if (size > maximumSize || size > int.MaxValue) { _connection.Close(); _selectedProtocol = _options.Protocol; throw new ProtocolException($"Remote file is {size} bytes, over the configured download limit of {maximumSize}."); }
            return _connection.ReadExact((int)size);
        }
    }

    public void UploadFile(string remotePath, ReadOnlySpan<byte> data, bool overwrite = false)
    {
        if (string.IsNullOrEmpty(remotePath)) throw new ProtocolException("Remote file path cannot be empty.");
        var quoted = ProtocolCodec.QuoteArgument(remotePath, "Remote file path"); var command = $"sendfile name={quoted} length=0x{data.Length:X}";
        lock (_sync)
        {
            var response = SendCommandRawLocked(command);
            if (response.StatusCode == 410 && overwrite) { SendCommandLocked("delete name=" + quoted); response = SendCommandRawLocked(command); }
            ProtocolCodec.ThrowIfCommandFailed(response); if (response.StatusCode != 204) throw new ProtocolException($"sendfile returned status {response.StatusCode} instead of 204.");
            _connection.SendAll(data); var final = ProtocolCodec.ParseResponseLine(_connection.ReadLine()); ProtocolCodec.ThrowIfCommandFailed(final);
            if (final.StatusCode != 200) throw new ProtocolException($"sendfile completion returned status {final.StatusCode} instead of 200.");
        }
    }

    private static void ValidateTransferOptions(FileTransferOptions options)
    {
        if (options.ChunkSize is <= 0 or > 16 * 1024 * 1024) throw new ProtocolException("File transfer chunk size must be between 1 byte and 16 MiB.");
    }
    private static TransferControl Progress(FileTransferOptions options, TransferPhase phase, string local, string remote, ulong transferred, ulong total) =>
        options.Progress?.Invoke(new TransferProgress(phase, local, remote, transferred, total, transferred, total, 1, 1)) ?? TransferControl.Continue;

    public TransferResult DownloadFileTo(string remotePath, string localPath, FileTransferOptions? options = null)
    {
        options ??= new FileTransferOptions(); ValidateTransferOptions(options);
        if (string.IsNullOrEmpty(remotePath) || string.IsNullOrEmpty(localPath)) throw new ProtocolException("Remote and local file paths cannot be empty.");
        if (Directory.Exists(localPath)) throw new IOException("Local destination is a directory.");
        if (File.Exists(localPath))
        {
            if (options.ExistingFile == ExistingFilePolicy.Skip) { var size = (ulong)new FileInfo(localPath).Length; return new TransferResult(Progress(options, TransferPhase.FileSkipped, localPath, remotePath, 0, size) == TransferControl.Cancel, 0, 1); }
            if (options.ExistingFile == ExistingFilePolicy.Fail) throw new IOException("Local destination file already exists.");
        }
        var temp = localPath + $".srpc-part-{Environment.ProcessId}-{Guid.NewGuid():N}"; ulong fileSize = 0, transferred = 0; var cancelled = false;
        try
        {
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            lock (_sync)
            {
                var response = SendCommandRawLocked("getfile name=" + ProtocolCodec.QuoteArgument(remotePath, "Remote file path")); ProtocolCodec.ThrowIfCommandFailed(response);
                if (response.StatusCode != 203) throw new ProtocolException($"getfile returned status {response.StatusCode} instead of 203.");
                fileSize = BinaryPrimitives.ReadUInt32LittleEndian(_connection.ReadExact(4));
                if (fileSize > options.MaximumFileSize) { AbandonBinaryLocked(); throw new ProtocolException($"Remote file exceeds the configured download limit of {options.MaximumFileSize}."); }
                _binaryTransferActive = true;
                try
                {
                    if (Progress(options, TransferPhase.Transferring, localPath, remotePath, 0, fileSize) == TransferControl.Cancel) { cancelled = true; AbandonBinaryLocked(); }
                    while (!cancelled && transferred < fileSize)
                    {
                        var count = (int)Math.Min((ulong)options.ChunkSize, fileSize - transferred); var bytes = _connection.ReadExact(count); output.Write(bytes); transferred += (ulong)count;
                        if (Progress(options, TransferPhase.Transferring, localPath, remotePath, transferred, fileSize) == TransferControl.Cancel) { cancelled = true; AbandonBinaryLocked(); }
                    }
                    if (!cancelled) _binaryTransferActive = false;
                }
                catch { if (_binaryTransferActive) AbandonBinaryLocked(); throw; }
            }
            if (cancelled) { if (options.RemovePartialFile) File.Delete(temp); return new TransferResult(true); }
            File.Move(temp, localPath, options.ExistingFile == ExistingFilePolicy.Overwrite);
        }
        catch { if (options.RemovePartialFile && File.Exists(temp)) File.Delete(temp); throw; }
        return new TransferResult(Progress(options, TransferPhase.FileComplete, localPath, remotePath, fileSize, fileSize) == TransferControl.Cancel, 1, 0, fileSize);
    }

    public TransferResult UploadFileFrom(string localPath, string remotePath, FileTransferOptions? options = null)
    {
        options ??= new FileTransferOptions(); ValidateTransferOptions(options);
        if (!File.Exists(localPath) || IsReparse(localPath)) throw new ProtocolException("Upload source must be a regular, non-reparse-point file.");
        if (string.IsNullOrEmpty(remotePath)) throw new ProtocolException("Remote file path cannot be empty.");
        var size = (ulong)new FileInfo(localPath).Length; if (size > uint.MaxValue || size > options.MaximumFileSize) throw new ProtocolException("Local file exceeds the configured upload limit.");
        if (Progress(options, TransferPhase.Transferring, localPath, remotePath, 0, size) == TransferControl.Cancel) return new TransferResult(true);
        var quoted = ProtocolCodec.QuoteArgument(remotePath, "Remote file path"); var command = $"sendfile name={quoted} length=0x{size:X}"; var skipped = false; var cancelled = false; ulong transferred = 0;
        using var input = File.OpenRead(localPath);
        lock (_sync)
        {
            var response = SendCommandRawLocked(command);
            if (response.StatusCode == 410)
            {
                if (options.ExistingFile == ExistingFilePolicy.Skip) skipped = true;
                else if (options.ExistingFile == ExistingFilePolicy.Overwrite) { SendCommandLocked("delete name=" + quoted); response = SendCommandRawLocked(command); }
            }
            if (!skipped)
            {
                ProtocolCodec.ThrowIfCommandFailed(response); if (response.StatusCode != 204) throw new ProtocolException($"sendfile returned status {response.StatusCode} instead of 204.");
                _binaryTransferActive = true;
                try
                {
                    var buffer = new byte[options.ChunkSize];
                    while (transferred < size)
                    {
                        var count = (int)Math.Min((ulong)buffer.Length, size - transferred); input.ReadExactly(buffer, 0, count); _connection.SendAll(buffer.AsSpan(0, count)); transferred += (ulong)count;
                        if (Progress(options, TransferPhase.Transferring, localPath, remotePath, transferred, size) == TransferControl.Cancel) { cancelled = true; AbandonBinaryLocked(); break; }
                    }
                    if (!cancelled) { var final = ProtocolCodec.ParseResponseLine(_connection.ReadLine()); _binaryTransferActive = false; ProtocolCodec.ThrowIfCommandFailed(final); if (final.StatusCode != 200) throw new ProtocolException($"sendfile completion returned status {final.StatusCode} instead of 200."); }
                }
                catch { if (_binaryTransferActive) AbandonBinaryLocked(); throw; }
            }
        }
        if (skipped) return new TransferResult(Progress(options, TransferPhase.FileSkipped, localPath, remotePath, 0, size) == TransferControl.Cancel, 0, 1);
        if (cancelled) return new TransferResult(true);
        return new TransferResult(Progress(options, TransferPhase.FileComplete, localPath, remotePath, size, size) == TransferControl.Cancel, 1, 0, size);
    }

    private sealed record ManifestEntry(string Local, string Remote, ulong Size, bool Directory);
    private static bool IsReparse(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
    private static readonly char[] InvalidXbox = { '\\', '/', ':', '"' };
    private static void ValidateComponent(string name)
    {
        if (name.Length == 0 || name is "." or ".." || name.IndexOfAny(InvalidXbox) >= 0 || name.Any(c => c < 0x20 || c == 0x7f)) throw new ProtocolException($"Invalid Xbox path component: {name}");
        if (name.EndsWith(' ') || name.EndsWith('.') || name.IndexOfAny(new[] { '<', '>', '|', '?', '*' }) >= 0) throw new ProtocolException($"Path component cannot be represented safely: {name}");
        var stem = name.Split('.')[0]; if (new[] { "con", "prn", "aux", "nul", "clock$", "conin$", "conout$" }.Contains(stem, StringComparer.OrdinalIgnoreCase) || stem.Length == 4 && (stem.StartsWith("com", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("lpt", StringComparison.OrdinalIgnoreCase)) && stem[3] is >= '1' and <= '9') throw new ProtocolException($"Reserved Windows filename: {name}");
    }
    private static string JoinRemote(string parent, string component) { ValidateComponent(component); return parent.TrimEnd('\\', '/') + "\\" + component; }
    private static void ValidateDirectoryOptions(DirectoryTransferOptions options)
    {
        ValidateTransferOptions(options.Files); if (options.MaximumDepth < 0 || options.MaximumEntries < 0) throw new ProtocolException("Directory transfer limits cannot be negative.");
    }
    private static bool ScanProgress(DirectoryTransferOptions options, string local, string remote, ulong size, int fileIndex) => options.Files.Progress?.Invoke(new TransferProgress(TransferPhase.Scanning, local, remote, 0, size, 0, null, fileIndex, null)) == TransferControl.Cancel;

    public TransferResult DownloadDirectoryTo(string remoteRoot, string localRoot, DirectoryTransferOptions? options = null)
    {
        options ??= new DirectoryTransferOptions(); ValidateDirectoryOptions(options); if (string.IsNullOrEmpty(remoteRoot) || string.IsNullOrEmpty(localRoot)) throw new ProtocolException("Transfer roots cannot be empty.");
        var manifest = new List<ManifestEntry>(); ulong total = 0; var files = 0; var cancelled = ScanProgress(options, localRoot, remoteRoot, 0, 0);
        void Scan(string remote, string local, int depth)
        {
            if (cancelled) return;
            var collisions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var child in DirectoryContents(remote).OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (depth >= options.MaximumDepth) throw new ProtocolException("Remote directory exceeds the configured recursion depth.");
                ValidateComponent(child.Name); if (!collisions.Add(child.Name)) throw new ProtocolException($"Remote directory contains names that collide on Windows: {child.Name}");
                var entry = new ManifestEntry(Path.Combine(local, child.Name), JoinRemote(remote, child.Name), child.Size, child.IsDirectory); manifest.Add(entry);
                if (manifest.Count > options.MaximumEntries) throw new ProtocolException("Directory manifest exceeds the configured entry limit.");
                if (!child.IsDirectory) { files++; total = checked(total + child.Size); if (child.Size > options.Files.MaximumFileSize || options.MaximumTotalSize is ulong max && total > max) throw new ProtocolException("Directory manifest exceeds a configured size limit."); }
                if (ScanProgress(options, entry.Local, entry.Remote, entry.Size, files)) { cancelled = true; return; }
                if (entry.Directory) Scan(entry.Remote, entry.Local, depth + 1);
            }
        }
        Scan(remoteRoot.Replace('/', '\\'), localRoot, 0); if (cancelled) return new TransferResult(true);
        if (!Directory.Exists(localRoot)) { if (!options.CreateDestinationRoot) throw new DirectoryNotFoundException(localRoot); Directory.CreateDirectory(localRoot); }
        var result = new TransferResult(); var index = 0;
        foreach (var entry in manifest)
        {
            if (entry.Directory) { Directory.CreateDirectory(entry.Local); continue; } index++;
            var fileOptions = AggregateOptions(options, entry, total, files, result, index); var file = DownloadFileTo(entry.Remote, entry.Local, fileOptions); result = Add(result, file); if (result.Cancelled) break;
        }
        return result;
    }

    public TransferResult UploadDirectoryFrom(string localRoot, string remoteRoot, DirectoryTransferOptions? options = null)
    {
        options ??= new DirectoryTransferOptions(); ValidateDirectoryOptions(options); if (!Directory.Exists(localRoot) || IsReparse(localRoot)) throw new ProtocolException("Local upload root must be a non-reparse-point directory.");
        var manifest = new List<ManifestEntry>(); ulong total = 0; var files = 0; var cancelled = ScanProgress(options, localRoot, remoteRoot, 0, 0);
        void Scan(string local, string remote, int depth)
        {
            if (cancelled) return;
            var collisions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in Directory.EnumerateFileSystemEntries(local).OrderBy(p => Path.GetFileName(p), StringComparer.Ordinal))
            {
                if (depth >= options.MaximumDepth) throw new ProtocolException("Local directory exceeds the configured recursion depth.");
                if (IsReparse(path)) throw new ProtocolException($"Refusing to traverse nested local reparse point: {path}"); var name = Path.GetFileName(path); ValidateComponent(name); if (!collisions.Add(name)) throw new ProtocolException($"Local directory contains names that collide on Xbox: {name}");
                var directory = Directory.Exists(path); var size = directory ? 0UL : (ulong)new FileInfo(path).Length; var entry = new ManifestEntry(path, JoinRemote(remote, name), size, directory); manifest.Add(entry);
                if (manifest.Count > options.MaximumEntries) throw new ProtocolException("Directory manifest exceeds the configured entry limit.");
                if (!directory) { files++; total = checked(total + size); if (size > options.Files.MaximumFileSize || options.MaximumTotalSize is ulong max && total > max) throw new ProtocolException("Directory manifest exceeds a configured size limit."); }
                if (ScanProgress(options, path, entry.Remote, size, files)) { cancelled = true; return; } if (directory) Scan(path, entry.Remote, depth + 1);
            }
        }
        Scan(localRoot, remoteRoot.Replace('/', '\\'), 0); if (cancelled) return new TransferResult(true); EnsureRemoteDirectory(remoteRoot, options.CreateDestinationRoot);
        var result = new TransferResult(); var index = 0;
        foreach (var entry in manifest)
        {
            if (entry.Directory) { EnsureRemoteDirectory(entry.Remote, true); continue; } index++;
            var fileOptions = AggregateOptions(options, entry, total, files, result, index); var file = UploadFileFrom(entry.Local, entry.Remote, fileOptions); result = Add(result, file); if (result.Cancelled) break;
        }
        return result;
    }
    private void EnsureRemoteDirectory(string path, bool create)
    {
        if (!create) { if (!IsDirectory(path)) throw new ProtocolException($"Remote destination root is not an existing directory: {path}"); return; }
        try { CreateDirectory(path); } catch (CommandException ex) when (ex.StatusCode == 410) { if (!IsDirectory(path)) throw new ProtocolException($"Remote directory path is occupied by a non-directory: {path}"); }
    }
    private static FileTransferOptions AggregateOptions(DirectoryTransferOptions directory, ManifestEntry entry, ulong total, int files, TransferResult completed, int index)
    {
        var result = directory.Files.Copy(); var callback = directory.Files.Progress;
        if (callback is not null) result.Progress = progress => callback(progress with { OverallBytesTransferred = completed.BytesTransferred + progress.FileBytesTransferred, OverallBytesTotal = total, FileIndex = index, FileCount = files, LocalPath = entry.Local, RemotePath = entry.Remote });
        return result;
    }
    private static TransferResult Add(TransferResult left, TransferResult right) => new(right.Cancelled, checked(left.FilesCompleted + right.FilesCompleted), checked(left.FilesSkipped + right.FilesSkipped), checked(left.BytesTransferred + right.BytesTransferred));
}
