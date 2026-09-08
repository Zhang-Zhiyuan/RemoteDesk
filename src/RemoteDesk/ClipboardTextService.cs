using System.Collections.Specialized;
using System.Runtime.InteropServices;

namespace RemoteDesk;

internal static class ClipboardTextService
{
    private static readonly TimeSpan ClipboardOperationTimeout = TimeSpan.FromSeconds(3);
    private const int ClipboardRetryCount = 8;
    private const int ClipboardRetryDelayMs = 50;
    private const int ClipboardSequencePollCount = 40;
    private static readonly TimeSpan ClipboardSequencePollInterval = TimeSpan.FromMilliseconds(15);
    internal const string PreferredDropEffectFormat = "Preferred DropEffect";
    internal const string DropEffectFormat = "DropEffect";

    public static Task<string> GetTextAsync()
    {
        return RunStaAsync(() => RunClipboardOperationWithRetries(ReadClipboardText));
    }

    public static Task SetTextAsync(string text)
    {
        return RunStaAsync(() => RunClipboardOperationWithRetries<object?>(() =>
        {
            SetClipboardText(text);
            return null;
        }));
    }

    public static Task<IReadOnlyList<string>> GetFileDropListAsync()
    {
        return RunStaAsync<IReadOnlyList<string>>(() => RunClipboardOperationWithRetries(ReadFileDropList));
    }

    public static Task SetFileDropListAsync(IEnumerable<string> paths)
    {
        string[] normalizedPaths = NormalizeFileDropPaths(paths);
        if (normalizedPaths.Length == 0)
        {
            throw new InvalidOperationException("没有可写入剪贴板的文件。");
        }

        return RunStaAsync(() => RunClipboardOperationWithRetries<object?>(() =>
        {
            Clipboard.SetDataObject(CreateFileDropDataObject(normalizedPaths), copy: true, ClipboardRetryCount, ClipboardRetryDelayMs);
            return null;
        }));
    }

    internal static uint ReadClipboardSequenceNumber()
    {
        return GetClipboardSequenceNumber();
    }

    internal static Task<bool> WaitForClipboardSequenceChangeAsync(
        uint baseline,
        CancellationToken cancellationToken)
    {
        return WaitForClipboardSequenceChangeAsync(
            baseline,
            ReadClipboardSequenceNumber,
            ClipboardSequencePollCount,
            ClipboardSequencePollInterval,
            static (delay, token) => Task.Delay(delay, token),
            cancellationToken);
    }

    internal static async Task<bool> WaitForClipboardSequenceChangeAsync(
        uint baseline,
        Func<uint> readSequence,
        int pollCount,
        TimeSpan pollInterval,
        Func<TimeSpan, CancellationToken, Task> delayAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(readSequence);
        ArgumentNullException.ThrowIfNull(delayAsync);
        ArgumentOutOfRangeException.ThrowIfNegative(pollCount);
        if (pollInterval < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(pollInterval));
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (readSequence() != baseline)
        {
            return true;
        }

        for (int poll = 0; poll < pollCount; poll++)
        {
            await delayAsync(pollInterval, cancellationToken).ConfigureAwait(false);
            if (readSequence() != baseline)
            {
                return true;
            }
        }

        return false;
    }

    internal static DataObject CreateFileDropDataObject(IEnumerable<string> paths)
    {
        string[] normalizedPaths = NormalizeFileDropPaths(paths);
        if (normalizedPaths.Length == 0)
        {
            throw new InvalidOperationException("没有可写入剪贴板的文件。");
        }

        var fileDropList = new StringCollection();
        fileDropList.AddRange(normalizedPaths);

        var dataObject = new DataObject();
        dataObject.SetFileDropList(fileDropList);
        dataObject.SetData(PreferredDropEffectFormat, CreateDropEffectStream(DragDropEffects.Copy));
        dataObject.SetData(DropEffectFormat, CreateDropEffectStream(DragDropEffects.Copy));
        return dataObject;
    }

    internal static MemoryStream CreateDropEffectStream(DragDropEffects effect)
    {
        return new MemoryStream(BitConverter.GetBytes((int)effect));
    }

    internal static T RunClipboardOperationWithRetries<T>(Func<T> operation, int retryCount, int retryDelayMs)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (retryCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(retryCount));
        }

        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return operation();
            }
            catch (ExternalException) when (attempt < retryCount - 1)
            {
                if (retryDelayMs > 0)
                {
                    Thread.Sleep(retryDelayMs);
                }
            }
        }
    }

    private static T RunClipboardOperationWithRetries<T>(Func<T> operation)
    {
        return RunClipboardOperationWithRetries(operation, ClipboardRetryCount, ClipboardRetryDelayMs);
    }

    private static string ReadClipboardText()
    {
        return Clipboard.ContainsText(TextDataFormat.UnicodeText)
            ? Clipboard.GetText(TextDataFormat.UnicodeText)
            : string.Empty;
    }

    private static string[] ReadFileDropList()
    {
        if (Clipboard.ContainsFileDropList())
        {
            return CopyFileDropList(Clipboard.GetFileDropList());
        }

        IDataObject? dataObject = Clipboard.GetDataObject();
        if (dataObject?.GetDataPresent(DataFormats.FileDrop, autoConvert: true) == true &&
            dataObject.GetData(DataFormats.FileDrop, autoConvert: true) is string[] fileDropPaths)
        {
            return NormalizeFileDropPaths(fileDropPaths);
        }

        return Array.Empty<string>();
    }

    private static void SetClipboardText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            Clipboard.Clear();
            return;
        }

        Clipboard.SetText(text, TextDataFormat.UnicodeText);
    }

    private static string[] CopyFileDropList(StringCollection fileDropList)
    {
        return NormalizeFileDropPaths(fileDropList.Cast<string?>());
    }

    internal static string[] NormalizeFileDropPaths(IEnumerable<string?> paths)
    {
        var normalized = new List<string>();
        foreach (string? path in paths)
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                normalized.Add(path.Trim());
            }
        }

        return normalized.ToArray();
    }

    private static Task<T> RunStaAsync<T>(Func<T> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                completion.SetResult(action());
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return completion.Task.WaitAsync(ClipboardOperationTimeout);
    }

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();
}
