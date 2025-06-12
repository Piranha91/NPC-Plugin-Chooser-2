namespace NPC_Plugin_Chooser_2.BackEnd;

public class OptionalUIModule
{
    private Action<string, bool, bool>? _appendLog;
    private Action<int, int, string>? _updateProgress;
    private Action? _resetProgress;
    private Action? _resetLog;
    
    public void ConnectToUILogger(Action<string, bool, bool>? appendLog, Action<int, int, string>? updateProgress, Action? resetProgresss, Action? resetLog)
    {
        _appendLog = appendLog;
        _updateProgress = updateProgress;
        _resetProgress = resetProgresss;
        _resetLog = resetLog;
    }
    
    protected void AppendLog(string message, bool isError = false, bool forceLog = false)
    {
        if (_appendLog == null) return;
        _appendLog(message, isError, forceLog);
    }

    protected void UpdateProgress(int current, int total, string message)
    {
        if (_updateProgress == null) return;
        _updateProgress(current, total, message);
    }

    protected void ResetProgress()
    {
        if (_resetProgress == null) return;
        _resetProgress();
    }

    protected void ResetLog()
    {
        if (_resetLog == null) return;
        _resetLog();
    }
}