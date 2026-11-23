namespace RpfPreview.Cli;

public sealed class Reporter(bool quiet)
{
    private readonly bool _interactive = !Console.IsErrorRedirected;
    private int _progressWidth;

    public void Progress(string message)
    {
        if (quiet) return;
        if (!_interactive)
        {
            Console.Error.WriteLine(message);
            return;
        }
        string line = message.Length > Console.BufferWidth - 1 ? message[..(Console.BufferWidth - 1)] : message;
        Console.Error.Write("\r" + line.PadRight(_progressWidth));
        _progressWidth = line.Length;
    }

    public void ClearProgress()
    {
        if (!_interactive || _progressWidth == 0) return;
        Console.Error.Write("\r" + new string(' ', _progressWidth) + "\r");
        _progressWidth = 0;
    }

    public void Info(string message)
    {
        if (quiet) return;
        ClearProgress();
        Console.Error.WriteLine(message);
    }

    public void Warn(string message)
    {
        ClearProgress();
        Console.Error.WriteLine("warning: " + message);
    }
}
