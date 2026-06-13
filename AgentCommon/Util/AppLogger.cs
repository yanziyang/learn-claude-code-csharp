namespace AgentCommon.Util;

public sealed class AppLogger : IDisposable
{
    private readonly object _lock = new();
    private readonly string _logDir;
    private DateOnly _currentDate;
    private string _currentFile;

    public AppLogger(string workDir, string subdir = "logs")
    {
        _logDir = Path.Combine(workDir, subdir);
        Directory.CreateDirectory(_logDir);
        _currentDate = DateOnly.FromDateTime(DateTime.Now);
        _currentFile = PathFor(_currentDate);
    }

    public string LogDir => _logDir;
    public string CurrentFile => _currentFile;

    public void Info(string source, string message) => Write("INFO", source, message);
    public void Warn(string source, string message) => Write("WARN", source, message);
    public void Error(string source, string message) => Write("ERROR", source, message);
    public void Debug(string source, string message) => Write("DEBUG", source, message);

    public void Hook(string message) => Info("hook", message);

    private void Write(string level, string source, string message)
    {
        var now = DateTime.Now;
        var today = DateOnly.FromDateTime(now);
        lock (_lock)
        {
            if (today != _currentDate)
            {
                _currentDate = today;
                _currentFile = PathFor(today);
            }
            var line = $"[{now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] [{source}] {message}{Environment.NewLine}";
            File.AppendAllText(_currentFile, line);
        }
    }

    private string PathFor(DateOnly date) =>
        Path.Combine(_logDir, $"app-{date:yyyy-MM-dd}.log");

    public void Dispose() { }
}
