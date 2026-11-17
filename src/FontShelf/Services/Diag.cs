using System;
using System.IO;
using System.Threading.Tasks;

namespace FontShelf.Services
{
    /// <summary>Jednostavan dnevnik (trace.log) i bilježenje rušenja (crash.log) u %LOCALAPPDATA%\FontShelf.</summary>
    public static class Diag
    {
        private static readonly object Lock = new object();
        public static string Dir = AppPaths.Local;
        public static string TracePath => Path.Combine(Dir, "trace.log");
        public static string CrashPath => Path.Combine(Dir, "crash.log");

        public static void Log(string msg)
        {
            try
            {
                lock (Lock)
                {
                    Directory.CreateDirectory(Dir);
                    var fi = new FileInfo(TracePath);
                    if (fi.Exists && fi.Length > 2_000_000) fi.Delete();
                    File.AppendAllText(TracePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {msg}{Environment.NewLine}");
                }
            }
            catch { }
        }

        public static void Crash(string where, Exception ex)
        {
            try
            {
                lock (Lock)
                {
                    Directory.CreateDirectory(Dir);
                    File.AppendAllText(CrashPath, $"===== {DateTime.Now:yyyy-MM-dd HH:mm:ss}  {where}{Environment.NewLine}{ex}{Environment.NewLine}");
                }
            }
            catch { }
            Log("RUŠENJE (" + where + "): " + ex?.Message);
        }

        public static void Install()
        {
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Crash("AppDomain", e.ExceptionObject as Exception);
            TaskScheduler.UnobservedTaskException += (s, e) => { Crash("Task", e.Exception); e.SetObserved(); };
        }
    }
}
