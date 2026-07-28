namespace AiLife.WindowsAgent.Runtime;

public static class Logger
{
    private static readonly object Lock = new();
    private static StreamWriter? _writer;
    private static DateOnly _currentDate;

    public static string LogPath => Path.Combine(AppContext.BaseDirectory, "agent.log");

    public static void Info(string message) => Write("INFO", message);
    public static void Warning(string message) => Write("WARN", message);
    public static void Error(string message) => Write("ERROR", message);

    public static void SetFileLogging(bool enabled)
    {
        lock (Lock)
        {
            if (enabled && _writer is null)
            {
                OpenWriter();
            }
            else if (!enabled && _writer is not null)
            {
                _writer.Dispose();
                _writer = null;
            }
        }
    }

    public static void Shutdown()
    {
        lock (Lock)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    private static void Write(string level, string message)
    {
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}";
        Console.WriteLine(line);

        lock (Lock)
        {
            try
            {
                RotateIfNeeded();
                _writer?.WriteLine(line);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static void OpenWriter()
    {
        try
        {
            _currentDate = DateOnly.FromDateTime(DateTime.Now);
            _writer = new StreamWriter(LogPath, append: true, encoding: new System.Text.UTF8Encoding(false))
            {
                AutoFlush = true,
            };
        }
        catch (IOException)
        {
            _writer = null;
        }
        catch (UnauthorizedAccessException)
        {
            _writer = null;
        }
    }

    private static void RotateIfNeeded()
    {
        if (_writer is null) return;

        var today = DateOnly.FromDateTime(DateTime.Now);
        if (today == _currentDate) return;

        _writer.Dispose();
        _writer = null;

        try
        {
            var archive = Path.Combine(AppContext.BaseDirectory, $"agent_{_currentDate:yyyyMMdd}.log");
            if (File.Exists(LogPath))
            {
                File.Move(LogPath, archive, overwrite: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        OpenWriter();
    }
}

