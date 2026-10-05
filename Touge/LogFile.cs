namespace Touge;

/// <summary>Console output (and crashes) additionally into a file: a game started by double-click has no console to show it.</summary>
public static class LogFile
{
    public static void Start(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (File.Exists(path)) File.Move(path, Path.ChangeExtension(path, ".prev.log"), true); // keep the last run (crash) after a restart
            var file = new StreamWriter(path, false) { AutoFlush = true };
            Console.SetOut(TextWriter.Synchronized(new Tee(Console.Out, file)));
            Console.SetError(TextWriter.Synchronized(new Tee(Console.Error, file)));
            Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} Touge {Environment.OSVersion} {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}, log {path}");
            AppDomain.CurrentDomain.UnhandledException += (_, e) => Console.Error.WriteLine($"CRASH: {e.ExceptionObject}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"log {path}: {e.Message}");
        }
    }

    private sealed class Tee(TextWriter a, TextWriter b) : TextWriter
    {
        public override System.Text.Encoding Encoding => a.Encoding;
        public override void Write(char value) { a.Write(value); b.Write(value); }
        public override void Write(string? value) { a.Write(value); b.Write(value); }
        public override void Write(char[] buffer, int index, int count) { a.Write(buffer, index, count); b.Write(buffer, index, count); }
        public override void WriteLine(string? value) { a.WriteLine(value); b.WriteLine(value); }
        public override void Flush() { a.Flush(); b.Flush(); }
    }
}
