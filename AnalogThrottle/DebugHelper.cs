using System;
using System.IO;

namespace WolfeLabs.AnalogThrottle
{
    public class DebugHelper
    {
        public static readonly string LogFile = GetLogFile();

#if DEBUG
        private static readonly StreamWriter LogWriter = CreateLogWriter();
#endif
        public static void Log (object data)
        {
#if DEBUG
            try {
                string line = $"[{ System.DateTime.Now.ToString("u") }] { Newtonsoft.Json.JsonConvert.SerializeObject(data) }";
                Console.WriteLine(line);

                if (null != LogWriter) {
                    LogWriter.WriteLine(line);
                    LogWriter.Flush();
                }
            } catch {
                // Debug logging should never prevent the game session from loading.
            }
#endif
        }

        private static string GetLogFile ()
        {
            string assemblyPath = typeof(Plugin).Assembly.Location;
            string logDirectory = string.IsNullOrEmpty(assemblyPath) ? null : Path.GetDirectoryName(assemblyPath);

            if (string.IsNullOrEmpty(logDirectory)) {
                logDirectory = AppDomain.CurrentDomain.BaseDirectory;
            }

            if (string.IsNullOrEmpty(logDirectory)) {
                logDirectory = Path.GetTempPath();
            }

            return Path.Combine(logDirectory, "AnalogThrottle.log");
        }

#if DEBUG
        private static StreamWriter CreateLogWriter ()
        {
            try {
                return new StreamWriter(DebugHelper.LogFile, true);
            } catch {
                return null;
            }
        }
#endif
    }
}
