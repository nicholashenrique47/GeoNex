using System;
using System.IO;

namespace GeoNex.Services {
    public static class DebugLogger {
        public static void Log(string msg) {
            try { Console.WriteLine("[DEBUG] " + msg); } catch {}
        }
    }
}
